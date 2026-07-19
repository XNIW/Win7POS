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
$productsWorkflow = Read-Text "src/Win7POS.Wpf/Products/ProductsWorkflowService.cs"
$productRepository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$mainWindowCode = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$settingsHub = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
$languageDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/LanguageSettingsDialog.xaml"
$operatorSwitch = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml"
$posView = Read-Text "src/Win7POS.Wpf/Pos/PosView.xaml"
$paymentView = Read-Text "src/Win7POS.Wpf/Pos/PaymentView.xaml"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$salesRegister = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterDialog.xaml"
$salesRegisterViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterViewModel.cs"
$dailyReportView = Read-Text "src/Win7POS.Wpf/Pos/DailyReportView.xaml"
$dailyReportViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DailyReportViewModel.cs"
$printerSettings = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$syncCenter = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml"
$syncCenterCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml.cs"
$syncCenterViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterViewModel.cs"
$startOfDayDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosStartOfDaySyncDialog.xaml"
$refundDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/RefundDialog.xaml"
$refundDialogCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/RefundDialog.xaml.cs"
$supplierImport = Read-Text "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml"
$supplierImportViewModel = Read-Text "src/Win7POS.Wpf/Import/SupplierExcelImportViewModel.cs"
$importView = Read-Text "src/Win7POS.Wpf/Import/ImportView.xaml"

Test-ContainsAll "MainWindow remains a maximize-only workstation shell" ($mainWindow + $mainWindowCode) @(
    'WindowState="Maximized"',
    'ResizeMode="CanMinimize"',
    'protected override void OnStateChanged(EventArgs e)',
    'if (WindowState == WindowState.Normal)',
    'SetCurrentValue(WindowStateProperty, WindowState.Maximized)'
)

Test-ContainsAll "ModernTextBoxStyle readable text/caret/selection" $modernStyles @(
    'x:Key="ModernTextBoxStyle"',
    'Foreground',
    'CaretBrush',
    'SelectionBrush'
)

Test-ContainsAll "Modern ComboBox selected value honors DisplayMemberPath" $modernStyles @(
    'x:Name="ContentSite"',
    'Content="{TemplateBinding SelectionBoxItem}"',
    'ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"'
)

