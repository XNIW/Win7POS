using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Win7POS.Core.Receipt;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.ViewModels;

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
            SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsBusy);
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

        public ICommand SaveCommand { get; }
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
                Status = "Dati caricati.";
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

        private async Task SaveAsync()
        {
            IsBusy = true;
            try
            {
                await _service.SaveShopInfoAsync(new ReceiptShopInfo
                {
                    Name = Name,
                    Address = Address,
                    City = City,
                    Rut = Rut,
                    Phone = Phone,
                    Footer = Footer
                }).ConfigureAwait(true);
                var boleta = int.TryParse(FiscalBoletaNumberText?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                await _service.SetFiscalBoletaNumberAsync(boleta).ConfigureAwait(true);
                Status = "Salvato.";
            }
            catch (Exception ex)
            {
                Status = "Errore salvataggio: " + ex.Message;
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
