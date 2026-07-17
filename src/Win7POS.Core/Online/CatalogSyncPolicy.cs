using System;

namespace Win7POS.Core.Online
{
    public sealed class CatalogSyncState
    {
        public CatalogSyncState(
            string persistedCursor = null,
            bool bootstrapCompleted = true,
            bool hasShopBinding = true,
            bool legacyCursorMissing = false,
            bool hasPartialCheckpoint = false,
            bool cursorRejectedOrExpired = false,
            bool serverRequestedReset = false,
            bool shopChanged = false,
            bool restoreRecoveryRequired = false,
            bool exactnessRepairRequired = false,
            bool administratorRepairAuthorized = false,
            bool migrationInvalidatedCursor = false,
            CatalogSyncFailure failure = CatalogSyncFailure.None,
            bool catalogIsStale = false)
        {
            PersistedCursor = persistedCursor ?? string.Empty;
            BootstrapCompleted = bootstrapCompleted;
            HasShopBinding = hasShopBinding;
            LegacyCursorMissing = legacyCursorMissing;
            HasPartialCheckpoint = hasPartialCheckpoint;
            CursorRejectedOrExpired = cursorRejectedOrExpired;
            ServerRequestedReset = serverRequestedReset;
            ShopChanged = shopChanged;
            RestoreRecoveryRequired = restoreRecoveryRequired;
            ExactnessRepairRequired = exactnessRepairRequired;
            AdministratorRepairAuthorized = administratorRepairAuthorized;
            MigrationInvalidatedCursor = migrationInvalidatedCursor;
            Failure = failure;
            CatalogIsStale = catalogIsStale;
        }

        public string PersistedCursor { get; }
        public bool BootstrapCompleted { get; }
        public bool HasShopBinding { get; }
        public bool LegacyCursorMissing { get; }
        public bool HasPartialCheckpoint { get; }
        public bool CursorRejectedOrExpired { get; }
        public bool ServerRequestedReset { get; }
        public bool ShopChanged { get; }
        public bool RestoreRecoveryRequired { get; }
        public bool ExactnessRepairRequired { get; }
        public bool AdministratorRepairAuthorized { get; }
        public bool MigrationInvalidatedCursor { get; }
        public CatalogSyncFailure Failure { get; }
        public bool CatalogIsStale { get; }
    }

