namespace Win7POS.Core.Online
{
    public enum CatalogSyncTrigger
    {
        FirstBootstrap = 0,
        StartOfDay = 1,
        Periodic = 2,
        Manual = 3,
        Foreground = 4,
        NetworkRecovered = 5,
        CatalogImportAcked = 6,
        PartialResume = 7,
        CursorRejected = 8,
        ServerFullRequired = 9,
        ShopTransition = 10,
        RestoreCompleted = 11,
        ExactnessMismatch = 12,
        AdministratorRepair = 13
    }

    public enum CatalogSyncFailure
    {
        None = 0,
        Network = 1,
        Timeout = 2,
        HttpServerError = 3,
        AuthenticationDenied = 4,
        UnsupportedContract = 5,
        DatabaseIntegrityFailed = 6,
        OperatorCancelled = 7,
        TerminalPaginationAmbiguous = 8
    }
}
