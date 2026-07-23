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

function Get-Slice([string]$text, [string]$startMarker, [string]$endMarker) {
    $start = $text.IndexOf($startMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) {
        return ""
    }

    $end = $text.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0) {
        return $text.Substring($start)
    }

    return $text.Substring($start, $end - $start)
}

function Get-Until([string]$text, [int]$start, [string]$endMarker) {
    if ($start -lt 0) {
        return ""
    }

    $end = $text.IndexOf($endMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0) {
        return ""
    }

    return $text.Substring($start, $end - $start + $endMarker.Length)
}

function Test-MethodDeclared([string]$text, [string]$methodName) {
    $pattern = "(?m)^\s*\[TestMethod\]\s*public\s+(?:async\s+)?Task\s+" +
        [regex]::Escape($methodName) + "\s*\("
    return [regex]::IsMatch($text, $pattern)
}

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs",
    "tests/Win7POS.Core.Tests/Data/SaleStockMovementWriterTests.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$writer = Read-Text "src/Win7POS.Data/Repositories/SaleStockMovementWriter.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleStockMovementWriterTests.cs"

if ($saleRepository -notmatch "private readonly SaleStockMovementWriter _stockMovementWriter" -or
    $saleRepository -notmatch "_stockMovementWriter\s*=\s*new SaleStockMovementWriter\(\)") {
    Fail "SaleRepository must retain the F3 stock-movement writer collaborator"
} else {
    Pass "SaleRepository retains the F3 stock-movement writer collaborator"
}

$facade = Get-Slice `
    $saleRepository `
    "public async Task ApplyLocalStockMovementsAsync" `
    "public async Task EnqueueSalesSyncOutboxAsync"
if ($facade -notmatch "if \(sale == null \|\| lines == null \|\| lines.Count == 0\)" -or
    $facade -notmatch "EnsureClientSaleIdAsync\(conn, tx, sale.Id\)" -or
    $facade -notmatch "sale.ClientSaleId\s*=\s*clientSaleId" -or
    $facade -notmatch "_stockMovementWriter\s*\.\s*ApplyAsync\(conn, tx, sale, lines, clientSaleId\)") {
    Fail "F3 facade must retain empty-input and client-sale-id fallback before delegation"
} else {
    Pass "F3 facade retains empty-input and client-sale-id fallback before delegation"
}

$emptyInputGuardIndex = $facade.IndexOf("if (sale == null || lines == null || lines.Count == 0)", [System.StringComparison]::Ordinal)
$fallbackIndex = $facade.IndexOf("EnsureClientSaleIdAsync", [System.StringComparison]::Ordinal)
$delegationIndex = $facade.IndexOf("_stockMovementWriter", [System.StringComparison]::Ordinal)
if ($emptyInputGuardIndex -lt 0 -or $fallbackIndex -le $emptyInputGuardIndex -or
    $delegationIndex -le $fallbackIndex) {
    Fail "F3 facade must return for empty input before client-sale-id fallback and writer delegation"
} else {
    Pass "F3 facade returns for empty input before fallback and writer delegation"
}

$facadeLeaks = @(
    "INSERT\s+OR\s+IGNORE\s+INTO\s+local_stock_movements",
    "UPDATE\s+product_meta",
    "sale_decrement",
    "refund_increment",
    "void_reverse",
    "DiscountKeys\.IsEconomicAdjustment",
    "DiscountKeys\.ManualPrefix"
) | Where-Object { $facade -match $_ }
if ($facadeLeaks.Count -gt 0) {
    Fail "SaleRepository F3 facade must delegate stock-ledger SQL and movement rules: $($facadeLeaks -join ', ')"
} else {
    Pass "SaleRepository F3 facade contains fallback and delegation only"
}

if ($facade -match "clientSaleId\.Trim\s*\(") {
    Fail "SaleRepository must pass the resolved nonblank client-sale-id through to F3 without trimming it"
} else {
    Pass "SaleRepository passes the resolved nonblank client-sale-id through without trimming it"
}

$forbiddenFacadeLifecycle = @(
    "\.Open(?:Async)?\s*\(",
    "BeginTransaction",
    "\.Commit\s*\(",
    "\.Rollback\s*\("
)
$facadeLifecycleLeaks = @($forbiddenFacadeLifecycle | Where-Object { $facade -match $_ })
if ($facadeLifecycleLeaks.Count -gt 0) {
    Fail "SaleRepository F3 facade must not own connection or transaction lifecycle: $($facadeLifecycleLeaks -join ', ')"
} else {
    Pass "SaleRepository F3 facade has no connection or transaction lifecycle ownership"
}

$insertSaleScope = Get-Slice `
    $saleRepository `
    "public async Task<long> InsertSaleAsync" `
    "public Task<IReadOnlyList<Sale>> LastSalesAsync"
