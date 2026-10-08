using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class SyncCenterDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly Func<CatalogSyncTrigger, bool, CancellationToken, Task<CatalogSyncRunResult>> _runSyncAsync;
        private readonly Func<Window, Task<bool>> _authorizeFullRepairAsync;
        private readonly DispatcherTimer _refreshTimer;
        private readonly SyncCenterViewModel _viewModel;
        private readonly Func<Window, Task<bool>> _authorizeImportAsync;
        private readonly Func<bool> _authorizeImportCommit;
        private readonly Func<OnlineSyncGeneration> _getGeneration;
        private CancellationTokenSource _operationCts;
        private PosSyncStatusSnapshot _snapshot;
        private bool _operationRunning;
        private bool _fullRepairRunning;
        private bool _refreshRunning;
        private bool _closed;

        public SyncCenterDialog(
            SqliteConnectionFactory factory,
            Func<CatalogSyncTrigger, bool, CancellationToken, Task<CatalogSyncRunResult>> runSyncAsync,
            Func<Window, Task<bool>> authorizeFullRepairAsync,
            Func<Window, Task<bool>> authorizeImportAsync = null,
            Func<bool> authorizeImportCommit = null,
            Func<OnlineSyncGeneration> getGeneration = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _runSyncAsync = runSyncAsync ?? throw new ArgumentNullException(nameof(runSyncAsync));
            _authorizeFullRepairAsync = authorizeFullRepairAsync ?? throw new ArgumentNullException(nameof(authorizeFullRepairAsync));
            _authorizeImportAsync = authorizeImportAsync ?? (_ => Task.FromResult(false));
            _authorizeImportCommit = authorizeImportCommit ?? (() => false);
            _getGeneration = getGeneration ?? (() => null);
            _viewModel = new SyncCenterViewModel();
            _refreshTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _refreshTimer.Tick += OnRefreshTimerTick;
            InitializeComponent();
            DataContext = _viewModel;
        }

        public bool CanClose => !_fullRepairRunning;

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await RefreshAsync().ConfigureAwait(true);
            if (!_closed)
            {
                _refreshTimer.Start();
            }
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
            _closed = true;
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = null;
            base.OnClosed(e);
        }

        private async void OnRefreshTimerTick(object sender, EventArgs e)
        {
            await RefreshAsync().ConfigureAwait(true);
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

        private async void OnImportRecoveryClick(object sender, RoutedEventArgs e)
        {
            if (_operationRunning || !((sender as Button)?.Tag is CatalogImportRecoveryBatch batch)) return;
            _operationRunning = true;
            SetOperationState();
            try
            {
                if (!await _authorizeImportAsync(this).ConfigureAwait(true) || _closed) return;
                var dialog = new CatalogImportRecoveryDialog(_factory, batch.OutboxId,
                    _authorizeImportCommit, _getGeneration)
                {
                    Owner = DialogOwnerHelper.GetSafeOwner(this)
                };
                dialog.ShowDialog();
            }
            catch (Exception)
            {
                if (!_closed) OperationStatusText.Text = PosLocalization.T("importRecovery.unknownBlocked");
            }
            finally
            {
                _operationRunning = false;
                SetOperationState();
                await RefreshAsync().ConfigureAwait(true);
            }
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
            if (_closed || _refreshRunning)
            {
                return;
            }

            _refreshRunning = true;
            try
            {
                var snapshot = await new PosSyncStatusReader(_factory)
                    .ReadAsync(markArticleCompletionsViewed: true)
                    .ConfigureAwait(true);
                if (!_closed)
                {
                    _snapshot = snapshot;
                    RenderSnapshot(_snapshot);
                    var batches = await new CatalogImportRecoveryService(_factory)
                        .ListAsync(CancellationToken.None).ConfigureAwait(true);
                    if (!_closed) _viewModel.ApplyImportRecoveries(batches);
                }
            }
            catch (Exception)
            {
                if (!_closed)
                {
                    HeaderStatusText.Text = PosLocalization.T("shell.syncUnavailable");
                    HeaderStatusBadge.Background = FindBrush("StatusErrorBrush", Colors.DarkRed);
                }
            }
            finally
            {
                _refreshRunning = false;
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
            OperationProgress.IsIndeterminate = _operationRunning;
            ImportRecoveryList.IsEnabled = !_operationRunning;
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
