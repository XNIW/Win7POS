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

function Read-Required([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "$relativePath missing"
        return ""
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require-All([string]$label, [string]$text, [string[]]$markers) {
    $missing = @($markers | Where-Object { $text.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
    if ($missing.Count -gt 0) { Fail "$label missing: $($missing -join ', ')" }
    else { Pass $label }
}

function Forbid([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

$windowXaml = Read-Required "src/Win7POS.Wpf/Pos/CustomerDisplay/CustomerDisplayWindow.xaml"
$windowCode = Read-Required "src/Win7POS.Wpf/Pos/CustomerDisplay/CustomerDisplayWindow.xaml.cs"
$manager = Read-Required "src/Win7POS.Wpf/Pos/CustomerDisplay/CustomerDisplayManager.cs"
$viewModel = Read-Required "src/Win7POS.Wpf/Pos/CustomerDisplay/CustomerDisplayViewModel.cs"
$placement = Read-Required "src/Win7POS.Wpf/Infrastructure/Displays/PhysicalWindowPlacement.cs"
$provider = Read-Required "src/Win7POS.Wpf/Infrastructure/Displays/WindowsDisplayTopologyProvider.cs"
$monitorPolicy = Read-Required "src/Win7POS.Core/Pos/CustomerDisplayMonitorPolicy.cs"
$snapshot = Read-Required "src/Win7POS.Core/Pos/CustomerDisplaySnapshot.cs"
$projection = Read-Required "src/Win7POS.Core/Pos/CustomerDisplayProjection.cs"
$mainWindow = Read-Required "src/Win7POS.Wpf/MainWindow.xaml.cs"
$app = Read-Required "src/Win7POS.Wpf/App.xaml.cs"
$exitDialog = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/ExitConfirmationDialog.xaml"
$settingsDialog = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/CustomerDisplaySettingsDialog.xaml"
$settingsHub = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml"
$settingsHubCode = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SettingsHubDialog.xaml.cs"
$translations = Read-Required "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"

if ($windowXaml.TrimStart().StartsWith("<Window ", [StringComparison]::Ordinal) -and
    $windowXaml.IndexOf("DialogShellWindow", [StringComparison]::Ordinal) -lt 0) {
    Pass "CustomerDisplayWindow is a dedicated non-dialog Window"
}
else { Fail "CustomerDisplayWindow must not derive from DialogShellWindow" }

Require-All "customer window no-activate/taskbar contract" ($windowXaml + $windowCode + $placement) @(
    'WindowStyle="None"',
    'ResizeMode="NoResize"',
    'ShowInTaskbar="False"',
    'ShowActivated="False"',
    'Focusable="False"',
    'WS_EX_NOACTIVATE',
    'WS_EX_TOOLWINDOW',
    'ApplyNoActivateToolWindow',
    'SetWindowPos'
)

Require-All "Win7 display topology and physical placement" ($provider + $placement + $monitorPolicy) @(
    'Screen.AllScreens',
    'WorkingArea',
    'BitsPerPixel',
    'monitor.BoundsLeft',
    'monitor.BoundsTop',
    'CustomerDisplayTopologyMode.Duplicate',
    'extend_required',
    'same_monitor',
    'selected_monitor_missing'
)

Forbid "no Win8/10-only per-monitor DPI APIs" ($windowCode + $manager + $placement + $provider) '(?i)GetDpiForWindow|GetDpiForMonitor|AdjustWindowRectExForDpi|SetThreadDpiAwarenessContext'
Forbid "customer display has no direct SQL" ($manager + $viewModel + $windowCode) '(?i)SELECT\s|INSERT\s+INTO|UPDATE\s+app_settings|DELETE\s+FROM|Microsoft\.Data\.Sqlite|Dapper'
Forbid "customer display has no direct HTTP" ($manager + $viewModel + $windowCode) '(?i)HttpClient|HttpWebRequest|WebRequest|https?://'
Forbid "public display DTO excludes sensitive fields" ($snapshot + $projection) '(?i)StockQty|PurchasePrice|Margin|Supplier|OperatorName|RemoteId|Outbox|Token|Password|Credential'

Require-All "SystemEvents lifecycle is centralized and cleaned" $manager @(
    'SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged',
    'SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged',
    'Interval = TimeSpan.FromMilliseconds(750)',
    'public void Dispose()',
    'CloseDisplay()'
)

Require-All "safe exit confirmation and programmatic bypass" ($mainWindow + $app + $exitDialog) @(
    'protected override void OnClosing(CancelEventArgs e)',
    'ExitConfirmationDialog',
    'CloseWithoutUserPrompt()',
    'PrepareForSessionEnding()',
    'protected override void OnSessionEnding(SessionEndingCancelEventArgs e)',
    'ExitConfirmationChoice.Minimize',
    'ExitConfirmationChoice.CloseApplication'
)

Require-All "settings surface and four-language copy are present" ($settingsDialog + $settingsHub + $settingsHubCode + $translations) @(
    'CustomerDisplaySettingsDialog',
    'WindowStartupLocation="CenterOwner"',
    'DialogActionButtonStyle',
    'DialogCancelButtonStyle',
    'DialogFooterMargin',
    'CustomerDisplayRequested',
    'customerDisplay.settings.title',
    'Display cliente',
    'Pantalla cliente',
    '顾客显示屏'
)

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
