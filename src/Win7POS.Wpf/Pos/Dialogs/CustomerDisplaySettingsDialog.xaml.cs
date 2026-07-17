using System;
using System.Windows;
using Win7POS.Core.Pos;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
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

        public CustomerDisplaySettingsDialog(
            CustomerDisplaySettingsViewModel viewModel,
            Action identify,
            Action preview,
            Action open,
            Action close)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _identify = identify;
            _preview = preview;
            _open = open;
            _close = close;
            InitializeComponent();
            DataContext = _viewModel;
        }

        public CustomerDisplaySettings Result { get; private set; }

        private void OnIdentifyClick(object sender, RoutedEventArgs e) => RunAction(_identify);
        private void OnPreviewClick(object sender, RoutedEventArgs e) => RunAction(_preview);
        private void OnOpenClick(object sender, RoutedEventArgs e) => RunAction(_open);
        private void OnCloseDisplayClick(object sender, RoutedEventArgs e) => RunAction(_close);
        private void OnInvertClick(object sender, RoutedEventArgs e) => _viewModel.InvertMonitors();
        private void OnResetAutomaticClick(object sender, RoutedEventArgs e) => _viewModel.ResetAutomatic();

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
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
