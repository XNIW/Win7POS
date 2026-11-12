using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Reports;

namespace Win7POS.Data.Reports
{
    /// <summary>Per-database, short publication boundary. Destination IO must never run inside it.</summary>
    public sealed class SalesReportSnapshotCoordinator
    {
        private static readonly ConcurrentDictionary<string, SalesReportSnapshotCoordinator> Coordinators = new ConcurrentDictionary<string, SalesReportSnapshotCoordinator>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _publicationGate = new SemaphoreSlim(1, 1);
        private readonly object _stampSync = new object();
        private Stamp _current;
        private AuthorityBinding _authority;
        private long _changeVersion;
        public event EventHandler SnapshotChanged;
        public event EventHandler HistoricalSyncStatusChanged;
        private static readonly TimeSpan AuthorityTransitionTimeout = TimeSpan.FromSeconds(5);
        public static SalesReportSnapshotCoordinator ForDatabase(string canonicalPath)
        {
            if (string.IsNullOrWhiteSpace(canonicalPath)) throw new ArgumentException("A database slot is required.");
            // Resolve aliases outside the short publication boundary. The owner
            // and every factory for the same physical slot share one coordinator.
            var full = Path.GetFullPath(canonicalPath);
            var slot = Directory.Exists(Path.GetDirectoryName(full)) ? PosDatabasePathIdentity.CanonicalSlot(full) : full;
            return Coordinators.GetOrAdd(slot, _ => new SalesReportSnapshotCoordinator());
        }
        public async Task<IDisposable> EnterPublicationAsync(CancellationToken cancellationToken)
        {
            await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GateLease(this, Volatile.Read(ref _changeVersion));
        }
        public bool TryUseSnapshot(SalesReportReadToken token, Action captureOnly)
        {
            if (token == null || captureOnly == null) throw new ArgumentNullException();
            if (!_publicationGate.Wait(0)) return false;
            try { if (!IsCurrent(token)) return false; captureOnly(); return true; }
            finally { _publicationGate.Release(); }
        }
        public bool IsCurrent(SalesReportReadToken token)
        {
            var binding = Volatile.Read(ref _authority);
            var current = Volatile.Read(ref _current);
            var authority = binding?.Read();
            // Read-side consumers (including Dispatcher getters) take no lock,
            // open no file and call only the owner's volatile stamp projection.
            return current != null && current.Matches(token) && Matches(authority, token) &&
                ReferenceEquals(current, Volatile.Read(ref _current)) && ReferenceEquals(binding, Volatile.Read(ref _authority)) &&
                ReferenceEquals(authority, binding.Read());
        }
        public void ObserveRead(SalesReportReadToken token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            lock (_stampSync)
            {
                if (!Matches(Volatile.Read(ref _authority)?.Read(), token)) throw new ReportSnapshotStaleException();
                if (_current == null || !_current.SameAuthority(token) || token.DataRevision > _current.Revision)
                    Volatile.Write(ref _current, new Stamp(token.DatasetId, token.DataRevision, token.RestoreEpoch, token.AuthorityInstanceEpoch));
            }
        }
        public void Invalidate()
        {
            WithAuthorityPublication(() => { lock (_stampSync) { Volatile.Write(ref _current, null); Interlocked.Increment(ref _changeVersion); } });
        }
        internal void BindAuthority(object owner, Func<SalesReportAuthorityStamp> readInMemory)
        {
            if (owner == null || readInMemory == null) throw new ArgumentNullException();
            WithAuthorityPublication(() =>
            {
                lock (_stampSync)
                {
                    Volatile.Write(ref _current, null);
                    Volatile.Write(ref _authority, new AuthorityBinding(owner, readInMemory));
                    Interlocked.Increment(ref _changeVersion);
                }
            });
        }
        internal void ChangeAuthority(object owner, Action publishMemoryOnly)
        {
            if (owner == null || publishMemoryOnly == null) throw new ArgumentNullException();
            WithAuthorityPublication(() =>
            {
                lock (_stampSync)
                {
                    if (!ReferenceEquals(_authority?.Owner, owner)) throw new ReportSnapshotStaleException();
                    publishMemoryOnly();
                    Volatile.Write(ref _current, null);
                    Interlocked.Increment(ref _changeVersion);
                }
            });
        }
        private void WithAuthorityPublication(Action action)
        {
            if (!_publicationGate.Wait(AuthorityTransitionTimeout)) throw new TimeoutException("report_authority_transition_timeout");
            var before = Volatile.Read(ref _changeVersion);
            try { action(); } finally { _publicationGate.Release(); NotifyAfterRelease(before); }
        }
        private void NotifyAfterRelease(long before)
        {
            if (before == Volatile.Read(ref _changeVersion)) return;
            var handlers = SnapshotChanged;
            if (handlers == null) return;
            // Subscribers schedule their own bounded reload. No subscriber can
            // turn an already committed write into a failed economic operation.
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); } catch { }
        }
        internal void NotifyHistoricalSyncStatusCommitted()
        {
            // Transport metadata does not change the economic report revision.
            // Call only after committing and releasing the SQLite writer.
            var handlers=HistoricalSyncStatusChanged;
            if(handlers==null)return;
            foreach(EventHandler handler in handlers.GetInvocationList())
                try{handler(this,EventArgs.Empty);}catch{}
        }
        private static bool Matches(SalesReportAuthorityStamp authority, SalesReportReadToken token)
            => authority != null && token != null && authority.RestoreEpoch == token.RestoreEpoch && authority.AuthorityInstanceEpoch == token.AuthorityInstanceEpoch;
        /// <summary>Called after a successful commit, before releasing the publication lease.</summary>
        public void PublishCommitted(string datasetId, long revision, SalesReportAuthorityStamp authority)
        {
            if (authority == null || string.IsNullOrWhiteSpace(datasetId) || revision < 0) throw new ArgumentException("Invalid report commit stamp.");
            lock (_stampSync)
            {
                var active = Volatile.Read(ref _authority)?.Read();
                if (active == null || active.RestoreEpoch != authority.RestoreEpoch || active.AuthorityInstanceEpoch != authority.AuthorityInstanceEpoch)
                    throw new ReportSnapshotStaleException();
                var next = new Stamp(datasetId, revision, authority.RestoreEpoch, authority.AuthorityInstanceEpoch);
                var old = Volatile.Read(ref _current);
                Volatile.Write(ref _current, next);
                if (old == null || old.Dataset != next.Dataset || old.Revision != next.Revision ||
                    old.RestoreEpoch != next.RestoreEpoch || old.Authority != next.Authority)
                    Interlocked.Increment(ref _changeVersion);
            }
        }
        public void CommitAndPublishReportRevision(SqliteConnection connection, SqliteTransaction transaction, Action commit, SalesReportAuthorityStamp authority)
        {
            if (connection == null || transaction == null || commit == null) throw new ArgumentNullException();
            // The caller must have acquired EnterPublicationAsync before its final authority check.
            var row = connection.QuerySingle<StateRow>("SELECT dataset_id AS DatasetId, data_revision+(SELECT revision FROM historical_cloud_cache_revision WHERE singleton_id=1) AS DataRevision FROM historical_import_dataset WHERE singleton_id=1", transaction: transaction);
            commit();
            PublishCommitted(row.DatasetId, row.DataRevision, authority);
        }
        private sealed class Stamp
        {
            internal Stamp(string dataset, long revision, long restoreEpoch, string authority) { Dataset = dataset; Revision = revision; RestoreEpoch = restoreEpoch; Authority = authority; }
            internal string Dataset { get; }
            internal long Revision { get; }
            internal long RestoreEpoch { get; }
            internal string Authority { get; }
            internal bool SameAuthority(SalesReportReadToken token) { return token.DatasetId == Dataset && token.RestoreEpoch == RestoreEpoch && token.AuthorityInstanceEpoch == Authority; }
            internal bool Matches(SalesReportReadToken token) { return token != null && SameAuthority(token) && token.DataRevision == Revision; }
        }
        private sealed class AuthorityBinding
        {
            internal AuthorityBinding(object owner, Func<SalesReportAuthorityStamp> read) { Owner = owner; Read = read; }
            internal object Owner { get; }
            internal Func<SalesReportAuthorityStamp> Read { get; }
        }
        private sealed class StateRow { public string DatasetId { get; set; } public long DataRevision { get; set; } }
        private sealed class GateLease : IDisposable
        {
            private SalesReportSnapshotCoordinator _owner;
            private readonly long _before;
            internal GateLease(SalesReportSnapshotCoordinator owner, long before) { _owner = owner; _before = before; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                owner._publicationGate.Release();
                owner.NotifyAfterRelease(_before);
            }
        }
    }
}
