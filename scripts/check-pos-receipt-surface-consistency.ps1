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
        Fail "missing $relativePath"
        return ""
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -notmatch $pattern) { Fail $label } else { Pass $label }
}

function Forbid-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

$layout = Read-Required "src/Win7POS.Core/Receipt/ReceiptTextLayout.cs"
$input = Read-Required "src/Win7POS.Core/Receipt/SalesReceiptRenderModel.cs"
$formatter = Read-Required "src/Win7POS.Core/Receipt/ReceiptFormatter.cs"
$dailyCore = Read-Required "src/Win7POS.Core/Reports/DailyTakingsReceiptFormatter.cs"
$salesRenderer = Read-Required "src/Win7POS.Wpf/Pos/PosReceiptTextRenderer.cs"
$dailyRenderer = Read-Required "src/Win7POS.Wpf/Pos/DailyCloseReceiptTextRenderer.cs"
$workflow = Read-Required "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$payment = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PaymentViewModel.cs"
$paymentXaml = Read-Required "src/Win7POS.Wpf/Pos/PaymentView.xaml"
$posViewModel = Read-Required "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$wpfProject = Read-Required "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$historyVm = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterViewModel.cs"
$historyXaml = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/SalesRegisterDialog.xaml"
$dailyVm = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/DailyReportViewModel.cs"
$dailyXaml = Read-Required "src/Win7POS.Wpf/Pos/DailyReportView.xaml"
$printerVm = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs"
$printerXaml = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$printOptions = Read-Required "src/Win7POS.Wpf/Printing/ReceiptPrintOptions.cs"
$spooler = Read-Required "src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs"
$settingKeys = Read-Required "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs"
$uiSmoke = Read-Required "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$coreTests = Read-Required "tests/Win7POS.Core.Tests/Pos/ReceiptSurfaceRenderingTests.cs"

Require-Pattern "shared layout exposes normalized visible-width primitives" $layout 'NormalizeColumns[\s\S]*VisibleWidth[\s\S]*WrapText[\s\S]*TwoColumnLine[\s\S]*Separator'
Require-Pattern "sales receipt input is immutable and freezes entities" $input 'sealed\s+class\s+SalesReceiptRenderModel[\s\S]*public\s+SaleSnapshot\s+Sale\s*\{\s*get;\s*\}[\s\S]*ReadOnlyCollection<LineSnapshot>'
Require-Pattern "sales formatter consumes immutable input and shared layout" $formatter 'Format\s*\(\s*SalesReceiptRenderModel\s+input[\s\S]*ReceiptTextLayout\.NormalizeColumns[\s\S]*ReceiptTextLayout\.TwoColumnLine'
Require-Pattern "WPF sales renderer constructs one immutable snapshot" $salesRenderer 'SalesReceiptRenderModel\.Create[\s\S]*BuildReceipt\s*\(SalesReceiptRenderModel\s+input[\s\S]*ReceiptFormatter\.Format'
Require-Pattern "refund and void metadata stays in the authoritative sales renderer" $salesRenderer 'SaleKind\.Refund[\s\S]*SaleKind\.Void[\s\S]*refund\.receiptHeader'

