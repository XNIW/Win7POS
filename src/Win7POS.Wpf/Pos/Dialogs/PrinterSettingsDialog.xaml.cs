using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win7POS.Core.Hardware;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PrinterSettingsDialog : DialogShellWindow
    {
        public PrinterSettingsViewModel ViewModel { get; }
        private readonly Stopwatch _scannerTiming = new Stopwatch();
        private bool _scannerComposing;

        public PrinterSettingsDialog(PrinterSettingsViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            ViewModel.RequestClose += OnRequestClose;
            Closed += OnDialogClosed;
            DataContext = ViewModel;
            ViewModel.FocusScannerTestRequested += OnFocusScannerTest;
            TextCompositionManager.AddPreviewTextInputStartHandler(ScannerTestInput, (sender, args) => _scannerComposing = true);
            TextCompositionManager.AddPreviewTextInputHandler(ScannerTestInput, (sender, args) => _scannerComposing = false);
            ScannerTestInput.LostKeyboardFocus += (sender, args) => _scannerComposing = false;
        }

        private void OnFocusScannerTest()
        {
            ScannerTestInput.Clear();
            _scannerTiming.Reset();
            if (ViewModel.ScannerTestArmed) ScannerTestInput.Focus();
            else ArmScannerTestButton.Focus();
        }

        private void OnScannerTestTextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel?.ScannerTestArmed == true && !_scannerTiming.IsRunning && ScannerTestInput.Text.Length > 0)
                _scannerTiming.Start();
        }

        private void OnScannerTestPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape)) _scannerComposing = false;
            if (_scannerComposing || e.Key == Key.ImeProcessed || e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.Enter && key != Key.Tab) return;
            // Both terminators stay inside this isolated surface, even while disarmed.
            e.Handled = true;
            var terminator = key == Key.Enter ? ScannerTerminator.Enter : ScannerTerminator.Tab;
            if (!ScannerInputPolicy.Accepts(ViewModel.ScannerSettings, terminator)) return;
            ViewModel.CompleteScannerTest(ScannerTestInput.Text, terminator, _scannerTiming.ElapsedMilliseconds);
            ScannerTestInput.Clear();
            _scannerTiming.Reset();
            if (ViewModel.ScannerTestArmed) ScannerTestInput.Focus();
            else ArmScannerTestButton.Focus();
        }

        private void OnRequestClose(bool ok)
        {
            DialogResult = ok;
        }

        private void OnDialogClosed(object sender, EventArgs e)
        {
            Closed -= OnDialogClosed;
            ViewModel.RequestClose -= OnRequestClose;
            ViewModel.FocusScannerTestRequested -= OnFocusScannerTest;
            ScannerTestInput.Clear();
            DataContext = null;
            ViewModel.Dispose();
        }
    }
}
