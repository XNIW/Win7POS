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

. (Join-Path $PSScriptRoot 'sales-sync-outbox-gate-helpers.ps1')

function Get-ReceiptMethod([string]$text,[string]$name) {
    $matches=[regex]::Matches($text,'(?m)^\s*(?:private|internal|public)\s+(?:static\s+)?(?:async\s+)?\S+\s+'+[regex]::Escape($name)+'\s*\(')
    if($matches.Count -ne 1){return ''}
    $body=Find-CSharpMethodBodyStart $text $matches[0].Index
    if($null -eq $body -or $body.Kind -cne 'block'){return ''}
    $end=Find-CSharpMatchingDelimiter $text $body.Index '{' '}'
    if($end -lt $body.Index){return ''}
    return $text.Substring($matches[0].Index,$end-$matches[0].Index+1)
}

function Get-AnalyticalReceiptViolations([string]$daily,[string]$analytical) {
    $errors=[Collections.Generic.List[string]]::new()
    $selected=Get-ReceiptMethod $analytical 'LoadAnalyticalSelectedPreviewAsync'
    $publish=Get-ReceiptMethod $analytical 'PublishAnalyticalSnapshot'
    $capture=Get-ReceiptMethod $analytical 'TryCaptureAnalyticalSnapshot'
    $scope=Get-ReceiptMethod $analytical 'DemandAnalyticalScope'
    $current=Get-ReceiptMethod $analytical 'DemandAnalyticalCurrentAsync'
    $operators=Get-ReceiptMethod $analytical 'GetAnalyticalOperatorScope'
    $day=Get-ReceiptMethod $analytical 'LoadAnalyticalDailyAsync'
    $history=Get-ReceiptMethod $analytical 'LoadAnalyticalHistoryAsync'
    $print=Get-ReceiptMethod $daily 'PrintStampaRiepilogoAsync'
    $canPrint=Get-ReceiptMethod $daily 'CanPrintStampaRiepilogo'
    $selectedFacade=Get-ReceiptMethod $daily 'LoadPreviewForSelectedHistoryAsync'
    $markedFacade=Get-ReceiptMethod $daily 'LoadPreviewForSingleMarkedAsync'
    if($selectedFacade -notmatch 'await\s+LoadAnalyticalSelectedPreviewAsync\(false\)' -or $markedFacade -notmatch 'await\s+LoadAnalyticalSelectedPreviewAsync\(true\)' -or
        $selected -notmatch 'singleMarked\s*\?\s*HistoryRows\.FirstOrDefault[\s\S]*SelectedHistoryRow' -or
        $selected -notmatch '!context\.Snapshot\.Coverage\.CanPublish' -or $selected -notmatch 'await\s+DemandAnalyticalCurrentAsync\(CancellationToken\.None\)' -or
        $selected -notmatch 'ReferenceEquals\(context,\s*_reportContext\)' -or $selected -notmatch 'ReferenceEquals\(selected,\s*row\)' -or
        $selected -notmatch 'context\.Snapshot\.Days\.FirstOrDefault\(day\s*=>\s*day\.Date\s*==\s*row\.Date\)\?\.Summary' -or
        $selected -notmatch 'TryCaptureAnalyticalSnapshot\(context,\s*_reportReadAuthority,\s*\(\)\s*=>' -or
        $selected -notmatch 'if\s*\(singleMarked\)\s*_singleMarkedReceiptPreview\s*=\s*preview' -or
        $selected -match 'GetDailySummaryAsync|OpenAnalyticalReportAsync'){$errors.Add('selected and marked previews must share one current context/day/coverage and exact row identity')}
    if($operators -notmatch 'DemandAnalyticalPermission\(\)' -or $operators -notmatch 'TryGetAuthorizationBoundUser\(out var user\)' -or
        $operators -notmatch 'canAll\s*=\s*_reportPermissions\.Has\(PermissionCodes\.RegisterViewAll\)' -or $operators -notmatch 'operatorId\s*=\s*canAll\s*\?\s*\(int\?\)null\s*:\s*user\.Id'){$errors.Add('analytical operator scope must derive from the current bound user and ViewAll grant')}
    foreach($loader in @($day,$history)){
        if($loader -notmatch 'GetAnalyticalOperatorScope\(out var canAll,\s*out var operatorId\)' -or
            $loader -notmatch 'OpenAnalyticalReportAsync\([^;]*,\s*origin,\s*canAll,\s*operatorId,\s*null,\s*cancellation\.Token\)' -or
            $loader -notmatch 'version\s*!=\s*_reportLoadVersion' -or $loader -notmatch 'cancellation\.Token\.ThrowIfCancellationRequested\(\)' -or
            $loader -notmatch 'CaptureSalesReportReadAuthority\(context\.Filter,\s*PermissionCodes\.DailyCloseView\)' -or $loader -notmatch 'PublishAnalyticalSnapshot\(context,\s*authority,'){$errors.Add('daily/history loaders must use the same versioned analytical scope and captured authority')}
    }
    if($scope -notmatch 'context\.Snapshot\.Token\.ScopeHash,\s*context\.Filter\.Fingerprint' -or
        $scope -notmatch 'IsSalesReportReadAuthorityCurrent\(_reportReadAuthority,\s*context\.Filter\)' -or
        @([regex]::Matches($current,'DemandAnalyticalPermission\(\)')).Count -ne 2 -or @([regex]::Matches($current,'DemandAnalyticalScope\(context\)')).Count -ne 2 -or
        $current -notmatch 'await\s+_service\.DemandAnalyticalReportCurrentAsync\(context,\s*cancellationToken\)' -or $current -notmatch 'context\.Coordinator\.IsCurrent\(context\.Snapshot\.Token\)'){$errors.Add('analytical current check must revalidate permission, scope, token and context around the query')}
    if($capture -notmatch 'context\.Coordinator\.TryUseSnapshot\(context\.Snapshot\.Token,\s*\(\)\s*=>' -or
        $capture -notmatch 'TryUseSalesReportReadAuthority\(authority,\s*context\.Filter,\s*captureOnly\)' -or $capture -notmatch 'throw new ReportSnapshotStaleException' -or
        $publish -notmatch 'snapshot\.Days\.ToDictionary' -or $publish -notmatch 'snapshot\.Coverage\.CanPublish' -or $publish -notmatch 'TryCaptureAnalyticalSnapshot\(context,\s*authority,'){$errors.Add('analytical publication must capture the common snapshot atomically under coordinator and authorization gates')}
    $calls=[regex]::Matches($analytical,'TryCaptureAnalyticalSnapshot\s*\(\s*context\s*,\s*(?:_reportReadAuthority|authority)\s*,\s*\(\)\s*=>')
    # Four call sites cover five paths: selected and single-marked share one
    # loader; initial publish, marked aggregates and header cover the others.
    if($calls.Count -ne 4){$errors.Add('all analytical presentation captures must use the common gated helper')}
    foreach($method in @('RefreshAnalyticalMarkedAggregates','RefreshAnalyticalHeader')){
        if((Get-ReceiptMethod $analytical $method) -notmatch 'TryCaptureAnalyticalSnapshot\(context,\s*_reportReadAuthority,'){$errors.Add('marked aggregates and header must retain their current authorized snapshot capture')}
    }
    foreach($call in $calls){
        $open=$analytical.IndexOf('(',$call.Index);$end=Find-CSharpMatchingDelimiter $analytical $open '(' ')'
        if($end -lt $open){$errors.Add('analytical capture boundary is unbalanced');continue}
        $body=$analytical.Substring($call.Index+$call.Length,$end-($call.Index+$call.Length))
        if($body -match 'OnPropertyChanged|Notify\w*\s*\(|\bawait\b|\.(?:Add|Clear|Execute|Query|Open|Commit)\s*\('){$errors.Add('gated presentation captures must remain silent memory-only assignments')}
    }
    $amounts=Get-ReceiptMethod $analytical 'CaptureAnalyticalAmounts'
    if(-not $amounts -or $amounts -match 'OnPropertyChanged|Notify\w*\s*\(|\bawait\b|\.(?:Add|Clear|Execute|Query|Open|Commit)\s*\('){$errors.Add('amount capture must remain silent memory-only assignment')}
    if($canPrint -notmatch 'if\s*\(MarkedCount\s*==\s*1\)[\s\S]*!IsMarkedPreviewLoading[\s\S]*SingleMarkedReceiptPreview' -or
        $canPrint -notmatch '!IsHistoryPreviewLoading' -or $print -notmatch 'await\s+DemandAnalyticalCurrentAsync\(CancellationToken\.None\)' -or
        $print -notmatch 'else if\s*\(MarkedCount\s*==\s*1\)\s*\{\s*textToPrint\s*=\s*SingleMarkedReceiptPreview\s*;' -or
        $print -notmatch 'PrintReceiptTextAsync\(\s*textToPrint,\s*_useReceipt42,\s*"STAMPA_RIEPILOGO_"[\s\S]*explicitUserAction:\s*true\)'){$errors.Add('single marked print must use the exact fenced preview, configured width and explicit print route')}
    if($analytical -notmatch 'await\s+importHistoryAction\(\)\.ConfigureAwait\(true\);\s*InvalidateAnalyticalReport\(\)' -or
        $analytical -notmatch 'AuthorizationStateChanged\s*\+=\s*OnReportSessionChanged'){$errors.Add('import/session authorization changes must invalidate displayed analytical receipts')}
    return $errors.ToArray()
}

