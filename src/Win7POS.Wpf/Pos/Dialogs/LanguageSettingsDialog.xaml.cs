using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class LanguageSettingsDialog : DialogShellWindow
    {
        public LanguageSettingsDialog()
        {
            InitializeComponent();
            LanguageComboBox.ItemsSource = PosLocalization.SupportedLanguages;
            LanguageComboBox.SelectedValue = PosLocalization.Current.CurrentLanguage;
        }

        public string SelectedLanguageCode { get; private set; }

        public static string ShowDialog(Window ownerWindow)
        {
            var dialog = new LanguageSettingsDialog
            {
                Owner = ownerWindow ?? DialogOwnerHelper.GetSafeOwner()
            };

            return dialog.ShowDialog() == true
                ? dialog.SelectedLanguageCode
                : null;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            SelectedLanguageCode = LanguageComboBox.SelectedValue as string;
            DialogResult = true;
            Close();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
