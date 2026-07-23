Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Read-RepoFile {
    param([string]$RelativePath)
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required source file missing: $RelativePath"
    }
    return [System.IO.File]::ReadAllText($path)
}

function Check {
    param([bool]$Condition, [string]$Pass, [string]$Fail)
    if ($Condition) {
        Write-Host "PASS: $Pass"
    }
    else {
        Write-Host "FAIL: $Fail"
        $failures.Add($Fail) | Out-Null
    }
}

$core = Read-RepoFile "src/Win7POS.Core/Products/ProductPaging.cs"
$repository = Read-RepoFile "src/Win7POS.Data/Repositories/ProductRepository.cs"
$queryRepository = Read-RepoFile "src/Win7POS.Data/Repositories/ProductQueryRepository.cs"
$workflow = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsWorkflowService.cs"
$viewModel = Read-RepoFile "src/Win7POS.Wpf/Products/ProductsViewModel.cs"
$catalogEvents = Read-RepoFile "src/Win7POS.Wpf/Infrastructure/CatalogEvents.cs"
$catalogPull = Read-RepoFile "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$coreTests = Read-RepoFile "tests/Win7POS.Core.Tests/Products/ProductPagingTests.cs"
$repositoryTests = Read-RepoFile "tests/Win7POS.Core.Tests/Data/ProductRepositoryPagingTests.cs"
$repositoryPerformanceTests = Read-RepoFile "tests/Win7POS.Core.Tests/Data/ProductRepositoryPagingPerformanceTests.cs"
$planTests = Read-RepoFile "tests/Win7POS.Core.Tests/Data/ProductQueryPlanTests.cs"
$wpfSmokeProgram = Read-RepoFile "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$wpfPagingSmoke = Read-RepoFile "tests/Win7POS.Wpf.UiSmokeHarness/ProductPagingWpfSmoke.cs"
$wpfPagingSmokeRunner = Read-RepoFile "scripts/run-product-paging-dispatcher-smoke.ps1"
$ciWorkflow = Read-RepoFile ".github/workflows/ci.yml"

Check ($core -match 'class\s+ProductPageCursor' -and
       $core -match 'ExactRank' -and
       $core -match 'Barcode' -and
       $core -match 'Id' -and
       $core -match 'FilterFingerprint' -and
       $core -match 'CatalogRevision') `
    "cursor is bound to exact-rank/barcode/id/filter/revision" `
    "product cursor is missing stable tuple or filter/revision binding"

Check ($core -match 'SHA256\.Create' -and
       $core -match 'maximumAnchors' -and
       $core -match 'Encoding\.UTF8\.GetBytes' -and
       $core -match 'StateVersion' -and
       $core -match 'OffsetFallback' -and
       $core -match 'ProductPageQueryKind\.Reverse') `
    "fingerprint, SQLite BINARY comparison, state fencing, bounded anchors, reverse paging and explicit fallback are present" `
    "pure paging policy is missing collation/state/fingerprint/bounds/reverse/fallback"

$forwardedQueryMethods = @(
    'GetByBarcodeAsync',
    'GetByBarcodesAsync',
    'GetByIdAsync',
    'ListAllAsync',
    'SearchAsync',
    'SearchDetailsAsync',
    'CountDetailsAsync',
    'GetCatalogStatsAsync',
    'SearchDetailsPageAsync',
    'GetDetailsByIdAsync',
    'GetDetailsByBarcodeAsync',
    'GetPriceHistoryByBarcodeAsync',
    'ListAllDetailsAsync',
    'ListDetailsByBarcodesAsync',
    'ListAllPriceHistoryAsync',
    'CountActiveRemoteProductsAsync'
)
$missingForwardedQueryMethods = @($forwardedQueryMethods | Where-Object {
    $repository -notmatch ("_queries\." + [regex]::Escape($_) + "\(")
})

Check ($repository -match 'private\s+readonly\s+ProductQueryRepository\s+_queries;' -and
       $missingForwardedQueryMethods.Count -eq 0) `
    "public ProductRepository remains a complete forwarding facade for query reads" `
    "ProductRepository no longer forwards every public query read through ProductQueryRepository"

Check ($queryRepository -match 'p\.barcode\s+COLLATE\s+BINARY\s+"\s*\+\s*barcodeComparison' -and
       $queryRepository -match 'p\.id\s+"\s*\+\s*idComparison' -and
       $queryRepository -match 'p\.barcode\s+COLLATE\s+BINARY\s+"\s*\+\s*direction' -and
       $queryRepository -match 'p\.id\s+"\s*\+\s*direction') `
    "Data keyset uses BINARY barcode and id in predicates/order" `
    "Data keyset predicate/order is not the stable barcode/id tuple"

