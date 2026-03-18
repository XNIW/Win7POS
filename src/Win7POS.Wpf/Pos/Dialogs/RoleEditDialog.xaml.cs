using System;
using System.Windows;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class RoleEditDialog : DialogShellWindow
    {
        private readonly bool _codeReadOnly;
        private bool _submitted;

        public string RoleCode => (CodeBox?.Text ?? "").Trim();
        public string RoleName => (NameBox?.Text ?? "").Trim();
        /// <summary>Validazione esterna opzionale sul codice. Ritorna messaggio errore o null se OK.</summary>
        public Func<string, string> ValidateCode { get; set; }

        public RoleEditDialog(string title, string initialCode = "", string initialName = "", bool codeReadOnly = false)
        {
            InitializeComponent();
            _codeReadOnly = codeReadOnly;
            Title = title ?? "Ruolo";
            TitleText.Text = Title;
            CodeBox.Text = initialCode ?? "";
            NameBox.Text = initialName ?? "";
            if (codeReadOnly && CodeRow != null) CodeRow.Visibility = Visibility.Collapsed;
            Loaded += OnLoaded;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (_submitted) return;

            if (string.IsNullOrWhiteSpace(RoleName))
            {
                ModernMessageDialog.Show(this, Title ?? "Ruolo", "Inserire il nome del ruolo.");
                NameBox.Focus();
                NameBox.SelectAll();
                return;
            }
            if (!_codeReadOnly && string.IsNullOrWhiteSpace(RoleCode))
            {
                ModernMessageDialog.Show(this, Title ?? "Ruolo", "Inserire il codice del ruolo (es. mio_ruolo).");
                CodeBox.Focus();
                CodeBox.SelectAll();
                return;
            }
            if (!_codeReadOnly && ValidateCode != null)
            {
                var error = ValidateCode(RoleCode);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    ModernMessageDialog.Show(this, Title ?? "Ruolo", error);
                    CodeBox.Focus();
                    CodeBox.SelectAll();
                    return;
                }
            }

            _submitted = true;
            DialogResult = true;
            Close();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_codeReadOnly)
            {
                NameBox.Focus();
                NameBox.SelectAll();
                return;
            }

            CodeBox.Focus();
            if (!string.IsNullOrEmpty(CodeBox.Text))
                CodeBox.SelectAll();
        }
    }
}