$refundOrVoidScope = Get-Slice `
    $saleRepository `
    "public async Task<long> InsertRefundOrVoidAsync" `
    "public async Task InsertSaleLinesAsync"
$applyReferenceCount = [regex]::Matches($saleRepository, "\bApplyLocalStockMovementsAsync\s*\(").Count
$applyDeclarationCount = [regex]::Matches(
    $saleRepository,
    "(?m)^\s*public async Task ApplyLocalStockMovementsAsync\s*\(").Count
$insertSaleApplyCount = [regex]::Matches($insertSaleScope, "\bApplyLocalStockMovementsAsync\s*\(").Count
$refundOrVoidApplyCount = [regex]::Matches($refundOrVoidScope, "\bApplyLocalStockMovementsAsync\s*\(").Count
$staleSaleRepositoryF3Markers = @(
    "local_stock_movements",
    "product_meta",
    "stock_qty",
    "movement_key",
    "quantity_delta",
    "movement_kind",
    "sale_decrement",
    "refund_increment",
    "void_reverse",
    "Math\.Abs\(line\.Quantity\)",
    "line\.Id\.ToString\(CultureInfo\.InvariantCulture\)"
) | Where-Object { $saleRepository -match $_ }
if ($applyReferenceCount -ne 3 -or $applyDeclarationCount -ne 1 -or
    $insertSaleApplyCount -ne 1 -or $refundOrVoidApplyCount -ne 1 -or
    $staleSaleRepositoryF3Markers.Count -gt 0) {
    Fail "SaleRepository must retain only the two F3 orchestration call sites and facade, without stale ledger/stock-rule ownership. References=$applyReferenceCount; declarations=$applyDeclarationCount; insert=$insertSaleApplyCount; refundVoid=$refundOrVoidApplyCount; stale=$($staleSaleRepositoryF3Markers -join ', ')"
} else {
    Pass "SaleRepository retains only F3 orchestration call sites and no ledger/stock-rule ownership"
}

if ($writer -notmatch "internal sealed class SaleStockMovementWriter" -or
    $writer -notmatch "internal async Task ApplyAsync\(\s*SqliteConnection conn,\s*SqliteTransaction tx,\s*Sale sale,\s*IReadOnlyList<SaleLine> lines,\s*string clientSaleId\s*\)") {
    Fail "SaleStockMovementWriter must expose the caller-owned F3 ApplyAsync surface"
} else {
    Pass "SaleStockMovementWriter exposes the caller-owned F3 ApplyAsync surface"
}

$requiredWriterRules = @(
    "line.Quantity == 0",
    "DiscountKeys\.IsEconomicAdjustment\(barcode\)",
    "DiscountKeys\.ManualPrefix",
    "Math\.Abs\(line.Quantity\)",
    "refund_increment",
    "void_reverse",
    "sale_decrement",
    'clientSaleId \+ ":"',
    "line.Id\.ToString\(CultureInfo\.InvariantCulture\)",
    "line\.Id == 0 \? \(long\?\)null : line\.Id",
    "INSERT\s+OR\s+IGNORE\s+INTO\s+local_stock_movements",
    "movement_key, sale_id, sale_line_id, barcode, quantity_delta, movement_kind, created_at",
    "UPDATE\s+product_meta",
    "WHEN stock_qty \+ @quantityDelta < 0 THEN 0"
)
$missingWriterRules = @($requiredWriterRules | Where-Object { $writer -notmatch $_ })
if ($missingWriterRules.Count -gt 0) {
    Fail "SaleStockMovementWriter must retain F3 ledger, reversal and clamp rules: $($missingWriterRules -join ', ')"
} else {
    Pass "SaleStockMovementWriter retains ledger, reversal and clamp rules"
}

$insertIndex = $writer.IndexOf("INSERT OR IGNORE INTO local_stock_movements", [System.StringComparison]::Ordinal)
$duplicateGuardIndex = $writer.IndexOf("if (inserted == 0)", [System.StringComparison]::Ordinal)
$updateIndex = $writer.IndexOf("UPDATE product_meta", [System.StringComparison]::Ordinal)
if ($insertIndex -lt 0 -or $duplicateGuardIndex -le $insertIndex -or $updateIndex -le $duplicateGuardIndex -or
    $writer -notmatch "if \(inserted == 0\)\s*\{\s*continue;") {
    Fail "stock must update only after a newly inserted idempotent ledger movement"
} else {
    Pass "stock updates only after a newly inserted idempotent ledger movement"
}

$ledgerWriteStart = $writer.IndexOf("var inserted = await conn.ExecuteAsync", [System.StringComparison]::Ordinal)
$ledgerWrite = Get-Slice $writer "var inserted = await conn.ExecuteAsync" "if (inserted == 0)"
$stockSearchStart = if ($duplicateGuardIndex -ge 0) { $duplicateGuardIndex } else { 0 }
$stockWriteStart = $writer.IndexOf(
    "await conn.ExecuteAsync",
    $stockSearchStart,
    [System.StringComparison]::Ordinal)
$stockWrite = Get-Until $writer $stockWriteStart ").ConfigureAwait(false);"
$writerExecuteCount = [regex]::Matches($writer, "ExecuteAsync\s*\(").Count
$ledgerExecuteCount = [regex]::Matches($ledgerWrite, "ExecuteAsync\s*\(").Count
$stockExecuteCount = [regex]::Matches($stockWrite, "ExecuteAsync\s*\(").Count
$callerTransactionArgument = '\},\s*(?:tx|transaction\s*:\s*tx)\)\.ConfigureAwait\(false\)'
if ($writerExecuteCount -ne 2 -or $ledgerWriteStart -lt 0 -or $stockWriteStart -le $duplicateGuardIndex -or
    $ledgerExecuteCount -ne 1 -or $stockExecuteCount -ne 1 -or
    $ledgerWrite -notmatch "local_stock_movements" -or
    $ledgerWrite -notmatch $callerTransactionArgument -or
    $stockWrite -notmatch "UPDATE\s+product_meta" -or
    $stockWrite -notmatch $callerTransactionArgument) {
    Fail "SaleStockMovementWriter must retain exactly two bounded ExecuteAsync calls, each using the caller transaction"
} else {
    Pass "SaleStockMovementWriter has exactly two bounded ExecuteAsync calls and both use the caller transaction"
}

$forbiddenWriterLifecycle = @(
    "_factory",
    "\.Open(?:Async)?\s*\(",
    "BeginTransaction",
    "\.Commit\s*\(",
    "\.Rollback\s*\(",
    "EnsureClientSaleId",
    "BuildClientSaleId",
    "clientSaleId\.Trim\s*\(",
    "sales_sync_outbox",
    "EnqueueSalesSyncOutboxAsync",
    "(?i)\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM)\s+sales\b",
    "(?i)\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|UPDATE|DELETE\s+FROM)\s+sale_lines\b",
    "\bconn\.(?:Close|CloseAsync|Dispose|DisposeAsync)\s*\(",
    "\btx\.(?:Dispose|DisposeAsync)\s*\(",
    "(?m)^\s*using\s+(?:var\s+|\(|(?:SqliteConnection|SqliteTransaction)\s+)"
)
$writerLifecycleLeaks = @($forbiddenWriterLifecycle | Where-Object { $writer -match $_ })
if ($writerLifecycleLeaks.Count -gt 0) {
    Fail "SaleStockMovementWriter must not own connection, transaction, header, line or outbox work: $($writerLifecycleLeaks -join ', ')"
} else {
    Pass "SaleStockMovementWriter has no lifecycle, header, line or outbox ownership leak"
}

$requiredTests = @(
    "SaleStockMovementWriter_AndSaleFacade_KeepMovementParity",
    "SaleStockMovementWriter_OrdinaryNegativeQuantityUsesNegativeAbsoluteDelta",
    "SaleStockMovementWriter_SkipsBlankZeroAndReservedEconomicLines",
    "SaleStockMovementWriter_DuplicateLineIsIdempotentAndStockClampsAtZero",
    "SaleStockMovementWriter_MissingProductMetadataKeepsLedgerOnly",
    "SaleStockMovementWriter_ZeroLineIdStoresNullSaleLineId",
    "SaleStockMovementWriter_UsesRefundAndVoidIncrementMappings",
    "SaleStockMovementWriter_CallerTransactionRollbackLeavesNoLedgerOrStockMutation",
    "SaleFacade_CallerTransactionRollbackLeavesNoLedgerOrStockMutation",
    "SaleFacade_EmptyLinesAndBlankClientSaleIdReturnsBeforeFallbackOrDelegation",
    "SaleFacade_BlankClientSaleIdFallsBackBeforeDelegatingToStockMovementWriter"
)
$missingTests = @($requiredTests | Where-Object {
    -not (Test-MethodDeclared -text $tests -methodName $_)
})
if ($tests -notmatch "new SaleStockMovementWriter" -or
    $tests -notmatch "new SaleRepository" -or
    $tests -notmatch "tx.Rollback\(\)" -or
    $tests -notmatch "Array.Empty<SaleLine>\(\)" -or
    $tests -notmatch "Assert.IsNull\(sale.ClientSaleId\)" -or
    $tests -notmatch "win7pos-sale-" -or
    $tests -notmatch "DISC:F3-SKIP" -or
    $tests -notmatch "MANUAL:F3-SKIP" -or
    $missingTests.Count -gt 0) {
    Fail "F3 [TestMethod]-decorated direct parity, negative-sale delta, skip, duplicate/clamp, ledger-only, reversal, facade/direct rollback, empty-input and fallback regressions are incomplete: $($missingTests -join ', ')"
} else {
    Pass "F3 direct parity, negative-sale delta, skip, duplicate/clamp, ledger-only, reversal, facade/direct rollback, empty-input and fallback regressions are present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
