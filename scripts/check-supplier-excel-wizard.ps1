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

$productsView = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsView.xaml"
$productsViewModel = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$dbMaintenanceView = Read-RepoFile "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml"
$dbMaintenanceViewModel = Read-RepoFile "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs"
$dialogXaml = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml"
$dialogCode = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml.cs"
$viewModel = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportViewModel.cs"
$workflow = Read-RepoFile "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs"
$applier = Read-RepoFile "src/Win7POS.Data/Import/SupplierExcelImportApplier.cs"
$helper = Read-RepoFile "src/Win7POS.Core/Import/SupplierRetailPriceHelper.cs"
$reader = Read-RepoFile "src/Win7POS.Core/Import/SupplierExcelImportReader.cs"
$cli = Read-RepoFile "src/Win7POS.Cli/Program.cs"

Assert-Contains $productsView "Import Excel fornitore" "Products entry point missing."
Assert-Contains $productsView "SupplierExcelImportCommand" "Products entry point is not wired."
Assert-Contains $productsViewModel "SupplierExcelImportDialog.ShowDialog(DialogOwnerHelper.GetSafeOwner())" "Products entry point does not open supplier dialog."
Assert-Contains $dbMaintenanceView "Import Excel fornitore" "DB maintenance entry point missing."
Assert-Contains $dbMaintenanceView "SupplierExcelImportCommand" "DB maintenance entry point is not wired."
Assert-Contains $dbMaintenanceViewModel "SupplierExcelImportDialog.ShowDialog" "DB maintenance entry point does not open supplier dialog."

Assert-Contains $dialogXaml "chrome:DialogShellWindow" "Supplier import must use WPF dialog shell."
Assert-Contains $dialogXaml "UseModalOverlay=`"True`"" "Supplier import dialog must be modal."
Assert-Contains $dialogCode "ShowDialog" "Supplier import must be shown with ShowDialog."
Assert-Contains $dialogXaml "1. Scegli file" "Step 1 missing."
Assert-Contains $dialogXaml "2. Analizza colonne" "Step 2 missing."
Assert-Contains $dialogXaml "3. Rivedi prezzi e applica" "Step 3 missing."

foreach ($required in @("originalColumnName", "canonicalKey", "headerSource", "confidence", "sampleValues", "enabled")) {
    Assert-Contains $dialogXaml $required "Step 2 mapping grid missing $required."
}
Assert-Contains $dialogXaml "SelectedItem=`"{Binding CanonicalKey" "Step 2 canonical override missing."
Assert-Contains $dialogXaml "Binding=`"{Binding IsEnabled" "Step 2 disable checkbox missing."
Assert-Contains $viewModel "Columns.ToDictionary(c => c.ColumnIndex" "Override state is not passed to analyzer."
Assert-Contains $viewModel "c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty" "Disable state is not passed to analyzer."
Assert-Contains $viewModel "await AnalyzeAsync().ConfigureAwait(true)" "Mapping changes must rebuild Step 3 preview."

foreach ($required in @("itemNumber", "productName", "secondProductName", "purchasePrice", "retailPrice", "quantity", "supplier", "category")) {
    Assert-Contains $dialogXaml "Header=`"$required`"" "Step 3 editable grid missing $required."
}
Assert-Contains $dialogXaml "IsReadOnly=`"True`"" "Step 3 barcode should be read-only."
Assert-Contains $viewModel "new[] { 10, 50, 100 }" "Bulk rounding options must be 10/50/100 CLP."
Assert-Contains $viewModel "_applyOnlyEmptyRetailPrice = true" "Bulk helper default must fill empty retailPrice only."
Assert-Contains $helper "ApplyMarkupToRetailPriceRows" "Bulk helper missing from core import code."
Assert-Contains $reader "CountWorksheets" "Reader must expose safe sheet count for smoke validation."
Assert-Contains $cli "--supplier-excel-drive-smoke <folder>" "CLI Drive smoke mode missing."
Assert-Contains $cli "RunSupplierExcelDriveSmoke" "CLI Drive smoke runner missing."
Assert-Contains $cli "Step2OverrideDisableCanCorrectMappingIssues" "Drive smoke summary must report Step 2 recoverability."
Assert-Contains $cli "Step3PriceEditCanResolveMissingRetail" "Drive smoke summary must report Step 3 price-edit recoverability."

Assert-Contains $viewModel "MissingNewRetailPriceCount == 0" "Missing retailPrice blocker is not in CanApply."
Assert-Contains $workflow "CreateBackupBeforeApplyAsync" "Apply backup missing."
Assert-Contains $workflow "Warning count" "Apply warning count missing."
Assert-Contains $workflow "Skipped" "Apply skipped count missing."
Assert-Contains $applier "BeginTransaction" "Apply transaction missing."
Assert-Contains $applier "tx.Rollback" "Apply rollback missing."
Assert-Contains $applier "'IMPORT'" "Price history IMPORT source missing."
Assert-Contains $applier "Nuovo prodotto senza retailPrice" "New product retailPrice blocker missing."
Assert-Contains $productsViewModel "CatalogEvents.RaiseCatalogChanged(null)" "Products catalog refresh missing."
Assert-Contains $dbMaintenanceViewModel "CatalogEvents.RaiseCatalogChanged(null)" "DB maintenance catalog refresh missing."

Write-Host "SUPPLIER EXCEL WIZARD CHECK PASS"
