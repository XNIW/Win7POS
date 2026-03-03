using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Win7POS.Core.Util;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class AddProductViewModel : INotifyPropertyChanged
    {
        private string _barcode;
        private string _productName = string.Empty;
        private string _priceText = "0";
        private string _purchasePriceText = "0";
        private string _stockText = "0";
        private SupplierListItem _selectedSupplier;
        private CategoryListItem _selectedCategory;

        public ObservableCollection<SupplierListItem> Suppliers { get; } = new ObservableCollection<SupplierListItem>();
        public ObservableCollection<CategoryListItem> Categories { get; } = new ObservableCollection<CategoryListItem>();

        public AddProductViewModel(string barcode)
        {
            Barcode = (barcode ?? string.Empty).Trim();
            ConfirmCommand = new RelayCommand(_ => Confirm(), _ => IsValid);
            CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => true);
        }

        private void Confirm()
        {
            if (string.IsNullOrWhiteSpace(ProductName))
            {
                ProductName = "Prodotto senza codice";
            }
            RequestClose?.Invoke(true);
        }

        public void SetSuppliers(System.Collections.Generic.IReadOnlyList<SupplierListItem> items)
        {
            Suppliers.Clear();
            Suppliers.Add(new SupplierListItem { Id = 0, Name = "(Nessuno)" });
            foreach (var x in items ?? Enumerable.Empty<SupplierListItem>())
                Suppliers.Add(x);
            SelectedSupplier = Suppliers.FirstOrDefault();
        }

        public void SetCategories(System.Collections.Generic.IReadOnlyList<CategoryListItem> items)
        {
            Categories.Clear();
            Categories.Add(new CategoryListItem { Id = 0, Name = "(Nessuna)" });
            foreach (var x in items ?? Enumerable.Empty<CategoryListItem>())
                Categories.Add(x);
            SelectedCategory = Categories.FirstOrDefault();
        }

        public string Barcode
        {
            get => _barcode;
            set
            {
                _barcode = (value ?? string.Empty).Trim();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string ProductName
        {
            get => _productName;
            set
            {
                _productName = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string PriceText
        {
            get => _priceText;
            set
            {
                _priceText = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsValid));
                RaiseCanExecuteChanged();
            }
        }

        public string PurchasePriceText
        {
            get => _purchasePriceText;
            set { _purchasePriceText = value ?? string.Empty; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public string StockText
        {
            get => _stockText;
            set { _stockText = value ?? string.Empty; OnPropertyChanged(); RaiseCanExecuteChanged(); }
        }

        public SupplierListItem SelectedSupplier
        {
            get => _selectedSupplier;
            set { _selectedSupplier = value; OnPropertyChanged(); }
        }

        public CategoryListItem SelectedCategory
        {
            get => _selectedCategory;
            set { _selectedCategory = value; OnPropertyChanged(); }
        }

        public int PriceMinor => MoneyClp.Parse(PriceText);
        public int PurchasePriceMinor => MoneyClp.Parse(PurchasePriceText);
        public int StockQty
        {
            get
            {
                if (int.TryParse(StockText?.Trim() ?? "0", out var n) && n >= 0) return n;
                return 0;
            }
        }

        public bool IsValid => Barcode.Length > 0 && PriceMinor > 0;

        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<bool> RequestClose;
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaiseCanExecuteChanged()
        {
            (ConfirmCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object> _execute;
            private readonly Func<object, bool> _canExecute;

            public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

            public void Execute(object parameter) => _execute(parameter);

            public event EventHandler CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
