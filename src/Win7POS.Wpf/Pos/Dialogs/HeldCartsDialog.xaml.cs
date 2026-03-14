using System;
using System.Windows;
using System.Windows.Input;
using Win7POS.Wpf.Chrome;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class HeldCartsDialog : DialogShellWindow
    {
        public HeldCartsViewModel ViewModel { get; }

        public HeldCartsDialog(HeldCartsViewModel vm)
        {
            InitializeComponent();
            ViewModel = vm ?? throw new System.ArgumentNullException(nameof(vm));
            DataContext = ViewModel;
            ViewModel.RequestClose += recovered =>
            {
                DialogResult = recovered;
                Close();
            };
            Loaded += OnLoaded;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            CenterToOwner();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
                e.Handled = true;
            }
        }

        private void CenterToOwner()
        {
            if (Owner == null) return;
            Left = Owner.Left + Math.Max(0, (Owner.ActualWidth - ActualWidth) / 2);
            Top = Owner.Top + Math.Max(0, (Owner.ActualHeight - ActualHeight) / 2);
        }
    }
}
