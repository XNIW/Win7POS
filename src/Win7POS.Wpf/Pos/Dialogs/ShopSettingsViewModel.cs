using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class ShopSettingsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly PosWorkflowService _service;
        private string _name = "";
        private string _address = "";
        private string _businessGiro = "";
        private string _city = "";
        private string _rut = "";
        private string _phone = "";
        private string _footer = "";
        private string _fiscalBoletaNumberText = "0";
        private string _catalogSyncText = "";
        private string _catalogCompletenessText = "";
        private string _catalogCountsText = "";
        private string _catalogRepairText = "";
        private string _catalogSyncModeText = "";
        private string _deviceStatusText = "";
        private string _lastOfficialSyncText = "";
        private string _officialSourceText = "";
        private string _pendingSalesText = "";
        private string _salesSyncText = "";
        private string _shopCode = "";
        private string _shopStatusText = "";
        private string _syncStatusText = "";
        private string _status = "";
        private int _activeLoads;
        private bool _disposed;

        public ShopSettingsViewModel(PosWorkflowService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            PosLocalization.Current.LanguageChanged += OnLanguageChanged;
            _ = LoadAsync();
        }

        public string Name { get => _name; set { _name = value ?? ""; OnPropertyChanged(); } }
        public string Address { get => _address; set { _address = value ?? ""; OnPropertyChanged(); } }
        public string BusinessGiro { get => _businessGiro; set { _businessGiro = value ?? ""; OnPropertyChanged(); } }
        public string City { get => _city; set { _city = value ?? ""; OnPropertyChanged(); } }
        public string Rut { get => _rut; set { _rut = value ?? ""; OnPropertyChanged(); } }
        public string Phone { get => _phone; set { _phone = value ?? ""; OnPropertyChanged(); } }
        public string Footer { get => _footer; set { _footer = value ?? ""; OnPropertyChanged(); } }
        public string FiscalBoletaNumberText { get => _fiscalBoletaNumberText; set { _fiscalBoletaNumberText = value ?? ""; OnPropertyChanged(); } }
        public string CatalogCompletenessText { get => _catalogCompletenessText; set { _catalogCompletenessText = value ?? ""; OnPropertyChanged(); } }
        public string CatalogCountsText { get => _catalogCountsText; set { _catalogCountsText = value ?? ""; OnPropertyChanged(); } }
        public string CatalogRepairText { get => _catalogRepairText; set { _catalogRepairText = value ?? ""; OnPropertyChanged(); } }
        public string CatalogSyncText { get => _catalogSyncText; set { _catalogSyncText = value ?? ""; OnPropertyChanged(); } }
        public string CatalogSyncModeText { get => _catalogSyncModeText; set { _catalogSyncModeText = value ?? ""; OnPropertyChanged(); } }
        public string DeviceStatusText { get => _deviceStatusText; set { _deviceStatusText = value ?? ""; OnPropertyChanged(); } }
        public string LastOfficialSyncText { get => _lastOfficialSyncText; set { _lastOfficialSyncText = value ?? ""; OnPropertyChanged(); } }
        public string OfficialSourceText { get => _officialSourceText; set { _officialSourceText = value ?? ""; OnPropertyChanged(); } }
        public string PendingSalesText { get => _pendingSalesText; set { _pendingSalesText = value ?? ""; OnPropertyChanged(); } }
        public string SalesSyncText { get => _salesSyncText; set { _salesSyncText = value ?? ""; OnPropertyChanged(); } }
        public string ShopCode { get => _shopCode; set { _shopCode = value ?? ""; OnPropertyChanged(); } }
        public string ShopStatusText { get => _shopStatusText; set { _shopStatusText = value ?? ""; OnPropertyChanged(); } }
        public string SyncStatusText { get => _syncStatusText; set { _syncStatusText = value ?? ""; OnPropertyChanged(); } }
        public string Status { get => _status; set { _status = value ?? ""; OnPropertyChanged(); } }
        public bool IsBusy => _activeLoads > 0;
        public bool CanClose => !IsBusy;
        public bool IsReadOnly => true;

        public event PropertyChangedEventHandler PropertyChanged;

        private async Task LoadAsync()
        {
            BeginLoad();
            try
            {
                var official = await _service.GetOfficialShopSnapshotAsync().ConfigureAwait(true);
                var shop = await _service.GetShopInfoAsync().ConfigureAwait(true);
                var sync = await _service.GetPosSyncStatusAsync().ConfigureAwait(true);

                Name = shop.Name ?? "";
                Address = shop.Address ?? "";
                BusinessGiro = shop.BusinessGiro ?? "";
                City = shop.City ?? "";
                Rut = shop.Rut ?? "";
                Phone = shop.Phone ?? "";
                Footer = shop.Footer ?? "";
                FiscalBoletaNumberText = (await _service.GetFiscalBoletaNumberAsync().ConfigureAwait(true)).ToString(CultureInfo.InvariantCulture);
                ShopCode = Friendly(official.ShopCode);
                ShopStatusText = PosLocalization.F("settings.shopStatus", Friendly(official.ShopStatus));
                OfficialSourceText = PosLocalization.F("settings.source", FriendlySource(official.Source));
                LastOfficialSyncText = PosLocalization.F("settings.officialSnapshot", FriendlyDate(official.SyncedAtUtc));
                CatalogSyncText = sync.LastCatalogSyncText + " | " + sync.CatalogVersionText;
                CatalogCompletenessText = sync.CatalogCompletenessText;
                CatalogCountsText = sync.CatalogCountsText;
                CatalogRepairText = sync.CatalogRepairText;
                CatalogSyncModeText = sync.CatalogSyncModeText;
                SalesSyncText = sync.LastSalesSyncText;
                PendingSalesText = sync.PendingSalesText;
                DeviceStatusText = sync.DeviceText + " | " + sync.StaffText;
                SyncStatusText = sync.ConnectivityText + " | " + sync.LastOnlineText;
                Status = official.HasOfficialData
                    ? PosLocalization.T("settings.shopReadOnlyOfflineCache")
                    : PosLocalization.T("settings.shopSnapshotMissing");
            }
            catch
            {
                Status = PosLocalization.T("settings.shopDataUnavailable");
            }
            finally
            {
                EndLoad();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            PosLocalization.Current.LanguageChanged -= OnLanguageChanged;
        }

        private void BeginLoad()
        {
            _activeLoads += 1;
            if (_activeLoads == 1)
            {
                NotifyBusyStateChanged();
            }
        }

        private void EndLoad()
        {
            if (_activeLoads <= 0)
            {
                return;
            }

            _activeLoads -= 1;
            if (_activeLoads == 0)
            {
                NotifyBusyStateChanged();
            }
        }

        private void NotifyBusyStateChanged()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanClose));
        }

        private static string Friendly(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? PosLocalization.T("sync.unavailable") : value.Trim();
        }

        private static string FriendlyDate(string value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }

            return PosLocalization.T("sync.never");
        }

        private static string FriendlySource(string value)
        {
            return string.Equals(value?.Trim(), "supabase_admin_server", StringComparison.OrdinalIgnoreCase)
                ? PosLocalization.T("settings.officialSourceAdminWeb")
                : Friendly(value);
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            _ = LoadAsync();
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    }
}
