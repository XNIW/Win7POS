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
    "src/Win7POS.Wpf/Printing/PrinterHardwareSafety.cs",
    "src/Win7POS.Wpf/Printing/ReceiptPrintOptions.cs",
    "src/Win7POS.Core/Receipt/ReceiptContentPolicy.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs",
    "src/Win7POS.Wpf/Pos/PosViewModel.cs",
    "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs",
    "src/Win7POS.Core/Models/Sale.cs",
    "src/Win7POS.Data/DbInitializer.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs",
    "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs",
    "scripts/start-offline-sales-qa.ps1",
    "docs/QA/WIN7POS_OFFLINE_SALES_SANDBOX.md"
)

foreach ($path in $required) {
    Require-File $path
}

if ($fail) {
    exit 1
}

$discovery = Read-Text "src/Win7POS.Wpf/Printing/WindowsPrinterDiscovery.cs"
$spooler = Read-Text "src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs"
$receiptContentPolicy = Read-Text "src/Win7POS.Core/Receipt/ReceiptContentPolicy.cs"
$hardwareSafety = Read-Text "src/Win7POS.Wpf/Printing/PrinterHardwareSafety.cs"
$printOptions = Read-Text "src/Win7POS.Wpf/Printing/ReceiptPrintOptions.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$dialogVm = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs"
$accessDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$posVm = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$keys = Read-Text "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$saleModel = Read-Text "src/Win7POS.Core/Models/Sale.cs"
$dbInitializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$saleTransactionWriter = Read-Text "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$uiSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$offlineQaLauncher = Read-Text "scripts/start-offline-sales-qa.ps1"
$offlineQaDocs = Read-Text "docs/QA/WIN7POS_OFFLINE_SALES_SANDBOX.md"
$combined = $discovery + $spooler + $dialog + $dialogVm + $workflow + $posVm + $keys + $translations + $uiSmoke + $offlineQaLauncher + $offlineQaDocs
$productionSecretScan = $discovery + $spooler + $hardwareSafety + $printOptions + $dialog + $dialogVm + $workflow + $posVm + $keys + $translations

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
    $drawerParseBody -match 'cmd\.Length\s*>\s*MaximumCashDrawerCommandLength' -and
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

