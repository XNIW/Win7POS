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
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Require-File([string]$relativePath) {
    if (-not (Test-Path (Join-Path $repoRoot $relativePath))) {
        Fail "$relativePath missing"
    }
}

$required = @(
    "src/Win7POS.Wpf/Printing/InstalledPrinterInfo.cs",
    "src/Win7POS.Wpf/Printing/WindowsPrinterDiscovery.cs",
    "src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs",
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs",
    "src/Win7POS.Wpf/Pos/PosViewModel.cs",
    "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
)

foreach ($path in $required) {
    Require-File $path
}

if ($fail) {
    exit 1
}

$discovery = Read-Text "src/Win7POS.Wpf/Printing/WindowsPrinterDiscovery.cs"
$spooler = Read-Text "src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$dialogVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$posVm = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$keys = Read-Text "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$combined = $discovery + $spooler + $dialog + $dialogVm + $workflow + $posVm + $keys + $translations

if ($discovery -notmatch "PrinterSettings\.InstalledPrinters" -or $discovery -notmatch "IsLikelyVirtualPrinter" -or $discovery -notmatch "GetDefaultPrinterName") {
    Fail "Win7-safe printer discovery/default/virtual detection missing"
} else {
    Pass "Win7-safe printer discovery/default/virtual detection present"
}

if ($dialog -notmatch "InstalledPrinters" -or $dialog -notmatch "printer\.receiptPrinter" -or $dialog -notmatch "printer\.testPrint") {
    Fail "printer settings UI must list installed printers, select receipt printer, and expose test print"
} else {
    Pass "printer settings UI exposes installed printers, receipt selection, and test print"
}

$requiredKeys = @(
    "pos.printer.receipt.enabled",
    "pos.printer.receipt.name",
    "pos.printer.receipt.auto_print_after_sale",
    "pos.printer.receipt.allow_windows_default",
    "pos.printer.receipt.allow_virtual_printers",
    "pos.cashdrawer.enabled",
    "pos.cashdrawer.mode",
    "pos.cashdrawer.printer_name",
    "pos.cashdrawer.open_on_cash_sale"
)

foreach ($key in $requiredKeys) {
    if ($keys -notmatch [regex]::Escape($key)) {
        Fail "missing app_settings key: $key"
    }
}

if (-not $fail) {
    Pass "printer/cashdrawer app_settings keys present"
}

if ($posVm -notmatch "CompleteSaleAsync[\s\S]{0,900}TryAutoOpenDrawerAfterPaymentAsync[\s\S]{0,900}PrintReceiptAsync") {
    Fail "payment flow must save sale before drawer/print"
} else {
    Pass "payment flow saves sale before drawer/print"
}

if ($posVm -notmatch "automaticAfterSale:\s*true" -or $workflow -notmatch "ResolveReceiptPrinterOrThrow") {
    Fail "automatic after-sale print must pass through configured-printer resolver"
} else {
    Pass "automatic after-sale print uses configured-printer resolver"
}

if ($keys -notmatch "allow_windows_default" -or $keys -notmatch "allow_virtual_printers" -or $workflow -notmatch "saleSavedVirtualPrinterBlocked") {
    Fail "default/virtual printer safety checks missing"
} else {
    Pass "default/virtual printer safety checks present"
}

if ($workflow -notmatch "ReceiptEnabled = receiptEnabled \?\? false" -or $workflow -notmatch "AutoPrint = autoPrint \?\? false" -or $workflow -notmatch "CashDrawerEnabled = cashDrawerEnabled \?\? false") {
    Fail "safe defaults for receipt auto-print/cashdrawer missing"
} else {
    Pass "safe defaults disable receipt auto-print and cashdrawer"
}

if ($spooler -match "doc\.PrinterSettings\.PrinterName\s*=\s*opt\.PrinterName[\s\S]{0,250}if\s*\(\s*!string\.IsNullOrWhiteSpace\(opt\.PrinterName\)") {
    Fail "spooler still conditionally falls back to Windows default printer"
} elseif ($spooler -notmatch "printer\.receiptPrinterNotConfigured" -or
    $translations -notmatch "Receipt printer is not configured" -or
    $spooler -notmatch "doc\.PrinterSettings\.PrinterName\s*=\s*opt\.PrinterName") {
    Fail "spooler must require explicit printer name"
} else {
    Pass "spooler requires explicit printer name"
}

if ($combined -match "new\s+PrintDialog|PrintDialog\s*\(|DefaultPrintQueue|LocalPrintServer") {
    Fail "interactive/default print path detected in automatic POS code"
} else {
    Pass "no interactive/default print path detected"
}

if ($combined -match "PrinterName\s*=\s*`"Microsoft Print to PDF`"" -or $combined -match "PrinterName\s*=\s*'Microsoft Print to PDF'") {
    Fail "Microsoft Print to PDF must not be hardcoded as printer target"
} else {
    Pass "Microsoft Print to PDF not hardcoded as printer target"
}

if ($translations -notmatch "printer\.saleSavedPrintWarning" -or $translations -notmatch "printer\.saleSavedPrinterNotConfigured" -or $translations -notmatch "printer\.saleSavedDrawerWarning") {
    Fail "sale-saved printer/drawer warning localization missing"
} else {
    Pass "sale-saved printer/drawer warning localization present"
}

if ($workflow -notmatch "ResolveCashDrawerPrinterOrThrow" -or $workflow -notmatch "allowWindowsDefault:\s*false" -or $workflow -notmatch "allowVirtualPrinters:\s*false") {
    Fail "cash drawer must require configured non-virtual printer"
} else {
    Pass "cash drawer requires configured non-virtual printer"
}

if ($dialogVm -notmatch "27,112,0,25,250" -or $workflow -notmatch "27,112,0,60,255|27,112,0,25,250") {
    Fail "ESC/POS drawer command handling missing"
} else {
    Pass "ESC/POS drawer command handling present"
}

if ($combined -match "(?i)sk-[a-z0-9]|api[_-]?key\s*=|secret\s*=|token\s*=|password\s*=|BEGIN (RSA |OPENSSH |EC )?PRIVATE KEY") {
    Fail "possible secret detected in printer/cashdrawer code"
} else {
    Pass "no secret-like printer/cashdrawer content detected"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
