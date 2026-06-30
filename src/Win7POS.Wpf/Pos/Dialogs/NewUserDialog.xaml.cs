using System.Linq;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class NewUserDialog : DialogShellWindow
    {
        public string Username => (UsernameBox?.Text ?? "").Trim();
        public string DisplayName => (DisplayNameBox?.Text ?? "").Trim();
        public string Pin => PinBox?.Password ?? "";
        public int RoleId { get; private set; }

        public NewUserDialog()
        {
            InitializeComponent();
            Loaded += OnLoadedAsync;
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            var options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(options);
            var roleRepo = new RoleRepository(factory);
            var roles = await roleRepo.GetAllAsync().ConfigureAwait(true);
            RoleCombo.ItemsSource = roles;
            RoleCombo.SelectedIndex = roles.Count > 0 ? 0 : -1;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Username)) { ModernMessageDialog.Show(this, PosLocalization.T("users.newUser"), PosLocalization.T("users.usernameRequired")); return; }
            if (string.IsNullOrWhiteSpace(DisplayName)) { ModernMessageDialog.Show(this, PosLocalization.T("users.newUser"), PosLocalization.T("users.displayNameRequired")); return; }
            if (Pin.Length < 4 || Pin.Length > 6 || !Pin.All(char.IsDigit)) { ModernMessageDialog.Show(this, PosLocalization.T("users.newUser"), PosLocalization.T("pin.invalidDigits")); return; }
            if (RoleCombo.SelectedItem is UserRole r)
                RoleId = r.Id;
            else
            { ModernMessageDialog.Show(this, PosLocalization.T("users.newUser"), PosLocalization.T("users.roleRequired")); return; }
            DialogResult = true;
            Close();
        }
    }
}
