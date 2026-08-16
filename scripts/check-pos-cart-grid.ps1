[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot

function Read-Required([string]$relativePath) {
    $path = Join-Path $root $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Missing required file: $relativePath"
    }
    return Get-Content -LiteralPath $path -Raw
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    Write-Host "PASS: $message"
}

$keys = Read-Required "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs"
$mode = Read-Required "src/Win7POS.Wpf/Pos/CartViewMode.cs"
$service = Read-Required "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$viewModel = Read-Required "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$view = Read-Required "src/Win7POS.Wpf/Pos/PosView.xaml"
$presenter = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImageListPresenter.xaml.cs"
$settingsHub = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
$settingsHubCode = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml.cs"
$dialog = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/CartViewSettingsDialog.xaml"
$dialogCode = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/CartViewSettingsDialog.xaml.cs"
$localization = Read-Required "src/Win7POS.Wpf/Localization/PosLocalization.cs"

Require ($keys -match 'PosCartViewMode\s*=\s*"pos\.cart\.view_mode"') "cart view setting key is centralized"
Require ($mode -match 'RowsValue\s*=\s*"rows"' -and $mode -match 'GridValue\s*=\s*"grid"') "cart view values are explicit"
Require ($mode -match 'CartViewMode\.Rows') "unknown values fail safe to rows"
Require ($service -match 'GetCartViewModeAsync' -and $service -match 'SetCartViewModeAsync') "cart view uses app_settings repository"
Require ($service -match 'ListDetailsByBarcodesAsync') "image metadata uses a batch product lookup"
Require ($viewModel -match 'CartProductImageCacheCapacity\s*=\s*512') "cart image metadata cache is bounded"
Require ($viewModel -match 'StartsWith\("MANUAL:"' -and $viewModel -match 'DiscountKeys\.IsDiscount') "manual and discount lines are excluded from image lookup"
Require ($viewModel -match 'UpdateFrom\(item\)' -and $viewModel -notmatch 'CartItems\.Clear\(\);\s*foreach \(var item in snapshot\.Lines\)') "quantity refresh reuses cart row objects"
Require ($view -match 'x:Name="CartListBox"' -and $view -match 'x:Name="CartGridListBox"') "rows and grid surfaces are present"
Require ($view -match '<WrapPanel[^>]+ItemWidth="184"') "grid wraps responsively"
Require ($view -match 'Product="\{Binding ProductImage\}"') "grid uses the shared product image presenter"
Require ($view -match 'IncreaseQtyForLineCommand' -and $view -match 'DecreaseQtyForLineCommand' -and $view -match 'RemoveLineForLineCommand' -and $view -match 'OpenChangeQuantityForLineCommand') "grid reuses cart commands"
Require ($presenter -match 'ProductImageVariant\.Thumb' -and $presenter -match 'ProductImageDecodeProfile\.ListThumbnail') "grid loads only list thumbnails"
Require ($settingsHub -match 'settings\.cartView\.title' -and $settingsHubCode -match 'CartViewSettingsDialog\.ShowDialog') "workstation settings exposes cart view"
Require ($dialog -match 'WindowStartupLocation="CenterOwner"' -and $dialog -match 'DialogActionButtonStyle' -and $dialog -match 'DialogCancelButtonStyle' -and $dialog -match 'DialogFooterMargin') "cart view dialog follows shared dialog resources"
Require ($dialogCode -match 'ownerWindow \?\? DialogOwnerHelper\.GetSafeOwner\(\)') "cart view dialog uses safe nested ownership"

$translationKeys = @(
    "settings.cartView.title",
    "settings.cartView.cardHelp",
    "settings.cartView.dialogHelp",
    "settings.cartView.rows",
    "settings.cartView.grid",
    "settings.cartView.rowsDescription",
    "settings.cartView.gridDescription",
    "settings.cartView.recommended",
    "settings.cartView.lowPowerNote",
    "settings.cartView.saveError"
)
foreach ($translationKey in $translationKeys) {
    Require ($localization -match [regex]::Escape("new TranslationEntry(`"$translationKey`"")) "localization key $translationKey is present"
}

Write-Host "PASS: POS cart rows/grid gate"
