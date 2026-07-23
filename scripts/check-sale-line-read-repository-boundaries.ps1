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

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleReadRepository.cs",
    "src/Win7POS.Data/Repositories/SaleLineReadRepository.cs",
    "tests/Win7POS.Core.Tests/Data/SaleLineReadRepositoryTests.cs"
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
$saleReadRepository = Read-Text "src/Win7POS.Data/Repositories/SaleReadRepository.cs"
$saleLineReadRepository = Read-Text "src/Win7POS.Data/Repositories/SaleLineReadRepository.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleLineReadRepositoryTests.cs"

if ($saleRepository -notmatch "private readonly SaleLineReadRepository _lineReads" -or
    $saleRepository -notmatch "new SaleLineReadRepository\(factory\)" -or
    $saleRepository -notmatch "_lineReads\s*\.\s*GetLinesBySaleIdAsync\s*\(\s*saleId\s*\)") {
    Fail "SaleRepository must retain the F2 line-reader collaborator and public facade delegation"
} else {
    Pass "SaleRepository delegates persisted line reads through SaleLineReadRepository"
}

$lineFacadeStart = $saleRepository.IndexOf("public Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync", [System.StringComparison]::Ordinal)
$lineFacadeEnd = $saleRepository.IndexOf("public Task<bool> IsVoidedAsync", $lineFacadeStart, [System.StringComparison]::Ordinal)
$lineFacade = if ($lineFacadeStart -ge 0 -and $lineFacadeEnd -gt $lineFacadeStart) {
    $saleRepository.Substring($lineFacadeStart, $lineFacadeEnd - $lineFacadeStart)
} else {
    ""
}
if ($lineFacade -notmatch "_lineReads\s*\.\s*GetLinesBySaleIdAsync\s*\(\s*saleId\s*\)" -or
    $lineFacade -match "(FROM\s+sale_lines|BeginTransaction|QueryAsync<SaleLine>)") {
    Fail "F2 public line-read facade must delegate without retaining reader SQL or transactions"
} else {
    Pass "F2 public line-read facade contains delegation only"
}

$obsoleteSaleRepositoryMarkers = @(
    "ReadSaleLineBudgetAsync",
    "SaleLineBudgetRow",
    "COALESCE\(MAX\(LENGTH\(COALESCE\(name, ''\)\), 0\) AS MaximumNameCharacters"
)
$obsoleteSaleRepositoryOwnership = @($obsoleteSaleRepositoryMarkers | Where-Object { $saleRepository -match $_ })
if ($obsoleteSaleRepositoryOwnership.Count -gt 0) {
    Fail "SaleRepository must not retain F2 budget implementation details: $($obsoleteSaleRepositoryOwnership -join ', ')"
} else {
    Pass "SaleRepository no longer owns F2 budget SQL or rows"
}

$insertLines = Get-Slice `
    $saleRepository `
    "public async Task InsertSaleLinesAsync" `
    "public async Task<string> EnsureClientSaleIdAsync"
if ($insertLines -notmatch "ReferenceEquals\(tx.Connection, conn\)" -or
    $insertLines -notmatch "SalesReceiptContentPolicy\.EnsureValidLines\(lines\)" -or
    $insertLines -notmatch "SaleLineReadRepository\s*\.\s*EnsureStoredLineBudgetAsync\(conn, tx, group.Key\)" -or
    $insertLines -notmatch "EnsureCumulativeLineBudget\(" -or
    $insertLines -notmatch "InsertSaleLineAsync\(conn, tx, line\)") {
    Fail "caller-owned sale-line writes must reuse the F2 budget guard before cumulative validation and inserts"
} else {
    Pass "caller-owned sale-line writes reuse the F2 budget guard before inserts"
}

$declaredLineReadMethods = @(
    [regex]::Matches(
        $saleLineReadRepository,
        '(?m)^\s*internal\s+(?:static\s+)?(?:async\s+)?Task(?:<[^\r\n]+>)?\s+([A-Za-z0-9_]+)\s*\(') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique
)
$expectedLineReadMethods = @(
    "EnsureStoredLineBudgetAsync",
    "GetLinesBySaleIdAsync"
)
$missingLineReadMethods = @($expectedLineReadMethods | Where-Object { $_ -notin $declaredLineReadMethods })
$unexpectedLineReadMethods = @($declaredLineReadMethods | Where-Object { $_ -notin $expectedLineReadMethods })
if ($saleLineReadRepository -notmatch "internal sealed class SaleLineReadRepository" -or
    $missingLineReadMethods.Count -gt 0 -or $unexpectedLineReadMethods.Count -gt 0) {
    Fail "SaleLineReadRepository must expose exactly the F2 read/budget surface. Missing: $($missingLineReadMethods -join ', '); unexpected: $($unexpectedLineReadMethods -join ', ')"
} else {
    Pass "SaleLineReadRepository exposes exactly the F2 read/budget surface"
}

$readMethod = Get-Slice `
    $saleLineReadRepository `
    "internal async Task<IReadOnlyList<SaleLine>> GetLinesBySaleIdAsync" `
    "internal static async Task<SaleLineBudgetRow> EnsureStoredLineBudgetAsync"
