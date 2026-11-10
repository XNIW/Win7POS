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
    // A shell capability is derived from current identity and catalog evidence.
    // It never grants historical permissions or ordinary-sale authority.
    internal static class PosShellAdmissionReader
    {
        internal static async Task<PosShellAdmission> ReadAsync(SqliteConnectionFactory factory,
            IOperatorSession session, OnlineSyncGeneration generation, PosAuthenticatedAccessMode mode,
            bool safeStart = false, bool recoveryRequired = false, CancellationToken cancellationToken = default)
        {
            if (mode == PosAuthenticatedAccessMode.LocalRecovery)
                return PosStartupCoordinatorPolicy.EvaluateAdmission(mode, session?.IsLoggedIn == true, false, false, false);
            var concrete = session as OperatorSession;
            var version = concrete?.CurrentOperatorAuthorityVersion;
            var authorized = ReferenceEquals(session, OperatorSessionHolder.Current) && session?.IsLoggedIn == true &&
                session.EnsureAuthorizationValid();
            if (!authorized)
                return PosStartupCoordinatorPolicy.EvaluateAdmission(mode, false, false, false, false);

            // A pull clears the empty observation before publishing its verified result.
            // Read the completed transition; entering recovery mid-pull would stop its publisher.
            using var catalogTransition = await new CatalogShopTransitionBarrier(factory)
                .EnterAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var currentGeneration = generation != null &&
                new PosTrustedDeviceStore().TryReadGeneration(generation, out _, out _) &&
                await new OnlineSyncGenerationRepository(factory).IsCurrentAndActiveAsync(generation).ConfigureAwait(false);
            var saleSafe = currentGeneration && await PosCatalogPullService.IsCatalogSaleSafeAsync(factory).ConfigureAwait(false);
            var empty = currentGeneration && await new CatalogShopStateRepository(factory)
                .IsAuthoritativelyEmptyAsync(generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            authorized = ReferenceEquals(session, OperatorSessionHolder.Current) && concrete != null &&
                concrete.CurrentOperatorAuthorityVersion == version && session.IsLoggedIn && session.EnsureAuthorizationValid();
            currentGeneration = currentGeneration && new PosTrustedDeviceStore().TryReadGeneration(generation, out _, out _) &&
                await new OnlineSyncGenerationRepository(factory).IsCurrentAndActiveAsync(generation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            authorized = authorized && ReferenceEquals(session, OperatorSessionHolder.Current) &&
                concrete.CurrentOperatorAuthorityVersion == version && session.IsLoggedIn && session.EnsureAuthorizationValid();
            return PosStartupCoordinatorPolicy.EvaluateAdmission(mode, authorized, authorized,
                currentGeneration && saleSafe, currentGeneration && empty, safeStart, recoveryRequired);
        }
    }
}
