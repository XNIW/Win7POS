namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Identifies the authority that initiated a product write. Only
    /// LocalUserSave is allowed to create a general article-mutation intent.
    /// </summary>
    public enum ProductWriteOrigin
    {
        LocalUserSave,
        SupplierImportApply,
        RemoteCatalogApply,
        ArticleMutationAck,
        SalesMovement,
        MaintenanceRestore,
        TestFixture
    }
}
