using System;

namespace Win7POS.Data.Import
{
    /// <summary>
    /// Bounds supplier workbooks before their retained worksheet can exhaust the x86 process.
    /// The 50,000-row, 12-column performance fixture remains below every worksheet bound.
    /// </summary>
    public static class SupplierExcelImportLimits
    {
        public const long MaximumInputFileBytes = 32L * 1024L * 1024L;
        public const int MaximumWorksheetRows = 60000;
        public const int MaximumWorksheetColumns = 256;
        public const long MaximumWorksheetCells = 1000000L;
        public const int MaximumCellCharacters = 32767;
        public const long MaximumAggregateRetainedCharacters = 16000000L;
        public const long MaximumOoxmlMetadataXmlBytes = 4L * 1024L * 1024L;
        public const long MaximumFirstWorksheetXmlBytes = 256L * 1024L * 1024L;
        public const int MaximumHtmlTableCandidates = 4096;
        public const int CancellationCheckRowInterval = 64;
    }

    public static class SupplierExcelImportErrorCodes
    {
        public const string FileTooLarge = "supplier_excel_file_too_large";
        public const string RowLimitExceeded = "supplier_excel_row_limit_exceeded";
        public const string ColumnLimitExceeded = "supplier_excel_column_limit_exceeded";
        public const string CellLimitExceeded = "supplier_excel_cell_limit_exceeded";
        public const string CellTextTooLarge = "supplier_excel_cell_text_too_large";
        public const string Cancelled = "supplier_excel_cancelled";
        public const string CorruptOrUnsupported = "supplier_excel_corrupt_or_unsupported";
    }

    public sealed class SupplierExcelImportException : Exception
    {
        public SupplierExcelImportException(string code, string message)
            : base(message)
        {
            Code = code ?? string.Empty;
        }

        public SupplierExcelImportException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code ?? string.Empty;
        }

        public string Code { get; }
    }
}
