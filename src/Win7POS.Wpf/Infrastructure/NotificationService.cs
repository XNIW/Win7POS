using System;
using System.Windows;

namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Implementazione che usa MessageBox per errori critici, log per info.</summary>
    public sealed class NotificationService : INotificationService
    {
        private static INotificationService _current;
        public static INotificationService Current => _current ??= new NotificationService();

        private readonly FileLogger _logger;
        public event Action<string> OnInfoMessage;

        public NotificationService(FileLogger logger = null)
        {
            _logger = logger ?? new FileLogger();
        }

        public static void SetDefault(INotificationService service) => _current = service;

        public void ShowInfo(string message)
        {
            _logger.LogInfo(message ?? string.Empty);
            OnInfoMessage?.Invoke(message ?? string.Empty);
        }

        public void ShowWarning(string message)
        {
            _logger.LogInfo("WARN: " + (message ?? string.Empty));
            MessageBox.Show(Application.Current?.MainWindow, message ?? string.Empty, "Attenzione", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void ShowError(string message, string title = "Errore")
        {
            _logger.LogError(null, message ?? string.Empty);
            MessageBox.Show(Application.Current?.MainWindow, message ?? string.Empty, title ?? "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
