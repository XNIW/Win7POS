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
    return [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Get-MethodSlice([string]$text, [string]$methodName, [string]$nextMarker) {
    $pattern = "(?ms)^\s*internal\s+(?:async\s+)?Task(?:<[^\r\n>]+>)?\s+" +
        [regex]::Escape($methodName) +
        "\s*\(.*?(?=\r?\n\s*" + $nextMarker + "|\z)"
    return [regex]::Match($text, $pattern).Value
}

function Get-TestSlice([string]$text, [string]$testName) {
    $pattern = "(?s)public\s+(?:async\s+)?Task\s+" +
        [regex]::Escape($testName) +
        "\s*\(.*?(?=\r?\n\s*\[TestMethod\]|\z)"
    return [regex]::Match($text, $pattern).Value
}

$required = @(
    "docs/specs/PERF_05_REMOTE_PRICE_APPLY_DIAGNOSTICS.md",
    "src/Win7POS.Data/Repositories/RemoteCatalogBatchRepository.cs",
    "src/Win7POS.Data/Repositories/RemotePriceHistoryRepository.cs",
    "tests/Win7POS.Core.Tests/Data/CatalogRunContextPerformanceTests.cs",
    "tests/Win7POS.Core.Tests/Data/CatalogBatchPerformanceScenario.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $path) -PathType Leaf)) {
        Fail "$path missing"
    }
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

$spec = Read-Text "docs/specs/PERF_05_REMOTE_PRICE_APPLY_DIAGNOSTICS.md"
$batch = Read-Text "src/Win7POS.Data/Repositories/RemoteCatalogBatchRepository.cs"
$prices = Read-Text "src/Win7POS.Data/Repositories/RemotePriceHistoryRepository.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/CatalogRunContextPerformanceTests.cs"
$benchmark = Read-Text "tests/Win7POS.Core.Tests/Data/CatalogBatchPerformanceScenario.cs"

$specMarkers = @(
    "CatalogApplyRunDiagnostics\.RemotePriceApply",
    "RemotePriceApplyDiagnostics",
    "SqlCommandCount",
    "SqlStatementCount",
    "page-local",
    "tx\.Commit\(\)",
    "16 SQL commands",
    "18 SQL statements",
    "not a performance budget",
    "rollback"
)
$missingSpecMarkers = @($specMarkers | Where-Object { $spec -notmatch $_ })
if ($missingSpecMarkers.Count -gt 0) {
    Fail "PERF-05 specification is missing contract markers: $($missingSpecMarkers -join ', ')"
} else {
    Pass "PERF-05 specification records the diagnostic contract and non-threshold baseline"
}

if ($batch -notmatch "public\s+sealed\s+class\s+RemotePriceApplyDiagnostics" -or
    $batch -notmatch "public\s+long\s+SetBasedPageCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "public\s+long\s+StagedRowCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "public\s+long\s+PreparedCommandCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "public\s+long\s+FallbackPageCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "public\s+long\s+SqlCommandCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "public\s+long\s+SqlStatementCount\s*\{\s*get;\s*internal\s+set;\s*\}" -or
    $batch -notmatch "internal\s+void\s+RecordSqlCommand\s*\(\s*int\s+statementCount\s*\)") {
    Fail "RemotePriceApplyDiagnostics must expose set-based usage and internal-write SQL counters"
} else {
    Pass "RemotePriceApplyDiagnostics exposes set-based usage and the constrained SQL contract"
}

if ($batch -notmatch "public\s+RemotePriceApplyDiagnostics\s+RemotePriceApply\s*\{\s*get;\s*\}\s*=\s*new\s+RemotePriceApplyDiagnostics\s*\(\s*\)") {
    Fail "CatalogApplyRunDiagnostics must expose one read-only RemotePriceApply aggregate"
} else {
    Pass "CatalogApplyRunDiagnostics exposes one read-only RemotePriceApply aggregate"
}

$applySlice = Get-MethodSlice $batch "ApplyWithinRunAsync" "private\s+static\s+void\s+ValidateBatchContent"
if ([string]::IsNullOrWhiteSpace($applySlice)) {
    Fail "ApplyWithinRunAsync implementation slice is missing"
} else {
    $localIndex = $applySlice.IndexOf("var pageRemotePriceApply = new RemotePriceApplyDiagnostics()", [System.StringComparison]::Ordinal)
    $setBasedIndex = $applySlice.IndexOf("TryApplyRemotePricesSetBasedInTransactionAsync", [System.StringComparison]::Ordinal)
    $prepareIndex = $applySlice.IndexOf("PrepareAuthoritativeRemotePriceRepairAsync", [System.StringComparison]::Ordinal)
    $upsertIndex = $applySlice.IndexOf("UpsertOrQueueRemotePriceHistoryInTransactionAsync", [System.StringComparison]::Ordinal)
    $pendingCalls = @([regex]::Matches($applySlice, "ApplyPendingRemotePricesInTransactionAsync[\s\S]{0,240}pageRemotePriceApply"))
    $prepareHasDiagnostics = $prepareIndex -ge 0 -and
        $applySlice.Substring($prepareIndex, [Math]::Min(700, $applySlice.Length - $prepareIndex)) -match "pageRemotePriceApply"
    $upsertHasDiagnostics = $upsertIndex -ge 0 -and
        $applySlice.Substring($upsertIndex, [Math]::Min(700, $applySlice.Length - $upsertIndex)) -match "pageRemotePriceApply"
    if ($localIndex -lt 0 -or
        $setBasedIndex -lt 0 -or
        $pendingCalls.Count -lt 2 -or
        -not $prepareHasDiagnostics -or
        -not $upsertHasDiagnostics) {
        Fail "each catalog page must use one fresh diagnostic through set-based, pending and fallback price paths"
    } else {
        Pass "each catalog page passes its fresh diagnostic through set-based, pending and fallback price paths"
    }

    $commitIndex = $applySlice.IndexOf("tx.Commit()", [System.StringComparison]::Ordinal)
    $publishMatches = @([regex]::Matches(
            $applySlice,
            "runContext\.RecordRemotePriceApply\(\s*pageRemotePriceApply\s*\)"))
    $publishIndex = if ($publishMatches.Count -eq 1) { $publishMatches[0].Index } else { -1 }
    if ($commitIndex -lt 0 -or
        $publishMatches.Count -ne 1 -or
        $publishIndex -le $commitIndex) {
        Fail "page remote-price diagnostics must be recorded exactly once and only after tx.Commit()"
    } else {
        Pass "page remote-price diagnostics are published exactly once after commit"
    }
}

if ($batch -notmatch "internal\s+void\s+RecordRemotePriceApply\s*\(\s*RemotePriceApplyDiagnostics\s+[A-Za-z_][A-Za-z0-9_]*\s*\)" -or
    $batch -notmatch "Diagnostics\.RemotePriceApply\.MergeFrom\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\)") {
    Fail "run context must merge the page-local remote-price diagnostic into the public aggregate"
} else {
    Pass "run context merges only the page-local remote-price diagnostic"
}

$priceMarkers = @(
    "RemotePriceApplyDiagnostics\s+diagnostics\s*=\s*null",
    "RecordSqlCommand\(\s*RemotePriceApplyDiagnostics\s+diagnostics\s*,\s*int\s+statementCount(?:\s*=\s*1)?\s*\)",
    "diagnostics\?\.RecordSqlCommand\(\s*statementCount\s*\)",
    "statementCount\s*:\s*2"
)
$missingPriceMarkers = @($priceMarkers | Where-Object { $prices -notmatch $_ })
if ($missingPriceMarkers.Count -gt 0) {
    Fail "remote-price SQL instrumentation is incomplete: $($missingPriceMarkers -join ', ')"
} else {
    Pass "remote-price helpers retain optional SQL instrumentation including the two-statement ownership write"
}

$baselineTest = Get-TestSlice $tests "PriceOnlyPagesPublishExactRemotePriceApplyDiagnostics"
if ([string]::IsNullOrWhiteSpace($baselineTest) -or
    $baselineTest -notmatch "Assert\.AreEqual\(\s*16L?\s*,[\s\S]{0,180}RemotePriceApply\.SqlCommandCount" -or
    $baselineTest -notmatch "Assert\.AreEqual\(\s*18L?\s*,[\s\S]{0,180}RemotePriceApply\.SqlStatementCount" -or
    $baselineTest -notmatch "RemotePriceApply\.SetBasedPageCount" -or
    $baselineTest -notmatch "RemotePriceApply\.FallbackPageCount") {
    Fail "the price-only test must assert the exact set-based 16-command/18-statement contract"
} else {
    Pass "the price-only test asserts the exact set-based 16-command/18-statement contract"
}

$rollbackTest = Get-TestSlice $tests "FailedPricePageDoesNotPublishRemotePriceApplyDiagnostics"
if ([string]::IsNullOrWhiteSpace($rollbackTest) -or
    $rollbackTest -notmatch "RemotePriceApply\.SqlCommandCount" -or
    $rollbackTest -notmatch "RemotePriceApply\.SqlStatementCount" -or
    $rollbackTest -notmatch "Assert\.AreEqual\(\s*0L?\s*,") {
    Fail "the failed-page test must prove that rollback publishes zero remote-price diagnostics"
} else {
    Pass "the failed-page test proves rollback publishes no remote-price diagnostics"
}

$benchmarkMarkers = @(
    '"batch-price-only"',
    "RemotePriceApplySqlCommandCount",
    "RemotePriceApplySqlStatementCount",
    "RemotePriceApplySetBasedPageCount",
    "RemotePriceApplyStagedRowCount",
    "remote_price_apply_sql_commands",
    "remote_price_apply_sql_statements",
    "remote_price_apply_set_based_pages",
    "remote_price_apply_staged_rows"
)
$missingBenchmarkMarkers = @($benchmarkMarkers | Where-Object { $benchmark -notmatch $_ })
if ($missingBenchmarkMarkers.Count -gt 0) {
    Fail "batch-price-only benchmark evidence is incomplete: $($missingBenchmarkMarkers -join ', ')"
} else {
    Pass "batch-price-only benchmark reports remote-price diagnostic evidence without a threshold"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
