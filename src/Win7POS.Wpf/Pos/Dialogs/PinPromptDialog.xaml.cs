using System.Windows;
using System.Windows.Input;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PinPromptDialog : Window
    {
        public PinPromptViewModel ViewModel { get; }

        public PinPromptDialog(string prompt)
        {
            InitializeComponent();
            ViewModel = new PinPromptViewModel(prompt);
            DataContext = ViewModel;
            Loaded += OnLoaded;
        }

        public string Pin => ViewModel.Pin ?? string.Empty;

        public void SetError(string message)
        {
            ViewModel.ErrorMessage = message ?? string.Empty;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Keyboard.Focus(PinBox);
        }

        private void OnPinChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Pin = PinBox.Password;
            if (ViewModel.ErrorMessage.Length > 0)
                ViewModel.ErrorMessage = string.Empty;
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (ViewModel.IsValid)
                    DialogResult = true;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
        }

        private void OnOkClicked(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsValid) return;
            DialogResult = true;
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
