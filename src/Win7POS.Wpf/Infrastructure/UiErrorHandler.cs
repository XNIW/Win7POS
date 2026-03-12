using System;
using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Helper per gestione errori in UI: log + MessageBox senza far crashare l'app.</summary>
    public static class UiErrorHandler
    {
        private const string LogPathMessage = "Si è verificato un errore. Controlla i log in C:\\ProgramData\\Win7POS\\logs\\app.log.";

        public static void Handle(Exception ex, FileLogger logger, string context)
        {
            var log = logger ?? new FileLogger();
            log.LogError(ex, context ?? "UI error");
            Win7POS.Wpf.Import.ModernMessageDialog.Show(Application.Current?.MainWindow, "Win7POS", LogPathMessage);
        }
    }
}
