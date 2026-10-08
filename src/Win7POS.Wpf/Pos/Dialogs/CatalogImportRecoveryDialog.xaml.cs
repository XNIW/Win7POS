using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Wpf.Chrome;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class CatalogImportRecoveryDialog : DialogShellWindow
    {
        private readonly CatalogImportRecoveryService _service;
        private readonly long _originalId;
        private readonly Func<bool> _authorizeCommit;
        private readonly Func<OnlineSyncGeneration> _getGeneration;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly IOperatorSession _operatorSession;
        private int _operatorChanged;
        private CatalogImportRecoveryDraft _draft;
        private OnlineSyncGeneration _generation;
        private bool _busy;
        private bool _closed;
        private bool _validated;
        private bool _closeRequested;
        private SupplierImportEditableRow _invalidRow;
        private int _invalidColumn;
        private readonly FileLogger _logger = new FileLogger("CatalogImportRecoveryDialog");
        private Stopwatch _operationTimer;
        private string _operationPhase;
        private string _operationCode;
        private long _replacementId;

        public CatalogImportRecoveryDialog(SqliteConnectionFactory factory, long originalId,
            Func<bool> authorizeCommit, Func<OnlineSyncGeneration> getGeneration)
        {
            _service = new CatalogImportRecoveryService(factory, AppPaths.BackupsDirectory);
            _originalId = originalId;
            _operatorSession = OperatorSessionHolder.Current;
            var hasEpoch = PosOnlineSyncRevocationLatch.TryCaptureAuthorizationEpoch(out var epoch);
            _authorizeCommit = () => Volatile.Read(ref _operatorChanged) == 0 &&
                ReferenceEquals(_operatorSession, OperatorSessionHolder.Current) &&
                (!hasEpoch || PosOnlineSyncRevocationLatch.IsAuthorizationEpochCurrent(epoch)) &&
                (authorizeCommit?.Invoke() == true);
            if (_operatorSession != null) _operatorSession.SessionChanged += OnOperatorChanged;
            _getGeneration = getGeneration ?? (() => null);
            InitializeComponent();
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            await PrepareAsync().ConfigureAwait(true);
        }

        private async void OnPrepareClick(object sender, RoutedEventArgs e)
        {
            if (_draft == null || !_draft.CanCommit)
                await PrepareAsync().ConfigureAwait(true);
            else
                await ValidateAsync().ConfigureAwait(true);
        }

        private async Task PrepareAsync()
        {
            if (_busy || _closed) return;
            BeginOperation("prepare");
            RecoveryStatus.Text = PosLocalization.T("importRecovery.verifying");
            try
            {
                DemandPermission();
                _generation = _getGeneration();
                if (_generation == null || !PosAdminWebOptions.TryLoad(out var options, out _) ||
                    !new PosTrustedDeviceStore().TryReadGeneration(_generation, out var session, out _))
                    throw new CatalogImportRecoveryException("authentication_required");
                var previousRows = _draft?.Rows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
                var prepared = await _service.PrepareAsync(_originalId, options, session,
                    _generation, _lifetime.Token).ConfigureAwait(true);
                if (_closed) return;
                DemandPermission();
                DetachRows();
                _draft = prepared;
                // Retry a failed receipt lookup without discarding corrections already entered.
                if (previousRows != null)
                    foreach (var row in _draft.Rows)
                        if (previousRows.TryGetValue(row.Barcode, out var previous))
                        {
                            row.PurchasePrice = previous.PurchasePrice;
                            row.RetailPrice = previous.RetailPrice;
                            row.Quantity = previous.Quantity;
                        }
                RecoveryRows.ItemsSource = _draft.Rows;
                foreach (var row in _draft.Rows) row.PropertyChanged += OnRowChanged;
                _validated = false;
                ShowPreparedState();
            }
            catch (OperationCanceledException) { ShowCancelled(); }
            catch (CatalogImportRecoveryException ex) { ShowError(ex.Code); }
            catch (Exception) { ShowError("unexpected_error"); }
            finally { SetBusy(false); }
        }

        private async void OnRetireClick(object sender, RoutedEventArgs e)
        {
            if (_busy || _closed || _draft?.CanRetire != true) return;
            BeginOperation("retire");
            try
            {
                DemandPermission();
                if (!ApplyConfirmDialog.ShowConfirm(DialogOwnerHelper.GetSafeOwner(this),
                    PosLocalization.T("importRecovery.retire"), PosLocalization.T("importRecovery.retireConfirm")))
                { _operationCode = "confirmation_declined"; return; }
                if (!PosAdminWebOptions.TryLoad(out var options, out _) ||
                    !new PosTrustedDeviceStore().TryReadGeneration(_generation, out var session, out _))
                    throw new CatalogImportRecoveryException("authentication_required");
                RecoveryStatus.Text = PosLocalization.T("importRecovery.verifying");
                var settled = await _service.RetireAsync(_draft, options, session, _generation,
                    _authorizeCommit, _lifetime.Token).ConfigureAwait(true);
                if (_closed) return;
                DemandPermission();
                // Settlement changes proof, never the operator's corrections.
                var edited = _draft.Rows.ToDictionary(row => row.Barcode, StringComparer.Ordinal);
                DetachRows();
                _draft = settled;
                foreach (var row in _draft.Rows)
                {
                    if (edited.TryGetValue(row.Barcode, out var previous))
                    {
                        row.PurchasePrice = previous.PurchasePrice;
                        row.RetailPrice = previous.RetailPrice;
                        row.Quantity = previous.Quantity;
                    }
                    row.PropertyChanged += OnRowChanged;
                }
                RecoveryRows.ItemsSource = _draft.Rows;
                _validated = false;
                ShowPreparedState();
            }
            catch (OperationCanceledException) { ShowCancelled(); }
            catch (CatalogImportRecoveryException ex) { ShowError(ex.Code); }
            catch (Exception) { ShowError("unexpected_error"); }
            finally { SetBusy(false); }
        }

        private async Task<bool> ValidateCoreAsync()
        {
            RecoveryRows.CommitEdit(DataGridEditingUnit.Cell, true);
            RecoveryRows.CommitEdit(DataGridEditingUnit.Row, true);
            DemandPermission();
            var preview = await _service.BuildPreviewAsync(_draft, _draft.Rows, _lifetime.Token)
                .ConfigureAwait(true);
            if (_closed) return false;
            DemandPermission();
            _validated = _draft.CanCommit && preview.CanApply;
            if (!_validated) _operationCode = "validation_failed";
            _invalidRow = null;
            RecoveryStatus.Text = _validated
                ? PosLocalization.F("importRecovery.previewReady", _draft.Rows.Count)
                : FormatErrors(preview.Errors);
            if (!_validated && preview.Errors.Count > 0)
            {
                var first = _draft.Rows.FirstOrDefault(row => row.RowNumber == preview.Errors[0].RowIndex);
                if (first != null)
                {
                    _invalidRow = first;
                    var message = (preview.Errors[0].Message ?? string.Empty).ToLowerInvariant();
                    _invalidColumn = message.Contains("purchase") ? 2 : message.Contains("quantity") ? 4 : 3;
                }
            }
            return _validated;
        }

        private async Task ValidateAsync()
        {
            if (_busy || _closed || _draft == null) return;
            BeginOperation("validate");
            try { await ValidateCoreAsync().ConfigureAwait(true); }
            catch (OperationCanceledException) { ShowCancelled(); }
            catch (CatalogImportRecoveryException ex) { ShowError(ex.Code); }
            catch (Exception) { ShowError("unexpected_error"); }
            finally { SetBusy(false); }
        }

        private async void OnCommitClick(object sender, RoutedEventArgs e)
        {
            if (_busy || _closed || _draft?.CanCommit != true ||
                (!_validated && !_draft.RequiresAcceptedReconciliation)) return;
            BeginOperation("commit"); // Single flight before validation, backup, or any other await.
            try
            {
                var reconcileAccepted = _draft.RequiresAcceptedReconciliation;
                if (!reconcileAccepted && !await ValidateCoreAsync().ConfigureAwait(true)) return;
                DemandPermission();
                RecoveryStatus.Text = PosLocalization.T("importRecovery.saving");
                var result = reconcileAccepted
                    ? await _service.ReconcileAcceptedReplacementAsync(_draft, _authorizeCommit,
                        _generation, _lifetime.Token).ConfigureAwait(true)
                    : await _service.CommitAsync(_draft, _draft.Rows, _authorizeCommit,
                        _generation, _lifetime.Token).ConfigureAwait(true);
                if (_closed) return;
                if (result.Errors > 0) { ShowError("validation_failed"); return; }
                _replacementId = result.CatalogImportOutboxId;
                if (!result.RecoveryAlreadyConverged)
                    PosOnlineSyncSignalBus.Signal(OnlineSyncLane.CatalogImportOutbox, OnlineSyncLaneTrigger.LocalCommit);
                _validated = false;
                _operationCode = reconcileAccepted ? "accepted_receipt_reconciled" : result.RecoveryAlreadyConverged ? "already_converged" : "replacement_queued";
                RecoveryStatus.Text = PosLocalization.T(reconcileAccepted ? "importRecovery.acceptedReconciled" : result.RecoveryAlreadyConverged
                    ? "importRecovery.converged" : "importRecovery.queued");
                ReceiptText.Text = PosLocalization.T(result.RecoveryAlreadyConverged
                    ? "importRecovery.resolved" : "importRecovery.awaitingAck");
                RecoveryRows.IsReadOnly = true;
                PrepareButton.Visibility = Visibility.Collapsed;
                CommitButton.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { ShowCancelled(); }
            catch (CatalogImportRecoveryException ex) { ShowError(ex.Code); }
            catch (Exception) { ShowError("unexpected_error"); }
            finally { SetBusy(false); }
        }

        private void DemandPermission()
        {
            if (!_authorizeCommit()) throw new CatalogImportRecoveryException("permission_denied");
        }

        private void OnRowChanged(object sender, PropertyChangedEventArgs e)
        {
            _validated = false;
            CommitButton.IsEnabled = false;
            if (!_busy) RecoveryStatus.Text = PosLocalization.T("importRecovery.editThenVerify");
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            if (!busy && _operationTimer != null)
            {
                _operationTimer.Stop();
                // One bounded summary per user operation; no credentials or row payloads.
                _logger.LogInfo("category=catalog.import.recovery phase=" + _operationPhase +
                    " code=" + SyncCenterViewModel.SafeCode(_operationCode) +
                    " original_id=" + _originalId.ToString(CultureInfo.InvariantCulture) +
                    " op_id=" + SyncCenterViewModel.SafeCode(_draft?.Batch.ClientImportId) +
                    " replacement_id=" + (_replacementId > 0 ? _replacementId : _draft?.Batch.ReplacementOutboxId ?? 0).ToString(CultureInfo.InvariantCulture) +
                    " payload_fingerprint=" + Fingerprint(_draft?.Batch.PayloadHash) +
                    " generation_fingerprint=" + Fingerprint(_generation?.Fingerprint) +
                    " transition_epoch=" + (_draft?.TransitionEpoch.ToString(CultureInfo.InvariantCulture) ?? "none") +
                    " revisions=" + (_draft?.RevisionCount ?? 0).ToString(CultureInfo.InvariantCulture) +
                    " revision_fingerprint=" + Fingerprint(_draft?.RevisionFingerprint) +
                    " rows=" + (_draft?.Rows.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                    " receipt=" + SyncCenterViewModel.SafeCode(_draft?.ReceiptStatus) +
                    " target_receipt=" + SyncCenterViewModel.SafeCode(_draft?.TargetReceiptStatus) +
                    " duration_ms=" + _operationTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                _operationTimer = null;
            }
            if (_closed) return;
            RecoveryProgress.IsIndeterminate = busy;
            RecoveryProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            RecoveryRows.IsEnabled = !busy && _draft != null;
            PrepareButton.IsEnabled = !busy;
            RetireButton.IsEnabled = !busy;
            CommitButton.IsEnabled = !busy && (_validated || _draft?.RequiresAcceptedReconciliation == true);
            CancelRecoveryButton.Content = PosLocalization.T(busy ? "common.cancel" : "common.close");
            if (!busy && _closeRequested) Close();
            else if (!busy && _invalidRow != null)
            {
                var row = _invalidRow;
                _invalidRow = null;
                var column = RecoveryRows.Columns[_invalidColumn];
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed || _busy || !RecoveryRows.IsEnabled) return;
                    RecoveryRows.SelectedItem = row;
                    RecoveryRows.ScrollIntoView(row, column);
                    RecoveryRows.CurrentCell = new DataGridCellInfo(row, column);
                    RecoveryRows.BeginEdit();
                    var input = FindInput(column.GetCellContent(row));
                    if (input != null) { input.Focus(); input.SelectAll(); }
                }), DispatcherPriority.Input);
            }
        }

        private void ShowCancelled()
        {
            _operationCode = "cancelled";
            if (!_closed) RecoveryStatus.Text = PosLocalization.T("sync.center.operationCancelled");
        }

        private void ShowError(string code)
        {
            _operationCode = code;
            _validated = false;
            if (!_closed) RecoveryStatus.Text = ImportRecoveryPresentation.FriendlyCause(code);
        }

        private void ShowPreparedState()
        {
            var reconcileAccepted = _draft.RequiresAcceptedReconciliation;
            RecoveryRows.IsReadOnly = reconcileAccepted;
            RetireButton.Visibility = _draft.CanRetire ? Visibility.Visible : Visibility.Collapsed;
            PrepareButton.Visibility = reconcileAccepted ? Visibility.Collapsed : Visibility.Visible;
            CommitButton.Content = PosLocalization.T(reconcileAccepted ? "importRecovery.reconcileAccepted" : "importRecovery.commit");
            ReceiptText.Text = PosLocalization.T(reconcileAccepted ? "importRecovery.acceptedPendingReconciliation" :
                "importRecovery.receipt." + (_draft.CanCommit ? (_draft.ReceiptStatus == "accepted" ? "accepted" : "safe") : "unknown"));
            RecoveryStatus.Text = PosLocalization.T(reconcileAccepted ? "importRecovery.acceptedDraftRetained" :
                _draft.CanCommit ? "importRecovery.editThenVerify" : "importRecovery.unknownBlocked");
        }

        internal static string FormatErrors(IEnumerable<SupplierImportError> errors)
        {
            return string.Join(Environment.NewLine, errors.Take(20).Select(error =>
                PosLocalization.F("importRecovery.rowError", error.RowIndex, error.Barcode,
                    PreviewFieldError(error.Message))));
        }

        private static string PreviewFieldError(string message)
        {
            var code = (message ?? string.Empty).ToLowerInvariant();
            var field = code.Contains("purchase") ? "purchase" : code.Contains("retail") ? "retail"
                : code.Contains("quantity") ? "quantity" : "data";
            return PosLocalization.T("importRecovery." + field) + ": " +
                PosLocalization.T(field == "quantity" ? "importRecovery.invalidQuantity" : "importRecovery.invalidData");
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) { Close(); }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_busy)
            {
                _closeRequested = true;
                _lifetime.Cancel();
                e.Cancel = true;
                RecoveryStatus.Text = PosLocalization.T("importRecovery.cancelling");
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            if (_operatorSession != null) _operatorSession.SessionChanged -= OnOperatorChanged;
            DetachRows();
            _lifetime.Dispose();
            base.OnClosed(e);
        }

        private void DetachRows()
        {
            if (_draft != null)
                foreach (var row in _draft.Rows) row.PropertyChanged -= OnRowChanged;
        }

        private void OnOperatorChanged() { Interlocked.Exchange(ref _operatorChanged, 1); }

        private void BeginOperation(string phase)
        {
            _operationPhase = phase;
            _operationCode = "ok";
            _operationTimer = Stopwatch.StartNew();
            SetBusy(true);
        }

        private static string Fingerprint(string value) => SyncCenterViewModel.SafeCode(
            (value ?? string.Empty).Substring(0, Math.Min(12, (value ?? string.Empty).Length)));

        private static TextBox FindInput(DependencyObject current)
        {
            if (current is TextBox input) return input;
            if (current == null) return null;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                var found = FindInput(VisualTreeHelper.GetChild(current, index));
                if (found != null) return found;
            }
            return null;
        }
    }
}