if ($spooler -match 'MaximumCashDrawerCommandLength\s*=\s*64' -and
    $dialog -match '<TextBox\b(?=[^>]*MaxLength="64")(?=[^>]*Text="\{Binding\s+CashDrawerCommand\b)[^>]*>' -and
    $workflow -match 'MaximumCashDrawerCommandLength' -and
    $uiSmoke -match 'MaximumCashDrawerCommandLength\s*\+\s*1' -and
    $uiSmoke -match 'new\s+string\s*\(\s*''9''\s*,\s*10000\s*\)') {
    Pass "cash-drawer command length is bounded before parsing and covered at UI/runtime boundaries"
} else {
    Fail "cash-drawer command length must be capped at 64 before trim/split with oversized-input coverage"
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

$startEffectBody = Get-MethodBody $spooler 'private\s+static\s+Task\s+StartExclusivePrinterEffect\s*\('
$awaitEffectBody = Get-MethodBody $spooler 'private\s+static\s+async\s+Task\s+AwaitEffectWithinTimeoutAsync\s*\('
$spoolerPrintBody = Get-MethodBody $spooler 'public\s+async\s+Task\s+PrintAsync\s*\('
$spoolerDrawerBody = Get-MethodBody $spooler 'public\s+async\s+Task\s+OpenCashDrawerAsync\s*\('
if ($spooler -match 'PrintAttemptTimeoutMilliseconds\s*=\s*[1-9]\d*' -and
    $awaitEffectBody -match 'Task\.WhenAny[\s\S]*Task\.Delay\s*\(\s*timeoutMilliseconds\s*\)' -and
    $spooler -match 'PrinterEffectTails\s*=\s*[\s\S]{0,180}StringComparer\.OrdinalIgnoreCase' -and
    $spoolerPrintBody -match 'StartExclusivePrinterEffect' -and
    $spoolerDrawerBody -match 'StartExclusivePrinterEffect' -and
    $startEffectBody -match 'TryGetValue\s*\([\s\S]*!existing\.IsCompleted[\s\S]*throw\s+new\s+InvalidOperationException' -and
    $startEffectBody -match 'current\s*=\s*Task\.Run\s*\(\s*effect\s*\)' -and
    $startEffectBody -match 'ReferenceEquals\s*\(\s*tail\s*,\s*completedEffect\s*\)' -and
    $startEffectBody -notmatch 'predecessor[\s\S]*ContinueWith' -and
    $spooler -notmatch 'TryPrintWithRetryAsync|RetryDelayMs' -and
    $uiSmoke -match 'VerifySharedPrinterEffectCoordinatorAsync[\s\S]*QA expected timeout[\s\S]*secondStarted\.Task\.IsCompleted') {
    Pass "print/drawer effects use fail-closed per-printer single-flight with bounded indeterminate timeout coverage"
}
else {
    Fail "spooler effects must reject a second same-printer effect while an indeterminate tail exists"
}

$workflowHardwareMethods = @(
    @{ Name = "TestReceiptPrinterAsync"; Body = (Get-MethodBody $workflow 'public\s+async\s+Task\s+TestReceiptPrinterAsync\s*\(') },
    @{ Name = "PrintReceiptTextAsync"; Body = (Get-MethodBody $workflow 'public\s+async\s+Task<PosPrintResult>\s+PrintReceiptTextAsync\s*\(') },
    @{ Name = "OpenCashDrawerAsync"; Body = (Get-MethodBody $workflow 'public\s+async\s+Task\s+OpenCashDrawerAsync\s*\(') },
    @{ Name = "TestCashDrawerAsync"; Body = (Get-MethodBody $workflow 'public\s+async\s+Task\s+TestCashDrawerAsync\s*\(') }
)
$workflowHardwareGuardsPass = $true
foreach ($entry in $workflowHardwareMethods) {
    $guardIndex = $entry.Body.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal)
    $discoveryIndex = $entry.Body.IndexOf("GetInstalledPrintersAsync", [StringComparison]::Ordinal)
    if ([string]::IsNullOrWhiteSpace($entry.Body) -or $guardIndex -lt 0 -or
        ($discoveryIndex -ge 0 -and $guardIndex -gt $discoveryIndex)) {
        $workflowHardwareGuardsPass = $false
    }
}
$discoveryBody = Get-MethodBody $workflow 'public\s+async\s+Task<IReadOnlyList<InstalledPrinterInfo>>\s+GetInstalledPrintersAsync\s*\('
$safeStartGuardPass =
    $workflowHardwareGuardsPass -and
    $hardwareSafety -match 'if\s*\(App\.IsSafeStart\)[\s\S]*throw\s+new\s+InvalidOperationException' -and
    $spoolerPrintBody.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal) -ge 0 -and
    $spoolerPrintBody.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal) -lt $spoolerPrintBody.IndexOf("StartExclusivePrinterEffect", [StringComparison]::Ordinal) -and
    $spoolerDrawerBody.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal) -ge 0 -and
    $spoolerDrawerBody.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal) -lt $spoolerDrawerBody.IndexOf("StartExclusivePrinterEffect", [StringComparison]::Ordinal) -and
    $discoveryBody -match 'if\s*\(App\.IsSafeStart\)[\s\S]{0,120}return\s+Array\.Empty<InstalledPrinterInfo>\(\)' -and
    $workflow -match 'effectiveAutoPrint\s*=\s*autoPrint\s*&&\s*!App\.IsSafeStart' -and
    $posVm -match 'OpenPrinterSettingsAsync[\s\S]{0,220}if\s*\(App\.IsSafeStart\)' -and
    $uiSmoke -match 'VerifySafeStartHardwareBoundaryAsync[\s\S]*discoveryTaskField[\s\S]*lastMileBlocked'
