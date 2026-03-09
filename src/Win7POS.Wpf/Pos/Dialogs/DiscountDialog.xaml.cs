using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DiscountDialog : Window
    {
        public DiscountViewModel ViewModel { get; }

        public DiscountDialog(string selectedLineBarcode, bool hasCartItems, PosWorkflowService service, PosViewModel posViewModel, DiscountPreviewContext previewContext = null)
        {
            InitializeComponent();
            ViewModel = new DiscountViewModel(selectedLineBarcode, hasCartItems, OnApplyAsync, previewContext);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            _service = service;
            _posViewModel = posViewModel;
            Loaded += DiscountDialog_Loaded;
        }

        private void DiscountDialog_Loaded(object sender, RoutedEventArgs e)
        {
            FocusAndSelectAll(ValueBox);
        }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            FocusAndSelectAll(ValueBox);
        }

        private void FocusAndSelectAll(TextBox tb)
        {
            if (tb == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
                Keyboard.Focus(tb);
            }), DispatcherPriority.Input);
        }

        private readonly PosWorkflowService _service;
        private readonly PosViewModel _posViewModel;

        private async System.Threading.Tasks.Task OnApplyAsync(int percentValue, long finalPriceMinor, bool isPercent, string lineBarcodeOrNull)
        {
            if (_service == null) return;

            PosWorkflowSnapshot snapshot = null;
            if (isPercent && string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyCartDiscountPercentAsync(percentValue).ConfigureAwait(true);
            else if (isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountPercentAsync(lineBarcodeOrNull, percentValue).ConfigureAwait(true);
            else if (!isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountByFinalPriceAsync(lineBarcodeOrNull, finalPriceMinor).ConfigureAwait(true);

            _posViewModel?.ApplyDiscountSnapshot(snapshot);
        }
    }
}
