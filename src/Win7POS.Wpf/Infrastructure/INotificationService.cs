namespace Win7POS.Wpf.Infrastructure
{
    /// <summary>Servizio per mostrare messaggi all'utente (toast, snackbar, errori critici).</summary>
    public interface INotificationService
    {
        void ShowInfo(string message);
        void ShowWarning(string message);
        void ShowError(string message, string title = "Errore");
    }
}