Test-ContainsAll "Sales Register operator filter exposes its friendly label" $salesRegister @(
    'ItemsSource="{Binding OperatorFilterList}"',
    'DisplayMemberPath="DisplayName"'
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
    'x:Key="IconExpandMore"',
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

Test-ContainsAll "shared semantic design tokens" $modernStyles @(
    'x:Key="Spacing4"',
    'x:Key="Spacing8"',
    'x:Key="Spacing12"',
    'x:Key="Spacing16"',
    'x:Key="Spacing24"',
    'x:Key="Spacing32"',
    'x:Key="RadiusSmall"',
    'x:Key="RadiusMedium"',
    'x:Key="RadiusLarge"',
    'x:Key="ButtonHeightCompact"',
    'x:Key="ButtonHeightStandard"',
    'x:Key="ButtonHeightTouch"',
    'x:Key="TypeTitleStyle"',
    'x:Key="TypeSubtitleStyle"',
    'x:Key="TypeBodyStyle"',
    'x:Key="TypeCaptionStyle"',
    'x:Key="FocusBrush"',
    'x:Key="StatusSuccessBrush"',
    'x:Key="StatusWarningBrush"',
    'x:Key="StatusErrorBrush"',
    'x:Key="StatusInfoBrush"'
)

Test-ContainsAll "modern app-wide ComboBox template" $modernStyles @(
    'x:Key="ModernComboBoxStyle"',
    'x:Key="ModernComboBoxItemStyle"',
    'x:Key="ModernComboBoxEditableTextBoxStyle"',
    'PART_EditableTextBox',
    'IconExpandMore',
    '<Style TargetType="{x:Type ComboBox}" BasedOn="{StaticResource ModernComboBoxStyle}"/>'
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

Test-ContainsAll "shared button keyboard focus ring" $buttonTemplateMatch.Value @(
    'Property="IsKeyboardFocused"',
    'Property="BorderBrush" Value="{StaticResource FocusBrush}"',
    'Property="BorderThickness" Value="2"'
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

Test-ContainsAll "Settings hub language is a normal card" $settingsHub @(
    'Height="620"',
    'Click="OnLanguageClick"',
    'settings.cardLanguageHelp',
    'Click="OnSyncCenterClick"',
    'settings.cardSyncCenterHelp'
)

Test-ContainsAll "Sync Center has textual status, safe actions and automation names" ($syncCenter + $syncCenterCode + $syncCenterViewModel) @(
    'sync.center.status',
    'sync.center.lastUpdated',
    'sync.center.syncNow',
    'sync.center.retryCheckpoint',
    'sync.center.fullRepair',
    'sync.center.copyDiagnostics',
    'AutomationProperties.Name',
    'ApplyConfirmDialog.ShowConfirm',
    '_fullRepairRunning'
)

Test-ContainsAll "primary surfaces keep responsive layout markers" ($productsView + $paymentView + $posView) @(
    '<WrapPanel Grid.Row="1"',
    'EnableRowVirtualization="True"',
    '<ColumnDefinition Width="1.05*"/>',
    '<ColumnDefinition Width="1.55*"/>',
    'x:Name="FooterPayButton"',
    'Style="{StaticResource PrimaryButtonStyle}"'
)

Test-ContainsAll "start-of-day progress is announced to assistive technology" $startOfDayDialog @(
    'AutomationProperties.LiveSetting="Assertive"',
    'AutomationProperties.LiveSetting="Polite"',
    'AutomationProperties.Name="{loc:Loc startOfDay.checking}"'
)

if ($settingsHub.IndexOf('SettingsLanguagePanel', [StringComparison]::Ordinal) -ge 0 -or
    $settingsHub.IndexOf('LanguageComboBox', [StringComparison]::Ordinal) -ge 0) {
    Fail "Settings hub still contains the clipped inline language selector"
}
elseif ($settingsHub.IndexOf('<ScrollViewer Grid.Row="1"', [StringComparison]::Ordinal) -lt 0) {
    Fail "Settings hub is missing the low-height safety ScrollViewer"
}
else {
    Pass "Settings hub has scroll-safe cards and no clipped inline language selector"
}

Test-ContainsAll "Language settings dialog follows dialog resource pattern" $languageDialog @(
    'LanguageSettingsDialog',
    'WindowStartupLocation="CenterOwner"',
    'x:Name="LanguageComboBox"',
    'DialogActionButtonStyle',
    'DialogCancelButtonStyle',
    'DialogFooterMargin'
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

Test-ContainsAll "POS checkout footer stays compact and single-line" $posView @(
    'x:Name="PosCheckoutFooter"',
    'AutomationProperties.AutomationId="Pos.CheckoutFooter"',
    'Padding="12,10"',
    'x:Name="PosToolActionsPanel"',
    'x:Name="FooterTotalPanel"',
    'x:Name="FooterPayButton"',
    'Width="{Binding ActualWidth, ElementName=PosToolActionsPanel}"',
    'VerticalAlignment="Center"'
)

if ($posView.IndexOf('Grid.Row="1" Grid.Column="4"', [StringComparison]::Ordinal) -ge 0 -or
    $posView.IndexOf('Grid.Row="1" Grid.Column="5"', [StringComparison]::Ordinal) -ge 0) {
    Fail "POS checkout footer still splits total/payment onto a second visual row"
}
else {
    Pass "POS checkout footer aligns actions, total and payment on one visual row"
}

if ($posView.IndexOf('Background="{StaticResource PrimaryLighterBrush}" Foreground="White"', [StringComparison]::Ordinal) -ge 0) {
    Fail "POS pay button locally overrides disabled-state colors"
}
else {
    Pass "POS pay button inherits enabled and disabled colors from PrimaryButtonStyle"
}

Test-ContainsAll "Products searchable filters" $productsView @(
    'ApplyFiltersCommand',
    'FilteredSuppliers',
    'FilteredCategories',
    'SupplierFilterText',
    'CategoryFilterText',
    'IsEditable="True"',
    'StaysOpenOnEdit="True"'
)

Test-ContainsAll "Products catalog stats are surfaced in header" $productsView @(
    'CatalogStatsChips',
    'CatalogStatChipStyle',
    'ResultSummary'
)

Test-ContainsAll "Products catalog stats are queried globally" $productRepository @(
    'ProductCatalogStats',
    'GetCatalogStatsAsync',
    'TotalProducts',
    'TotalCategories',
    'TotalSuppliers',
    'TotalStockUnits'
)

Test-ContainsAll "Products stats refresh with catalog changes" ($productsViewModel + $productsWorkflow) @(
    'RefreshCatalogStatsAsync',
    'CatalogStatsChips',
    'GetCatalogStatsAsync'
)

Test-ContainsAll "Products filter state kept client-side until apply" $productsViewModel @(
    'ApplyFiltersCommand',
    'ResolveTypedFilterSelections',
    'RefreshFilteredSuppliers',
    'RefreshFilteredCategories',
    'ClearFiltersCommand'
)

Test-ContainsAll "Operator switch uses manual staff code" $operatorSwitch @(
    'x:Name="StaffCodeBox"',
    'operator.switch.staffCode',
    'operator.login.pin',
    'operator.switch.posAccess'
)

if ($operatorSwitch.IndexOf('OperatorCombo', [StringComparison]::Ordinal) -ge 0) {
    Fail "Operator switch still forces an operator ComboBox"
}
else {
    Pass "Operator switch no longer requires selecting from a list"
}

Test-ContainsAll "Shell title is bound to shop title property" ($mainWindow + $mainWindowCode) @(
    'ShellTitle',
    'ShopOfficialSnapshotRepository',
    'GetLastPosLoginShopCodeAsync'
)

if ($mainWindow.IndexOf('Text="Win7POS"', [StringComparison]::Ordinal) -ge 0) {
    Fail "MainWindow header still hardcodes Text=`"Win7POS`""
}
else {
    Pass "MainWindow header is not hardcoded to Win7POS"
}

Test-ContainsAll "POS status is shown as toast, not footer line" ($posView + $posViewModel) @(
    'IsStatusToastVisible',
    'StatusToastMessage',
    'PosNoticeSeverity',
    'PosNoticePolicy.GetAutoDismissDelay',
    'DismissStatusToastCommand'
)

Test-ContainsAll "Sales Register exposes a lazy selectable receipt preview" ($salesRegister + $salesRegisterViewModel) @(
    'sales.previewTab',
    'DetailReceiptPreview',
    'IsPreviewLoading',
    'sales.previewEmpty',
    'FontFamily="Consolas"',
    'IsReadOnly="True"',
    'VirtualizingPanel.IsVirtualizing="True"',
    'VirtualizingPanel.VirtualizationMode="Recycling"'
)

Test-ContainsAll "Daily Close uses an 80mm receipt-paper preview" ($dailyReportView + $dailyReportViewModel) @(
    'AutomationProperties.AutomationId="DailyCloseReceiptPreview"',
    'SummaryReceiptPreview',
    'FontFamily="Consolas"',
    'IsReadOnly="True"',
    'Background="#FFFDF8"',
    'PrintReceiptTextAsync(SummaryReceiptPreview'
)

Test-ContainsAll "Printer Settings explains receipt history storage" $printerSettings @(
    'x:Name="ReceiptHistoryStorageInfo"',
    'printer.receiptHistoryStorageInfo'
)

if ($posView.IndexOf('Text="{Binding StatusMessage}"', [StringComparison]::Ordinal) -ge 0) {
    Fail "POS view still binds StatusMessage as a persistent footer line"
}
else {
    Pass "POS initialized/status message is not permanently bound in the footer"
}

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

Test-ContainsAll "Refund confirmation is shared by mouse and keyboard" $refundDialogCode @(
    'TryConfirmWithPrompt()',
    'if (TryConfirmWithPrompt())',
    'refund.confirmVoidTitle',
    'refund.confirmVoidMessage'
)

if ($refundDialogCode.IndexOf('if (ViewModel.IsValid && ViewModel.TryConfirm())', [StringComparison]::Ordinal) -ge 0) {
    Fail "Refund Enter path still bypasses the full-void confirmation prompt"
}
else {
    Pass "Refund Enter path cannot bypass full-void confirmation"
}

Test-ContainsAll "Refund remains keyboard-accessible and work-area adaptable" ($refundDialog + $refundDialogCode) @(
    'ResizeMode="CanResize"',
    'MinWidth="720"',
    'HorizontalScrollBarVisibility="Auto"',
    'CardStorno_KeyDown',
    'CardReso_KeyDown',
    'IsKeyboardFocusWithin',
    'AutomationProperties.Name="{loc:Loc refund.fullVoid}"',
    'AutomationProperties.Name="{loc:Loc refund.partialReturn}"',
    'ApplyAdaptiveDialogSizing'
)

Test-ContainsAll "POS modal exits restore scanner focus" $posViewModel @(
    'private async Task RecoverCartAsync()',
    'private async void OpenEditProductExecute(object parameter)',
    'private void OpenChangeQuantity()',
    'private async void OpenDiscount()',
    'finally',
    'RequestFocusBarcode();'
)

$legacyImportFormats = @(
    'StringFormat=Items count:',
    'StringFormat=DB:',
    'StringFormat=Foglio',
    'StringFormat=Nuovi:',
    'StringFormat=Aggiornati:',
    'StringFormat=Totale:',
    'StringFormat=Senza modifiche:',
    'StringFormat=Skippati:'
)
$legacyImportLeaks = @()
foreach ($needle in $legacyImportFormats) {
    if ($importView.IndexOf($needle, [StringComparison]::Ordinal) -ge 0 -or
        $supplierImport.IndexOf($needle, [StringComparison]::Ordinal) -ge 0) {
        $legacyImportLeaks += $needle
    }
}
if ($legacyImportLeaks.Count -gt 0) {
    Fail "Reachable import XAML still contains hardcoded summary labels: $($legacyImportLeaks -join ', ')"
}
else {
    Pass "Reachable import summary labels use EN/ES/IT/ZH localization keys"
}

Test-ContainsAll "Supplier import status path uses localization keys" $supplierImportViewModel @(
    'supplierExcelImport.statusChooseFile',
    'supplierExcelImport.statusAnalysisComplete',
    'supplierExcelImport.recalculateBeforeApply',
    'supplierExcelImport.statusSyncReady',
    'supplierExcelImport.markupApplied',
    'supplierExcelImport.filePickerTitle'
)

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
