using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public sealed class SyncCenterViewModel : INotifyPropertyChanged
    {
        private PosSyncStatusSnapshot _snapshot;
        private string _catalogCountsText = string.Empty;
        private string _catalogCursorText = string.Empty;
        private string _catalogFullRatioText = string.Empty;
        private string _catalogLastSuccessText = string.Empty;
        private string _catalogModeText = string.Empty;
        private string _catalogProgressText = string.Empty;
        private string _catalogRepairReasonText = string.Empty;
        private string _catalogRowsText = string.Empty;
        private string _catalogTriggerText = string.Empty;
        private string _catalogVerificationText = string.Empty;
        private string _headerStatusText = string.Empty;
        private string _importLastAckText = string.Empty;
        private string _importQueueText = string.Empty;
        private string _lastUpdatedText = string.Empty;
        private string _salesErrorText = string.Empty;
        private string _salesLastAckText = string.Empty;
        private string _salesQueueText = string.Empty;

        public string CatalogCountsText { get => _catalogCountsText; private set => Set(ref _catalogCountsText, value); }
        public string CatalogCursorText { get => _catalogCursorText; private set => Set(ref _catalogCursorText, value); }
        public string CatalogFullRatioText { get => _catalogFullRatioText; private set => Set(ref _catalogFullRatioText, value); }
        public bool CatalogHasMore => _snapshot?.CatalogHasMore == true;
        public string CatalogLastSuccessText { get => _catalogLastSuccessText; private set => Set(ref _catalogLastSuccessText, value); }
        public string CatalogModeText { get => _catalogModeText; private set => Set(ref _catalogModeText, value); }
        public string CatalogProgressText { get => _catalogProgressText; private set => Set(ref _catalogProgressText, value); }
        public string CatalogRepairReasonText { get => _catalogRepairReasonText; private set => Set(ref _catalogRepairReasonText, value); }
        public string CatalogRowsText { get => _catalogRowsText; private set => Set(ref _catalogRowsText, value); }
        public string CatalogTriggerText { get => _catalogTriggerText; private set => Set(ref _catalogTriggerText, value); }
        public string CatalogVerificationText { get => _catalogVerificationText; private set => Set(ref _catalogVerificationText, value); }
        public string HeaderStatusText { get => _headerStatusText; private set => Set(ref _headerStatusText, value); }
        public string ImportLastAckText { get => _importLastAckText; private set => Set(ref _importLastAckText, value); }
        public string ImportQueueText { get => _importQueueText; private set => Set(ref _importQueueText, value); }
        public string LastUpdatedText { get => _lastUpdatedText; private set => Set(ref _lastUpdatedText, value); }
        public PosSyncStatusSnapshot Snapshot => _snapshot;
        public string SalesErrorText { get => _salesErrorText; private set => Set(ref _salesErrorText, value); }
        public string SalesLastAckText { get => _salesLastAckText; private set => Set(ref _salesLastAckText, value); }
        public string SalesQueueText { get => _salesQueueText; private set => Set(ref _salesQueueText, value); }

        public event PropertyChangedEventHandler PropertyChanged;

        public void Apply(PosSyncStatusSnapshot status, DateTimeOffset updatedAt)
        {
            _snapshot = status ?? throw new ArgumentNullException(nameof(status));
            HeaderStatusText = status.ConnectivityText;
            LastUpdatedText = PosLocalization.F(
                "sync.center.lastUpdated",
                updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            CatalogProgressText = PosLocalization.T(status.CatalogHasMore
                ? "sync.center.partial"
                : "sync.center.complete");
            CatalogModeText = Field("sync.center.mode", status.CatalogSyncModeText);
            CatalogTriggerText = Field("sync.center.trigger", status.CatalogLastTriggerCode);
            CatalogLastSuccessText = Field("sync.center.lastSuccess", status.CatalogLastSuccessText);
            CatalogCursorText = Field("sync.center.cursorFingerprint", status.CatalogCursorFingerprint);
            CatalogRowsText = PosLocalization.F(
                "sync.center.pagesRowsDuration",
                status.CatalogPages,
                status.CatalogRows,
                status.CatalogDurationMilliseconds);
            CatalogFullRatioText = PosLocalization.F("sync.center.fullRatio", status.CatalogFullRatioPercent);
            CatalogVerificationText = status.CatalogCompletenessText;
            CatalogCountsText = status.CatalogCountsText;
            CatalogRepairReasonText = status.CatalogRepairText + " | " +
                Field("sync.center.fullReason", status.CatalogLastFullReasonCode);
            SalesQueueText = QueueText(
                status.SalesPending,
                status.SalesRetry,
                status.SalesBlocked,
                status.SalesInProgress);
            SalesLastAckText = Field("sync.center.lastAck", status.SalesLastAckText);
            SalesErrorText = status.SalesErrorText;
            ImportQueueText = QueueText(
                status.ImportPending,
                status.ImportRetry,
                status.ImportBlocked,
                status.ImportInProgress);
            ImportLastAckText = Field("sync.center.lastAck", status.ImportLastAckText);
            OnPropertyChanged(nameof(CatalogHasMore));
            OnPropertyChanged(nameof(Snapshot));
        }

        public string BuildSafeDiagnostics()
        {
            if (_snapshot == null)
            {
                return "sync.status=unavailable";
            }

            var text = new StringBuilder();
            text.AppendLine("sync.connectivity=" + SafeCode(_snapshot.ConnectivityState));
            text.AppendLine("catalog.trigger=" + SafeCode(_snapshot.CatalogLastTriggerCode));
            text.AppendLine("catalog.cursor_fingerprint=" + SafeCode(_snapshot.CatalogCursorFingerprint));
            text.AppendLine("catalog.partial=" + _snapshot.CatalogHasMore.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("catalog.pages=" + _snapshot.CatalogPages.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("catalog.rows=" + _snapshot.CatalogRows.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("catalog.duration_ms=" + _snapshot.CatalogDurationMilliseconds.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("catalog.full_reason=" + SafeCode(_snapshot.CatalogLastFullReasonCode));
            text.AppendLine("catalog.full_ratio_percent=" + SafeCode(_snapshot.CatalogFullRatioPercent));
            text.AppendLine("sales.pending=" + _snapshot.SalesPending.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("sales.retry=" + _snapshot.SalesRetry.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("sales.blocked=" + _snapshot.SalesBlocked.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("sales.in_progress=" + _snapshot.SalesInProgress.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("imports.pending=" + _snapshot.ImportPending.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("imports.retry=" + _snapshot.ImportRetry.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("imports.blocked=" + _snapshot.ImportBlocked.ToString(CultureInfo.InvariantCulture));
            text.Append("imports.in_progress=" + _snapshot.ImportInProgress.ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static string Field(string labelKey, string value)
        {
            return PosLocalization.F(
                "sync.center.fieldValue",
                PosLocalization.T(labelKey),
                string.IsNullOrWhiteSpace(value) ? PosLocalization.T("sync.unavailable") : value);
        }

        private static string QueueText(long pending, long retry, long blocked, long inProgress)
        {
            return PosLocalization.F("sync.center.queueCounts", pending, retry, blocked, inProgress);
        }

        internal static string SafeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "none";
            }

            var builder = new StringBuilder();
            foreach (var character in value.Trim())
            {
                if (char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.')
                {
                    builder.Append(character);
                }

                if (builder.Length >= 80)
                {
                    break;
                }
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private void Set(ref string field, string value, [CallerMemberName] string propertyName = null)
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(field, normalized, StringComparison.Ordinal))
            {
                return;
            }

            field = normalized;
            OnPropertyChanged(propertyName);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
