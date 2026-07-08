$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $path)) {
        Fail "$relativePath missing"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Test-ContainsAll([string]$label, [string]$text, [string[]]$needles) {
    $missing = @()
    foreach ($needle in $needles) {
        if ($text.IndexOf($needle, [StringComparison]::Ordinal) -lt 0) {
            $missing += $needle
        }
    }

    if ($missing.Count -gt 0) {
        Fail "$label missing: $($missing -join ', ')"
    }
    else {
        Pass $label
    }
}

$modernStyles = Read-Text "src/Win7POS.Wpf/ModernStyles.xaml"
$productsView = Read-Text "src/Win7POS.Wpf/Products/ProductsView.xaml"
$productsViewModel = Read-Text "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$settingsHub = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"

Test-ContainsAll "ModernTextBoxStyle readable text/caret/selection" $modernStyles @(
    'x:Key="ModernTextBoxStyle"',
    'Foreground',
    'CaretBrush',
    'SelectionBrush'
)

Test-ContainsAll "basic vector icon button resources" $modernStyles @(
    'x:Key="IconButtonContent"',
    'x:Key="ButtonIconPathStyle"',
    'x:Key="IconSettings"',
    'x:Key="IconSearch"',
    'x:Key="IconClear"'
)

Test-ContainsAll "Settings hub cards available" $settingsHub @(
    'SettingsHubDialog',
    'shell.officialShopData',
    'shell.printerSettings',
    'operations.dbMaintenance',
    'shell.usersRoles',
    'settings.language',
    'shell.aboutSupport'
)

Test-ContainsAll "Products searchable filters" $productsView @(
    'ApplyFiltersCommand',
    'FilteredSuppliers',
    'FilteredCategories',
    'SupplierFilterText',
    'CategoryFilterText',
    'IsEditable="True"',
    'StaysOpenOnEdit="True"'
)

Test-ContainsAll "Products filter state kept client-side until apply" $productsViewModel @(
    'ApplyFiltersCommand',
    'ResolveTypedFilterSelections',
    'RefreshFilteredSuppliers',
    'RefreshFilteredCategories',
    'ClearFiltersCommand'
)

$sidebarKeys = @(
    'OfficialShopDataMenuButton',
    'PrinterSettingsMenuButton',
    'DbMaintenanceMenuButton',
    'UsersRolesMenuButton',
    'AboutSupportMenuButton',
    'OpenCashDrawerMenuButton'
)

$sidebarLeaks = @()
foreach ($key in $sidebarKeys) {
    if ($mainWindow.IndexOf($key, [StringComparison]::Ordinal) -ge 0) {
        $sidebarLeaks += $key
    }
}

if ($sidebarLeaks.Count -gt 0) {
    Fail "MainWindow sidebar still exposes settings/action entries: $($sidebarLeaks -join ', ')"
}
else {
    Pass "MainWindow sidebar is compact"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
