using System;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Evento statico per notificare modifiche al catalogo prodotti (create/update/delete). La POS può sottoscriversi per aggiornare il carrello. Barcode = riga da sincronizzare; null/empty = refresh completo.</summary>
    public static class CatalogEvents
    {
        public static event Action<string> CatalogChanged;

        public static void RaiseCatalogChanged(string barcode = null) => CatalogChanged?.Invoke(barcode ?? string.Empty);
    }
}