if ($safeStartGuardPass) {
    Pass "Safe Start blocks printer discovery/settings and every workflow/last-mile hardware effect"
}
else {
    Fail "Safe Start must be an executable fail-closed printer/cash-drawer boundary"
}

$entryDocumentGuardIndex = $spoolerPrintBody.IndexOf("ReceiptDocumentPolicy.EnsureValidDocument", [StringComparison]::Ordinal)
$entryHardwareGuardIndex = $spoolerPrintBody.IndexOf("PrinterHardwareSafety.DemandHardwareOutputAllowed", [StringComparison]::Ordinal)
$printOnceBody = Get-MethodBody $spooler 'private\s+static\s+void\s+PrintOnce\s*\('
if ($receiptContentPolicy -notmatch 'MaxDocumentCharacters\s*=\s*131072' -or
    $entryDocumentGuardIndex -lt 0 -or
    $entryHardwareGuardIndex -lt 0 -or
    $entryDocumentGuardIndex -gt $entryHardwareGuardIndex -or
    $printOnceBody -notmatch 'ReceiptDocumentPolicy\.EnsureValidDocument\(receiptText\)' -or
    $uiSmoke -notmatch 'invalidDocumentRejectedBeforeEffect') {
    Fail "invalid receipt documents must fail before hardware/effect-tail creation and be rechecked at the print worker"
} else {
    Pass "receipt document limits are enforced before hardware and again at the print worker"
}

if ($printOptions -match 'MinimumCopies\s*=\s*1' -and
    $printOptions -match 'MaximumCopies\s*=\s*3' -and
    $dialog -match 'x:Name="CopiesTextBox"[\s\S]{0,400}MaxLength="1"' -and
    $dialogVm -match 'IsValidCopyCount|ParsedCopies\s*<=\s*ReceiptPrintOptions\.MaximumCopies' -and
    $workflow -match '!ReceiptPrintOptions\.IsValidCopyCount\(settings\.Copies\)' -and
    $workflow -match 'persistedCopies[\s\S]{0,220}ReceiptPrintOptions\.MinimumCopies' -and
    $spoolerPrintBody -match 'IsValidCopyCount\(opt\.Copies\)[\s\S]*StartExclusivePrinterEffect' -and
    $spooler -match 'checked\s*\(\s*\(short\)opt\.Copies\s*\)' -and
    $spooler -match 'MaximumCopies[\s\S]{0,220}opt\.Copies\s*>\s*driverMaximumCopies' -and
    $uiSmoke -match 'VerifyPrinterCopyCountPolicyAsync[\s\S]*int\.MaxValue[\s\S]*tailCountBefore') {
    Pass "receipt copies are strictly bounded to 1..3 before persistence and physical output"
}
else {
    Fail "receipt copies must be validated at UI, persistence, service, driver and last-mile boundaries"
}

if ($spooler -match 'MaximumReceiptPages\s*=\s*128' -and
    $spooler -match 'EnsurePageProgress\s*\(' -and
    $spooler -match 'pageCount\s*>\s*MaximumReceiptPages' -and
    $uiSmoke -match 'VerifyReceiptPageProgressGuard' -and
    $uiSmoke -match 'VerifySharedPrinterEffectCoordinatorAsync') {
    Pass "receipt pagination and shared-effect ordering have bounded smoke coverage"
} else {
    Fail "receipt pagination needs a page/progress guard and the coordinator needs smoke coverage"
}

