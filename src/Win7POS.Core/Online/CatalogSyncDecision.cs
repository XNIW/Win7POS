namespace Win7POS.Core.Online
{
    public enum CatalogSyncMode
    {
        NoOp = 0,
        Incremental = 1,
        ResumeIncremental = 2,
        Full = 3,
        Blocked = 4
    }

    public enum CatalogFullSyncReason
    {
        None = 0,
        FirstBootstrap = 1,
        MissingShopBinding = 2,
        MissingLegacyCursor = 3,
        CursorRejectedOrExpired = 4,
        ServerRequestedReset = 5,
        ShopChanged = 6,
        RestoreRecovery = 7,
        ExactnessRepair = 8,
        AdministratorRepair = 9,
        MigrationInvalidatedCursor = 10
    }

    public sealed class CatalogSyncDecision
    {
        internal CatalogSyncDecision(
            CatalogSyncMode mode,
            CatalogFullSyncReason fullReason,
            bool isBlocking,
            bool preserveExistingSaleSafe,
            string resumeCursor,
            string operatorMessageCode,
            string diagnosticCode)
        {
            Mode = mode;
            FullReason = fullReason;
            IsBlocking = isBlocking;
            PreserveExistingSaleSafe = preserveExistingSaleSafe;
            ResumeCursor = resumeCursor ?? string.Empty;
            OperatorMessageCode = operatorMessageCode ?? string.Empty;
            DiagnosticCode = diagnosticCode ?? string.Empty;
        }

        public CatalogSyncMode Mode { get; }
        public CatalogFullSyncReason FullReason { get; }
        public bool IsBlocking { get; }
        public bool PreserveExistingSaleSafe { get; }
        public string ResumeCursor { get; }
        public string OperatorMessageCode { get; }
        public string DiagnosticCode { get; }
    }
}
