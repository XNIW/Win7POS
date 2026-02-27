namespace Win7POS.Core.Import
{
    public enum ImportDiffKind
    {
        NewProduct = 1,
        UpdatePrice = 2,
        UpdateName = 3,
        UpdateBoth = 4,
        NoChange = 5,
        InvalidRow = 6
    }
}
