using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Win7POS.Core.Import;
using Win7POS.Data.Online;
using Win7POS.Wpf.Import;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Pos.Dialogs
{
    public partial class CatalogImportRecoveryDialog
    {
        private DispatcherTimer _draftSaveTimer;
        private DispatcherTimer _planProgressTimer;
        private Task<bool> _draftSaveTask;
        private long _draftVersion;
        private long _savedDraftVersion;
        private bool _draftApplied;
        private bool _closingDraftSave;
        private bool _allowClose;
        private bool _progressReading;
        private int _planTotalRows;

        private void InitializeDraftPersistence()
        {
            _draftSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(500) };
            _draftSaveTimer.Tick += OnDraftSaveTick;
            _planProgressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromSeconds(2) };
            _planProgressTimer.Tick += OnPlanProgressTick;
            _planProgressTimer.Start();
        }

        private void BindDraft(CatalogImportRecoveryDraft draft)
        {
            DetachRows();
            _draft = draft;
            RecoveryRows.ItemsSource = draft.Rows;
            foreach (var row in draft.Rows) row.PropertyChanged += OnRowChanged;
            _validated = false;
            ShowDraftState();
        }

        private void MarkDraftChanged()
        {
            if (_closed || _draftApplied) return;
            _draftVersion++;
            _draftSaveTimer.Stop();
            _draftSaveTimer.Start();
            DraftStateText.Text = PosLocalization.T("importRecovery.draftSaving");
            DiscardDraftButton.Visibility = Visibility.Visible;
        }

        private async void OnDraftSaveTick(object sender, EventArgs e)
        {
            _draftSaveTimer.Stop();
            await FlushDraftAsync().ConfigureAwait(true);
        }

        private async Task<bool> FlushDraftAsync()
        {
            _draftSaveTimer.Stop();
            if (_draftSaveTask != null) return await _draftSaveTask.ConfigureAwait(true);
            if (_draft == null || _draftApplied || _draftVersion == _savedDraftVersion) return true;
            _draftSaveTask = SaveDraftVersionsAsync();
            try { return await _draftSaveTask.ConfigureAwait(true); }
            finally { _draftSaveTask = null; }
        }

        private async Task<bool> SaveDraftVersionsAsync()
        {
            try
            {
                while (!_closed && !_draftApplied && _draftVersion != _savedDraftVersion)
                {
                    var version = _draftVersion;
                    // Capture bindings on the UI thread. SQLite serialization
                    // never reads a row while the operator is editing it.
                    var rows = _draft.Rows.Select(CopyDraftRow).ToArray();
                    await _service.SaveDraftAsync(_draft, rows, CancellationToken.None).ConfigureAwait(true);
                    _savedDraftVersion = version;
                }
                if (!_closed) ShowDraftState();
                return true;
            }
            catch (Exception)
            {
                if (!_closed) DraftStateText.Text = PosLocalization.T("importRecovery.draftSaveFailed");
                return false;
            }
        }

        private static SupplierImportEditableRow CopyDraftRow(SupplierImportEditableRow row) =>
            new SupplierImportEditableRow
            {
                RowNumber = row.RowNumber, Exists = row.Exists, IsSkipped = row.IsSkipped,
                Barcode = row.Barcode, ItemNumber = row.ItemNumber, ProductName = row.ProductName,
                SecondProductName = row.SecondProductName, PurchasePrice = row.PurchasePrice,
                RetailPrice = row.RetailPrice, Quantity = row.Quantity, Supplier = row.Supplier,
                Category = row.Category, HasItemNumberSource = row.HasItemNumberSource,
                HasProductNameSource = row.HasProductNameSource, HasSecondProductNameSource = row.HasSecondProductNameSource,
                HasPurchasePriceSource = row.HasPurchasePriceSource, HasRetailPriceSource = row.HasRetailPriceSource,
                HasQuantitySource = row.HasQuantitySource, HasSupplierSource = row.HasSupplierSource,
                HasCategorySource = row.HasCategorySource,
                RetailPriceMissingButPurchasePresent = row.RetailPriceMissingButPurchasePresent
            };

        private void ShowDraftState()
        {
            if (_closed) return;
            var hasDraft = !_draftApplied && (_draft?.HasSavedDraft == true || _draftVersion > 0);
            DiscardDraftButton.Visibility = hasDraft ? Visibility.Visible : Visibility.Collapsed;
            DraftStateText.Text = PosLocalization.T(_draftApplied && _draft?.HasDeferredEdits == true ? "importRecovery.deferredDraftSaved" :
                _draftApplied ? "importRecovery.localSaved" :
                _draft?.HasPreparedPlan == true ? "importRecovery.preparedPlan" :
                _draft?.IsSavedDraftStale == true ? "importRecovery.draftStale" :
                hasDraft ? "importRecovery.draftSaved" : "importRecovery.draftExplanation");
        }

        private async void SaveDraftAndClose()
        {
            _closingDraftSave = true;
            try
            {
                if (await FlushDraftAsync().ConfigureAwait(true))
                {
                    _allowClose = true;
                    Close();
                }
                else
                {
                    _closeRequested = false;
                    // A failed draft flush keeps this window open. The completed
                    // operation's cancellation must not disable a later retry.
                    if (_lifetime.IsCancellationRequested)
                    {
                        _lifetime.Dispose();
                        _lifetime = new CancellationTokenSource();
                    }
                }
            }
            finally { _closingDraftSave = false; }
        }

        private async void OnDiscardDraftClick(object sender, RoutedEventArgs e)
        {
            if (_busy || _closed || _draftApplied) return;
            BeginOperation("discard_draft");
            var discarded = false;
            try
            {
                if (!ApplyConfirmDialog.ShowConfirm(OwnerWindow ?? Win7POS.Wpf.Infrastructure.DialogOwnerHelper.GetSafeOwner(),
                    PosLocalization.T("importRecovery.discardDraft"), PosLocalization.T("importRecovery.discardDraftConfirm"))) return;
                _draftSaveTimer.Stop();
                if (_draftSaveTask != null) await _draftSaveTask.ConfigureAwait(true);
                await _service.DiscardDraftAsync(_draft?.Batch.OutboxId ?? _originalId,
                    CancellationToken.None).ConfigureAwait(true);
                DetachRows();
                _draft = null;
                RecoveryRows.ItemsSource = null;
                _draftVersion = _savedDraftVersion = 0;
                discarded = true;
            }
            catch (Exception) { DraftStateText.Text = PosLocalization.T("importRecovery.draftSaveFailed"); }
            finally { SetBusy(false); }
            if (discarded && !_closed) await PrepareAsync().ConfigureAwait(true);
        }

        private async void OnPlanProgressTick(object sender, EventArgs e)
        {
            if (!_busy) await RefreshPlanProgressAsync().ConfigureAwait(true);
        }

        private async Task RefreshPlanProgressAsync()
        {
            if (_closed || _progressReading || _draft == null) return;
            _progressReading = true;
            try
            {
                var progress = await _service.GetPlanProgressAsync(_originalId, _lifetime.Token).ConfigureAwait(true);
                if (_closed || progress == null || progress.TotalParts == 0) return;
                _planTotalRows = progress.TotalRows;
                PlanProgressText.Text = PosLocalization.F("importRecovery.planProgress", progress.CompletedRows,
                    progress.TotalRows, progress.CompletedParts, progress.TotalParts);
                if (progress.FailedParts > 0)
                    PlanProgressText.Text += " " + PosLocalization.F("importRecovery.planFailed", progress.FailedParts) +
                        " " + ImportRecoveryPresentation.FriendlyCause(progress.LastErrorCode);
                if (!_busy)
                {
                    RecoveryProgress.IsIndeterminate = false;
                    RecoveryProgress.Maximum = Math.Max(1, progress.TotalRows);
                    RecoveryProgress.Value = progress.CompletedRows;
                    RecoveryProgress.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!_closed) PlanProgressText.Text = PosLocalization.T("importRecovery.progressUnavailable");
            }
            finally { _progressReading = false; }
        }

        private void StopDraftPersistence()
        {
            _draftSaveTimer.Stop();
            _draftSaveTimer.Tick -= OnDraftSaveTick;
            _planProgressTimer.Stop();
            _planProgressTimer.Tick -= OnPlanProgressTick;
        }
    }
}
