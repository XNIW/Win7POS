using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;

namespace Win7POS.Wpf.Pos.Online
{
    // This capability opens only the historical workbench. It never authorizes
    // the POS shell, a sale, a payment or recovery access.
    internal sealed class HistoricalOnboardingAccess
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly OnlineSyncGeneration _generation;
        private readonly OperatorSession.HistoricalReadAuthority _authority;
        private readonly OperatorSession _session;
        private HistoricalOnboardingAccess(SqliteConnectionFactory factory, OperatorSession session,
            OnlineSyncGeneration generation, OperatorSession.HistoricalReadAuthority authority)
        { _factory = factory; _session = session; _generation = generation; _authority = authority; }

        internal OperatorSession Session => _session;
        internal bool IsCurrentInMemory => _authority.IsCurrentInMemory;

        internal static async Task<HistoricalOnboardingAccess> CreateAsync(SqliteConnectionFactory factory,
            OperatorSession session, OnlineSyncGeneration generation, CancellationToken cancellationToken)
        {
            if (factory == null || session == null || generation == null)
                throw new InvalidOperationException("historical_onboarding_authority_required");
            var permissions = new PermissionService(session);
            var permission = permissions.Has(PermissionCodes.RegisterImportHistory) ? PermissionCodes.RegisterImportHistory :
                permissions.Has(PermissionCodes.RegisterCancelHistory) ? PermissionCodes.RegisterCancelHistory :
                permissions.Has(PermissionCodes.DailyCloseView) && permissions.Has(PermissionCodes.RegisterViewAll)
                    ? PermissionCodes.RegisterViewAll : null;
            if (permission == null) throw new InvalidOperationException("historical_onboarding_permission_denied");
            var access = new HistoricalOnboardingAccess(factory, session, generation,
                session.CaptureHistoricalUiReadAuthority(permission, generation));
            await access.DemandAsync(cancellationToken).ConfigureAwait(false);
            return access;
        }

        internal async Task DemandAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _authority.DemandStillValid();
            if (!await Task.Run(() => new CatalogShopStateRepository(_factory).IsAuthoritativelyEmptyAsync(_generation),
                    cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("historical_onboarding_empty_evidence_required");
            cancellationToken.ThrowIfCancellationRequested();
            _authority.DemandStillValid();
        }
    }
}
