using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;

namespace Win7POS.Wpf.Pos
{
    public partial class UserManagementView : UserControl
    {
        public UserManagementView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is Dialogs.UserManagementViewModel vm && vm.PermissionItems != null)
            {
                var cvs = new CollectionViewSource { Source = vm.PermissionItems };
                cvs.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
                PermissionItemsControl.ItemsSource = cvs.View;
            }
        }

        private void NewPinBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is Dialogs.UserManagementViewModel vm && sender is PasswordBox pb)
                vm.NewPin = pb.Password;
        }
    }
}
