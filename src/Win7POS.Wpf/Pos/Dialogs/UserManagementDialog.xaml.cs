using System;
using System.Windows;
using System.Windows.Data;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class UserManagementDialog : DialogShellWindow
    {
        private CollectionViewSource _groupedPermissionsView;
        private UserManagementViewModel _viewModel;
        private Action<bool> _requestCloseHandler;

        public UserManagementDialog(UserManagementViewModel vm)
        {
            _viewModel = vm ?? throw new ArgumentNullException(nameof(vm));
            InitializeComponent();
            WindowSizingHelper.ApplyAdaptiveDialogSizing(this, minWidth: 720, minHeight: 480, maxWidthPercent: 0.92, maxHeightPercent: 0.92, allowResize: true);
            vm.OwnerWindow = this;
            DataContext = vm;
            Loaded += OnLoaded;
            Closed += OnClosed;
            _requestCloseHandler = ok =>
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => CloseWithResult(ok));
                    return;
                }
                CloseWithResult(ok);
            };
            vm.RequestClose += _requestCloseHandler;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            Closed -= OnClosed;
            Loaded -= OnLoaded;
            if (_viewModel != null)
            {
                _viewModel.RequestClose -= _requestCloseHandler;
                _viewModel.OwnerWindow = null;
            }

            PermissionItemsControl.ItemsSource = null;
            if (_groupedPermissionsView != null)
                _groupedPermissionsView.Source = null;
            _groupedPermissionsView = null;
            _requestCloseHandler = null;
            _viewModel = null;
            DataContext = null;
            DialogContent = null;
            Content = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is UserManagementViewModel vm && vm.PermissionItems != null)
            {
                _groupedPermissionsView = new CollectionViewSource { Source = vm.PermissionItems };
                _groupedPermissionsView.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
                PermissionItemsControl.ItemsSource = _groupedPermissionsView.View;
            }
        }

        private void NewPinBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is UserManagementViewModel vm && sender is System.Windows.Controls.PasswordBox pb)
                vm.NewPin = pb.Password;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CloseWithResult(bool ok)
        {
            try { DialogResult = ok; }
            catch { Close(); }
        }
    }
}