$readStart = $readMethod.IndexOf("GetLinesBySaleIdAsync", [System.StringComparison]::Ordinal)
$deferredSnapshot = $readMethod.IndexOf("conn.BeginTransaction(deferred: true)", [System.StringComparison]::Ordinal)
$budgetPreflight = $readMethod.IndexOf("EnsureStoredLineBudgetAsync(conn, tx, saleId)", [System.StringComparison]::Ordinal)
$materialize = $readMethod.IndexOf("QueryAsync<SaleLine>", [System.StringComparison]::Ordinal)
$lineValidation = $readMethod.IndexOf("SalesReceiptContentPolicy.EnsureValidLines(result)", [System.StringComparison]::Ordinal)
$commit = $readMethod.IndexOf("tx.Commit()", [System.StringComparison]::Ordinal)
if ($readStart -lt 0 -or $deferredSnapshot -le $readStart -or
    $budgetPreflight -le $deferredSnapshot -or $materialize -le $budgetPreflight -or
    $lineValidation -le $materialize -or $commit -le $lineValidation -or
    $readMethod -notmatch "QueryAsync<SaleLine>\([\s\S]*?new\s*\{\s*saleId\s*\}\s*,\s*tx\s*\)" -or
    $readMethod -notmatch "FROM sale_lines" -or $readMethod -notmatch "ORDER BY id ASC") {
    Fail "line reader must keep the deferred snapshot, shared transaction, budget preflight, ordered materialization and post-read validation sequence"
} else {
    Pass "line reader keeps the F2 deferred snapshot, shared transaction and validation sequence"
}

$budgetGuard = Get-Slice `
    $saleLineReadRepository `
    "internal static async Task<SaleLineBudgetRow> EnsureStoredLineBudgetAsync" `
    "private static async Task<SaleLineBudgetRow> ReadSaleLineBudgetAsync"
$budgetImplementation = Get-Slice `
    $saleLineReadRepository `
    "internal static async Task<SaleLineBudgetRow> EnsureStoredLineBudgetAsync" `
    "internal sealed class SaleLineBudgetRow"
