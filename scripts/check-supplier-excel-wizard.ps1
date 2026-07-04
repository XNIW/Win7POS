Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-RepoFile {
    param([string]$RelativePath)
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required source file missing: $RelativePath"
    }
    return [System.IO.File]::ReadAllText($path)
}

function Assert-Contains {
    param(
        [string]$Source,
        [string]$Required,
        [string]$Message
    )
    if ($Source.IndexOf($Required, [System.StringComparison]::Ordinal) -lt 0) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [string]$Source,
        [string]$Forbidden,
        [string]$Message
    )
    if ($Source.IndexOf($Forbidden, [System.StringComparison]::Ordinal) -ge 0) {
        throw $Message
    }
}

$productsView = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsView.xaml"
$productsViewModel = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$dbMaintenanceView = Read-RepoFile "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml"
$dbMaintenanceDialogCode = Read-RepoFile "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs"
$dbMaintenanceViewModel = Read-RepoFile "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs"
$dialogXaml = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml"
$dialogCode = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml.cs"
$viewModel = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportViewModel.cs"
$dialogOwnerHelper = Read-RepoFile "src/Win7POS.Wpf/Infrastructure/DialogOwnerHelper.cs"
$workflow = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs"
$applier = Read-RepoFile "src/Win7POS.Data/Import/SupplierExcelImportApplier.cs"
$analyzer = Read-RepoFile "src/Win7POS.Core/Import/SupplierImportAnalyzer.cs"
$helper = Read-RepoFile "src/Win7POS.Core/Import/SupplierRetailPriceHelper.cs"
$reader = Read-RepoFile "src/Win7POS.Core/Import/SupplierExcelImportReader.cs"
$cli = Read-RepoFile "src/Win7POS.Cli/Program.cs"
$generalImportViewModel = Read-RepoFile "src/Win7POS.Wpf/Import/ImportViewModel.cs"
$generalImportWorkflow = Read-RepoFile "src/Win7POS.Wpf/Import/ImportWorkflowService.cs"
$generalImportViewCode = Read-RepoFile "src/Win7POS.Wpf/Import/ImportView.xaml.cs"
$productDbImportViewModel = Read-RepoFile "src/Win7POS.Wpf/Import/ProductDbImportViewModel.cs"

Assert-Contains $productsView "Import Excel fornitore" "Products entry point missing."
Assert-Contains $productsView "SupplierExcelImportCommand" "Products entry point is not wired."
Assert-Contains $productsViewModel "SupplierExcelImportDialog.ShowDialog(DialogOwnerHelper.GetSafeOwner())" "Products entry point does not open supplier dialog."
Assert-Contains $dbMaintenanceView "Import Excel fornitore" "DB maintenance entry point missing."
Assert-Contains $dbMaintenanceView "SupplierExcelImportCommand" "DB maintenance entry point is not wired."
Assert-Contains $dbMaintenanceViewModel "internal Window OwnerWindow { get; set; }" "DB maintenance must keep the current dialog owner."
Assert-Contains $dbMaintenanceDialogCode "vm.OwnerWindow = this" "DB maintenance dialog must pass itself as current owner."
Assert-Contains $dbMaintenanceViewModel "SupplierExcelImportDialog.ShowDialog(OwnerWindow ?? DialogOwnerHelper.GetSafeOwner())" "DB maintenance entry point must open supplier dialog from the current owner chain."

