using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class ShopSettingsViewModel : INotifyPropertyChanged
    {
        private readonly PosWorkflowService _service;
        private string _name = "";
        private string _address = "";
        private string _city = "";
        private string _rut = "";
        private string _phone = "";
        private string _footer = "";
        private string _fiscalBoletaNumberText = "0";
        private string _status = "";
        private bool _isBusy;

        public ShopSettingsViewModel(PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _ = LoadAsync();
        }

        public string Name { get => _name; set { _name = value ?? ""; OnPropertyChanged(); } }
        public string Address { get => _address; set { _address = value ?? ""; OnPropertyChanged(); } }
        public string City { get => _city; set { _city = value ?? ""; OnPropertyChanged(); } }
        public string Rut { get => _rut; set { _rut = value ?? ""; OnPropertyChanged(); } }
        public string Phone { get => _phone; set { _phone = value ?? ""; OnPropertyChanged(); } }
        public string Footer { get => _footer; set { _footer = value ?? ""; OnPropertyChanged(); } }
        public string FiscalBoletaNumberText { get => _fiscalBoletaNumberText; set { _fiscalBoletaNumberText = value ?? ""; OnPropertyChanged(); } }
        public string Status { get => _status; set { _status = value ?? ""; OnPropertyChanged(); } }
        public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }
        public bool IsReadOnly => true;

        public event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
                Name = shop.Name ?? "";
                Address = shop.Address ?? "";
                City = shop.City ?? "";
                Rut = shop.Rut ?? "";
                Phone = shop.Phone ?? "";
                Footer = shop.Footer ?? "";
                FiscalBoletaNumberText = (await _service.GetFiscalBoletaNumberAsync().ConfigureAwait(true)).ToString(CultureInfo.InvariantCulture);
                Status = "Dati ufficiali caricati. Modifica disponibile solo in Admin Web.";
            }
            catch (Exception ex)
            {
                Status = "Errore: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
