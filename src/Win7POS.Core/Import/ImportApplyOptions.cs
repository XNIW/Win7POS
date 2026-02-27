namespace Win7POS.Core.Import
{
    public sealed class ImportApplyOptions
    {
        public bool DryRun { get; set; }
        public bool InsertNew { get; set; } = true;
        public bool UpdatePrice { get; set; } = true;
        public bool UpdateName { get; set; }
    }
}
