using System.Linq;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Import;

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
            if (string.IsNullOrWhiteSpace(Username)) { ModernMessageDialog.Show(this, "Nuovo utente", "Inserire username."); return; }
            if (string.IsNullOrWhiteSpace(DisplayName)) { ModernMessageDialog.Show(this, "Nuovo utente", "Inserire nome visualizzato."); return; }
            if (Pin.Length < 4 || Pin.Length > 6 || !Pin.All(char.IsDigit)) { ModernMessageDialog.Show(this, "Nuovo utente", "Il PIN deve essere di 4-6 cifre numeriche."); return; }
            if (RoleCombo.SelectedItem is UserRole r)
                RoleId = r.Id;
            else
            { ModernMessageDialog.Show(this, "Nuovo utente", "Selezionare un ruolo."); return; }
            DialogResult = true;
            Close();
        }
    }
}
