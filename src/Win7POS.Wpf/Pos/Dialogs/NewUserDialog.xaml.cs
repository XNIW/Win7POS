using System.Linq;
using System.Windows;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class NewUserDialog : Window
    {
        public string Username => (UsernameBox?.Text ?? "").Trim();
        public string DisplayName => (DisplayNameBox?.Text ?? "").Trim();
        public string Pin => PinBox?.Password ?? "";
        public int RoleId { get; private set; }

        public NewUserDialog()
        {
            InitializeComponent();
            var options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(options);
            var roleRepo = new RoleRepository(factory);
            var roles = roleRepo.GetAllAsync().GetAwaiter().GetResult();
            RoleCombo.ItemsSource = roles;
            RoleCombo.SelectedIndex = roles.Count > 0 ? 0 : -1;
        }

        private void OnCreateClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Username)) { MessageBox.Show("Inserire username.", "Nuovo utente", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (string.IsNullOrWhiteSpace(DisplayName)) { MessageBox.Show("Inserire nome visualizzato.", "Nuovo utente", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (Pin.Length < 4 || Pin.Length > 6) { MessageBox.Show("PIN 4-6 cifre.", "Nuovo utente", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (RoleCombo.SelectedItem is UserRole r)
                RoleId = r.Id;
            else
            { MessageBox.Show("Selezionare un ruolo.", "Nuovo utente", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
            Close();
        }
    }
}
