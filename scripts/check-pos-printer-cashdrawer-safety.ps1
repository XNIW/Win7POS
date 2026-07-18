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

function Get-MethodBody([string]$text, [string]$signaturePattern) {
    $signature = [regex]::Match($text, $signaturePattern)
    if (-not $signature.Success) {
        return ""
    }

    $bodyStart = $text.IndexOf("{", $signature.Index + $signature.Length, [StringComparison]::Ordinal)
    if ($bodyStart -lt 0) {
        return ""
    }

    $depth = 0
    for ($i = $bodyStart; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq "{") {
            $depth++
        }
        elseif ($text[$i] -eq "}") {
            $depth--
            if ($depth -eq 0) {
                return $text.Substring($signature.Index, ($i - $signature.Index) + 1)
            }
        }
    }

    return ""
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
    "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs",
    "src/Win7POS.Core/Models/Sale.cs",
    "src/Win7POS.Data/DbInitializer.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
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
$saleModel = Read-Text "src/Win7POS.Core/Models/Sale.cs"
$dbInitializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$uiSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$combined = $discovery + $spooler + $dialog + $dialogVm + $workflow + $posVm + $keys + $translations + $uiSmoke

if ($discovery -notmatch "PrinterSettings\.InstalledPrinters" -or $discovery -notmatch "IsLikelyVirtualPrinter" -or $discovery -notmatch "GetDefaultPrinterName") {
    Fail "Win7-safe printer discovery/default/virtual detection missing"
} else {
    Pass "Win7-safe printer discovery/default/virtual detection present"
}