Assert-Contains $dialogXaml "chrome:DialogShellWindow" "Supplier import must use WPF dialog shell."
Assert-Contains $dialogXaml "UseModalOverlay=`"True`"" "Supplier import dialog must be modal."
Assert-Contains $dialogCode "ShowDialog" "Supplier import must be shown with ShowDialog."
Assert-Contains $dialogCode "new SupplierExcelFileDialogService(() => this)" "Supplier import dialog must provide itself as file picker owner."
Assert-Contains $viewModel "ISupplierExcelFileDialogService" "Supplier import file picker must be owner-aware behind an interface."
Assert-Contains $viewModel "SelectSupplierExcelFile()" "Supplier import Browse must use the owner-aware file picker service."
Assert-Contains $viewModel "DialogOwnerHelper.GetSafeOwner(_ownerProvider == null ? null : _ownerProvider())" "Supplier import file picker must resolve the current safe owner."
Assert-Contains $viewModel "dlg.ShowDialog(owner)" "Supplier import file picker must be shown with an explicit owner."
Assert-NotContains $viewModel "dlg.ShowDialog() == true" "Supplier import file picker must not use ownerless ShowDialog()."
Assert-NotContains $viewModel "ModernMessageDialog.Show(Application.Current?.MainWindow" "Supplier import flow must not hardcode MainWindow for nested dialog messages."
Assert-NotContains $dbMaintenanceViewModel "SupplierExcelImportDialog.ShowDialog(System.Windows.Application.Current?.MainWindow)" "DB maintenance supplier import must not hardcode MainWindow."
Assert-Contains $dialogOwnerHelper "window.IsVisible && window.IsEnabled" "DialogOwnerHelper must skip invisible or disabled owners."
Assert-Contains $dialogOwnerHelper "window.IsActive" "DialogOwnerHelper must prefer the active safe owner."
Assert-Contains $dialogOwnerHelper "LastOrDefault(IsSafeOwner)" "DialogOwnerHelper must fall back only to visible/enabled owners."
Assert-Contains $dialogXaml "1. Scegli file" "Step 1 missing."
Assert-Contains $dialogXaml "2. Analizza colonne" "Step 2 missing."
Assert-Contains $dialogXaml "3. Correggi righe" "Step 3 missing."
Assert-Contains $dialogXaml "4. Verifica Sync DB" "Step 4 missing."
Assert-Contains $dialogXaml "Verifica Sync Database" "Step 4 Sync DB title missing."
Assert-Contains $dialogXaml "Nuovi" "Step 4 new products tab missing."
Assert-Contains $dialogXaml "Aggiornamenti" "Step 4 updates tab missing."
Assert-Contains $dialogXaml "Senza modifiche" "Step 4 no-change tab missing."
Assert-Contains $dialogXaml "Skippati" "Step 4 skipped tab missing."
Assert-Contains $dialogXaml "SyncSearchText" "Step 4 search/filter input missing."
Assert-Contains $dialogXaml "SyncNewProductsView" "Step 4 new products must use filtered view."
Assert-Contains $dialogXaml "SyncUpdatedProductsView" "Step 4 updates must use filtered view."
Assert-Contains $dialogXaml "Continua a Sync DB" "Step 3 must continue to Sync DB."
Assert-Contains $dialogXaml "Visibility=`"{Binding IsStep4" "Apply must only be visible on Step 4."
Assert-Contains $viewModel "BuildSyncPreviewAsync" "Step 4 sync preview builder missing."
Assert-Contains $viewModel "InvalidateSyncPreview" "Step 3 edits must invalidate Step 4 preview."
Assert-Contains $viewModel "CollectionViewSource.GetDefaultView" "Step 4 search/filter collection views missing."
Assert-Contains $viewModel "SyncProductMatches" "Step 4 product search predicate missing."
Assert-Contains $viewModel "StepIndex == 3 && SyncCanApply" "Apply must be enabled only from valid Step 4."
Assert-Contains $workflow "BuildSyncPreviewAsync" "Workflow must build DB sync preview."
Assert-Contains $workflow "rebuilt.Fingerprint" "Workflow must recompute and verify Step 4 before writing."
Assert-Contains $applier "ApplyAsync(preview.ValidatedRows" "Data applier must apply validated Step 4 rows."
Assert-Contains $generalImportViewModel "CanApplyImport" "General catalog import must gate Apply on a current DB sync preview."
Assert-Contains $generalImportViewModel "BuildCurrentAnalyzeFingerprint" "General catalog import must invalidate stale analyzed files/options."
Assert-Contains $generalImportViewModel "InvalidateAnalyzeResult" "General catalog import must clear stale preview state."
Assert-Contains $generalImportWorkflow "DiffSummariesMatch" "General catalog import must recompute DB diff before writing."
Assert-Contains $generalImportWorkflow "Sync DB preview non aggiornato" "General catalog import must reject stale DB sync preview."
Assert-Contains $generalImportViewCode ".xls" "General catalog import drag/drop must accept legacy .xls files."
Assert-Contains $productDbImportViewModel "HasCurrentWorkbook" "Legacy product DB import must reject stale analyzed workbooks."
Assert-Contains $productDbImportViewModel "BuildCurrentWorkbookFingerprint" "Legacy product DB import must fingerprint analyzed workbook files."