if ($saleModel -match 'ReceiptShopSnapshotJson' -and
    $dbInitializer -match 'receipt_shop_snapshot' -and
    $saleTransactionWriter -match 'receipt_shop_snapshot' -and
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

$physicalQaBody = Get-MethodBody $uiSmoke 'public\s+async\s+Task<string>\s+RunPhysicalPrinterQaAsync\s*\('
$physicalQaBuilder = Get-MethodBody $uiSmoke 'private\s+static\s+IReadOnlyList<PhysicalPrinterQaJob>\s+BuildPhysicalPrinterQaJobs\s*\('
$physicalQaOptions = Get-MethodBody $uiSmoke 'private\s+static\s+ReceiptPrintOptions\s+CreatePhysicalPrinterQaOptions\s*\('
$physicalQaValidator = Get-MethodBody $uiSmoke 'private\s+static\s+void\s+ValidatePhysicalPrinterQaJobs\s*\('
$physicalQaManifest = Get-MethodBody $uiSmoke 'private\s+static\s+void\s+WritePhysicalPrinterQaManifest\s*\('
$physicalQaAtomicManifest = Get-MethodBody $uiSmoke 'private\s+static\s+void\s+WritePhysicalPrinterQaManifestAtomically\s*\('
$physicalQaIsFailClosed =
    -not [string]::IsNullOrWhiteSpace($physicalQaBody) -and
    -not [string]::IsNullOrWhiteSpace($physicalQaBuilder) -and
    -not [string]::IsNullOrWhiteSpace($physicalQaOptions) -and
    -not [string]::IsNullOrWhiteSpace($physicalQaValidator) -and
    -not [string]::IsNullOrWhiteSpace($physicalQaManifest) -and
    -not [string]::IsNullOrWhiteSpace($physicalQaAtomicManifest) -and
    $uiSmoke -match 'HasArg\(args,\s*"--physical-printer-qa"\)' -and
    $uiSmoke -match 'requires explicit --no-drawer' -and
    $uiSmoke -match 'ValueAfter\(args,\s*"--printer-name"\)' -and
    $physicalQaBody -match 'WindowsPrinterDiscovery\.GetInstalledPrinters' -and
    $physicalQaBody -match 'IsInventoryFresh' -and
    $physicalQaBody -match 'IsAvailable' -and
    $physicalQaBody -match 'IsPhysical' -and
    $physicalQaBody -match 'for\s*\(\s*var\s+index\s*=\s*0;\s*index\s*<\s*jobs\.Count;\s*index\+\+\s*\)' -and
    [regex]::Matches($physicalQaBody, 'await\s+printer\.PrintAsync').Count -eq 1 -and
    $physicalQaBody -notmatch 'OpenCashDrawerAsync' -and
    $physicalQaBody -notmatch 'PosWorkflowService|Repository|Outbox|TryPrintWithRetry|RetryDelay|Task\.WhenAll|Task\.Run|while\s*\(' -and
    $physicalQaBody -match 'INDETERMINATE_DO_NOT_RETRY' -and
    $physicalQaBody -match 'SUBMITTED_AWAITING_VISUAL_CONFIRMATION' -and
    $physicalQaBody -match 'EnsureNoPhysicalQaDatabaseArtifacts' -and
    $physicalQaBody -match 'catch\s*\(TimeoutException[\s\S]{0,900}return\s+File\.ReadAllText' -and
    $physicalQaBody -match 'catch\s*\(Exception[\s\S]{0,900}return\s+File\.ReadAllText' -and
    $physicalQaBuilder -notmatch 'PosWorkflowService|Repository|Outbox|OpenCashDrawerAsync|DbInitializer' -and
    $physicalQaBuilder -match 'receipt-original[\s\S]*receipt-reprint-identical' -and
    [regex]::Matches($physicalQaBuilder, 'new\s+PhysicalPrinterQaJob\s*\(').Count -eq 6 -and
    $physicalQaOptions -match 'Copies\s*=\s*1' -and
    $physicalQaOptions -match 'CashDrawerCommand\s*=\s*string\.Empty' -and
    $physicalQaValidator -match 'jobs\.Count\s*!=\s*6' -and
    $physicalQaValidator -match 'jobs\[2\]\.Text[\s\S]*jobs\[3\]\.Text' -and
    $physicalQaValidator -match 'jobs\[2\]\.TextSha256[\s\S]*jobs\[3\]\.TextSha256' -and
    $physicalQaValidator -match 'jobs\[2\]\.RequestSha256[\s\S]*jobs\[3\]\.RequestSha256' -and
    $physicalQaManifest -match 'DRAWER_CALLS=0' -and
    $physicalQaManifest -match 'FISCAL_NUMBER_SOURCE=SYNTHETIC_NOT_RESERVED' -and
    $physicalQaManifest -match 'HASH_ENCODING=SHA256_UTF8_NO_BOM' -and
    $physicalQaManifest -match 'TEXT_SHA256' -and
    $physicalQaManifest -match 'REQUEST_SHA256' -and
    $physicalQaManifest -match 'USE_RECEIPT_HEADER_STYLE' -and
    $physicalQaManifest -match 'SALE_CODE_FOR_BARCODE' -and
    $physicalQaManifest -match 'DRAWER_COMMAND_EMPTY' -and
    $physicalQaManifest -match 'WritePhysicalPrinterQaManifestAtomically' -and
    $physicalQaAtomicManifest -match 'FileStream' -and
    $physicalQaAtomicManifest -match 'Flush\s*\(\s*true\s*\)' -and
    $physicalQaAtomicManifest -match 'File\.Replace' -and
    $physicalQaAtomicManifest -match 'File\.Move' -and
    $uiSmoke -notmatch 'File\.WriteAllText\s*\(\s*Path\.Combine\s*\(\s*dataDir\s*,\s*"physical-printer-qa\.txt"'
if ($physicalQaIsFailClosed) {
    Pass "physical printer QA is explicit, six-job, one-copy, DB-free and structurally drawer-free"
} else {
    Fail "physical printer QA must require exact fresh physical output and never invoke the drawer"
}

$trustedSeedIsFailClosed =
    $uiSmoke -match 'HasArg\(args,\s*"--seed-trusted-session"\)' -and
    $uiSmoke -match 'EnsureSyntheticTrustedSessionSeedPath\(dataDir\)' -and
    $uiSmoke -match 'Path\.IsPathRooted\(dataDir\)' -and
    $uiSmoke -match '"Win7POS-QA"' -and
    $uiSmoke -match 'DriveType\.Fixed' -and
    $uiSmoke -match 'existingAncestor' -and
    $uiSmoke -match 'FileAttributes\.ReparsePoint' -and
    $uiSmoke -match 'Directory\.EnumerateFileSystemEntries\(fullPath\)\.Any\(\)' -and
    $uiSmoke -match 'PosOnlineContract\.OfflineAuthorizationMaxAgeSeconds'
$trustedSeedGuardIndex = $uiSmoke.IndexOf(
    'dataDir = EnsureSyntheticTrustedSessionSeedPath(dataDir);',
    [System.StringComparison]::Ordinal)
$seedDirectoryCreateIndex = $uiSmoke.IndexOf(
    'Directory.CreateDirectory(dataDir);',
    [System.StringComparison]::Ordinal)
$trustedSeedIsFailClosed = $trustedSeedIsFailClosed -and
    $trustedSeedGuardIndex -ge 0 -and
    $seedDirectoryCreateIndex -gt $trustedSeedGuardIndex
if ($trustedSeedIsFailClosed) {
    Pass "synthetic trusted-session seed is explicit and restricted to a new Win7POS-QA directory"
} else {
    Fail "synthetic trusted-session seed must be explicit and fail closed outside a new Win7POS-QA directory"
}

$offlineQaIsFailClosed =
    $uiSmoke -match 'HasArg\(args,\s*"--offline-sales-sandbox"\)' -and
    $uiSmoke -match 'SeedOfflineSalesSandboxAsync' -and
    $uiSmoke -match 'SeedOfflineSalesOperatorAsync' -and
    $uiSmoke -match 'UpsertRemoteStaffMirrorAsync' -and
    $uiSmoke -match 'remoteUsers\s*!=\s*1\s*\|\|\s*localRecoveryUsers\s*!=\s*0' -and
    $uiSmoke -match 'Offline sales sandbox seed postconditions failed' -and
    $uiSmoke -match 'VerifyTrustedDeviceSession' -and
    $uiSmoke -match 'ReceiptEnabled\s*=\s*false' -and
    $uiSmoke -match 'CashDrawerEnabled\s*=\s*false' -and
    $offlineQaLauncher -match 'WIN7POS_SAFE_START\s*=\s*"1"' -and
    $offlineQaLauncher -match 'WIN7POS_ADMIN_WEB_BASE_URL\s*=\s*"http://127\.0\.0\.1:9"' -and
    $accessDialog -match 'IsAdminWebOptionsAllowedForCurrentLaunch[\s\S]*App\.IsSafeStart[\s\S]*BaseUri\.IsLoopback' -and
    $accessDialog -match 'OnConnectClick[\s\S]*TryCreateAdminWebOptionsForCurrentLaunch[\s\S]*BootstrapAsync' -and
    $accessDialog -match 'RunCatalogRetryAsync[\s\S]*IsAdminWebOptionsAllowedForCurrentLaunch[\s\S]*AttachCurrentTrustAsync[\s\S]*OnlineSyncLane\.CatalogDelta[\s\S]*OnlineSyncLaneTrigger\.Manual' -and
    $offlineQaLauncher -match 'Get-Process\s+-Name\s+"Win7POS\.Wpf"' -and
    $offlineQaLauncher -match 'Get-CanonicalLocalQaPath' -and
    $offlineQaLauncher -match 'DriveType\]::Fixed' -and
    $offlineQaLauncher -match 'StartsWith\(\$qaPrefix' -and
    $offlineQaLauncher -match 'Assert-NoExistingReparsePointAncestor' -and
    $offlineQaLauncher -match 'FileAttributes\]::ReparsePoint' -and
    $offlineQaLauncher -match 'ResumeExistingSandbox requires an explicit DataDir' -and
    $offlineQaLauncher -match 'QA OFFLINE SANDBOX - TEST / NON FISCAL' -and
    $offlineQaLauncher -match 'if \(-not \$ResumeExistingSandbox\) \{\s*\$seedArguments' -and
    $offlineQaLauncher -match '--verify-offline-sales-sandbox-safety' -and
    $offlineQaLauncher -match 'verifyProcess\.ExitCode -ne 73' -and
    $uiSmoke -match 'VerifyOfflineSalesSandboxSafetyAsync' -and
    $uiSmoke -match 'Shutdown\(OfflineSalesSafetyVerifiedExitCode\)' -and
    $uiSmoke -match 'hardware output is enabled' -and
    $uiSmoke -match 'synthetic shop identity or fiscal lock is invalid' -and
    $uiSmoke -match 'loopbackAllowed[\s\S]*stagingBlocked' -and
    $offlineQaDocs -match 'must never reuse' -and
    $offlineQaDocs -match 'does not intentionally alter\s+existing sales, stock or outbox rows' -and
    $offlineQaDocs -match 'Do not choose \*\*Local\s+recovery sign-in\*\*' -and
    $offlineQaDocs -match 'Safe Start keeps[\s\S]*hardware-disabled[\s\S]*dedicated printer harness'
if ($offlineQaIsFailClosed) {
    Pass "offline sales QA launcher is isolated, externally offline and hardware-disabled for the launch"
} else {
    Fail "offline sales QA launcher must remain isolated and fail closed"
}

if ($translations -match 'printer\.testInvalidCommand' -and
    $dialogVm -match 'IsCashDrawerCommandValid' -and
    $dialogVm -match 'IsValid\s*=>\s*ParsedCopies\s*>=\s*1[\s\S]{0,160}MaximumCopies[\s\S]{0,160}IsCashDrawerCommandValid' -and
    $dialogVm -match 'PosLocalization\.T\("printer\.testInvalidCommand"\)' -and
    $uiSmoke -match '!vm\.IsCashDrawerCommandValid\s*&&\s*!vm\.IsValid') {
    Pass "invalid drawer command is localized and blocks settings confirmation"
} else {
    Fail "invalid drawer command must show a localized error and block settings save"
}

if ($productionSecretScan -match "(?i)sk-[a-z0-9]|api[_-]?key\s*=|secret\s*=|(?<![A-Za-z0-9_])token\s*=|password\s*=|BEGIN (RSA |OPENSSH |EC )?PRIVATE KEY") {
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