Check ($queryRepository -match 'plan\.Kind\s*==\s*ProductPageQueryKind\.OffsetFallback' -and
       $queryRepository -match 'OFFSET\s+@offset' -and
       $queryRepository -match 'ProductPageQueryKind\.Forward' -and
       $queryRepository -match 'ProductPageQueryKind\.Reverse') `
    "OFFSET is isolated behind the explicit arbitrary-jump plan" `
    "OFFSET fallback is not explicitly isolated from ordinary navigation"

Check ($workflow -match 'ProductPagingCoordinator' -and
       $workflow -match 'SemaphoreSlim' -and
       $workflow -match 'Task\.Run\([\s\S]{0,180}SearchDetailsPageAsync' -and
       $workflow -match 'CatalogEvents\.Revision' -and
       $workflow -match 'SearchDetailsPageAsync\(filter,\s*plan\)' -and
       $workflow -match '_paging\.Accept') `
    "workflow coordinates revision-fenced pages and accepts only completed queries" `
    "workflow does not revision-fence and coordinate keyset results"

Check ($catalogEvents -match 'AdvanceRevision\(\)\s*=>\s*Interlocked\.Increment\(ref\s+_revision\)' -and
       $catalogEvents -match 'Interlocked\.Read\(ref\s+_revision\)' -and
       (($catalogPull | Select-String -Pattern 'CatalogEvents\.AdvanceRevision\(\)' -AllMatches).Matches.Count -ge 3) -and
       ($catalogPull -match 'ReconcileAndVerifyStagedAsync[\s\S]{0,3000}finally[\s\S]{0,300}CatalogEvents\.AdvanceRevision\(\)') -and
       ($workflow -match 'UpdateAsync\([\s\S]{0,2200}CatalogEvents\.RaiseCatalogChanged') -and
       ($workflow -match 'UpdateProductPricesAsync\([\s\S]{0,800}CatalogEvents\.AdvanceRevision')) `
    "catalog mutation events expose an x86-safe monotonic revision" `
    "catalog revision is missing or not atomic on x86"

Check ($wpfSmokeProgram -match '--product-paging-dispatcher-smoke' -and
       $wpfPagingSmoke -match 'DispatcherTimer' -and
       $wpfPagingSmoke -match 'pulseCount\s*<=\s*0' -and
       $wpfPagingSmoke -match 'LoadDetailsPageAsync' -and
       $wpfPagingSmokeRunner -match '--product-paging-dispatcher-smoke' -and
       $wpfPagingSmokeRunner -match 'GetTempPath' -and
       $ciWorkflow -match 'run-product-paging-dispatcher-smoke\.ps1') `
    "WPF harness proves dispatcher pulses during a 100k product page load" `
    "WPF dispatcher responsiveness smoke is missing or incomplete"

Check ($viewModel -match 'LoadDetailsPageAsync' -and
       $viewModel -notmatch '\(PageIndex\s*-\s*1\)\s*\*\s*PageSize' -and
       $viewModel -notmatch 'PageIndex\s*\+\+' -and
       $viewModel -notmatch 'PageIndex\s*--') `
    "ViewModel is a thin target-page consumer and advances only after success" `
    "ViewModel still computes OFFSET or advances page state before query success"

Check ($coreTests -match 'duplicate' -and
       $coreTests -match 'SqliteUtf8BinaryOrder' -and
       $coreTests -match 'ConcurrentPlan' -and
       $coreTests -match 'OutOfRangePage' -and
       $coreTests -match 'RevisionMismatch' -and
       $repositoryTests -match 'InsertAndDeleteBetweenPages' -and
       $repositoryTests -match 'TraversesMutatedResultWithoutSkipOrDuplicate' -and
       $repositoryTests -match 'SqliteBinaryUnicodeOrder' -and
       $repositoryTests -match 'ReverseKeyset' -and
       $repositoryTests -match 'OffsetFallback' -and
       $repositoryTests -match 'QueryRepository_AndPublicFacade_ReturnEquivalentKeysetSnapshot' -and
       $repositoryTests -match 'QueryRepository_RejectsPagingPlanForDifferentFilter' -and
       $repositoryTests -match 'QueryRepository_SupportsParallelReadsWithoutSharedQueryState' -and
       $repositoryTests -match 'QueryRepository_BatchLookup_TrimsDeduplicatesAndCrossesSqliteParameterBoundary' -and
       $repositoryPerformanceTests -match 'PRODUCT_REPOSITORY_KEYSET_100K' -and
       $repositoryPerformanceTests -match 'offset_p95_ms' -and
       $repositoryPerformanceTests -match '1\.15d' -and
       $repositoryPerformanceTests -match 'SearchDetailsPageAsync' -and
       $planTests -match 'PRODUCT_KEYSET_100K' -and
       $planTests -match 'ForwardP95Milliseconds') `
    "duplicate, mutation, mismatch, backward, fallback and 100k p95 tests are present" `
    "required keyset correctness/performance test coverage is incomplete"

if ($failures.Count -gt 0) {
    Write-Host "RESULT: FAIL"
    exit 1
}

Write-Host "RESULT: PASS"
exit 0
