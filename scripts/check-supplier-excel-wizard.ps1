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

function Assert-Matches {
    param(
        [string]$Source,
        [string]$Pattern,
        [string]$Message
    )
    if ($Source -notmatch $Pattern) {
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
$dialogShellWindow = Read-RepoFile "src/Win7POS.Wpf/Chrome/DialogShellWindow.cs"
$dialogOwnerHelper = Read-RepoFile "src/Win7POS.Wpf/Infrastructure/DialogOwnerHelper.cs"
$localization = Read-RepoFile "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$workflow = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs"
$applier = Read-RepoFile "src/Win7POS.Data/Import/SupplierExcelImportApplier.cs"
$analyzer = Read-RepoFile "src/Win7POS.Core/Import/SupplierImportAnalyzer.cs"
$helper = Read-RepoFile "src/Win7POS.Core/Import/SupplierRetailPriceHelper.cs"
$reader = Read-RepoFile "src/Win7POS.Data/Import/SupplierExcelImportReader.cs"
$cli = Read-RepoFile "src/Win7POS.Cli/Program.cs"
$generalImportViewModel = Read-RepoFile "src/Win7POS.Wpf/Import/ImportViewModel.cs"
$generalImportWorkflow = Read-RepoFile "src/Win7POS.Wpf/Import/ImportWorkflowService.cs"
$generalImportViewCode = Read-RepoFile "src/Win7POS.Wpf/Import/ImportView.xaml.cs"
$productDbImportViewModel = Read-RepoFile "src/Win7POS.Wpf/Import/ProductDbImportViewModel.cs"

Assert-Contains $productsView "supplierExcelImport.title" "Products localized entry point missing."
Assert-Contains $productsView "SupplierExcelImportCommand" "Products entry point is not wired."
Assert-Matches $productsViewModel 'SupplierExcelImportDialog\.ShowDialog\(\s*DialogOwnerHelper\.GetSafeOwner\(\),\s*\(\)\s*=>\s*DemandProductPermission\([\s\S]{0,180}PermissionCodes\.CatalogImport' "Products entry point must open the supplier dialog with an apply-time catalog.import authorizer."
Assert-Contains $dbMaintenanceView "supplierExcelImport.title" "DB maintenance localized entry point missing."
Assert-Contains $dbMaintenanceView "SupplierExcelImportCommand" "DB maintenance entry point is not wired."
Assert-Contains $localization "Import Excel fornitore" "Supplier Excel Italian title translation missing."
Assert-Contains $dbMaintenanceViewModel "internal Window OwnerWindow { get; set; }" "DB maintenance must keep the current dialog owner."
Assert-Contains $dbMaintenanceDialogCode "vm.OwnerWindow = this" "DB maintenance dialog must pass itself as current owner."
Assert-Matches $dbMaintenanceViewModel 'SupplierExcelImportDialog\.ShowDialog\(\s*OwnerWindow\s*\?\?\s*DialogOwnerHelper\.GetSafeOwner\(\),\s*_hasCatalogImportPermission\)' "DB maintenance entry point must pass its current owner and catalog.import authorizer."

Assert-Contains $dialogXaml "chrome:DialogShellWindow" "Supplier import must use WPF dialog shell."
Assert-Contains $dialogXaml "UseModalOverlay=`"True`"" "Supplier import dialog must be modal."
Assert-Contains $dialogXaml "WindowStartupLocation=`"CenterOwner`"" "Supplier import dialog must center on its safe owner."
Assert-Contains $dialogXaml "MaxWidth=`"1120`"" "Supplier import dialog must cap width for work-area clamp."
Assert-Contains $dialogXaml "MaxHeight=`"720`"" "Supplier import dialog must cap height for work-area clamp."
Assert-Contains $dialogXaml "<ScrollViewer Grid.Row=`"2`"" "Supplier import step content must be inside a scrollable content row."
Assert-Contains $dialogXaml "<Grid Grid.Row=`"3`" Margin=`"{StaticResource DialogFooterMargin}`"" "Supplier import footer must be a fixed root row."
Assert-Contains $dialogShellWindow "ApplyOverlayPosition(outerBorder)" "Overlay positioning must account for the actual dialog card size."
Assert-Contains $dialogShellWindow "CanHostOverlayCard" "Nested overlays must fall back to monitor work area when the owner cannot host the card."
Assert-Contains $dialogCode "ShowDialog" "Supplier import must be shown with ShowDialog."
Assert-Contains $dialogCode "Owner = DialogOwnerHelper.GetSafeOwner(owner)" "Supplier import dialog owner must be normalized through DialogOwnerHelper."
Assert-Contains $dialogCode "new SupplierExcelFileDialogService(() => this)" "Supplier import dialog must provide itself as file picker owner."
Assert-Matches $dialogCode 'ShowDialog\(Window owner, Func<bool> authorizeApply\)' "Supplier import dialog must require an apply-time authorizer."
Assert-Contains $dialogCode "new SupplierExcelImportWorkflowService(authorizeApply)" "Supplier import dialog must forward the apply-time authorizer."
Assert-Contains $viewModel "ISupplierExcelFileDialogService" "Supplier import file picker must be owner-aware behind an interface."
Assert-Contains $viewModel "ISupplierExcelCompletionDialogService" "Supplier import completion dialog must be injectable for WPF smoke."
Assert-Contains $viewModel "RunSmokeAsync" "Supplier import ViewModel smoke path missing."
Assert-Contains $viewModel "SelectSupplierExcelFile()" "Supplier import Browse must use the owner-aware file picker service."
Assert-Contains $viewModel "DialogOwnerHelper.GetSafeOwner(_ownerProvider == null ? null : _ownerProvider())" "Supplier import file picker must resolve the current safe owner."
Assert-Contains $viewModel "dlg.ShowDialog(owner)" "Supplier import file picker must be shown with an explicit owner."
Assert-NotContains $viewModel "dlg.ShowDialog() == true" "Supplier import file picker must not use ownerless ShowDialog()."
Assert-NotContains $viewModel "ModernMessageDialog.Show(Application.Current?.MainWindow" "Supplier import flow must not hardcode MainWindow for nested dialog messages."
Assert-NotContains $dbMaintenanceViewModel "SupplierExcelImportDialog.ShowDialog(System.Windows.Application.Current?.MainWindow)" "DB maintenance supplier import must not hardcode MainWindow."
Assert-Matches $workflow 'SupplierExcelImportWorkflowService\(Func<bool> authorizeApply\)[\s\S]{0,220}_authorizeApply\s*=\s*authorizeApply\s*\?\?\s*\(\(\)\s*=>\s*false\)' "Supplier import workflow must fail closed when its authorizer is absent."
Assert-Matches $workflow 'if\s*\(!dryRun\)[\s\S]{0,120}DemandApplyAuthorization\(\)[\s\S]{0,180}CreateBackupBeforeApplyAsync' "Supplier import must reauthorize immediately before backup and mutation."
Assert-Contains $dialogOwnerHelper "window.IsVisible && window.IsEnabled" "DialogOwnerHelper must skip invisible or disabled owners."
Assert-Contains $dialogOwnerHelper "window.IsActive" "DialogOwnerHelper must prefer the active safe owner."
Assert-Contains $dialogOwnerHelper "LastOrDefault(IsSafeOwner)" "DialogOwnerHelper must fall back only to visible/enabled owners."

$stepScrollStart = $dialogXaml.IndexOf("<ScrollViewer Grid.Row=`"2`"", [System.StringComparison]::Ordinal)
$stepScrollEnd = if ($stepScrollStart -ge 0) { $dialogXaml.IndexOf("</ScrollViewer>", $stepScrollStart, [System.StringComparison]::Ordinal) } else { -1 }
$footerStart = $dialogXaml.IndexOf("<Grid Grid.Row=`"3`"", [System.StringComparison]::Ordinal)
if ($stepScrollStart -lt 0 -or $stepScrollEnd -lt 0 -or $footerStart -lt 0 -or $footerStart -le $stepScrollEnd) {
    throw "Supplier import footer must stay outside the step ScrollViewer."
}
foreach ($buttonKey in @("supplierExcelImport.back", "supplierExcelImport.analyze", "supplierExcelImport.next", "supplierExcelImport.continueSyncDb", "supplierExcelImport.confirmApply", "common.cancel")) {
    $buttonIndex = $dialogXaml.IndexOf("{loc:Loc $buttonKey}", $footerStart, [System.StringComparison]::Ordinal)
    if ($buttonIndex -lt $footerStart) {
        throw "Supplier import footer button '$buttonKey' must be outside the ScrollViewer."
    }

    $buttonStart = $dialogXaml.LastIndexOf("<Button", $buttonIndex, [System.StringComparison]::Ordinal)
    $buttonEnd = if ($buttonStart -ge 0) { $dialogXaml.IndexOf("</Button>", $buttonStart, [System.StringComparison]::Ordinal) } else { -1 }
    if ($buttonStart -lt $footerStart -or $buttonEnd -lt $buttonIndex) {
        throw "Supplier import footer localization '$buttonKey' must belong to a footer Button."
    }

    $buttonMarkup = $dialogXaml.Substring($buttonStart, $buttonEnd + 9 - $buttonStart)
    if ($buttonMarkup -notmatch '(Command|Click)\s*=') {
        throw "Supplier import footer button '$buttonKey' must be wired to a command or click handler."
    }
}

Assert-Contains $dialogXaml "supplierExcelImport.stepChooseFile" "Step 1 key missing."
Assert-Contains $dialogXaml "supplierExcelImport.stepAnalyzeColumns" "Step 2 key missing."
Assert-Contains $dialogXaml "supplierExcelImport.stepFixRows" "Step 3 key missing."
Assert-Contains $dialogXaml "supplierExcelImport.stepVerifySync" "Step 4 key missing."
Assert-Contains $dialogXaml "supplierExcelImport.verifySyncDatabase" "Step 4 Sync DB title key missing."
Assert-Contains $dialogXaml "supplierExcelImport.verifySyncHelp" "Step 4 help key missing."
Assert-Contains $dialogXaml "supplierExcelImport.tabNew" "Step 4 new products tab key missing."
Assert-Contains $dialogXaml "supplierExcelImport.tabUpdates" "Step 4 updates tab key missing."
Assert-Contains $dialogXaml "supplierExcelImport.tabNoChanges" "Step 4 no-change tab key missing."
Assert-Contains $dialogXaml "supplierExcelImport.tabSkipped" "Step 4 skipped tab key missing."
Assert-Contains $localization "1. Scegli file" "Step 1 Italian label missing."
Assert-Contains $localization "2. Analizza colonne" "Step 2 Italian label missing."
Assert-Contains $localization "3. Correggi righe" "Step 3 Italian label missing."
Assert-Contains $localization "4. Verifica Sync DB" "Step 4 Italian label missing."
Assert-Contains $localization "Verifica Sync Database" "Step 4 Sync DB Italian title missing."
Assert-Contains $localization "coda Admin Web pending" "Step 4 Italian help must explain local apply plus pending Admin Web queue."
Assert-Contains $localization "Nuovi" "Step 4 Italian new products tab missing."
Assert-Contains $localization "Aggiornamenti" "Step 4 Italian updates tab missing."
Assert-Contains $localization "Senza modifiche" "Step 4 Italian no-change tab missing."
Assert-Contains $localization "Skippati" "Step 4 Italian skipped tab missing."
Assert-Contains $dialogXaml "SyncSearchText" "Step 4 search/filter input missing."
Assert-Contains $dialogXaml "SyncNewProductsView" "Step 4 new products must use filtered view."
Assert-Contains $dialogXaml "SyncUpdatedProductsView" "Step 4 updates must use filtered view."
Assert-Contains $dialogXaml "Updated.SecondProductName" "Step 4 updated products must show secondProductName."
Assert-Contains $dialogXaml "Binding=`"{Binding SecondProductName" "Step 4 new/skipped products must show secondProductName."
Assert-Contains $dialogXaml "supplierExcelImport.continueSyncDb" "Step 3 must continue to Sync DB."
Assert-Contains $dialogXaml "Visibility=`"{Binding IsStep4" "Apply must only be visible on Step 4."
Assert-Contains $viewModel "BuildSyncPreviewAsync" "Step 4 sync preview builder missing."
Assert-Contains $viewModel "InvalidateSyncPreview" "Step 3 edits must invalidate Step 4 preview."
Assert-Contains $viewModel "CollectionViewSource.GetDefaultView" "Step 4 search/filter collection views missing."
Assert-Contains $viewModel "SyncProductMatches" "Step 4 product search predicate missing."
Assert-Contains $viewModel "StepIndex == 3 && SyncCanApply" "Apply must be enabled only from valid Step 4."
Assert-Contains $viewModel "string.IsNullOrWhiteSpace(row.SecondProductName)" "Missing new identity count must accept secondProductName like the analyzer."
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

foreach ($required in @("supplierExcelImport.columnOriginalName", "supplierExcelImport.columnCanonicalKey", "supplierExcelImport.columnHeaderSource", "supplierExcelImport.columnConfidence", "supplierExcelImport.columnSampleValues", "supplierExcelImport.columnEnabled")) {
    Assert-Contains $dialogXaml $required "Step 2 mapping grid missing $required."
}
foreach ($translated in @("originalColumnName", "canonicalKey", "headerSource", "confidence", "sampleValues", "enabled")) {
    Assert-Contains $localization $translated "Step 2 mapping translation missing $translated."
}
Assert-Contains $dialogXaml "SelectedItem=`"{Binding CanonicalKey" "Step 2 canonical override missing."
Assert-Contains $dialogXaml "Binding=`"{Binding IsEnabled" "Step 2 disable checkbox missing."
Assert-Contains $viewModel "Columns.ToDictionary(c => c.ColumnIndex" "Override state is not passed to analyzer."
Assert-Contains $viewModel "c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty" "Disable state is not passed to analyzer."
Assert-Contains $viewModel "await AnalyzeAsync().ConfigureAwait(true)" "Mapping changes must rebuild Step 3 preview."

foreach ($required in @("supplierExcelImport.fieldBarcode", "supplierExcelImport.fieldItemNumber", "supplierExcelImport.fieldProductName", "supplierExcelImport.fieldSecondProductName", "supplierExcelImport.fieldPurchasePrice", "supplierExcelImport.fieldRetailPrice", "supplierExcelImport.fieldQuantity", "supplierExcelImport.fieldSupplier", "supplierExcelImport.fieldCategory")) {
    Assert-Contains $dialogXaml "Header=`"{loc:Loc $required}`"" "Step 3 editable grid missing $required."
}
foreach ($translated in @("barcode", "itemNumber", "productName", "secondProductName", "purchasePrice", "retailPrice", "quantity", "supplier", "category")) {
    Assert-Contains $localization $translated "Step 3 field translation missing $translated."
}
Assert-Contains $dialogXaml "Header=`"{loc:Loc supplierExcelImport.fieldSkip}`"" "Step 3 skip checkbox missing."
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
Assert-Contains $dialogXaml "supplierExcelImport.identityWarning" "Step 3 identity warning key missing."
Assert-Contains $localization "Nuovi prodotti senza productName, secondProductName o itemNumber" "Step 3 identity warning translation must mention secondProductName."
Assert-Contains $viewModel "row.IsSkipped" "Apply must track operator-skipped rows."
Assert-Contains $viewModel "SyncErrors" "Step 4 blocker list must expose sync preview errors."
Assert-Contains $viewModel "supplierExcelImport.recalculateBeforeApply" "Apply blocker must require Sync DB recalculation."
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
Assert-Contains $dbMaintenanceViewModel "dlg.ShowDialog(owner)" "DB maintenance restore picker must be shown with an explicit owner."
Assert-Contains $dbMaintenanceViewModel "DialogOwnerHelper.GetSafeOwner(OwnerWindow)" "DB maintenance restore picker must resolve the current safe owner."
$app = Read-RepoFile "src/Win7POS.Wpf/App.xaml.cs"
$wpfSmoke = Read-RepoFile "tests/Win7POS.Wpf.UiSmokeHarness/SupplierExcelWpfViewModelSmoke.cs"
$uiHarness = Read-RepoFile "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$proofReader = Read-RepoFile "src/Win7POS.Data/Import/SupplierExcelImportProofReader.cs"
Assert-NotContains $app "SupplierExcelWpfViewModelSmoke" "Shipping WPF app must not expose the supplier Excel mutation smoke hook."
Assert-NotContains $app "--supplier-excel-wpf-viewmodel-smoke" "Shipping WPF app must not recognize the supplier Excel mutation smoke flag."
Assert-Contains $uiHarness "SupplierExcelWpfViewModelSmoke.TryRun" "UI test harness must expose the supplier Excel ViewModel smoke flag."
Assert-Contains $wpfSmoke "--supplier-excel-wpf-viewmodel-smoke" "WPF supplier smoke flag missing."
Assert-Contains $wpfSmoke "new SmokeFileDialogService" "WPF supplier smoke must bypass native file picker through the same ViewModel service seam."
Assert-Contains $wpfSmoke "RunSmokeAsync" "WPF supplier smoke must drive the ViewModel through Analyze, Step 4 and Apply."
Assert-Contains $wpfSmoke "VerifyDeniedApplyHasNoSideEffectsAsync" "WPF supplier smoke must prove denied overloads create no database or backup side effects."
Assert-Contains $wpfSmoke "backupCreated" "WPF supplier smoke must report backup proof."
Assert-Contains $wpfSmoke "SupplierExcelImportProofReader" "WPF supplier smoke must verify DB proof through Data."
Assert-Contains $proofReader "catalog_import_outbox" "Data supplier proof reader must verify outbox proof."

Write-Host "SUPPLIER EXCEL WIZARD CHECK PASS"
