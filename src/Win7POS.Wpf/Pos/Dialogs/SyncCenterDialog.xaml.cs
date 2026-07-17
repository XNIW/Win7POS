using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SyncCenterDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly Func<CatalogSyncTrigger, bool, CancellationToken, Task<CatalogSyncRunResult>> _runSyncAsync;
        private readonly Func<Window, Task<bool>> _authorizeFullRepairAsync;
        private readonly SyncCenterViewModel _viewModel;
        private CancellationTokenSource _operationCts;
        private PosSyncStatusSnapshot _snapshot;
        private bool _operationRunning;
        private bool _fullRepairRunning;

        public SyncCenterDialog(
            SqliteConnectionFactory factory,
            Func<CatalogSyncTrigger, bool, CancellationToken, Task<CatalogSyncRunResult>> runSyncAsync,
            Func<Window, Task<bool>> authorizeFullRepairAsync)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _runSyncAsync = runSyncAsync ?? throw new ArgumentNullException(nameof(runSyncAsync));
            _authorizeFullRepairAsync = authorizeFullRepairAsync ?? throw new ArgumentNullException(nameof(authorizeFullRepairAsync));
            _viewModel = new SyncCenterViewModel();
            InitializeComponent();
            DataContext = _viewModel;
        }

        public bool CanClose => !_fullRepairRunning;

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await RefreshAsync().ConfigureAwait(true);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_fullRepairRunning)
            {
                e.Cancel = true;
                OperationStatusText.Text = PosLocalization.T("sync.center.closeBlocked");
                return;
            }

            if (_operationRunning)
            {
                _operationCts?.Cancel();
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = null;
            base.OnClosed(e);
        }

        private async void OnSyncNowClick(object sender, RoutedEventArgs e)
        {
            await RunOperationAsync(CatalogSyncTrigger.Manual, administratorRepairAuthorized: false)
                .ConfigureAwait(true);
        }

        private async void OnRetryClick(object sender, RoutedEventArgs e)
        {
            await RunOperationAsync(CatalogSyncTrigger.PartialResume, administratorRepairAuthorized: false)
                .ConfigureAwait(true);
        }

        private async void OnFullRepairClick(object sender, RoutedEventArgs e)
        {
            if (_operationRunning)
            {
                return;
            }

            if (!await _authorizeFullRepairAsync(this).ConfigureAwait(true))
            {
                return;
            }

            var reason = _snapshot?.CatalogRepairText;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = PosLocalization.T("sync.unavailable");
            }

            if (!ApplyConfirmDialog.ShowConfirm(
                    this,
                    PosLocalization.T("sync.center.fullRepair"),
                    PosLocalization.F("sync.center.repairConfirm", reason)))
            {
                return;
            }

            await RunOperationAsync(CatalogSyncTrigger.AdministratorRepair, administratorRepairAuthorized: true)
                .ConfigureAwait(true);
        }

        private async Task RunOperationAsync(
            CatalogSyncTrigger trigger,
            bool administratorRepairAuthorized)
        {
            if (_operationRunning)
            {
                return;
            }

            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            _operationRunning = true;
            _fullRepairRunning = administratorRepairAuthorized;
            SetOperationState();
            OperationStatusText.Text = PosLocalization.T("sync.center.operationStarted");

            try
            {
                var result = await _runSyncAsync(
                        trigger,
                        administratorRepairAuthorized,
                        _operationCts.Token)
                    .ConfigureAwait(true);
                OperationStatusText.Text = result.Success
                    ? PosLocalization.F(
                        "sync.center.operationCompleted",
                        result.Pages,
                        result.Rows,
                        SyncCenterViewModel.SafeCode(result.Code))
                    : PosLocalization.F("sync.center.operationFailed", SyncCenterViewModel.SafeCode(result.Code));
            }
            catch (OperationCanceledException)
            {
                OperationStatusText.Text = PosLocalization.T("sync.center.operationCancelled");
            }
            catch (Exception)
            {
                OperationStatusText.Text = PosLocalization.F("sync.center.operationFailed", "unexpected_error");
            }
            finally
            {
                _operationRunning = false;
                _fullRepairRunning = false;
                SetOperationState();
                await RefreshAsync().ConfigureAwait(true);
            }
        }

        private async Task RefreshAsync()
        {
            try
            {
                _snapshot = await new PosSyncStatusReader(_factory).ReadAsync().ConfigureAwait(true);
                RenderSnapshot(_snapshot);
            }
            catch (Exception)
            {
                HeaderStatusText.Text = PosLocalization.T("shell.syncUnavailable");
                HeaderStatusBadge.Background = FindBrush("StatusErrorBrush", Colors.DarkRed);
            }
        }

        private void RenderSnapshot(PosSyncStatusSnapshot status)
        {
            _viewModel.Apply(status, DateTimeOffset.Now);
            HeaderStatusText.Foreground = Brushes.White;
            HeaderStatusBadge.Background = status.RequiresAttention
                ? FindBrush("StatusWarningBrush", Colors.DarkOrange)
                : status.ConnectivityState == "online"
                    ? FindBrush("StatusSuccessBrush", Colors.DarkGreen)
                    : FindBrush("StatusInfoBrush", Colors.DarkSlateBlue);
            RetryButton.IsEnabled = !_operationRunning && status.CatalogHasMore;
        }

        private void SetOperationState()
        {
            OperationProgress.Visibility = _operationRunning ? Visibility.Visible : Visibility.Collapsed;
            SyncNowButton.IsEnabled = !_operationRunning;
            RetryButton.IsEnabled = !_operationRunning && (_snapshot?.CatalogHasMore == true);
            FullRepairButton.IsEnabled = !_operationRunning;
            CopyDiagnosticsButton.IsEnabled = !_fullRepairRunning;
            CloseButton.IsEnabled = !_fullRepairRunning;
        }

        private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_viewModel.BuildSafeDiagnostics());
                OperationStatusText.Text = PosLocalization.T("sync.center.diagnosticsCopied");
            }
            catch (Exception)
            {
                OperationStatusText.Text = PosLocalization.T("sync.center.diagnosticsCopyFailed");
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private Brush FindBrush(string key, Color fallback)
        {
            return TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        }
    }
}
