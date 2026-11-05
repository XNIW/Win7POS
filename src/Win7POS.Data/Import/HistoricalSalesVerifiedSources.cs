using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Win7POS.Data.Import
{
    /// <summary>Opaque verified source handles, bound to one immutable request and held through its use.</summary>
    public sealed class HistoricalSalesVerifiedSources : IDisposable
    {
        private readonly object _sync = new object();
        private readonly string _requestFingerprint;
        private List<FileStream> _handles;
        private bool _disposed;
        private int _uses;
        internal HistoricalSalesVerifiedSources(string requestFingerprint, List<FileStream> handles)
        { _requestFingerprint = requestFingerprint; _handles = handles ?? throw new ArgumentNullException(nameof(handles)); }
        internal IDisposable AcquireUse(string requestFingerprint)
        {
            lock (_sync)
            {
                if (_disposed || _handles == null) throw new ObjectDisposedException(nameof(HistoricalSalesVerifiedSources));
                if (!string.Equals(_requestFingerprint, requestFingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("historical_verified_sources_request_mismatch");
                _uses++; return new Use(this);
            }
        }
        private void ReleaseUse()
        {
            List<FileStream> close = null;
            lock (_sync) { _uses--; if (_uses == 0 && _disposed) { close = _handles; _handles = null; } }
            Close(close);
        }
        public void Dispose()
        {
            List<FileStream> close = null;
            lock (_sync) { if (_disposed) return; _disposed = true; if (_uses == 0) { close = _handles; _handles = null; } }
            Close(close);
        }
        private static void Close(List<FileStream> handles) { if (handles != null) foreach (var handle in handles) handle.Dispose(); }
        private sealed class Use : IDisposable
        {
            private HistoricalSalesVerifiedSources _owner;
            internal Use(HistoricalSalesVerifiedSources owner) { _owner = owner; }
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseUse();
        }
    }
}
