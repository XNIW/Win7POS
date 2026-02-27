namespace Win7POS.Core.Import
{
    public sealed class ImportParseError
    {
        public int LineNumber { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
