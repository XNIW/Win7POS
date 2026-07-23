namespace Win7POS.Data.Repositories
{
    /// <summary>
    /// Lightweight supplier/category reference used while applying product metadata.
    /// It intentionally belongs to neither the local façade nor the remote writer.
    /// </summary>
    internal sealed class ProductMetaReference
    {
        public int? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
