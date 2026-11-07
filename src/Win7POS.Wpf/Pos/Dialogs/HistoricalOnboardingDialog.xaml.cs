using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Core.Security;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class HistoricalOnboardingDialog : DialogShellWindow
    {
        private static readonly FileLogger _logger = new FileLogger("HistoricalOnboardingDialog");
        private readonly HistoricalOnboardingAccess _access;
        private readonly PosWorkflowService _service;
        private readonly PermissionService _permissions;
        private readonly DispatcherTimer _expiryTimer;
        private Window _child;
        private bool _busy;

        internal HistoricalOnboardingDialog(HistoricalOnboardingAccess access, PosWorkflowService service)
        {
            InitializeComponent();
            _access = access ?? throw new ArgumentNullException(nameof(access));
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _permissions = new PermissionService(access.Session);
            WindowSizingHelper.CapMaxHeightToOwner(this);
            _expiryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (_, __) => RefreshAuthority(), Dispatcher);
            Closed += (_, __) => _expiryTimer.Stop();
            RefreshAuthority();
            _expiryTimer.Start();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (ImportButton.IsEnabled) ImportButton.Focus();
            else if (ReportButton.IsEnabled) ReportButton.Focus();
        }

        private void RefreshAuthority()
        {
            var current = _access.IsCurrentInMemory;
            ImportButton.IsEnabled = current && !_busy &&
                (_permissions.Has(PermissionCodes.RegisterImportHistory) || _permissions.Has(PermissionCodes.RegisterCancelHistory));
            ReportButton.IsEnabled = current && !_busy && _permissions.Has(PermissionCodes.DailyCloseView) &&
                _permissions.Has(PermissionCodes.RegisterViewAll);
            if (current) return;
            StatusText.Text = PosLocalization.T("history.authorizationChanged");
            // The existing child models immediately hide stale data and cancel
            // work. Closing an import in progress follows its bounded cancel path.
            _child?.Close();
        }

        private async Task RunAsync(Func<Task> action)
        {
            if (_busy) return;
            _busy = true; RefreshAuthority();
            try { await _access.DemandAsync(CancellationToken.None).ConfigureAwait(true); await action().ConfigureAwait(true); }
            catch (InvalidOperationException) { StatusText.Text = PosLocalization.T("history.authorizationChanged"); }
            catch (Exception ex)
            {
                _logger.LogWarning("Historical workspace operation failed: exceptionType=" + ex.GetType().Name);
                StatusText.Text = PosLocalization.T("history.onboardingOpenFailed");
            }
            finally { _child = null; _busy = false; RefreshAuthority(); }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e) => await RunAsync(OpenImportAsync);
        private Task OpenImportAsync()
        {
            var permission = _permissions.Has(PermissionCodes.RegisterImportHistory)
                ? PermissionCodes.RegisterImportHistory : PermissionCodes.RegisterCancelHistory;
            _permissions.Demand(permission, PosLocalization.T("history.title"));
            var model = new HistoricalSalesImportViewModel(_service, _access.Session);
            var dialog = new HistoricalSalesImportDialog(model) { Owner = DialogOwnerHelper.GetSafeOwner(this) };
            _child = dialog; dialog.ShowDialog(); _child = null;
            return Task.CompletedTask;
        }

        private async void OnReportClick(object sender, RoutedEventArgs e) => await RunAsync(() =>
        {
            _permissions.Demand(PermissionCodes.DailyCloseView, PosLocalization.T("historyReport.analyticalTitle"));
            _permissions.Demand(PermissionCodes.RegisterViewAll, PosLocalization.T("historyReport.analyticalTitle"));
            // No native POS view model, override service, printer or refund entry point.
            var model = new DailyReportViewModel(_service, permissions: _permissions, session: _access.Session,
                allowPrinting: false);
            var dialog = new DailyReportDialog(model) { Owner = DialogOwnerHelper.GetSafeOwner(this) };
            _child = dialog; dialog.ShowDialog(); _child = null;
            return Task.CompletedTask;
        });

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}