foreach ($required in @("originalColumnName", "canonicalKey", "headerSource", "confidence", "sampleValues", "enabled")) {
    Assert-Contains $dialogXaml $required "Step 2 mapping grid missing $required."
}
Assert-Contains $dialogXaml "SelectedItem=`"{Binding CanonicalKey" "Step 2 canonical override missing."
Assert-Contains $dialogXaml "Binding=`"{Binding IsEnabled" "Step 2 disable checkbox missing."
Assert-Contains $viewModel "Columns.ToDictionary(c => c.ColumnIndex" "Override state is not passed to analyzer."
Assert-Contains $viewModel "c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty" "Disable state is not passed to analyzer."
Assert-Contains $viewModel "await AnalyzeAsync().ConfigureAwait(true)" "Mapping changes must rebuild Step 3 preview."

foreach ($required in @("barcode", "itemNumber", "productName", "secondProductName", "purchasePrice", "retailPrice", "quantity", "supplier", "category")) {
    Assert-Contains $dialogXaml "Header=`"$required`"" "Step 3 editable grid missing $required."
}
Assert-Contains $dialogXaml "Header=`"Skip`"" "Step 3 skip checkbox missing."
Assert-Contains $dialogXaml "Binding=`"{Binding IsSkipped" "Step 3 skip binding missing."
Assert-Contains $dialogXaml "Binding=`"{Binding Barcode, Mode=TwoWay" "Step 3 barcode must be editable."
Assert-Contains $viewModel "new[] { 10, 50, 100 }" "Bulk rounding options must be 10/50/100 CLP."
Assert-Contains $viewModel "_applyOnlyEmptyRetailPrice = true" "Bulk helper default must fill empty retailPrice only."
Assert-Contains $helper "ApplyMarkupToRetailPriceRows" "Bulk helper missing from core import code."
Assert-Contains $reader "CountWorksheets" "Reader must expose safe sheet count for smoke validation."
Assert-Contains $cli "--supplier-excel-drive-smoke <folder>" "CLI Drive smoke mode missing."
Assert-Contains $cli "RunSupplierExcelDriveSmoke" "CLI Drive smoke runner missing."
Assert-Contains $cli "SearchOption.AllDirectories" "CLI Drive smoke must scan subfolders."
Assert-Contains $cli "IsSupportedWorkbookFile" "CLI Drive smoke must include Excel files detected by signature, not extension only."
Assert-Contains $cli "Step2OverrideDisableCanCorrectMappingIssues" "Drive smoke summary must report Step 2 recoverability."
Assert-Contains $cli "Step3PriceEditCanResolveMissingRetail" "Drive smoke summary must report Step 3 price-edit recoverability."
Assert-Contains $cli "--supplier-excel-apply-selftest" "CLI supplier apply selftest mode missing."
Assert-Contains $cli "RunSupplierExcelApplySelfTestAsync" "CLI supplier apply selftest runner missing."
Assert-Contains $cli "WIN7POS_DATA_DIR" "Supplier apply selftest must use a temp data directory."
Assert-Contains $cli "PRAGMA integrity_check" "Supplier apply selftest must verify SQLite integrity."
Assert-Contains $cli "supplier_import_forced_failure" "Supplier apply selftest must prove rollback on forced failure."
Assert-Contains $cli "product_price_history" "Supplier apply selftest must verify IMPORT price history."
Assert-Contains $cli "--supplier-excel-drive-completion-report <folder>" "CLI Drive completion report mode missing."
Assert-Contains $cli "RunSupplierExcelDriveCompletionReport" "CLI Drive completion report runner missing."
Assert-Contains $cli "ready_to_apply" "Completion report missing ready_to_apply state."
Assert-Contains $cli "ready_after_mapping_override" "Completion report missing mapping override state."
Assert-Contains $cli "ready_after_price_edit" "Completion report missing price edit state."
Assert-Contains $cli "ready_after_barcode_edit_or_skip" "Completion report missing barcode edit-or-skip state."
Assert-Contains $cli "unsupported_or_corrupt_with_clear_message" "Completion report missing unsupported/corrupt state."

Assert-Contains $analyzer "Nuovo prodotto senza retailPrice." "Step 4 missing retailPrice blocker missing."
Assert-Contains $analyzer "Barcode richiesto prima del Sync DB." "Step 4 missing barcode blocker missing."
Assert-Contains $analyzer "Nuovo prodotto senza productName, secondProductName o itemNumber." "Step 4 missing product identity blocker missing."
Assert-Contains $viewModel "row.IsSkipped" "Apply must track operator-skipped rows."
Assert-Contains $viewModel "SyncErrors" "Step 4 blocker list must expose sync preview errors."
Assert-Contains $viewModel "Ricalcola Sync DB prima di applicare." "Apply blocker must require Sync DB recalculation."
Assert-Contains $workflow "CreateBackupBeforeApplyAsync" "Apply backup missing."
Assert-Contains $workflow "WalCheckpointAsync" "Apply backup must checkpoint WAL before copying the DB."
Assert-Contains $workflow "Warning count" "Apply warning count missing."
Assert-Contains $workflow "Skipped" "Apply skipped count missing."
Assert-Contains $workflow "Skipped by operator" "Apply summary must count operator-skipped rows."
Assert-Contains $workflow "No change" "Apply summary must count no-change rows."
Assert-Contains $applier "BeginTransaction" "Apply transaction missing."
Assert-Contains $applier "tx.Rollback" "Apply rollback missing."
Assert-Contains $applier "row.IsSkipped" "Data applier must ignore skipped rows defensively."
Assert-Contains $applier "'IMPORT'" "Price history IMPORT source missing."
Assert-Contains $applier "Nuovo prodotto senza retailPrice" "New product retailPrice blocker missing."
Assert-Contains $analyzer "viene usata l'ultima occorrenza" "Duplicate final barcode must warn and use last occurrence."
Assert-Contains $productsViewModel "CatalogEvents.RaiseCatalogChanged(null)" "Products catalog refresh missing."
Assert-Contains $dbMaintenanceViewModel "CatalogEvents.RaiseCatalogChanged(null)" "DB maintenance catalog refresh missing."

Write-Host "SUPPLIER EXCEL WIZARD CHECK PASS"
