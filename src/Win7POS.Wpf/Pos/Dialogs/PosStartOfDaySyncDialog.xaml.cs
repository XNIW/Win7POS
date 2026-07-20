using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class PosStartOfDaySyncDialog : DialogShellWindow
    {
        private readonly SqliteConnectionFactory _factory;
        private readonly bool _ownsSyncHost;
        private readonly PosOnlineSyncSupervisorHost _syncHost;
        private CancellationTokenSource _activeCts;
        private bool _running;
        private bool _userCancelling;

        public PosStartOfDaySyncDialog()
            : this(new SqliteConnectionFactory(PosDbOptions.Default()), null)
        {
        }

        public PosStartOfDaySyncDialog(SqliteConnectionFactory factory)
            : this(factory, null)
        {
        }

        public PosStartOfDaySyncDialog(
            SqliteConnectionFactory factory,
            PosOnlineSyncSupervisorHost syncHost)
        {
            InitializeComponent();
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _syncHost = syncHost ?? new PosOnlineSyncSupervisorHost(_factory);
            _ownsSyncHost = syncHost == null;
            ContentRendered += OnContentRendered;
            Closed += (_, __) =>
            {
                if (_ownsSyncHost)
                    _syncHost.Dispose();
            };
            ApplyAuthenticatedAccessStatus();
            ResetSteps();
        }

        public StartOfDaySyncResult Result { get; private set; }

        public event EventHandler SyncDetailsRequested;

        private void OnContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= OnContentRendered;
            _ = RunPreflightAsync();
        }

        private async Task RunPreflightAsync()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _userCancelling = false;
            Result = null;
            ResetSteps();
            SetBusy(true);
            SetStatus(PosLocalization.T("startOfDay.checking"));

            try
            {
                using (_activeCts = new CancellationTokenSource(PosStartOfDaySyncService.StartOfDayTotalTimeout))
                {
                    var progress = new Progress<PosStartOfDaySyncProgress>(UpdateProgress);
                    Result = await new PosStartOfDaySyncService(_factory, _syncHost)
                        .RunAsync(progress, _activeCts.Token)
                        .ConfigureAwait(true);
                }

                ApplyResult(Result);
            }
            catch (OperationCanceledException)
            {
                if (!_userCancelling &&
                    await new SettingsRepository(_factory)
                        .GetBoolAsync(RestoreShopSafetyRepository.RestoreNeedsReviewKey)
                        .ConfigureAwait(true) == true)
                {
                    Result = new StartOfDaySyncResult
                    {
                        CanOpenPos = false,
                        RequiresOperatorAction = true,
                        BlockingReason = "restore_needs_review",
                        StatusMessage = PosLocalization.T("startOfDay.blockRestoreReview"),
                        RestoreNeedsReview = true,
                    };
                    ApplyResult(Result);
                }
                else if (!_userCancelling)
                {
                    Result = new StartOfDaySyncResult
                    {
                        CanOpenPos = true,
                        ShouldContinueInBackground = true,
                        StatusMessage = PosLocalization.T("startOfDay.continueBackground"),
                        CatalogSaleSafe = true,
                        CatalogStatus = "background",
                    };
                    ApplyResult(Result);
                }
            }
            catch
            {
                Result = new StartOfDaySyncResult
                {
                    CanOpenPos = false,
                    RequiresOperatorAction = true,
                    BlockingReason = "preflight_exception",
                    StatusMessage = PosLocalization.T("startOfDay.blockLocalDb"),
                };
                ApplyResult(Result);
            }
            finally
            {
                _activeCts = null;
                _running = false;
                SetBusy(false);
            }
        }

        private void ApplyResult(StartOfDaySyncResult result)
        {
            if (result == null)
            {
                return;
            }

            SetStatus(result.StatusMessage);
            UpdateCounts(result);
            ContinueButton.IsEnabled = result.CanOpenPos;
            RetryButton.Visibility = result.CanOpenPos ? Visibility.Collapsed : Visibility.Visible;
            SyncDetailsButton.Visibility = result.RequiresOperatorAction
                ? Visibility.Visible
                : Visibility.Collapsed;
            ActionHelpText.Text = result.RequiresOperatorAction
                ? PosLocalization.T("startOfDay.actionRequiredHelp")
                : string.Empty;
            ActionHelpText.Visibility = result.RequiresOperatorAction
                ? Visibility.Visible
                : Visibility.Collapsed;
            WaitButton.Visibility = result.CanOpenPos && result.ShouldContinueInBackground
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (result.CanOpenPos &&
                !result.ShouldContinueInBackground &&
                !result.RequiresOperatorAction)
            {
                DialogResult = true;
                Close();
            }
        }

        private void UpdateProgress(PosStartOfDaySyncProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            SetStatus(progress.Message);

            switch ((progress.Step ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "database":
                    SetStep(StepDatabaseText, progress.State, PosLocalization.T("startOfDay.stepDatabase"));
                    break;
                case "session":
                    SetStep(StepSessionText, progress.State, PosLocalization.T("startOfDay.stepSession"));
                    break;
                case "outbox":
                    SetStep(StepOutboxText, progress.State, PosLocalization.T("startOfDay.stepOutbox"));
                    break;
                case "sales":
                    SetStep(StepSalesText, progress.State, PosLocalization.T("startOfDay.stepSales"));
                    break;
                case "catalog":
                    SetStep(StepCatalogText, progress.State, PosLocalization.T("startOfDay.stepCatalog"));
                    break;
                case "complete":
                    SetStep(StepCompleteText, progress.State, PosLocalization.T("startOfDay.stepComplete"));
                    break;
            }
        }

        private void ResetSteps()
        {
            SetStep(StepDatabaseText, "pending", PosLocalization.T("startOfDay.stepDatabase"));
            SetStep(StepSessionText, "pending", PosLocalization.T("startOfDay.stepSession"));
            SetStep(StepOutboxText, "pending", PosLocalization.T("startOfDay.stepOutbox"));
            SetStep(StepSalesText, "pending", PosLocalization.T("startOfDay.stepSales"));
            SetStep(StepCatalogText, "pending", PosLocalization.T("startOfDay.stepCatalog"));
            SetStep(StepCompleteText, "pending", PosLocalization.T("startOfDay.stepComplete"));
            CountsText.Text = string.Empty;
            ContinueButton.IsEnabled = false;
            RetryButton.Visibility = Visibility.Collapsed;
            SyncDetailsButton.Visibility = Visibility.Collapsed;
            WaitButton.Visibility = Visibility.Collapsed;
            ActionHelpText.Text = string.Empty;
            ActionHelpText.Visibility = Visibility.Collapsed;
        }

        private void ApplyAuthenticatedAccessStatus()
        {
            var session = OperatorSessionHolder.Current;
            if (session == null || !session.IsLoggedIn)
            {
                AccessVerifiedPanel.Visibility = Visibility.Collapsed;
                AccessVerifiedText.Text = string.Empty;
                return;
            }

            AccessVerifiedText.Text = PosLocalization.F(
                "startOfDay.accessVerified",
                session.CurrentDisplayName,
                session.CurrentRoleName);
            AccessVerifiedPanel.Visibility = Visibility.Visible;
        }

        private static void SetStep(System.Windows.Controls.TextBlock textBlock, string state, string label)
        {
            var normalized = (state ?? string.Empty).Trim().ToLowerInvariant();
            string marker;
            Brush brush;

            switch (normalized)
            {
                case "ok":
                    marker = "[OK]";
                    brush = Brushes.DarkGreen;
                    break;
                case "active":
                    marker = "[..]";
                    brush = Brushes.DarkSlateBlue;
                    break;
                case "warning":
                    marker = "[!]";
                    brush = Brushes.DarkOrange;
                    break;
                case "error":
                    marker = "[X]";
                    brush = Brushes.Firebrick;
                    break;
                default:
                    marker = "[ ]";
                    brush = Brushes.DimGray;
                    break;
            }

            textBlock.Text = marker + " " + label;
            textBlock.Foreground = brush;
        }

        private void SetStatus(string message)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(message)
                ? PosLocalization.T("startOfDay.checking")
                : message.Trim();
        }

        private void UpdateCounts(StartOfDaySyncResult result)
        {
            CountsText.Text = PosLocalization.F(
                "startOfDay.counts",
                result.PendingSales,
                result.RetrySales,
                result.BlockedSales,
                result.CatalogSaleSafe ? PosLocalization.T("sync.catalogReady") : PosLocalization.T("sync.catalogNeverDownloaded"));
        }

        private void SetBusy(bool busy)
        {
            StartOfDayProgressBar.IsIndeterminate = busy;
            if (!busy)
            {
                StartOfDayProgressBar.Value = 100;
            }

            ContinueButton.IsEnabled = !busy && Result?.CanOpenPos == true;
            RetryButton.IsEnabled = !busy;
            SyncDetailsButton.IsEnabled = !busy;
            WaitButton.IsEnabled = !busy;
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (Result?.CanOpenPos == true)
            {
                DialogResult = true;
                Close();
            }
        }

        private async void Wait_Click(object sender, RoutedEventArgs e)
        {
            await RunPreflightAsync().ConfigureAwait(true);
        }

        private async void Retry_Click(object sender, RoutedEventArgs e)
        {
            await RunPreflightAsync().ConfigureAwait(true);
        }

        private void SyncDetails_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                return;
            }

            SyncDetailsRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            _userCancelling = true;
            _activeCts?.Cancel();
            DialogResult = false;
            Close();
        }
    }
}
