using System.Threading.Tasks;

namespace Win7POS.Wpf.Infrastructure.Security
{
    /// <summary>Servizio per richiedere autorizzazione override (es. supervisore) per operazioni riservate.</summary>
    public interface IOverrideAuthService
    {
        /// <summary>Mostra dialog e verifica PIN di un operatore con il permesso richiesto. Ritorna (true, authorizerUserId) se OK.</summary>
        Task<(bool ok, int? authorizerUserId)> RequestOverrideAsync(string operationText, string requiredPermissionCode);

        /// <summary>Mostra dialog e verifica PIN di un utente admin. Ritorna (true, authorizerUserId) se OK. Per sblocco vista completa vendite.</summary>
        Task<(bool ok, int? authorizerUserId)> RequestAdminOverrideAsync(string operationText);
    }
}
