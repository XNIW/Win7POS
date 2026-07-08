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
$appXaml = Read-Text "src/Win7POS.Wpf/App.xaml"
$materialSymbols = Read-Text "src/Win7POS.Wpf/Icons/MaterialSymbols.xaml"
$productsView = Read-Text "src/Win7POS.Wpf/Products/ProductsView.xaml"
$productsViewModel = Read-Text "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$settingsHub = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
$posView = Read-Text "src/Win7POS.Wpf/Pos/PosView.xaml"

Test-ContainsAll "ModernTextBoxStyle readable text/caret/selection" $modernStyles @(
    'x:Key="ModernTextBoxStyle"',
    'Foreground',
    'CaretBrush',
    'SelectionBrush'
)

Test-ContainsAll "basic vector icon button resources" $modernStyles @(
    'x:Key="IconButtonContent"',
    'x:Key="ButtonIconPathStyle"',
    'Fill" Value="{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type Button}}}"',
    'Stroke" Value="{x:Null}"',
    'StrokeThickness" Value="0"',
    'Icon geometries are defined in Icons/MaterialSymbols.xaml.'
)

Test-ContainsAll "Material Symbols Rounded vector icon resources" $materialSymbols @(
    'Google Material Symbols Rounded',
    'https://github.com/google/material-design-icons',
    'x:Key="IconSettings"',
    'x:Key="IconSearch"',
    'x:Key="IconFilterOff"',
    'x:Key="IconSuspend"',
    'x:Key="IconRecover"',
    'x:Key="IconUploadFile"',
    'x:Key="IconHistory"'
)

Test-ContainsAll "Material Symbols dictionary is merged before styles" $appXaml @(
    'Icons/MaterialSymbols.xaml',
    'ModernStyles.xaml'
)

if ($modernStyles.IndexOf('<PathGeometry x:Key="Icon', [StringComparison]::Ordinal) -ge 0) {
    Fail "ModernStyles.xaml still contains inline temporary icon geometries"
}
else {
    Pass "temporary icon geometries removed from ModernStyles.xaml"
}

$settingsIconStyleMatch = [regex]::Match(
    $modernStyles,
    '<Style x:Key="SettingsCardIconPathStyle"[\s\S]*?</Style>',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
)

if (-not $settingsIconStyleMatch.Success) {
    Fail "SettingsCardIconPathStyle missing"
}
elseif ($settingsIconStyleMatch.Value -notmatch 'Property="Width"\s+Value="2[0-9]"' -or $settingsIconStyleMatch.Value -notmatch 'Property="Height"\s+Value="2[0-9]"') {
    Fail "SettingsCardIconPathStyle is not bounded to a compact Material icon size"
}
else {
    Pass "Settings card icons have compact bounded dimensions"
}

$iconGlyphNeedles = @('material-icons', 'MaterialIcons', 'FontAwesome', '.woff', '.ttf')
$iconGlyphLeaks = @()
foreach ($needle in $iconGlyphNeedles) {
    if ($modernStyles.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $appXaml.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $productsView.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $mainWindow.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $posView.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $iconGlyphLeaks += $needle
    }
}

if ($iconGlyphLeaks.Count -gt 0) {
    Fail "font/icon package or glyph fallback references found: $($iconGlyphLeaks -join ', ')"
}
else {
    Pass "icons remain vector geometry resources without font/package fallbacks"
}

Test-ContainsAll "disabled button contrast resources" $modernStyles @(
    'x:Key="DisabledButtonBackgroundBrush"',
    'x:Key="DisabledButtonBorderBrush"',
    'x:Key="DisabledButtonForegroundBrush"',
    'x:Key="FooterDisabledButtonBackgroundBrush"',
    'x:Key="FooterDisabledButtonForegroundBrush"',
    'x:Key="FooterSecondaryButtonStyle"'
)

$buttonTemplateMatch = [regex]::Match(
    $modernStyles,
    '<ControlTemplate x:Key="RoundedButtonTemplate"[\s\S]*?</ControlTemplate>',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
)
$lowOpacityDisabledButton = $buttonTemplateMatch.Success -and [regex]::IsMatch(
    $buttonTemplateMatch.Value,
    '<Trigger Property="IsEnabled" Value="False">[\s\S]*?Opacity"\s+Value="0\.[0-5]"',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
)

if ($lowOpacityDisabledButton) {
    Fail "RoundedButtonTemplate still dims disabled buttons below readable contrast"
}
else {
    Pass "RoundedButtonTemplate keeps disabled buttons opaque"
}

Test-ContainsAll "Settings hub cards available" $settingsHub @(
    'SettingsHubDialog',
    'shell.officialShopData',
    'shell.printerSettings',
    'operations.dbMaintenance',
    'shell.usersRoles',
    'settings.language',
    'shell.aboutSupport'
)

Test-ContainsAll "Settings hub language panel is not clipped in card grid" $settingsHub @(
    '<ScrollViewer Grid.Row="1"',
    'x:Name="SettingsLanguagePanel"',
    'Width="220"',
    'Height="640"'
)

if ($settingsHub.IndexOf('<UniformGrid Grid.Row="1" Columns="2" Rows="3">', [StringComparison]::Ordinal) -ge 0) {
    Fail "Settings hub still uses the clipped 2x3 UniformGrid layout"
}
else {
    Pass "Settings hub avoids clipped 2x3 layout"
}

Test-ContainsAll "POS footer uses readable disabled button style" $posView @(
    'SuspendCartCommand',
    'RecoverCartCommand',
    'ClearCartCommand',
    'FooterSecondaryButtonStyle'
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
