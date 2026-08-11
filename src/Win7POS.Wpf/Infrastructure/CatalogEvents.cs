using System;
using System.Threading;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Evento statico per notificare modifiche al catalogo prodotti (create/update/delete). La POS può sottoscriversi per aggiornare il carrello. Barcode = riga da sincronizzare; null/empty = refresh completo.</summary>
    public static class CatalogEvents
    {
        private static long _revision;

        public static event Action<string> CatalogChanged;

        public static long Revision => Interlocked.Read(ref _revision);

        public static long AdvanceRevision() => Interlocked.Increment(ref _revision);

        public static void RaiseCatalogChanged(string barcode = null)
        {
            AdvanceRevision();
            CatalogChanged?.Invoke(barcode ?? string.Empty);
        }
    }
}
