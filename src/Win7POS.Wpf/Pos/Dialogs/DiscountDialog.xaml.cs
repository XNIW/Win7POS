using System;
using System.Windows;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DiscountDialog : Window
    {
        public DiscountViewModel ViewModel { get; }

        public DiscountDialog(string selectedLineBarcode, bool hasCartItems, PosWorkflowService service, PosViewModel posViewModel)
        {
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 520, minHeight: 380, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            ViewModel = new DiscountViewModel(selectedLineBarcode, hasCartItems, OnApplyAsync);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            _service = service;
            _posViewModel = posViewModel;
        }

        private readonly PosWorkflowService _service;
        private readonly PosViewModel _posViewModel;

        private async System.Threading.Tasks.Task OnApplyAsync(int value, bool isPercent, string lineBarcodeOrNull)
        {
            if (_service == null) return;

            PosWorkflowSnapshot snapshot = null;
            if (isPercent && string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyCartDiscountPercentAsync(value).ConfigureAwait(true);
            else if (isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountPercentAsync(lineBarcodeOrNull, value).ConfigureAwait(true);
            else if (!isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountAmountAsync(lineBarcodeOrNull, value).ConfigureAwait(true);

            _posViewModel?.ApplyDiscountSnapshot(snapshot);
        }
    }
}
