using System.Threading;

namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Serializes catalog mutations that must not interleave with a full-refresh
    /// reconciliation or another catalog batch.
    /// </summary>
    internal static class CatalogMutationGate
    {
        internal static readonly SemaphoreSlim Instance = new SemaphoreSlim(1, 1);
    }
}
