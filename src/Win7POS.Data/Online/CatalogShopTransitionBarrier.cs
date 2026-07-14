using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Win7POS.Data.Online
{
    public sealed class CatalogShopTransitionBarrier
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly SemaphoreSlim _gate;

        public CatalogShopTransitionBarrier(SqliteConnectionFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var key = Path.GetFullPath(factory.DbPath);
            _gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(_gate);
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim _gate;

            public Lease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