if ($dialog -notmatch "InstalledPrinters" -or $dialog -notmatch "printer\.receiptPrinter" -or $dialog -notmatch "printer\.(testPrint|printTestReceipt)") {
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

$payBody = Get-MethodBody $posVm 'private\s+async\s+Task\s+PayAsync\s*\(\s*\)'
$completeSaleIndex = $payBody.IndexOf("CompleteSaleAsync", [StringComparison]::Ordinal)
$applySnapshotIndex = $payBody.IndexOf("ApplySnapshot", [StringComparison]::Ordinal)
$autoDrawerIndex = $payBody.IndexOf("TryAutoOpenDrawerAfterPaymentAsync", [StringComparison]::Ordinal)
$printReceiptIndex = $payBody.IndexOf("PrintReceiptAsync", $autoDrawerIndex + 1, [StringComparison]::Ordinal)
if ([string]::IsNullOrWhiteSpace($payBody) -or
    $completeSaleIndex -lt 0 -or
    $applySnapshotIndex -le $completeSaleIndex -or
    $autoDrawerIndex -le $applySnapshotIndex -or
    $printReceiptIndex -le $autoDrawerIndex) {
    Fail "payment flow must commit/apply sale before drawer and receipt printing"
}
else {
    Pass "payment flow commits and applies sale before drawer/print"
}

$drawerBody = Get-MethodBody $posVm 'private\s+async\s+Task\s+TryAutoOpenDrawerAfterPaymentAsync\s*\(\s*PaymentViewModel\s+vm\s*\)'
$drawerOpenIndex = $drawerBody.IndexOf("OpenCashDrawerAsync", [StringComparison]::Ordinal)
if ([string]::IsNullOrWhiteSpace($drawerBody) -or
    $drawerBody -notmatch 'if\s*\(\s*!IsCashDrawerConfigured\s*\)\s*return' -or
    $drawerBody -notmatch 'if\s*\(\s*!vm\.OpenDrawerForCurrentPayment\s*\)\s*return' -or
    $drawerBody -notmatch 'if\s*\(\s*vm\.CashAmountMinor\s*<=\s*0\s*\)\s*return' -or
    $drawerOpenIndex -lt 0) {
    Fail "automatic drawer path must require configured/enabled drawer and cash amount > 0"
}
else {
    Pass "cash amount > 0 is required; card-only payment cannot auto-open drawer"
}

if ($drawerBody -notmatch 'try[\s\S]*OpenCashDrawerAsync[\s\S]*catch\s*\(' -or
    $drawerBody -match 'catch\s*\([^\)]*\)\s*\{[^\}]*\bthrow\b') {
    Fail "drawer failure must be reported without escaping into the committed-sale path"
}
else {
    Pass "drawer failure is isolated after sale commit"
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

if ($spooler -notmatch "VisibleClipBounds" -or
    $spooler -notmatch "CreateColumnFittedFont" -or
    $spooler -notmatch "opt\.CharactersPerLine" -or
    $uiSmoke -notmatch "VerifyReceiptColumnFit") {
    Fail "receipt renderer must fit declared columns to the real printable graphics bounds"
} else {
    Pass "receipt renderer fits declared columns to the real printable graphics bounds"
}

$testReceiptBody = Get-MethodBody $workflow 'private\s+static\s+string\s+BuildPrinterTestReceipt\s*\('
if ([string]::IsNullOrWhiteSpace($testReceiptBody) -or
    $testReceiptBody -notmatch 'new\s+Sale' -or
    $testReceiptBody -notmatch 'BuildReceiptPreview' -or
    $testReceiptBody -match 'CompleteSale|SaveAsync|Insert|_sales' -or
    $dialog -notmatch 'TestReceiptPreview' -or
    $dialogVm -notmatch 'TestReceiptPreview' -or
    $posVm -notmatch 'TestReceiptPrinterAsync[\s\S]{0,160}vm\.TestReceiptPreview' -or
    $workflow -notmatch 'SaleCodeForBarcode\s*=\s*"TEST-NO-SALE"') {
    Fail "printer test must preview and print the same non-persisted fictitious receipt"
} else {
    Pass "printer test previews and prints the same non-persisted fictitious receipt with sale barcode"
}

if ($workflow -match 'ReceiptShopInfo\s+receiptShopSnapshot\s*=\s*null' -and
    $workflow -match 'receiptShopSnapshot\s*\?\?' -and
    $posVm -match 'CompleteSaleAsync\([\s\S]{0,240}draft\.ShopInfo') {
    Pass "payment freezes the shop snapshot used by completed-sale receipt output"
} else {
    Fail "payment preview and completed-sale receipt must use the same shop snapshot"
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

$drawerParseBody = Get-MethodBody $spooler 'private\s+static\s+byte\[\]\s+ParseCashDrawerCommand\s*\('
$strictDrawerParser =
    -not [string]::IsNullOrWhiteSpace($drawerParseBody) -and
    $drawerParseBody -match 'StringSplitOptions\.None' -and
    $drawerParseBody -match 'byte\.TryParse' -and
    $drawerParseBody -match 'list\.Count\s*!=\s*5' -and
    $drawerParseBody -match 'list\[0\]\s*!=\s*EscPosEscape' -and
    $drawerParseBody -match 'list\[1\]\s*!=\s*EscPosPulse' -and
    $drawerParseBody -match 'list\[2\]\s*!=\s*0\s*&&\s*list\[2\]\s*!=\s*1\s*&&\s*list\[2\]\s*!=\s*48\s*&&\s*list\[2\]\s*!=\s*49' -and
    $drawerParseBody -match 'list\[3\]\s*>=\s*list\[4\]' -and
    $drawerParseBody -match 'throw\s+InvalidCashDrawerCommand' -and
    $drawerParseBody -match 'string\.IsNullOrWhiteSpace\s*\(cmd\)[\s\S]{0,120}throw\s+InvalidCashDrawerCommand' -and
    $drawerParseBody -notmatch 'list\.Count\s*>\s*0\s*\?'
if ($strictDrawerParser) {
    Pass "cash-drawer parser rejects malformed/non-ESC-p commands without fallback"
} else {
    Fail "cash-drawer parser must validate every byte and reject malformed non-empty input"
}

if ($spooler -match 'DefaultCashDrawerCommand' -or
    $spooler -match 'string\.IsNullOrWhiteSpace\s*\(opt\.CashDrawerCommand\)[\s\S]{0,120}\?') {
    Fail "drawer runtime must not turn a blank command into a physical default pulse"
}
elseif ($workflow -notmatch 'cashDrawerActive\s*=\s*settings\.CashDrawerEnabled' -or
        $workflow -notmatch 'cashDrawerActive[\s\S]{0,260}IsCashDrawerCommandValid' -or
        $posVm -match 'CashDrawerCommand\s*=\s*string\.IsNullOrWhiteSpace') {
    Fail "drawer settings must validate an active command before persistence without a UI blank fallback"
}
else {
    Pass "blank drawer commands are rejected at runtime and before active-setting persistence"
}

if ($spooler -match 'PrintAttemptTimeoutMilliseconds\s*=\s*[1-9]\d*' -and
    $spooler -match 'Task\.WhenAny\s*\([\s\S]{0,240}Task\.Delay\s*\(PrintAttemptTimeoutMilliseconds\)' -and
    $spooler -match 'catch\s*\(TimeoutException\)[\s\S]{0,240}throw' -and
    $workflow -match 'var installedPrinters = await GetInstalledPrintersAsync\(\)' -and
    $workflow -match '_gate\.Release\(\);[\s\S]{0,300}await _receiptPrinter\.PrintAsync') {
    Pass "spooler attempts are bounded and physical print runs after the POS gate is released"
}
else {
    Fail "spooler print must have a bounded no-retry timeout outside the POS gate"
}

if ($saleModel -match 'ReceiptShopSnapshotJson' -and
    $dbInitializer -match 'receipt_shop_snapshot' -and
    $saleRepository -match 'receipt_shop_snapshot' -and
    $workflow -match 'SerializeReceiptShopSnapshot' -and
    $workflow -match 'GetReceiptShopInfoNoLockAsync' -and
    $uiSmoke -match 'VerifyReceiptShopSnapshotReprintAsync') {
    Pass "historical receipt reprints use a persisted immutable shop snapshot"
}
else {
    Fail "receipt shop snapshot persistence/reprint coverage missing"
}

$taskBasedTestEvents =
    $dialogVm -match 'event\s+Func<Task>\s+TestPrintRequested' -and
    $dialogVm -match 'event\s+Func<string,\s*string,\s*Task>\s+TestCashDrawerRequested' -and
    $posVm -match 'Func<Task>\s+testPrintHandler' -and
    $posVm -match 'Func<string,\s*string,\s*Task>\s+testCashDrawerHandler' -and
    $dialogVm -notmatch 'event\s+Action[^;\r\n]*Test(Print|CashDrawer)Requested'
if ($taskBasedTestEvents) {
    Pass "printer/drawer test callbacks are Task-based"
} else {
    Fail "printer/drawer test callbacks must not use async-void Action handlers"
}

$singleFlightTests =
    $dialogVm -match 'IsTestOperationInProgress' -and
    $dialogVm -match 'RunTestOperationAsync' -and
    $dialogVm -match '!IsTestOperationInProgress' -and
    $dialogVm -match 'ActiveTestOperation' -and
    $uiSmoke -match 'printSingleFlight\s*=\s*printCalls\s*==\s*1' -and
    $uiSmoke -match 'drawerSingleFlight\s*=\s*drawerCalls\s*==\s*1' -and
    $uiSmoke -match 'await\s+vm\.ActiveTestOperation'
if ($singleFlightTests) {
    Pass "test print/drawer commands are disabled and smoke-tested as single-flight"
} else {
    Fail "test print/drawer commands require single-flight disable-until-complete coverage"
}

if ($uiSmoke -match 'VerifyCashDrawerCommandParsing' -and
    $uiSmoke -match 'string\.Empty' -and
    $uiSmoke -match '27,,112,0,25,250' -and
    $uiSmoke -match '27,112,0,25,256' -and
    $uiSmoke -match '27,112,2,25,250' -and
    $uiSmoke -match '27,112,49,0,255' -and
    $uiSmoke -match '27,112,0,25,25') {
    Pass "UI smoke covers malformed, out-of-range and invalid-pin drawer commands"
} else {
    Fail "UI smoke must cover strict cash-drawer command rejection"
}

$trustedSeedIsFailClosed =
    $uiSmoke -match 'HasArg\(args,\s*"--seed-trusted-session"\)' -and
    $uiSmoke -match 'EnsureSyntheticTrustedSessionSeedPath\(dataDir\)' -and
    $uiSmoke -match 'Path\.IsPathRooted\(dataDir\)' -and
    $uiSmoke -match '"Win7POS-QA"' -and
    $uiSmoke -match 'Directory\.EnumerateFileSystemEntries\(fullPath\)\.Any\(\)' -and
    $uiSmoke -match 'PosOnlineContract\.OfflineAuthorizationMaxAgeSeconds'
if ($trustedSeedIsFailClosed) {
    Pass "synthetic trusted-session seed is explicit and restricted to a new Win7POS-QA directory"
} else {
    Fail "synthetic trusted-session seed must be explicit and fail closed outside a new Win7POS-QA directory"
}

if ($translations -match 'printer\.testInvalidCommand' -and
    $dialogVm -match 'IsCashDrawerCommandValid' -and
    $dialogVm -match 'IsValid\s*=>\s*ParsedCopies\s*>=\s*1\s*&&\s*IsCashDrawerCommandValid' -and
    $dialogVm -match 'PosLocalization\.T\("printer\.testInvalidCommand"\)' -and
    $uiSmoke -match '!vm\.IsCashDrawerCommandValid\s*&&\s*!vm\.IsValid') {
    Pass "invalid drawer command is localized and blocks settings confirmation"
} else {
    Fail "invalid drawer command must show a localized error and block settings save"
}

if ($combined -match "(?i)sk-[a-z0-9]|api[_-]?key\s*=|secret\s*=|(?<![A-Za-z0-9_])token\s*=|password\s*=|BEGIN (RSA |OPENSSH |EC )?PRIVATE KEY") {
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
