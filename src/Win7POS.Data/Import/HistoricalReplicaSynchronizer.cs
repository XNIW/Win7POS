using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Import
{
    public sealed class HistoricalCloudResnapshotRequiredException : InvalidOperationException
    {
        public HistoricalCloudResnapshotRequiredException() : base("history_cloud_resnapshot_required") { }
    }

    /// <summary>
    /// Bounded, resumable network/staging work. Owns no commercial or backup gate.
    /// The store publishes only a complete, scope-fenced SQLite generation.
    /// </summary>
    public sealed class HistoricalReplicaSynchronizer
    {
        public const int MaximumPagesPerTurn = 8;
        private readonly HistoricalReplicaStore _store;
        private readonly Action _demand;
        private readonly Func<string, HistoricalCloudPayload, CancellationToken, Task<HistoricalCloudResult>> _request;

        public HistoricalReplicaSynchronizer(HistoricalReplicaStore store, Action demandCurrent,
            Func<string, HistoricalCloudPayload, CancellationToken, Task<HistoricalCloudResult>> request)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _demand = demandCurrent ?? throw new ArgumentNullException(nameof(demandCurrent));
            _request = request ?? throw new ArgumentNullException(nameof(request));
        }

        // SQLite's async provider can enter synchronous lock waits. Keep the entire
        // turn, including initial reads and publication/checksum, off the Dispatcher.
        public Task<bool> RunAsync(string shop, string buildSha, CancellationToken ct)
            => Task.Run(() => RunCoreAsync(shop, buildSha, ct), ct);

        private async Task<bool> RunCoreAsync(string shop, string buildSha, CancellationToken ct)
        {
            HistoricalReplicaProtocol.DemandId(shop);
            if (buildSha == null || !System.Text.RegularExpressions.Regex.IsMatch(buildSha, "^[0-9a-f]{40}$"))
                throw new InvalidOperationException("history_cloud_build_identity_required");
            try
            {
                _demand();
                var prior = await _store.ReadAsync(shop, ct).ConfigureAwait(false);
                var authority = await _request("authority", new HistoricalCloudPayload(), ct).ConfigureAwait(false);
                await _store.ObserveAuthorityAsync(shop, authority, ct).ConfigureAwait(false);
                var delta = prior?.GenerationId != null && prior.DatasetId == authority.DatasetId &&
                    prior.WriterEpoch == HistoricalReplicaProtocol.Integer(authority.WriterEpoch) && prior.PublishedRevision.HasValue;
                var pending = await _store.ReadPendingAsync(shop, ct).ConfigureAwait(false);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var manifest = attempt == 0 && pending.GenerationId != null ? pending.Manifest :
                            await _request(attempt == 0 && delta ? "delta_start" : "snapshot_start", attempt == 0 && delta ?
                                new HistoricalCloudPayload { DatasetId = prior.DatasetId, Revision = prior.PublishedRevision.Value.ToString(CultureInfo.InvariantCulture), WriterEpoch = authority.WriterEpoch, PageSize = 200 } :
                                new HistoricalCloudPayload { RequestKey = Guid.NewGuid().ToString("D"), PageSize = 200 }, ct).ConfigureAwait(false);
                        var generation = attempt == 0 && pending.GenerationId != null ? pending.GenerationId :
                            await _store.BeginAsync(shop, manifest, ct).ConfigureAwait(false);
                        var cursor = attempt == 0 && pending.Complete ? null : manifest.Cursor;
                        for (var pages = 0; cursor != null; pages++)
                        {
                            if (pages == MaximumPagesPerTurn) return true;
                            _demand(); ct.ThrowIfCancellationRequested();
                            var page = await _request("page", new HistoricalCloudPayload { Cursor = cursor }, ct).ConfigureAwait(false);
                            await _store.StageAsync(generation, page, ct).ConfigureAwait(false);
                            cursor = page.NextCursor;
                        }
                        var finalCursor = await _store.PublishAsync(generation, ct).ConfigureAwait(false);
                        var ack = await _request("replica_ack", new HistoricalCloudPayload { Cursor = finalCursor, Checksum = manifest.Checksum, ProtocolVersion = 1, ClientBuildSha = buildSha }, ct).ConfigureAwait(false);
                        if (!ack.Acknowledged || ack.Revision != manifest.Revision || ack.WriterEpoch != manifest.WriterEpoch)
                            throw new InvalidOperationException("history_replica_ack_invalid");
                        _demand(); return false;
                    }
                    catch (HistoricalCloudResnapshotRequiredException) when (attempt == 0) { _demand(); }
                }
                throw new InvalidOperationException("history_replica_resnapshot_required");
            }
            catch
            {
                try { await _store.MarkStaleAsync(shop, CancellationToken.None).ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }
}
