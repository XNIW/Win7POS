using System;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Evento statico per notificare modifiche al catalogo prodotti (create/update/delete). La POS può sottoscriversi per aggiornare il carrello.</summary>
    public static class CatalogEvents
    {
        public static event Action CatalogChanged;

        public static void RaiseCatalogChanged() => CatalogChanged?.Invoke();
    }
}