$budgetQuery = Get-Slice `
    $saleLineReadRepository `
    "private static async Task<SaleLineBudgetRow> ReadSaleLineBudgetAsync" `
    "internal sealed class SaleLineBudgetRow"
if ($budgetGuard -notmatch "SqliteConnection conn" -or
    $budgetGuard -notmatch "SqliteTransaction tx" -or
    $budgetGuard -notmatch "ReadSaleLineBudgetAsync\(conn, tx, saleId\)" -or
    $budgetGuard -notmatch "SalesReceiptContentPolicy\.EnsureStoredLineBudget\(") {
    Fail "F2 budget guard must consume the caller connection/transaction and retain receipt budget validation"
} else {
    Pass "F2 budget guard consumes the caller connection/transaction"
}

$budgetSqlMarkers = @(
    "COUNT\(1\) AS LineCount",
    "MaximumNameCharacters",
    "MaximumBarcodeCharacters",
    "AggregateNameCharacters",
    "AggregateNameUtf8Bytes",
    "LENGTH\(CAST\(COALESCE\(name, ''\) AS BLOB\)\)"
)
$missingBudgetSql = @($budgetSqlMarkers | Where-Object { $budgetQuery -notmatch $_ })
if ($budgetQuery -notmatch "QuerySingleAsync<SaleLineBudgetRow>\([\s\S]*?new\s*\{\s*saleId\s*\}\s*,\s*tx\s*\)" -or
    $missingBudgetSql.Count -gt 0) {
    Fail "F2 budget query must retain every receipt limit projection and the caller transaction: $($missingBudgetSql -join ', ')"
} else {
    Pass "F2 budget query retains receipt limit projections and the caller transaction"
}

$forbiddenBudgetLifecycle = @(
    "_factory",
    "\.Open(?:Async)?\s*\(",
    "BeginTransaction",
    "\.Commit\s*\(",
    "\.Rollback\s*\(",
    "\.ExecuteAsync\s*\(",
    "\.ExecuteScalarAsync\s*\(",
    "(?i)\b(INSERT|UPDATE|DELETE|REPLACE)\b"
)
$leakedBudgetLifecycle = @($forbiddenBudgetLifecycle | Where-Object { $budgetImplementation -match $_ })
if ($leakedBudgetLifecycle.Count -gt 0) {
    Fail "F2 budget implementation must not open or own a transaction or write: $($leakedBudgetLifecycle -join ', ')"
} else {
    Pass "F2 budget implementation has no connection, transaction or write ownership leak"
}

$forbiddenReaderOwnership = @(
    "\.ExecuteAsync\s*\(",
    "\.ExecuteScalarAsync\s*\(",
    "(?i)\b(INSERT|UPDATE|DELETE|REPLACE)\b",
    "sales_sync_outbox",
    "local_stock_movements",
    "InsertSaleAsync",
    "InsertRefund",
    "InsertSaleLinesAsync",
    "ApplyLocalStockMovements",
    "EnqueueSalesSyncOutboxAsync",
    "ValidateReversalBoundary",
    "GetReversalEconomics",
    "PosReversalEconomics",
    "EnsureClientSaleId"
)
$leakedReaderOwnership = @($forbiddenReaderOwnership | Where-Object { $saleLineReadRepository -match $_ })
if ($leakedReaderOwnership.Count -gt 0) {
    Fail "SaleLineReadRepository must exclude write/outbox/reversal/stock ownership: $($leakedReaderOwnership -join ', ')"
} else {
    Pass "SaleLineReadRepository excludes write, outbox, reversal and stock ownership"
}

$f1ForbiddenLineMarkers = @(
    "GetLinesBySaleIdAsync",
    "EnsureStoredLineBudgetAsync",
    "ReadSaleLineBudgetAsync",
    "sale_lines"
)
$f1ReaderLeak = @($f1ForbiddenLineMarkers | Where-Object { $saleReadRepository -match $_ })
if ($f1ReaderLeak.Count -gt 0) {
    Fail "SaleReadRepository must remain outside the F2 line/budget slice: $($f1ReaderLeak -join ', ')"
} else {
    Pass "SaleReadRepository remains outside the F2 line/budget slice"
}

$requiredTests = @(
    "SaleLineReadRepository_AndSaleFacade_KeepOrderedFullLineParity",
    "SaleLineReadRepository_AndSaleFacade_KeepEmptyResultParity",
    "SaleLineReadRepository_RejectsOversizedAggregateAndUtf8HistoricalBudgetParity",
    "SaleLineReadRepository_ParallelReadsLeavePersistentStateUnchanged",
    "SaleLineReadRepository_StoredBudgetGuardUsesCallerTransactionWithoutCommit"
)
$missingTests = @($requiredTests | Where-Object { $tests -notmatch $_ })
if ($tests -notmatch "new SaleLineReadRepository" -or
    $tests -notmatch "new SaleRepository" -or
    $tests -notmatch "ReceiptContentValidationException" -or
    $tests -notmatch "u754c" -or
    $tests -notmatch "MaxSaleLineBarcodeCharacters" -or
    $tests -notmatch "MaxSaleLines \+ 1" -or
    $tests -notmatch "EnsureStoredLineBudgetAsync\(conn, tx, saleId\)" -or
    $missingTests.Count -gt 0) {
    Fail "F2 direct parity, historical budget, parallel-read and caller-transaction regressions are incomplete: $($missingTests -join ', ')"
} else {
    Pass "F2 direct parity, historical budget, parallel-read and caller-transaction regressions are present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
