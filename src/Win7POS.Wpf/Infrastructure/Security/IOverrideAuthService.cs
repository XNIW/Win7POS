namespace Win7POS.Wpf.Infrastructure.Security
{
    /// <summary>Servizio per richiedere autorizzazione override (es. supervisore) per operazioni riservate.</summary>
    public interface IOverrideAuthService
    {
        /// <summary>Mostra dialog e verifica PIN di un operatore con il permesso richiesto. Ritorna true e authorizerUserId se OK.</summary>
        bool RequestOverride(string operationText, string requiredPermissionCode, out int? authorizerUserId);
    }
}