function Get-ReportReadAuthorityViolations([string]$publication,[string]$revocation) {
    $errors=[Collections.Generic.List[string]]::new()
    $read=Get-ReceiptMethod $publication 'IsSalesReportReadAuthorityCurrent'
    $capture=Get-ReceiptMethod $publication 'TryUseSalesReportReadAuthority'
    $forbidden='\block\s*\(|Monitor\s*\.\s*Enter|\.\s*(?:Wait|WaitAsync|Open|OpenAsync|DemandPath)\s*\(|TryReadOperatorAuthorityIf|TryCaptureAuthorizationEpoch|IsAuthorizationEpochCurrent|EvaluateAuthorizationUse|DemandAuthorizationUseStillValid|CommitIfAuthorizationCurrent'
    if(-not $read -or -not $capture -or ($read+$capture) -match $forbidden){$errors.Add('Dispatcher report authority reads must not enter a latch, owner monitor, semaphore, database or lease/store evaluator')}
    if($read -notmatch 'Volatile\.Read\(ref\s+_currentUser\)' -or $read -notmatch 'authority\.Expiry\.Evaluate\(TimeSpan\.Zero\)\.Allowed' -or
        $read -notmatch 'filter\.CanViewAllOperators\s*\?\s*HasPermission\(user,\s*PermissionCodes\.RegisterViewAll\)' -or
        [regex]::Matches($read,'IsAuthorizationReadStampCurrent\(authority\.Stamp\)').Count -ne 2){$errors.Add('passive report authority must check immutable stamp, frozen expiry and exact operator scope before and after its memory predicate')}
    if($capture -notmatch 'if\s*\(captureOnly\s*==\s*null\s*\|\|\s*!IsSalesReportReadAuthorityCurrent\(authority,\s*filter\)\)\s*return false;' -or
        $capture -notmatch 'captureOnly\(\);\s*return IsSalesReportReadAuthorityCurrent\(authority,\s*filter\);'){$errors.Add('silent UI capture must reject authority changes both before and after backing field capture')}
    if($revocation -notmatch 'ReadAuthorizationStamp\(\)\s*=>\s*Volatile\.Read\(ref _authorizationReadStamp\);' -or
        $revocation -notmatch 'IsAuthorizationReadStampCurrent\(AuthorizationReadStamp stamp\)\s*=>\s*stamp != null && !stamp\.Inactive && ReferenceEquals\(stamp, Volatile\.Read\(ref _authorizationReadStamp\)\);'){$errors.Add('Dispatcher authorization stamp comparison must remain a lock-free immutable reference read')}
    return $errors.ToArray()
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
$dailyAnalytical = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/DailyReportViewModel.Analytical.cs"
$reportPublication = Read-Required "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.SalesReportPublication.cs"
$reportRevocation = Read-Required "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs"
$dailyXaml = Read-Required "src/Win7POS.Wpf/Pos/DailyReportView.xaml"
$printerVm = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsViewModel.cs"
$printerXaml = Read-Required "src/Win7POS.Wpf/Pos/Dialogs/PrinterSettingsDialog.xaml"
$printOptions = Read-Required "src/Win7POS.Wpf/Printing/ReceiptPrintOptions.cs"
$spooler = Read-Required "src/Win7POS.Wpf/Printing/WindowsSpoolerReceiptPrinter.cs"
$contentPolicy = Read-Required "src/Win7POS.Core/Receipt/ReceiptContentPolicy.cs"
$saleRepository = Read-Required "src/Win7POS.Data/Repositories/SaleRepository.cs"
$saleTransactionWriter = Read-Required "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$saleLineReadRepository = Read-Required "src/Win7POS.Data/Repositories/SaleLineReadRepository.cs"
$receiptPolicyTests = Read-Required "tests/Win7POS.Core.Tests/Pos/ReceiptContentPolicyTests.cs"
$settingKeys = Read-Required "src/Win7POS.Wpf/Infrastructure/AppSettingKeys.cs"
$uiSmoke = Read-Required "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$coreTests = Read-Required "tests/Win7POS.Core.Tests/Pos/ReceiptSurfaceRenderingTests.cs"

Require-Pattern "shared layout exposes normalized visible-width primitives" $layout 'NormalizeColumns[\s\S]*VisibleWidth[\s\S]*WrapText[\s\S]*TwoColumnLine[\s\S]*Separator'
Require-Pattern "sales receipt input is immutable and freezes entities" $input 'sealed\s+class\s+SalesReceiptRenderModel[\s\S]*public\s+SaleSnapshot\s+Sale\s*\{\s*get;\s*\}[\s\S]*ReadOnlyCollection<LineSnapshot>'
$linePreflightIndex = $input.IndexOf(
    "SalesReceiptContentPolicy.EnsureValid(sale, lines)",
    [System.StringComparison]::Ordinal)
$lineSnapshotIndex = $input.IndexOf(
    "var frozenLines = new List<LineSnapshot>()",
    [System.StringComparison]::Ordinal)
if ($linePreflightIndex -lt 0 -or $lineSnapshotIndex -le $linePreflightIndex) {
    Fail "sales receipt line preflight must precede snapshot allocation"
} else {
    Pass "sales receipt line preflight precedes snapshot allocation"
}
$saleInsertStart = $saleTransactionWriter.IndexOf("internal async Task<long> InsertSaleAsync", [System.StringComparison]::Ordinal)
$saleInsertGuard = $saleTransactionWriter.IndexOf("SalesReceiptContentPolicy.EnsureValid(sale, lines)", $saleInsertStart, [System.StringComparison]::Ordinal)
$saleInsertOpen = $saleTransactionWriter.IndexOf("_factory.OpenAsync()", $saleInsertStart, [System.StringComparison]::Ordinal)
$refundInsertStart = $saleTransactionWriter.IndexOf("internal async Task<long> InsertRefundOrVoidAsync", [System.StringComparison]::Ordinal)
$refundInsertGuard = $saleTransactionWriter.IndexOf("SalesReceiptContentPolicy.EnsureValid(refundSale, refundLines)", $refundInsertStart, [System.StringComparison]::Ordinal)
$refundInsertOpen = $saleTransactionWriter.IndexOf("_factory.Open()", $refundInsertStart, [System.StringComparison]::Ordinal)
if ($saleInsertStart -lt 0 -or $saleInsertGuard -le $saleInsertStart -or $saleInsertOpen -le $saleInsertGuard -or
    $refundInsertStart -lt 0 -or $refundInsertGuard -le $refundInsertStart -or $refundInsertOpen -le $refundInsertGuard) {
    Fail "sale and refund persistence must validate receipt lines before opening SQLite"
} else {
    Pass "sale and refund persistence validates receipt lines before opening SQLite"
}
$lineInsertStart = $saleTransactionWriter.IndexOf("internal async Task InsertSaleLinesAsync", [System.StringComparison]::Ordinal)
$lineInsertEnd = $saleTransactionWriter.IndexOf("internal async Task<string> EnsureClientSaleIdAsync", $lineInsertStart, [System.StringComparison]::Ordinal)
$lineInsertTransactionGuard = $saleTransactionWriter.IndexOf("ReferenceEquals(tx.Connection, conn)", $lineInsertStart, [System.StringComparison]::Ordinal)
$lineInsertGuard = $saleTransactionWriter.IndexOf("SalesReceiptContentPolicy.EnsureValidLines(lines)", $lineInsertStart, [System.StringComparison]::Ordinal)
$lineInsertStoredBudget = $saleTransactionWriter.IndexOf("EnsureStoredLineBudgetAsync(conn, tx, group.Key)", $lineInsertStart, [System.StringComparison]::Ordinal)
$lineInsertBudget = $saleTransactionWriter.IndexOf("EnsureCumulativeLineBudget(", $lineInsertStart, [System.StringComparison]::Ordinal)
$lineInsertWrite = $saleTransactionWriter.IndexOf("InsertSaleLineAsync(conn, tx, line)", $lineInsertStart, [System.StringComparison]::Ordinal)
if ($lineInsertStart -lt 0 -or $lineInsertEnd -le $lineInsertStart -or
    $lineInsertTransactionGuard -le $lineInsertStart -or $lineInsertTransactionGuard -ge $lineInsertEnd -or
    $lineInsertGuard -le $lineInsertTransactionGuard -or
    $lineInsertGuard -ge $lineInsertEnd -or
    $lineInsertStoredBudget -le $lineInsertGuard -or
    $lineInsertStoredBudget -ge $lineInsertEnd -or
    $lineInsertBudget -le $lineInsertStoredBudget -or $lineInsertBudget -ge $lineInsertEnd -or
    $lineInsertWrite -le $lineInsertBudget -or $lineInsertWrite -ge $lineInsertEnd) {
    Fail "F6 direct sale-line persistence must require its owning transaction and enforce budgets before writes"
} else {
    Pass "F6 direct sale-line persistence requires its owning transaction and enforces budgets before writes"
}
$lineFacadeStart = $saleRepository.IndexOf("public Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync", [System.StringComparison]::Ordinal)
$lineFacadeDelegate = $saleRepository.IndexOf("_lineReads.GetLinesBySaleIdAsync(saleId)", $lineFacadeStart, [System.StringComparison]::Ordinal)
$lineReadStart = $saleLineReadRepository.IndexOf("GetLinesBySaleIdAsync", [System.StringComparison]::Ordinal)
$lineReadEnd = $saleLineReadRepository.IndexOf("internal static async Task<SaleLineBudgetRow> EnsureStoredLineBudgetAsync", $lineReadStart, [System.StringComparison]::Ordinal)
$lineReadDeferredTransaction = $saleLineReadRepository.IndexOf("conn.BeginTransaction(deferred: true)", $lineReadStart, [System.StringComparison]::Ordinal)
$lineReadBudget = $saleLineReadRepository.IndexOf("EnsureStoredLineBudgetAsync(conn, tx, saleId)", $lineReadStart, [System.StringComparison]::Ordinal)
$lineReadMaterialize = $saleLineReadRepository.IndexOf("QueryAsync<SaleLine>", $lineReadStart, [System.StringComparison]::Ordinal)
$lineReadMethod = if ($lineReadStart -ge 0 -and $lineReadEnd -gt $lineReadStart) {
    $saleLineReadRepository.Substring($lineReadStart, $lineReadEnd - $lineReadStart)
} else {
    ""
}
$lineReadUsesTransaction = $lineReadMethod -match "QueryAsync<SaleLine>\([\s\S]*?new\s*\{\s*saleId\s*\}\s*,\s*tx\s*\)"
if ($lineFacadeStart -lt 0 -or $lineFacadeDelegate -le $lineFacadeStart -or
    $lineReadStart -lt 0 -or $lineReadEnd -le $lineReadStart -or
    $lineReadDeferredTransaction -le $lineReadStart -or $lineReadDeferredTransaction -ge $lineReadEnd -or
    $lineReadBudget -le $lineReadDeferredTransaction -or $lineReadBudget -ge $lineReadEnd -or
    $lineReadMaterialize -le $lineReadBudget -or $lineReadMaterialize -ge $lineReadEnd -or
    -not $lineReadUsesTransaction) {
    Fail "historical sale-line reads must use one deferred transaction for preflight SQL budgets and row materialization"
} else {
    Pass "historical sale-line reads use one deferred snapshot transaction through preflight and materialization"
}
Require-Pattern "sale-line policy bounds individual, aggregate and count amplification" $contentPolicy 'class\s+SalesReceiptContentPolicy[\s\S]*MaxSaleLines\s*=\s*512[\s\S]*MaxSaleLineNameCharacters\s*=\s*512[\s\S]*MaxAggregateLineNameCharacters[\s\S]*MaxAggregateLineNameUtf8Bytes'
Require-Pattern "sale-line amplification regressions execute across render, write and historical boundaries" $receiptPolicyTests 'SaleLinePreflight_AcceptsBoundariesAndRejectsAmplificationOrUnsafeText[\s\S]*SalesReceiptRenderModel_RejectsUnsafeLinesBeforeFormatting[\s\S]*UnsafeSaleAndRefundLines_AreRejectedBeforeDatabaseMutation[\s\S]*LegacyRefundIngress_RejectsUnsafeReasonBeforeMutation[\s\S]*RepeatedSaleLineBatches_CannotExceedCumulativeBudget[\s\S]*DirectSaleLineInsert_RequiresTheOwningTransaction[\s\S]*HistoricalCorruptSaleLines_AreRejectedBeforeRowMaterialization'
Require-Pattern "sales formatter consumes immutable input and shared layout" $formatter 'Format\s*\(\s*SalesReceiptRenderModel\s+input[\s\S]*ReceiptTextLayout\.NormalizeColumns[\s\S]*ReceiptTextLayout\.TwoColumnLine'
Require-Pattern "WPF sales renderer constructs one immutable snapshot" $salesRenderer 'SalesReceiptRenderModel\.Create[\s\S]*BuildReceipt\s*\(SalesReceiptRenderModel\s+input[\s\S]*ReceiptFormatter\.Format'
Require-Pattern "refund and void metadata stays in the authoritative sales renderer" $salesRenderer 'SaleKind\.Refund[\s\S]*SaleKind\.Void[\s\S]*refund\.receiptHeader'
Require-Pattern "sales renderer validates shop metadata before formatting and final document after formatting" ($input + $salesRenderer) 'ReceiptShopMetadataPolicy\.EnsureValidReceiptShop[\s\S]*ReceiptFormatter\.Format[\s\S]*ReceiptDocumentPolicy\.EnsureValidDocument'

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
Require-Pattern "daily receipt metadata and final document are bounded" ($dailyCore + $dailyRenderer + $contentPolicy) 'ReceiptShopMetadataPolicy\.EnsureValid[\s\S]*ReceiptDocumentPolicy\.EnsureValidDocument[\s\S]*MaxDocumentCharacters'
Require-Pattern "daily close preview is the exact print payload" $dailyVm 'SummaryReceiptPreview[\s\S]*PrintReceiptTextAsync\(SummaryReceiptPreview'
Require-Pattern "daily history clears stale preview and blocks print while loading" $dailyVm 'SelectedHistoryRow[\s\S]*SummaryReceiptPreview\s*=\s*string\.Empty[\s\S]*CanPrintStampaRiepilogo[\s\S]*!IsHistoryPreviewLoading'
Require-Pattern "daily date edits immediately clear stale preview" $dailyVm 'DateText[\s\S]*InvalidateDailyPreview'
$analyticalViolations=@(Get-AnalyticalReceiptViolations $dailyVm $dailyAnalytical)
if($analyticalViolations.Count){foreach($violation in $analyticalViolations){Fail $violation}}else{Pass 'daily selected/marked previews share the current analytical snapshot, coverage, scope and exact print payload'}
# In-memory negative source fixtures exercise the guard's actual predicates;
# no application, database, printer or mutable checkout is touched.
$receiptMutations=@(
    @{name='row identity';daily=$dailyVm;analytical=$dailyAnalytical.Replace('if (!ReferenceEquals(selected, row)) return;','')},
    @{name='operator scope';daily=$dailyVm;analytical=$dailyAnalytical.Replace('origin, canAll, operatorId, null, cancellation.Token','origin, true, null, null, cancellation.Token')},
    @{name='authorization latch';daily=$dailyVm;analytical=$dailyAnalytical.Replace('TryUseSalesReportReadAuthority(authority, context.Filter, captureOnly)','TryUseUnboundCapture(authority, context.Filter, captureOnly)')},
    @{name='silent capture';daily=$dailyVm;analytical=$dailyAnalytical.Replace('_singleMarkedReceiptPreview = preview;','_singleMarkedReceiptPreview = preview; OnPropertyChanged(nameof(SingleMarkedReceiptPreview));')},
    @{name='shared day snapshot';daily=$dailyVm;analytical=$dailyAnalytical.Replace('context.Snapshot.Days.FirstOrDefault','GetUnrelatedSnapshot().Days.FirstOrDefault')},
    @{name='exact marked payload';daily=$dailyVm.Replace('textToPrint = SingleMarkedReceiptPreview;','textToPrint = SummaryReceiptPreview;');analytical=$dailyAnalytical}
)
foreach($mutation in $receiptMutations){
    if(($mutation.daily -ceq $dailyVm -and $mutation.analytical -ceq $dailyAnalytical) -or @(Get-AnalyticalReceiptViolations $mutation.daily $mutation.analytical).Count -eq 0){Fail ('analytical receipt mutation escaped: '+$mutation.name)}else{Pass ('analytical receipt negative fixture rejected: '+$mutation.name)}
}
$readAuthorityViolations=@(Get-ReportReadAuthorityViolations $reportPublication $reportRevocation)
if($readAuthorityViolations.Count){foreach($violation in $readAuthorityViolations){Fail $violation}}else{Pass 'Dispatcher report authority uses only immutable memory stamps, expiry and exact scope'}
$readMutations=@(
    @{name='read latch';publication=$reportPublication.Replace('authority.Expiry.Evaluate(TimeSpan.Zero).Allowed','PosOnlineSyncRevocationLatch.TryReadOperatorAuthorityIf(authority.Stamp.Epoch, () => true, () => { }) && authority.Expiry.Evaluate(TimeSpan.Zero).Allowed');revocation=$reportRevocation},
    @{name='read monitor';publication=$reportPublication.Replace('var user = Volatile.Read(ref _currentUser);','lock (_authorizationReadSync) { } var user = Volatile.Read(ref _currentUser);');revocation=$reportRevocation},
    @{name='expired passive read';publication=$reportPublication.Replace('authority.Expiry.Evaluate(TimeSpan.Zero).Allowed','true');revocation=$reportRevocation},
    @{name='capture recheck';publication=$reportPublication.Replace('return IsSalesReportReadAuthorityCurrent(authority, filter);','return true;');revocation=$reportRevocation},
    @{name='stamp lock-free read';publication=$reportPublication;revocation=$reportRevocation.Replace('ReadAuthorizationStamp() => Volatile.Read(ref _authorizationReadStamp);','ReadAuthorizationStamp() => ReadStampUnderGate();')}
)
foreach($mutation in $readMutations){
    if(($mutation.publication -ceq $reportPublication -and $mutation.revocation -ceq $reportRevocation) -or @(Get-ReportReadAuthorityViolations $mutation.publication $mutation.revocation).Count -eq 0){Fail ('report memory authority mutation escaped: '+$mutation.name)}else{Pass ('report memory authority negative fixture rejected: '+$mutation.name)}
}
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
