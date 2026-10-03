using System;
using System.Windows;
using Microsoft.Win32;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class CustomerDisplaySettingsDialog : DialogShellWindow
    {
        private readonly CustomerDisplaySettingsViewModel _viewModel;
        private readonly Action _identify;
        private readonly Action _preview;
        private readonly Action _open;
        private readonly Action _close;
        private readonly Action<CustomerDisplaySettings> _previewSettings;
        private readonly Action<CustomerDisplaySettings> _testPattern;
        private readonly Action _stopPreview;
        private bool _closed;

        public CustomerDisplaySettingsDialog(
            CustomerDisplaySettingsViewModel viewModel,
            Action identify,
            Action preview,
            Action open,
            Action close, Action<CustomerDisplaySettings> previewSettings = null,
            Action<CustomerDisplaySettings> testPattern = null, Action stopPreview = null)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _identify = identify;
            _preview = preview;
            _open = open;
            _close = close;
            _previewSettings = previewSettings;
            _testPattern = testPattern;
            _stopPreview = stopPreview;
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(
                this,
                minWidth: 700,
                minHeight: 540,
                maxWidthPercent: 0.92,
                maxHeightPercent: 0.92,
                allowResize: true);
            DataContext = _viewModel;
            Closed += OnDialogClosed;
        }

        public CustomerDisplaySettings Result { get; private set; }

        private void OnIdentifyClick(object sender, RoutedEventArgs e) => RunAction(_identify);
        private void OnPreviewClick(object sender, RoutedEventArgs e) => RunSettingsAction(_previewSettings, _preview);
        private void OnTestPatternClick(object sender, RoutedEventArgs e) => RunSettingsAction(_testPattern, null);
        private void OnStopPreviewClick(object sender, RoutedEventArgs e) => RunAction(_stopPreview);
        private void OnRemoveLogoClick(object sender, RoutedEventArgs e) => _viewModel.RemoveLogo();
        private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e) => RunAction(() => Clipboard.SetText(_viewModel.DiagnosticsText));

        private async void OnImportLogoClick(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog { Filter = "PNG / JPEG / BMP|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            try { await _viewModel.ImportLogoAsync(picker.FileName).ConfigureAwait(true); }
            catch { if (!_closed) ModernMessageDialog.Show(this, PosLocalization.Current.Text("customerDisplay.settings.title"), PosLocalization.Current.Text("customerDisplay.polish.logoError")); }
        }

        private void RunSettingsAction(Action<CustomerDisplaySettings> action, Action legacy)
        {
            if (action == null) { RunAction(legacy); return; }
            if (!_viewModel.TryBuild(out var settings, out var error))
            {
                ModernMessageDialog.Show(this, PosLocalization.Current.Text("customerDisplay.settings.title"), PosLocalization.Current.Text("customerDisplay.error." + error));
                return;
            }
            RunAction(() => action(settings));
        }

        private void OnDialogClosed(object sender, EventArgs e)
        {
            Closed -= OnDialogClosed;
            _closed = true;
            _stopPreview?.Invoke();
        }
        private void OnOpenClick(object sender, RoutedEventArgs e) => RunAction(_open);
        private void OnCloseDisplayClick(object sender, RoutedEventArgs e) => RunAction(_close);
        private void OnInvertClick(object sender, RoutedEventArgs e) => _viewModel.InvertMonitors();
        private void OnResetAutomaticClick(object sender, RoutedEventArgs e) => _viewModel.ResetAutomatic();

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.CanSave) return;
            if (!_viewModel.TryBuild(out var settings, out var errorCode))
            {
                ModernMessageDialog.Show(
                    this,
                    PosLocalization.Current.Text("customerDisplay.settings.title"),
                    PosLocalization.Current.Text("customerDisplay.error." + errorCode));
                return;
            }
            Result = settings;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void RunAction(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception)
            {
                ModernMessageDialog.Show(
                    this,
                    PosLocalization.Current.Text("customerDisplay.settings.title"),
                    PosLocalization.Current.Text("customerDisplay.error.actionFailed"));
            }
        }
    }
}
