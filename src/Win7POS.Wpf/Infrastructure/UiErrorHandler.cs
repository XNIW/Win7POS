using System;
using System.Windows;
using Win7POS.Core;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Helper per gestione errori in UI: log + MessageBox senza far crashare l'app. Il messaggio indica il path dei log per debug.</summary>
    public static class UiErrorHandler
    {
        private static string LogPathMessage =>
            "Si è verificato un errore. Controlla i log per dettagli:\n" + (AppPaths.LogPath ?? "app.log");

        public static void Handle(Exception ex, FileLogger logger, string context)
        {
            var log = logger ?? new FileLogger("UiErrorHandler");
            log.LogError(ex, context ?? "UI error");
            try
            {
                Win7POS.Wpf.Import.ModernMessageDialog.Show(Application.Current?.MainWindow, "Win7POS", LogPathMessage);
            }
            catch (Exception showEx)
            {
                log.LogError(showEx, "UiErrorHandler: impossibile mostrare MessageBox");
            }
        }
    }
}