    public static class CatalogSyncPolicy
    {
        public static CatalogSyncDecision Evaluate(
            CatalogSyncTrigger trigger,
            CatalogSyncState state)
        {
            if (!Enum.IsDefined(typeof(CatalogSyncTrigger), trigger))
            {
                throw new ArgumentOutOfRangeException(nameof(trigger));
            }

            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            switch (state.Failure)
            {
                case CatalogSyncFailure.AuthenticationDenied:
                    return Blocked("catalog_sync_relink_required", "catalog_sync_auth_denied");
                case CatalogSyncFailure.UnsupportedContract:
                    return Blocked("catalog_sync_update_required", "catalog_sync_contract_unsupported");
                case CatalogSyncFailure.DatabaseIntegrityFailed:
                    return Blocked("catalog_sync_maintenance_required", "catalog_sync_database_integrity_failed");
                case CatalogSyncFailure.OperatorCancelled:
                    return new CatalogSyncDecision(
                        CatalogSyncMode.NoOp,
                        CatalogFullSyncReason.None,
                        false,
                        true,
                        state.PersistedCursor,
                        "catalog_sync_cancelled",
                        "catalog_sync_operator_cancelled");
            }

            if (trigger == CatalogSyncTrigger.AdministratorRepair &&
                !state.AdministratorRepairAuthorized)
            {
                return Blocked(
                    "catalog_sync_repair_permission_required",
                    "catalog_sync_administrator_repair_denied");
            }

            if (trigger == CatalogSyncTrigger.FirstBootstrap && !state.BootstrapCompleted)
            {
                return Full(CatalogFullSyncReason.FirstBootstrap);
            }

            if (!state.HasShopBinding)
            {
                return Full(CatalogFullSyncReason.MissingShopBinding);
            }

            if (state.LegacyCursorMissing)
            {
                return Full(CatalogFullSyncReason.MissingLegacyCursor);
            }

            if (state.MigrationInvalidatedCursor)
            {
                return Full(CatalogFullSyncReason.MigrationInvalidatedCursor);
            }

            if (trigger == CatalogSyncTrigger.CursorRejected || state.CursorRejectedOrExpired)
            {
                return Full(CatalogFullSyncReason.CursorRejectedOrExpired);
            }

            if (trigger == CatalogSyncTrigger.ServerFullRequired || state.ServerRequestedReset)
            {
                return Full(CatalogFullSyncReason.ServerRequestedReset);
            }

            if (trigger == CatalogSyncTrigger.ShopTransition && state.ShopChanged)
            {
                return Full(CatalogFullSyncReason.ShopChanged);
            }

            if (trigger == CatalogSyncTrigger.RestoreCompleted && state.RestoreRecoveryRequired)
            {
                return Full(CatalogFullSyncReason.RestoreRecovery);
            }

            if (trigger == CatalogSyncTrigger.ExactnessMismatch && state.ExactnessRepairRequired)
            {
                return Full(CatalogFullSyncReason.ExactnessRepair);
            }

            if (trigger == CatalogSyncTrigger.AdministratorRepair)
            {
                return Full(CatalogFullSyncReason.AdministratorRepair);
            }

            if (state.HasPartialCheckpoint)
            {
                if (string.IsNullOrWhiteSpace(state.PersistedCursor))
                {
                    return Blocked(
                        "catalog_sync_repair_required",
                        "catalog_sync_partial_checkpoint_invalid");
                }

                return Incremental(
                    CatalogSyncMode.ResumeIncremental,
                    state.PersistedCursor,
                    "catalog_sync_resuming",
                    DiagnosticCodeFor(state.Failure, "catalog_sync_resume_incremental"));
            }

            return Incremental(
                CatalogSyncMode.Incremental,
                string.Empty,
                "catalog_sync_incremental",
                DiagnosticCodeFor(state.Failure, "catalog_sync_incremental"));
        }

        private static CatalogSyncDecision Full(CatalogFullSyncReason reason)
        {
            var reasonCode = ToSnakeCase(reason.ToString());
            return new CatalogSyncDecision(
                CatalogSyncMode.Full,
                reason,
                true,
                false,
                string.Empty,
                "catalog_sync_full_required",
                "catalog_sync_full_" + reasonCode);
        }

        private static CatalogSyncDecision Incremental(
            CatalogSyncMode mode,
            string resumeCursor,
            string operatorMessageCode,
            string diagnosticCode)
        {
            return new CatalogSyncDecision(
                mode,
                CatalogFullSyncReason.None,
                false,
                true,
                resumeCursor,
                operatorMessageCode,
                diagnosticCode);
        }

        private static CatalogSyncDecision Blocked(
            string operatorMessageCode,
            string diagnosticCode)
        {
            return new CatalogSyncDecision(
                CatalogSyncMode.Blocked,
                CatalogFullSyncReason.None,
                true,
                true,
                string.Empty,
                operatorMessageCode,
                diagnosticCode);
        }

        private static string DiagnosticCodeFor(CatalogSyncFailure failure, string successCode)
        {
            switch (failure)
            {
                case CatalogSyncFailure.Network:
                    return "catalog_sync_network_retry";
                case CatalogSyncFailure.Timeout:
                    return "catalog_sync_timeout_retry";
                case CatalogSyncFailure.HttpServerError:
                    return "catalog_sync_server_retry";
                default:
                    return successCode;
            }
        }

        private static string ToSnakeCase(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var result = value.Substring(0, 1).ToLowerInvariant();
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsUpper(character))
                {
                    result += "_";
                }

                result += char.ToLowerInvariant(character);
            }

            return result;
        }
    }
}
