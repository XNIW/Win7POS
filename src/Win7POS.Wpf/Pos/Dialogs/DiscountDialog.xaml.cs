using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Import;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class DiscountDialog : DialogShellWindow
    {
        public DiscountViewModel ViewModel { get; }

        public DiscountDialog(string selectedLineBarcode, bool hasCartItems, PosWorkflowService service, PosViewModel posViewModel, int maxDiscountPercent, Func<System.Threading.Tasks.Task<bool>> overrideLimitCheck, DiscountPreviewContext previewContext = null)
        {
            InitializeComponent();
            ViewModel = new DiscountViewModel(selectedLineBarcode, hasCartItems, OnApplyAsync, previewContext);
            ViewModel.RequestClose += ok => DialogResult = ok;
            DataContext = ViewModel;
            _service = service;
            _posViewModel = posViewModel;
            _maxDiscountPercent = Math.Max(0, maxDiscountPercent);
            _overrideLimitCheck = overrideLimitCheck;
            _previewContext = previewContext;
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
        private readonly int _maxDiscountPercent;
        private readonly Func<System.Threading.Tasks.Task<bool>> _overrideLimitCheck;
        private readonly DiscountPreviewContext _previewContext;

        private async System.Threading.Tasks.Task<bool> OnApplyAsync(int percentValue, long finalPriceMinor, bool isPercent, string lineBarcodeOrNull)
        {
            if (_service == null) return false;
            if (!await EnsureDiscountWithinLimitAsync(percentValue, finalPriceMinor, isPercent).ConfigureAwait(true))
                return false;

            PosWorkflowSnapshot snapshot = null;
            if (isPercent && string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyCartDiscountPercentAsync(percentValue).ConfigureAwait(true);
            else if (isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountPercentAsync(lineBarcodeOrNull, percentValue).ConfigureAwait(true);
            else if (!isPercent && !string.IsNullOrEmpty(lineBarcodeOrNull))
                snapshot = await _service.ApplyLineDiscountByFinalPriceAsync(lineBarcodeOrNull, finalPriceMinor).ConfigureAwait(true);

            _posViewModel?.ApplyDiscountSnapshot(snapshot);
            return snapshot != null;
        }

        private async System.Threading.Tasks.Task<bool> EnsureDiscountWithinLimitAsync(int percentValue, long finalPriceMinor, bool isPercent)
        {
            var exceedsLimit = false;
            if (isPercent)
            {
                exceedsLimit = percentValue > _maxDiscountPercent;
            }
            else if (_previewContext != null && _previewContext.OriginalUnitPrice > 0)
            {
                var discountMinor = _previewContext.OriginalUnitPrice - finalPriceMinor;
                exceedsLimit = discountMinor > 0 && (discountMinor * 100L) > ((long)_maxDiscountPercent * _previewContext.OriginalUnitPrice);
            }

            if (!exceedsLimit)
                return true;

            if (_overrideLimitCheck == null)
            {
                ModernMessageDialog.Show(this, "Sconto", "Sconto oltre il limite consentito per l'operatore corrente.");
                return false;
            }

            return await _overrideLimitCheck().ConfigureAwait(true);
        }
    }
}
