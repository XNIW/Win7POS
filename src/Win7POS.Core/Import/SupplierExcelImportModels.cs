using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Win7POS.Core.Import
{
    public static class AndroidImportKeys
    {
        public const string Barcode = "barcode";
        public const string ProductName = "productName";
        public const string ItemNumber = "itemNumber";
        public const string PurchasePrice = "purchasePrice";
        public const string RetailPrice = "retailPrice";
        public const string Quantity = "quantity";
        public const string Supplier = "supplier";
        public const string Category = "category";
        public const string SecondProductName = "secondProductName";
        public const string TotalPrice = "totalPrice";
        public const string RowNumber = "rowNumber";
        public const string Discount = "discount";
        public const string DiscountedPrice = "discountedPrice";
        public const string OldPurchasePrice = "oldPurchasePrice";
        public const string OldRetailPrice = "oldRetailPrice";
        public const string RealQuantity = "realQuantity";
        public const string Complete = "complete";

        public static readonly string[] RequiredKeys =
        {
            Barcode,
            ProductName,
            PurchasePrice
        };

        public static readonly string[] AllKeys =
        {
            Barcode,
            ProductName,
            ItemNumber,
            PurchasePrice,
            RetailPrice,
            Quantity,
            Supplier,
            Category,
            SecondProductName,
            TotalPrice,
            RowNumber,
            Discount,
            DiscountedPrice,
            OldPurchasePrice,
            OldRetailPrice,
            RealQuantity,
            Complete
        };
    }

    public sealed class SupplierExcelRawTable
    {
        public string SheetName { get; set; } = string.Empty;
        public bool HasHeader { get; set; }
        public int DataRowIndex { get; set; }
        public int DroppedSummaryRows { get; set; }
        public List<SupplierExcelColumn> Columns { get; } = new List<SupplierExcelColumn>();
        public List<SupplierExcelRow> Rows { get; } = new List<SupplierExcelRow>();
    }

    public sealed class SupplierExcelColumn : INotifyPropertyChanged
    {
        private string _canonicalKey = string.Empty;
        private bool _isEnabled;

        public event PropertyChangedEventHandler PropertyChanged;

        public int ColumnIndex { get; set; }
        public string OriginalHeader { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string CanonicalKey
        {
            get { return _canonicalKey; }
            set
            {
                var next = value ?? string.Empty;
                if (_canonicalKey == next) return;
                _canonicalKey = next;
                OnPropertyChanged();
            }
        }
        public string HeaderSource { get; set; } = "unknown";
        public string Confidence { get; set; } = "low";
        public string SampleValues { get; set; } = string.Empty;
        public bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
        public bool IsGenerated { get; set; }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class SupplierExcelRow
    {
        public int RowNumber { get; set; }
        public List<string> Values { get; } = new List<string>();
    }

    public sealed class SupplierImportAnalysis
    {
        public string SheetName { get; set; } = string.Empty;
        public List<SupplierExcelColumn> Columns { get; } = new List<SupplierExcelColumn>();
        public List<SupplierImportEditableRow> EditableRows { get; } = new List<SupplierImportEditableRow>();
        public List<SupplierImportProductRow> NewProducts { get; } = new List<SupplierImportProductRow>();
        public List<SupplierProductUpdate> UpdatedProducts { get; } = new List<SupplierProductUpdate>();
        public List<SupplierImportWarning> Warnings { get; } = new List<SupplierImportWarning>();
        public List<SupplierImportError> Errors { get; } = new List<SupplierImportError>();
        public int SourceRowCount { get; set; }
        public int DroppedSummaryRows { get; set; }
        public bool HasHeader { get; set; }
        public int DataRowIndex { get; set; }
        public int HeaderRowNumber { get; set; }
        public int SkippedMetadataRows { get; set; }
        public bool CanApply { get { return Errors.Count == 0; } }
    }

    public sealed class SupplierImportWarning
    {
        public SupplierImportWarning()
        {
        }

        public SupplierImportWarning(string message, IReadOnlyList<int> rows)
        {
            Message = message ?? string.Empty;
            Rows = rows ?? Array.Empty<int>();
        }

        public string Message { get; set; } = string.Empty;
        public IReadOnlyList<int> Rows { get; set; } = Array.Empty<int>();
    }

    public sealed class SupplierImportError
    {
        public SupplierImportError()
        {
        }

        public SupplierImportError(string message, int rowIndex, string barcode)
        {
            Message = message ?? string.Empty;
            RowIndex = rowIndex;
            Barcode = barcode ?? string.Empty;
        }

        public string Message { get; set; } = string.Empty;
        public int RowIndex { get; set; }
        public string Barcode { get; set; } = string.Empty;
    }

    public sealed class SupplierProductUpdate
    {
        public SupplierImportProductRow Existing { get; set; }
        public SupplierImportProductRow Updated { get; set; }
    }

    public sealed class SupplierImportSyncPreview
    {
        public SupplierImportSyncSummary Summary { get; set; } = new SupplierImportSyncSummary();
        public List<SupplierImportProductRow> NewProducts { get; } = new List<SupplierImportProductRow>();
        public List<SupplierImportSyncRow> UpdatedProducts { get; } = new List<SupplierImportSyncRow>();
        public List<SupplierImportSyncRow> NoChangeRows { get; } = new List<SupplierImportSyncRow>();
        public List<SupplierImportSyncSkippedRow> SkippedRows { get; } = new List<SupplierImportSyncSkippedRow>();
        public List<SupplierImportWarning> Warnings { get; } = new List<SupplierImportWarning>();
        public List<SupplierImportError> Errors { get; } = new List<SupplierImportError>();
        public List<SupplierImportEditableRow> FinalRows { get; } = new List<SupplierImportEditableRow>();
        public List<SupplierImportEditableRow> ValidatedRows { get; } = new List<SupplierImportEditableRow>();
        public string Fingerprint { get; set; } = string.Empty;
        public bool CanApply
        {
            get { return Errors.Count == 0 && Summary.NonSkippedRows > 0; }
        }
    }

    public sealed class SupplierImportSyncSummary
    {
        public int TotalRows { get; set; }
        public int NonSkippedRows { get; set; }
        public int NewProducts { get; set; }
        public int UpdatedProducts { get; set; }
        public int NoChangeRows { get; set; }
        public int SkippedRows { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
    }

    public sealed class SupplierImportSyncRow
    {
        public int RowNumber { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public SupplierImportProductRow Existing { get; set; }
        public SupplierImportProductRow Updated { get; set; }
        public List<SupplierImportSyncUpdateDiff> Diffs { get; } = new List<SupplierImportSyncUpdateDiff>();
        public string DiffSummary
        {
            get
            {
                if (Diffs.Count == 0) return string.Empty;
                var parts = new List<string>();
                foreach (var diff in Diffs)
                    parts.Add(diff.Field + ": " + diff.Before + " -> " + diff.After);
                return string.Join("; ", parts);
            }
        }
    }

    public sealed class SupplierImportSyncSkippedRow
    {
        public int RowNumber { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
    }

    public sealed class SupplierImportSyncUpdateDiff
    {
        public string Field { get; set; } = string.Empty;
        public string Before { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
    }

    public sealed class SupplierImportProductRow
    {
        public int RowNumber { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string SecondProductName { get; set; } = string.Empty;
        public string PurchasePrice { get; set; } = string.Empty;
        public string RetailPrice { get; set; } = string.Empty;
        public string Quantity { get; set; } = string.Empty;
        public string Supplier { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }

    public sealed class SupplierImportEditableRow : INotifyPropertyChanged
    {
        private bool _isSkipped;
        private string _barcode = string.Empty;
        private string _itemNumber = string.Empty;
        private string _productName = string.Empty;
        private string _secondProductName = string.Empty;
        private string _purchasePrice = string.Empty;
        private string _retailPrice = string.Empty;
        private string _quantity = string.Empty;
        private string _supplier = string.Empty;
        private string _category = string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        public int RowNumber { get; set; }
        public bool Exists { get; set; }
        public bool IsSkipped
        {
            get { return _isSkipped; }
            set
            {
                if (_isSkipped == value) return;
                _isSkipped = value;
                OnPropertyChanged();
            }
        }
        public string Barcode
        {
            get { return _barcode; }
            set { SetString(ref _barcode, value); }
        }
        public string ItemNumber
        {
            get { return _itemNumber; }
            set { SetString(ref _itemNumber, value); }
        }
        public string ProductName
        {
            get { return _productName; }
            set { SetString(ref _productName, value); }
        }
        public string SecondProductName
        {
            get { return _secondProductName; }
            set { SetString(ref _secondProductName, value); }
        }
        public string PurchasePrice
        {
            get { return _purchasePrice; }
            set { SetString(ref _purchasePrice, value); }
        }
        public string RetailPrice
        {
            get { return _retailPrice; }
            set { SetString(ref _retailPrice, value); }
        }
        public string Quantity
        {
            get { return _quantity; }
            set { SetString(ref _quantity, value); }
        }
        public string Supplier
        {
            get { return _supplier; }
            set { SetString(ref _supplier, value); }
        }
        public string Category
        {
            get { return _category; }
            set { SetString(ref _category, value); }
        }
        public bool HasItemNumberSource { get; set; }
        public bool HasProductNameSource { get; set; }
        public bool HasSecondProductNameSource { get; set; }
        public bool HasPurchasePriceSource { get; set; }
        public bool HasRetailPriceSource { get; set; }
        public bool HasQuantitySource { get; set; }
        public bool HasSupplierSource { get; set; }
        public bool HasCategorySource { get; set; }
        public bool RetailPriceMissingButPurchasePresent { get; set; }

        private void SetString(ref string field, string value, [CallerMemberName] string name = null)
        {
            var next = value ?? string.Empty;
            if (field == next) return;
            field = next;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