Require-Pattern "payment preview uses authoritative sales renderer" $payment 'PosReceiptTextRenderer\.BuildReceipt'
Require-Pattern "fiscal boleta prints its preview text directly" $payment 'PrintFiscalBoletaAsync[\s\S]*_printFiscalToThermal\(FiscalPreviewText,\s*SaleCode\)'
Forbid-Pattern "fiscal boleta operational failures are not converted to a hidden false result" $payment 'PrintFiscalBoletaAsync[\s\S]{0,700}catch\s*\([\s\S]{0,160}return\s+false'
Require-Pattern "payment wires fiscal boleta to the configured receipt spooler" $posViewModel 'new\s+PaymentViewModel\([\s\S]{0,500}PrintReceiptTextAsync\([\s\S]{0,260}isFiscalPrint:\s*true[\s\S]{0,120}automaticAfterSale:\s*true'
Require-Pattern "post-commit fiscal print failure is logged and shown with reserved boleta context" $posViewModel 'CompleteSaleAsync[\s\S]*TriggerAutoPrintFiscalBoletaIfEnabledAsync[\s\S]{0,500}catch\s*\(Exception\s+fiscalPrintEx\)[\s\S]{0,900}LogError[\s\S]{0,900}paymentOkFiscalPrintFailed[\s\S]{0,900}fiscalSaleSavedPrintWarning'
Require-Pattern "printed status is marked only after confirmed fiscal output" $posViewModel 'if\s*\(fiscalBoletaPrinted\)[\s\S]{0,900}MarkPdfPrintedAsync'
Require-Pattern "printed-status persistence failure is isolated and warns against duplicate reprint" $posViewModel 'catch\s*\(Exception\s+fiscalStatusEx\)[\s\S]{0,1800}fiscalPrintedStatusSaveWarning'
Forbid-Pattern "printed-status persistence failure sends no second physical print" $posViewModel 'catch\s*\(Exception\s+fiscalStatusEx\)[\s\S]{0,1800}(TriggerAutoPrintFiscalBoletaIfEnabledAsync|PrintReceiptTextAsync)'
Forbid-Pattern "fiscal print path creates no PDF or delayed cleanup" ($payment + $posViewModel) 'FiscalPdfService|GenerateFiscalPdfAsync|ExportsDirectory|File\.Delete|Task\.Delay\(15000\)'
Forbid-Pattern "payment UI exposes no local PDF workflow" $paymentXaml '(?i)\bPDF\b'
Forbid-Pattern "WPF runtime has no PDF writer package" $wpfProject '(?i)PDFsharp'
$fiscalPdfServicePath = Join-Path $repoRoot "src/Win7POS.Wpf/Fiscal/FiscalPdfService.cs"
if (Test-Path -LiteralPath $fiscalPdfServicePath -PathType Leaf) {
    Fail "automatic fiscal PDF writer must be removed"
}
else {
    Pass "automatic fiscal PDF writer removed"
}
Require-Pattern "final sale and printer sample use authoritative sales preview path" $workflow 'BuildReceiptPreview\([\s\S]*PosReceiptTextRenderer\.BuildReceipt[\s\S]*BuildPrinterTestReceipt'
Require-Pattern "historical preview is reconstructed on demand from persisted sale and lines" $workflow 'GetReceiptPreviewBySaleIdAsync[\s\S]*GetByIdAsync\(saleId\)[\s\S]*GetLinesBySaleIdAsync\(saleId\)[\s\S]*GetReceiptShopInfoNoLockAsync\(sale\)'
Require-Pattern "historical print sends the exact selected preview" $historyVm 'var\s+preview\s*=\s*DetailReceiptPreview[\s\S]*PrintReceiptTextAsync\(\s*preview'
Require-Pattern "historical selection uses cancellation and version fencing" $historyVm 'CancellationTokenSource\s+_selectionLoadCts[\s\S]*_selectionVersion[\s\S]*CancelSelectedSaleLoad[\s\S]*IsCurrentSelection'
Require-Pattern "historical view keeps virtualization and selectable monospaced preview" $historyXaml 'VirtualizingPanel\.IsVirtualizing="True"[\s\S]*VirtualizationMode="Recycling"[\s\S]*DetailReceiptPreview[\s\S]*IsReadOnly="True"[\s\S]*FontFamily="Consolas"'

