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
        private string _retailPrice = string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        public int RowNumber { get; set; }
        public bool Exists { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string SecondProductName { get; set; } = string.Empty;
        public string PurchasePrice { get; set; } = string.Empty;
        public string RetailPrice
        {
            get { return _retailPrice; }
            set
            {
                var next = value ?? string.Empty;
                if (_retailPrice == next) return;
                _retailPrice = next;
                OnPropertyChanged();
            }
        }
        public string Quantity { get; set; } = string.Empty;
        public string Supplier { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool HasItemNumberSource { get; set; }
        public bool HasProductNameSource { get; set; }
        public bool HasSecondProductNameSource { get; set; }
        public bool HasPurchasePriceSource { get; set; }
        public bool HasRetailPriceSource { get; set; }
        public bool HasQuantitySource { get; set; }
        public bool HasSupplierSource { get; set; }
        public bool HasCategorySource { get; set; }
        public bool RetailPriceMissingButPurchasePresent { get; set; }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
