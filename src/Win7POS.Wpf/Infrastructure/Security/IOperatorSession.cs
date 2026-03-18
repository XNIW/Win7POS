using System.Threading.Tasks;
using Win7POS.Core.Security;

namespace Win7POS.Wpf.Infrastructure.Security
{
    public enum LoginResult
    {
        Success,
        Failed,
        LockedOut
    }

    public interface IOperatorSession
    {
        UserAccount CurrentUser { get; }
        bool IsLoggedIn { get; }
        bool CurrentUserIsAdmin { get; }
        string CurrentDisplayName { get; }
        string CurrentRoleName { get; }

        /// <summary>Esegue login con username e PIN. Ritorna l'esito dell'autenticazione.</summary>
        Task<LoginResult> LoginAsync(string username, string pin);

        void Logout();

        /// <summary>Logout forzato (es. annullamento cambio PIN); registra forced_logout in audit.</summary>
        void LogoutForced();

        /// <summary>Registra in audit un override (autorizzazione da supervisore).</summary>
        void LogOverride(string permissionCode, string operationText, int authorizerUserId);

        /// <summary>Registra evento sicurezza (audit) per l'operatore corrente.</summary>
        void LogSecurityEvent(string eventType, string details);

        /// <summary>Notifica cambiamento sessione (per binding UI).</summary>
        event System.Action SessionChanged;
    }
}