Require-Pattern "daily close has a dedicated renderer using shared primitives" ($dailyCore + $dailyRenderer) 'class\s+DailyCloseReceiptTextRenderer[\s\S]*ReceiptTextLayout\.NormalizeColumns[\s\S]*ReceiptTextLayout\.TwoColumnLine'
Require-Pattern "daily close preview is the exact print payload" $dailyVm 'SummaryReceiptPreview[\s\S]*PrintReceiptTextAsync\(SummaryReceiptPreview'
Require-Pattern "daily history clears stale preview and blocks print while loading" $dailyVm 'SelectedHistoryRow[\s\S]*SummaryReceiptPreview\s*=\s*string\.Empty[\s\S]*CanPrintStampaRiepilogo[\s\S]*!IsHistoryPreviewLoading'
Require-Pattern "daily date edits immediately clear stale preview" $dailyVm 'DateText[\s\S]*InvalidateDailyPreview'
Require-Pattern "daily asynchronous results are version fenced" $dailyVm '_dailyPreviewVersion[\s\S]*BeginDailyPreviewLoad[\s\S]*IsCurrentDailyPreview'
Require-Pattern "daily history uses the same complete sales scope for selected and marked previews" $dailyVm 'GetDailySummaryAsync\(row\.Date,\s*includeFiscalPrinted:\s*true\)[\s\S]*GetDailySummaryAsync\(row\.Date,\s*includeFiscalPrinted:\s*true\)'
Require-Pattern "single marked daily preview is fenced and is the exact print payload" $dailyVm 'IsMarkedPreviewLoading[\s\S]*MarkedCount\s*==\s*1[\s\S]*SingleMarkedReceiptPreview[\s\S]*textToPrint\s*=\s*SingleMarkedReceiptPreview[\s\S]*IsCurrentMarkedPreview'
Require-Pattern "daily close preview is selectable monospaced receipt paper" $dailyXaml 'DailyCloseReceiptPreview[\s\S]*FontFamily="Consolas"[\s\S]*IsReadOnly="True"'
Forbid-Pattern "daily close never calls the drawer" $dailyVm 'OpenCashDrawer|CashDrawer'

Require-Pattern "Printer Settings explains SQLite history without file copies" $printerXaml 'printer\.receiptHistoryStorageInfo'
$archiveRuntime = $printOptions + $spooler + $workflow + $printerVm + $settingKeys
Forbid-Pattern "automatic receipt-copy settings are removed from runtime" $archiveRuntime 'SaveCopyToFile|ReceiptOutputDirectory|SavedCopyPath|ReceiptSaveCopy'
Forbid-Pattern "receipt printer contains no receipt archive writer" $spooler '(?i)File\.(WriteAllText|WriteAllBytes|Create|OpenWrite|AppendAllText)|Directory\.CreateDirectory|StreamWriter'
Require-Pattern "receipt and boleta jobs use the shared safe physical copy policy" ($printOptions + $workflow + $spooler) 'MaximumCopies\s*=\s*3[\s\S]*IsValidCopyCount[\s\S]*Copies\s*=\s*ReceiptPrintOptions\.IsValidCopyCount\(printer\.Copies\)[\s\S]*checked\s*\(\s*\(short\)opt\.Copies\s*\)'

Require-Pattern "core tests cover immutable snapshot, widths, languages and daily parity" $coreTests 'FreezesPersistedEconomicsAndShopSnapshot[\s\S]*Fit32And42[\s\S]*en-US[\s\S]*zh-CN[\s\S]*DailyClose_PreviewAndPrintTextAreDeterministicAndFit32And42'
Require-Pattern "UI lifecycle covers Sales Register and Daily Close 20 times" $uiSmoke 'salesRegisterCycles=20[\s\S]*dailyReportCycles=20|dailyReportCycles=20[\s\S]*salesRegisterCycles=20'
Require-Pattern "UI harness verifies archive API removal" $uiSmoke 'VerifyReceiptArchiveRemoval[\s\S]*receiptArchiveRemovalPass'
Require-Pattern "UI harness verifies rapid history selection fencing" $uiSmoke 'VerifySalesRegisterRapidSelectionAsync[\s\S]*salesRegisterRapidSelectionPass'
Require-Pattern "UI harness verifies daily-history stale-preview fencing" $uiSmoke 'VerifyDailyHistoryPreviewFencingAsync[\s\S]*dailyHistoryPreviewFencingPass'
Require-Pattern "UI harness verifies daily-date stale-preview fencing" $uiSmoke 'VerifyDailyDatePreviewFencing[\s\S]*dailyDatePreviewFencingPass'
Require-Pattern "UI harness proves fiscal printer exceptions propagate" $uiSmoke 'expectedFailure[\s\S]*ReferenceEquals\(ex,\s*expectedFailure\)'
Require-Pattern "UI harness proves copy limits fail before hardware" $uiSmoke 'VerifyPrinterCopyCountPolicyAsync[\s\S]*QA INVALID COPY COUNT MUST NOT PRINT'

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
