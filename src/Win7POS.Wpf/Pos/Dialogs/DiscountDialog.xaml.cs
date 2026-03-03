using System;
using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DiscountDialog : Window
    {
        public DiscountViewModel ViewModel { get; }

        public DiscountDialog(string selectedLineBarcode, bool hasCartItems, PosWorkflowService service, PosViewModel posViewModel)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyDialogSizing(this, widthPercent: 0.36, heightPercent: 0.6, minWidth: 360, minHeight: 420);
            ViewModel = new DiscountViewModel(selectedLineBarcode, hasCartItems, OnApply);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            _service = service;
            _posViewModel = posViewModel;
        }

        private readonly PosWorkflowService _service;
        private readonly PosViewModel _posViewModel;

        private void OnApply(int value, bool isPercent, string lineBarcodeOrNull)
        {
            if (_service == null) return;

            PosWorkflowSnapshot snapshot = null;
            if (isPercent && string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = _service.ApplyCartDiscountPercentAsync(value).GetAwaiter().GetResult();
            else if (isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = _service.ApplyLineDiscountPercentAsync(lineBarcodeOrNull, value).GetAwaiter().GetResult();
            else if (!isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = _service.ApplyLineDiscountAmountAsync(lineBarcodeOrNull, value).GetAwaiter().GetResult();

            _posViewModel?.ApplyDiscountSnapshot(snapshot);
        }

        private void Chiudi_Click(object sender, MouseButtonEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
