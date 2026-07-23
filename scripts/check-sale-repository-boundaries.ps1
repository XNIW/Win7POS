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

$required = @(
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleReadRepository.cs",
    "tests/Win7POS.Core.Tests/Data/SaleReadRepositoryTests.cs"
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
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/SaleReadRepositoryTests.cs"

$f1ReadMethods = @(
    "LastSalesAsync",
    "GetSalesBetweenAsync",
    "GetDailySummaryAsync",
    "GetDailySummariesAsync",
    "GetDailySummariesRangeAsync",
    "GetSalesForDateAsync",
    "GetHourlySalesAsync",
    "GetByIdAsync",
    "GetByCodeLikeAsync"
)

if ($saleRepository -notmatch "private readonly SaleReadRepository _reads") {
    Fail "SaleRepository must retain the SaleReadRepository collaborator"
}

$missingFacadeDelegations = @($f1ReadMethods | Where-Object {
    $saleRepository -notmatch ("_reads\s*\.\s*{0}\s*\(" -f [regex]::Escape($_))
})
if ($missingFacadeDelegations.Count -gt 0) {
    Fail "SaleRepository must delegate every F1 read: $($missingFacadeDelegations -join ', ')"
} else {
    Pass "SaleRepository delegates every F1 read through SaleReadRepository"
}

$declaredReadMethods = @(
    [regex]::Matches(
        $saleReadRepository,
        '(?m)^\s*internal\s+(?:async\s+)?Task(?:<[^\r\n]+>)?\s+([A-Za-z0-9_]+)\s*\(') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique
)
$missingReadMethods = @($f1ReadMethods | Where-Object { $_ -notin $declaredReadMethods })
$unexpectedReadMethods = @($declaredReadMethods | Where-Object { $_ -notin $f1ReadMethods })
if ($missingReadMethods.Count -gt 0 -or $unexpectedReadMethods.Count -gt 0) {
    Fail "SaleReadRepository must expose exactly the F1 read surface. Missing: $($missingReadMethods -join ', '); unexpected: $($unexpectedReadMethods -join ', ')"
} else {
    Pass "SaleReadRepository exposes exactly the F1 read surface"
}

$f1SqlMarkers = @(
    "ORDER BY id DESC LIMIT @take",
    "WHERE createdAt >= @fromMs AND createdAt < @toMs",
    "COUNT\(CASE WHEN kind = 0 THEN 1 END\)",
    "date\(createdAt/1000, 'unixepoch', 'localtime'\)",
    "strftime\('%H', datetime\(createdAt/1000, 'unixepoch', 'localtime'\)\)",
    "SELECT length\(receipt_shop_snapshot\)",
    "WHERE code LIKE @pattern"
)
$missingReadSql = @($f1SqlMarkers | Where-Object { $saleReadRepository -notmatch $_ })
$leakedReadSql = @($f1SqlMarkers | Where-Object { $saleRepository -match $_ })
if ($missingReadSql.Count -gt 0 -or $leakedReadSql.Count -gt 0 -or
    $saleReadRepository -notmatch "ReceiptDocumentPolicy\.EnsureValidSnapshotJson" -or
    $saleReadRepository -notmatch "ReceiptDocumentPolicy\.MaxSnapshotJsonCharacters" -or
    $saleReadRepository -notmatch "bool includeFiscalPrinted = true") {
    Fail "F1 sale read SQL, receipt validation or fiscal compatibility parameters are not exclusively owned by SaleReadRepository"
} else {
    Pass "F1 sale read SQL, receipt validation and fiscal compatibility are owned by SaleReadRepository"
}

$forbiddenReadMarkers = @(
    "BeginTransaction",
    "SqliteTransaction",
    "sale_lines",
    "sales_sync_outbox",
    "local_stock_movements",
    "GetLinesBySaleIdAsync",
    "ReadSaleLineBudgetAsync",
    "GetRefundedQtyAsync",
    "GetReturnableLinesAsync",
    "GetReversalEconomics",
    "InsertSaleAsync",
    "InsertRefund",
    "MarkSaleVoided",
    "ApplyLocalStockMovements",
    "EnqueueSalesSyncOutboxAsync",
    "ValidateReversalBoundaryAsync",
    "\.ExecuteAsync\s*\(",
    "(?im)^\s*(INSERT|UPDATE|DELETE|REPLACE)\s+"
)
$forbiddenReadOwnership = @($forbiddenReadMarkers | Where-Object { $saleReadRepository -match $_ })
if ($forbiddenReadOwnership.Count -gt 0) {
    Fail "SaleReadRepository must exclude F1 line/budget/reversal/stock/outbox write ownership: $($forbiddenReadOwnership -join ', ')"
} else {
    Pass "SaleReadRepository excludes F1 line, budget, reversal, stock and outbox writes"
}

$retainedSaleRepositoryMarkers = @(
    "GetLinesBySaleIdAsync",
    "InsertSaleLinesAsync",
    "GetReversalEconomicsSnapshotAsync",
    "InsertSaleAsync",
    "ApplyLocalStockMovementsAsync",
    "EnqueueSalesSyncOutboxAsync"
)
$missingRetainedOwnership = @($retainedSaleRepositoryMarkers | Where-Object { $saleRepository -notmatch $_ })
if ($missingRetainedOwnership.Count -gt 0) {
    Fail "F1 must leave the deferred writer/reversal/stock/outbox boundaries in SaleRepository: $($missingRetainedOwnership -join ', ')"
} else {
    Pass "F1 leaves the deferred writer, reversal, stock and outbox boundaries in SaleRepository"
}

$requiredTests = @(
    "SaleReadRepository_AndSaleFacade_KeepReadParity",
    "SaleReadRepository_DateBoundariesOperatorAndFiscalCompatibilityFlagKeepReadParity",
    "SaleReadRepository_InvalidRangesAndBlankCodeKeepReadParity",
    "SaleReadRepository_GetByIdKeepsReceiptAndReversalSnapshotParity",
    "SaleReadRepository_GetByIdRejectsOversizedReceiptSnapshotParity",
    "SaleReadRepository_ParallelReadsLeavePersistentStateUnchanged"
)
$missingTests = @($requiredTests | Where-Object { $tests -notmatch $_ })
if ($tests -notmatch "new SaleReadRepository" -or
    $tests -notmatch "includeFiscalPrinted:\s*false" -or
    $tests -notmatch "ReceiptContentValidationException" -or
    $missingTests.Count -gt 0) {
    Fail "SaleReadRepository direct parity, boundary, snapshot and parallel-read regressions are incomplete: $($missingTests -join ', ')"
} else {
    Pass "SaleReadRepository direct parity, boundary, snapshot and parallel-read regressions are present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
