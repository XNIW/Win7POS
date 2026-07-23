$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$srcRoot = Join-Path $repoRoot "src"
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
    "src/Win7POS.Data/Online/PosAdminWebClient.cs",
    "src/Win7POS.Data/Online/CatalogFullRefreshReconciler.cs",
    "src/Win7POS.Data/Online/CatalogShopStateRepository.cs",
    "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs",
    "src/Win7POS.Data/Online/CatalogFullResponseStageRepository.cs",
    "src/Win7POS.Data/Online/RemoteCatalogContentPolicy.cs",
    "src/Win7POS.Core/Online/CatalogHeartbeatPolicy.cs",
    "src/Win7POS.Core/Online/CatalogFullLaneEvidenceTracker.cs",
    "src/Win7POS.Core/Online/CatalogPaginationSafetyPolicy.cs",
    "src/Win7POS.Data/Repositories/RemoteCatalogBatchRepository.cs",
    "src/Win7POS.Data/Repositories/CatalogMutationGate.cs",
    "src/Win7POS.Data/Repositories/LocalProductWriter.cs",
    "src/Win7POS.Data/Repositories/ProductIdentityPolicy.cs",
    "src/Win7POS.Data/Repositories/ProductMetaReference.cs",
    "src/Win7POS.Data/Repositories/ProductMetaResolver.cs",
    "src/Win7POS.Data/Repositories/RemoteCatalogProductWriter.cs",
    "src/Win7POS.Data/Repositories/RemotePriceHistoryRepository.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs",
    "src/Win7POS.Data/Online/OnlineSyncSupervisor.cs",
    "src/Win7POS.Core/Online/OnlineSyncSupervisorContracts.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Cli/Program.cs",
    "tests/Win7POS.Core.Tests/Data/CatalogExactnessTests.cs",
    "tests/Win7POS.Core.Tests/Data/CatalogFullResponseStageRepositoryTests.cs",
    "tests/Win7POS.Core.Tests/Data/RemoteCatalogBatchRepositoryTests.cs",
    "tests/Win7POS.Core.Tests/Data/LocalProductWriterTests.cs",
    "tests/Win7POS.Core.Tests/Data/RemoteCatalogProductWriterTests.cs",
    "tests/Win7POS.Core.Tests/Data/RemoteCatalogReferenceTombstoneTests.cs",
    "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs",
    "tests/Win7POS.Core.Tests/Online/CatalogPaginationSafetyPolicyTests.cs",
    "tests/Win7POS.Core.Tests/Online/CatalogHeartbeatPolicyTests.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$client = (Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs") + "`n" + (Read-Text "src/Win7POS.Core/Online/PosOnlineTransportContracts.cs")
$service = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$startupCoordinator = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs"
$syncHost = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$supervisor = Read-Text "src/Win7POS.Data/Online/OnlineSyncSupervisor.cs"
$supervisorContracts = Read-Text "src/Win7POS.Core/Online/OnlineSyncSupervisorContracts.cs"
$fullLaneEvidence = Read-Text "src/Win7POS.Core/Online/CatalogFullLaneEvidenceTracker.cs"
$paginationPolicy = Read-Text "src/Win7POS.Core/Online/CatalogPaginationSafetyPolicy.cs"
$fullStage = Read-Text "src/Win7POS.Data/Online/CatalogFullResponseStageRepository.cs"
$shopState = Read-Text "src/Win7POS.Data/Online/CatalogShopStateRepository.cs"
$catalogImportOutbox = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$fullRefresh = Read-Text "src/Win7POS.Data/Online/CatalogFullRefreshReconciler.cs"
$compatibility = Read-Text "src/Win7POS.Data/Online/PosOnlineCompatibilityValidator.cs"
$catalogContentPolicy = Read-Text "src/Win7POS.Data/Online/RemoteCatalogContentPolicy.cs"
$heartbeatPolicy = Read-Text "src/Win7POS.Core/Online/CatalogHeartbeatPolicy.cs"
$cli = Read-Text "src/Win7POS.Cli/Program.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$repository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
$catalogMutationGate = Read-Text "src/Win7POS.Data/Repositories/CatalogMutationGate.cs"
$localProductWriter = Read-Text "src/Win7POS.Data/Repositories/LocalProductWriter.cs"
$productIdentityPolicy = Read-Text "src/Win7POS.Data/Repositories/ProductIdentityPolicy.cs"
$productMetaReference = Read-Text "src/Win7POS.Data/Repositories/ProductMetaReference.cs"
$productMetaResolver = Read-Text "src/Win7POS.Data/Repositories/ProductMetaResolver.cs"
$remoteCatalogProductWriter = Read-Text "src/Win7POS.Data/Repositories/RemoteCatalogProductWriter.cs"
$remotePriceHistoryRepository = Read-Text "src/Win7POS.Data/Repositories/RemotePriceHistoryRepository.cs"
$categoryRepository = Read-Text "src/Win7POS.Data/Repositories/CategoryRepository.cs"
$supplierRepository = Read-Text "src/Win7POS.Data/Repositories/SupplierRepository.cs"
$categorySupplierResolver = Read-Text "src/Win7POS.Data/Import/CategorySupplierResolver.cs"
$productImportApply = Read-Text "src/Win7POS.Data/Import/ProductImportApplyService.cs"
$productDbImporter = Read-Text "src/Win7POS.Data/ImportDb/ProductDbImporter.cs"
$batchRepository = Read-Text "src/Win7POS.Data/Repositories/RemoteCatalogBatchRepository.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$catalogExactnessTests = Read-Text "tests/Win7POS.Core.Tests/Data/CatalogExactnessTests.cs"
$batchRepositoryTests = Read-Text "tests/Win7POS.Core.Tests/Data/RemoteCatalogBatchRepositoryTests.cs"
$localProductWriterTests = Read-Text "tests/Win7POS.Core.Tests/Data/LocalProductWriterTests.cs"
$remoteCatalogProductWriterTests = Read-Text "tests/Win7POS.Core.Tests/Data/RemoteCatalogProductWriterTests.cs"
$referenceTombstoneTests = Read-Text "tests/Win7POS.Core.Tests/Data/RemoteCatalogReferenceTombstoneTests.cs"
$restoreTests = Read-Text "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs"
$paginationTests = Read-Text "tests/Win7POS.Core.Tests/Online/CatalogPaginationSafetyPolicyTests.cs"
$heartbeatTests = Read-Text "tests/Win7POS.Core.Tests/Online/CatalogHeartbeatPolicyTests.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($client -notmatch "/api/pos/catalog/pull") { Fail "catalog pull path missing" } else { Pass "catalog pull path present" }
if ($client -notmatch "CatalogPullAsync") { Fail "CatalogPullAsync missing" } else { Pass "CatalogPullAsync present" }
if ($service -notmatch "PosTrustedDeviceStore") { Fail "trusted device store not used for catalog pull" } else { Pass "trusted device store used" }
$stopAuthStart = $syncHost.IndexOf("private async Task StopAuthenticationAsync", [System.StringComparison]::Ordinal)
$credentialsStart = $syncHost.IndexOf("private Task<OnlineSyncRequestCredentials> ReadCredentialsAsync", [System.StringComparison]::Ordinal)
$stopAuthBody = if ($stopAuthStart -ge 0 -and $credentialsStart -gt $stopAuthStart) {
    $syncHost.Substring($stopAuthStart, $credentialsStart - $stopAuthStart)
} else { "" }
$authLatch = $stopAuthBody.IndexOf("PosOnlineSyncRevocationLatch.Revoke(generation)", [System.StringComparison]::Ordinal)
$authStop = $stopAuthBody.IndexOf("StopIfCurrentAsync(", [System.StringComparison]::Ordinal)
$authRecheck = $stopAuthBody.IndexOf("IsCurrentAndActiveAsync(generation)", [System.StringComparison]::Ordinal)
$authClear = $stopAuthBody.IndexOf("_store.TryClear(generation.GenerationId)", [System.StringComparison]::Ordinal)
if ($syncHost -notmatch "new\s+OnlineSyncSupervisor\([\s\S]{0,700}StopAuthenticationAsync" -or
    $authLatch -lt 0 -or $authStop -le $authLatch -or
    $authRecheck -le $authStop -or $authClear -le $authRecheck) {
    Fail "catalog auth denial must latch the generation, stop its durable fence and clear trust globally"
} else { Pass "catalog auth denial latches the generation and stops all supervisor lanes" }
if ($service -notmatch "RemoteCatalogBatchRepository" -or
    $service -notmatch "\.ApplyAsync\(\s*batch,\s*cancellationToken,\s*CreateCommitFence") { Fail "catalog pages are not delegated to the fenced batch repository with cancellation" } else { Pass "catalog pages use the cancellation-aware fenced batch repository" }
if ($batchRepository -notmatch "class RemoteCatalogBatchRepository" -or $batchRepository -notmatch "Task<RemoteCatalogBatchApplyResult>\s+ApplyAsync") { Fail "remote catalog batch repository contract missing" } else { Pass "remote catalog batch repository contract present" }
$batchWriteBody = [regex]::Match(
    $batchRepository,
    "internal\s+async\s+Task<RemoteCatalogBatchApplyResult>\s+ApplyWithinRunAsync[\s\S]*?(?=\r?\n\s*private\s+static\s+void\s+ValidateBatchContent)").Value
$batchCancellationIndex = $batchWriteBody.LastIndexOf("cancellationToken.ThrowIfCancellationRequested")
$batchTransactionIndex = $batchWriteBody.IndexOf("conn.BeginTransaction")
$batchCommitIndex = $batchWriteBody.IndexOf("tx.Commit()")
$batchRollbackIndex = $batchWriteBody.IndexOf("tx.Rollback()")
if ($batchWriteBody -notmatch "WaitAsync\(cancellationToken\)" -or
    $batchCancellationIndex -lt 0 -or
    $batchTransactionIndex -lt 0 -or
    $batchCancellationIndex -gt $batchTransactionIndex) {
    Fail "catalog batch must observe cancellation before entering its write transaction"
} else {
    Pass "catalog batch observes cancellation before its atomic write section"
}
if ($batchTransactionIndex -lt 0 -or
    $batchCommitIndex -lt $batchTransactionIndex -or
    $batchRollbackIndex -lt $batchTransactionIndex) {
    Fail "catalog batch transaction must commit on success and roll back on failure"
} else {
    Pass "catalog batch transaction has commit/rollback boundaries"
}
if ($batchTransactionIndex -ge 0 -and
    $batchWriteBody.Substring($batchTransactionIndex) -match "ThrowIfCancellationRequested|WaitAsync\(cancellationToken\)") {
    Fail "catalog batch must not abandon a partially written page on mid-transaction cancellation"
} else {
    Pass "catalog batch cancellation cannot split a page transaction"
}
if ($service -notmatch "using\s+var\s+catalogApplyRun[\s\S]{0,180}CreateRunContext" -or
    $service -notmatch "ApplyCatalogAsync\(\s*catalogApplyRun" -or
    $batchRepository -notmatch "class\s+RemoteCatalogApplyRunContext") {
    Fail "catalog sync must reuse one apply context across its pages"
} else {
    Pass "catalog sync reuses one apply context across pages"
}
if ($batchRepository -notmatch "temp_catalog_page_product_identities" -or
    $batchRepository -notmatch "LoadPageProductIdentitiesAsync" -or
    $batchRepository -notmatch "LoadPagePendingStockAsync" -or
    $batchRepository -notmatch "PreparedCommandCount") {
    Fail "catalog run context must use page-scoped identity/pending-stock queries and prepared commands"
} else {
    Pass "catalog run context uses page-scoped queries and prepared commands"
}
if ($batchRepository -notmatch "UpsertRemoteInTransactionAsync" -or
    $batchRepository -notmatch "RemoteCatalogProductWriter\s*\.\s*UpsertProductAndMetaInTransactionCoreAsync" -or
    $batchRepository -notmatch "RemotePriceHistoryRepository\s*\.\s*UpsertOrQueueRemotePriceHistoryInTransactionAsync" -or
    $batchRepository -notmatch "RemotePriceHistoryRepository\s*\.\s*ApplyPendingRemotePricesInTransactionAsync" -or
    $batchRepository -notmatch "RemotePriceHistoryRepository\s*\.\s*PrepareAuthoritativeRemotePriceRepairAsync" -or
    $batchRepository -notmatch "RemoteCatalogProductWriter\s*\.\s*ApplyRemoteProductTombstoneInTransactionAsync") {
    Fail "catalog batch does not keep all catalog mutations inside the shared transaction"
} else {
    Pass "catalog batch routes products, references, prices and tombstones through one transaction"
}
if ($service -notmatch "pos.catalog.last_sync_at") { Fail "last catalog sync setting missing" } else { Pass "last catalog sync setting present" }
if ($service -notmatch "pos.catalog.last_sync_cursor") { Fail "last catalog sync cursor setting missing" } else { Pass "last catalog sync cursor setting present" }
if ($service -notmatch "pos.catalog.last_error") { Fail "catalog last error diagnostic missing" } else { Pass "catalog last error diagnostic present" }
if ($service -notmatch "pos.catalog.last_updated_products") { Fail "catalog updated products diagnostic missing" } else { Pass "catalog updated products diagnostic present" }
if ($service -notmatch "pos.catalog.last_tombstones_received") { Fail "catalog tombstones received diagnostic missing" } else { Pass "catalog tombstones received diagnostic present" }
if ($service -notmatch "pos.catalog.last_tombstones_applied") { Fail "catalog tombstones applied diagnostic missing" } else { Pass "catalog tombstones applied diagnostic present" }
if ($service -notmatch "pos.catalog.last_has_more") { Fail "catalog hasMore diagnostic missing" } else { Pass "catalog hasMore diagnostic present" }
if ($service -notmatch "pos.catalog.last_catalog_version") { Fail "catalog version diagnostic missing" } else { Pass "catalog version diagnostic present" }
if ($service -notmatch "pos.catalog.bootstrap_status" -or $statusReader -notmatch "pos.catalog.bootstrap_status") { Fail "catalog bootstrap status setting missing from writer/status reader" } else { Pass "catalog bootstrap status persisted and surfaced" }
if ($service -notmatch "CatalogPullPageLimit\s*=\s*1000") { Fail "catalog pull must request server limit 1000" } else { Pass "catalog pull limit set to 1000" }
if ($client -notmatch 'DataMember\(Name = "limit"') { Fail "catalog pull request limit wire field missing" } else { Pass "catalog pull limit wire field present" }
if ($service -notmatch "Limit\s*=\s*CatalogPullPageLimit") { Fail "catalog pull request does not send Limit" } else { Pass "catalog pull request sends Limit" }
if ($service -notmatch "PosCatalogPullProgress" -or $service -notmatch "IProgress<PosCatalogPullProgress>" -or $service -notmatch "ForCatalogPage") { Fail "catalog pull progress callback missing" } else { Pass "catalog pull progress callback present" }
if ($service -match "MaxCatalogPullPages\s*=\s*10") { Fail "catalog pull page cap regressed to unsafe low value" } else { Pass "unsafe MaxCatalogPullPages=10 absent" }
if ($service -notmatch "MaxBackgroundCatalogPullPages") { Fail "background catalog pull cap missing" } else { Pass "background catalog pull cap present" }
$hardCapMatch = [regex]::Match($service, "MaxAuthoritativeCatalogPullPages\s*=\s*(\d+)")
if (-not $hardCapMatch.Success -or [int]$hardCapMatch.Groups[1].Value -lt 100) {
    Fail "authoritative catalog hard ceiling is missing or too low for 100,000-row lanes"
} else { Pass "authoritative catalog hard ceiling supports 100,000-row lanes" }
if ($service -notmatch "effectiveMaxPages" -or
    $service -notmatch "firstPageBudget\.PageBudget" -or
    $paginationPolicy -notmatch "CalculatePageBudget" -or
    $paginationPolicy -notmatch "Math\.Max\(" -or
    $service -notmatch "for\s*\(var page = 1; page <= effectiveMaxPages; page\+\+\)") {
    Fail "server-selected full_refresh does not use an authoritative lane-derived budget"
} else {
    Pass "server-selected full_refresh uses the authoritative lane-derived budget"
}
if ($paginationPolicy -notmatch "ExpandFullPageBudgetForTombstoneContinuation" -or
    $paginationPolicy -notmatch "!cumulative\.HasAnyTombstones" -or
    $service -notmatch "ExpandFullPageBudgetForTombstoneContinuation" -or
    $paginationTests -notmatch "FullHasMore_TombstonesCanContinueBeyondActiveSummaryBudget") {
    Fail "full tombstone chains must drain beyond the active-only summary budget within the hard ceiling"
} else {
    Pass "full tombstone chains drain to a validated terminal page beyond the active-only summary budget"
}
if ($fullLaneEvidence -notmatch "ProductActiveTombstoneConflictCode" -or
    $fullLaneEvidence -notmatch "CategoryActiveTombstoneConflictCode" -or
    $fullLaneEvidence -notmatch "SupplierActiveTombstoneConflictCode" -or
    $fullLaneEvidence -notmatch "DetectActiveTombstoneOverlap" -or
    $paginationTests -notmatch "FullLaneEvidence_RejectsActiveTombstoneOverlapBeforePromotion") {
    Fail "full snapshot active/tombstone overlap must fail before destructive promotion"
} else {
    Pass "full snapshot active/tombstone overlap fails before destructive promotion"
}
if ($startupCoordinator -notmatch "StartBackground[\s\S]{0,500}host\.StartContinuous\(\)" -or
    $supervisor -notmatch 'new\s+OnlineSyncLaneOutcome\(false,\s*"lane_exception"\)' -or
    $supervisor -notmatch "OnlineSyncLaneSchedulePolicy\.Evaluate[\s\S]{0,700}if\s*\(schedule\.ShouldSchedule\)[\s\S]{0,120}Schedule\(slot,\s*schedule\.Delay,\s*outcome\)" -or
    $supervisorContracts -notmatch "outcome\.AuthenticationDenied\s*\|\|\s*outcome\.Terminal[\s\S]{0,260}OnlineSyncLaneScheduleDecision\(false" -or
    $supervisorContracts -notmatch "var\s+nextFailureCount[\s\S]{0,700}new\s+OnlineSyncLaneScheduleDecision\([\s\S]{0,80}true") {
    Fail "supervisor runner failures must become retryable lane outcomes without terminating continuous sync"
} else {
    Pass "supervisor runner failures become retryable lane outcomes"
}
if ($service -notmatch "TryPullInitialCatalogAsync") { Fail "initial catalog pull path missing" } else { Pass "initial catalog pull path present" }
if ($service -notmatch "completed" -or $service -notmatch "partial_has_more" -or $service -notmatch "failed_retryable" -or $service -notmatch "failed_auth_denied") { Fail "catalog bootstrap status values incomplete" } else { Pass "catalog bootstrap status values present" }
if ($service -notmatch "pos\.catalog\.sale_safe_at" -or $service -notmatch "pos\.catalog\.initial_completed_at" -or $service -notmatch "CountActiveRemoteProductsAsync") { Fail "catalog sale-safe readiness markers missing" } else { Pass "catalog sale-safe readiness markers present" }
if ($service -notmatch "has_more_not_drained") { Fail "catalog HasMore cap error code missing" } else { Pass "catalog HasMore cap error code present" }
if ($service -notmatch "lastResponse\.HasMore[\s\S]*StoreCatalogFailureAsync\(CatalogHasMoreNotDrainedCode\)[\s\S]*PosCatalogPullOutcome\.Failure") { Fail "catalog HasMore after cap is not treated as failure/partial outcome" } else { Pass "catalog HasMore after cap fails visibly" }
if ($service -notmatch "cursorSaved=" -or $service -notmatch "!fullRefresh") { Fail "catalog partial HasMore log must distinguish resumable delta from restartable full refresh" } else { Pass "catalog partial HasMore log distinguishes delta/full-refresh cursor persistence" }
if ($service -notmatch "CatalogPullWithRetryAsync") { Fail "catalog pull retry helper missing" } else { Pass "catalog pull retry helper present" }
if ($service -notmatch "Task.Delay") { Fail "catalog pull backoff missing" } else { Pass "catalog pull backoff present" }
if ($service -notmatch "var\s+committedCursor\s*=\s*binding\.Cursor" -or
    $service -notmatch "var\s+requestCursor" -or
    $service -notmatch "SyncCursor\s*=\s*requestCursor" -or
    $service -notmatch "EnsureAndLoadCursorAsync") { Fail "catalog pull syncCursor is not loaded from committed shop-bound state" } else { Pass "catalog pull syncCursor uses committed shop-bound state" }
$capturedSessionCheckIndex = $service.IndexOf("ValidateCapturedSessionAsync")
$bindingIndex = $service.IndexOf("EnsureAndLoadCursorAsync")
$networkIndex = $service.IndexOf("new PosAdminWebClient")
if ($capturedSessionCheckIndex -lt 0 -or $bindingIndex -lt 0 -or $networkIndex -lt 0 -or $capturedSessionCheckIndex -gt $bindingIndex -or $capturedSessionCheckIndex -gt $networkIndex -or $shopState -notmatch "catalog_session_shop_changed") { Fail "captured catalog session must be revalidated inside the transition barrier before bind/network" } else { Pass "captured catalog session is revalidated before bind/network" }
if ($shopState -notmatch "pos\.catalog\.bound_shop_id" -or $shopState -notmatch "pos\.catalog\.bound_shop_code" -or $shopState -notmatch "Catalog state shop binding mismatch") { Fail "persistent catalog shop binding missing" } else { Pass "persistent catalog shop binding present" }
if ($service -notmatch "stagedResponseShopError[\s\S]*response_shop_mismatch[\s\S]*ApplyCatalogAsync") { Fail "catalog response shop must be validated before local apply" } else { Pass "catalog response shop validated before local apply" }
if ($service -notmatch "StageAuthoritativePageAsync" -or
    $batchRepository -notmatch "catalog_authoritative_id_stage" -or
    $batchRepository -notmatch "category_remote_id" -or
    $batchRepository -notmatch "supplier_remote_id" -or
    $batchRepository -notmatch "product_remote_id") {
    Fail "full refresh must durably retain authoritative rows and validated reference identities"
} else {
    Pass "full refresh durably retains authoritative rows and reference identities"
}
if ($batchRepository -notmatch "AddStageOccurrences" -or
    $batchRepository -notmatch "occurrences\[key\]\s*=\s*checked\(count\s*\+\s*1\)" -or
    $batchRepository -notmatch "occurrence_count") {
    Fail "authoritative staging must preserve invalid and duplicate row evidence"
} else {
    Pass "authoritative staging preserves invalid/duplicate evidence"
}
if ($service -notmatch "CatalogFullRefreshReconciler" -or $fullRefresh -notmatch "NOT EXISTS" -or $fullRefresh -notmatch "remote_product_id" -or $fullRefresh -notmatch "remote_category_id" -or $fullRefresh -notmatch "remote_supplier_id") { Fail "full_refresh reconciliation missing" } else { Pass "full_refresh reconciles remote rows absent from authoritative snapshot" }
if ($service -notmatch "RequestFullRepairWhileBarrierHeldAsync" -or
    $service -notmatch "catalog_full_repair_requires_full_refresh" -or
    $service -notmatch "catalog_full_repair_required" -or
    $service -notmatch "catalog_empty_cursor_requires_full_refresh" -or
    $service -notmatch "!fullRefresh\s*&&\s*requestCursor\.Length\s*==\s*0") {
    Fail "explicit/full-refresh repair policy is incomplete"
} else {
    Pass "full repair is explicit and delta is blocked while repair is required"
}
if ($service -notmatch "IsCatalogCursorRejectionCode" -or
    $service -notmatch "requestCursor\.Length\s*>\s*0" -or
    $service -notmatch "requestCursor\s*=\s*string\.Empty" -or
    $service -notmatch "request\.SyncCursor\s*=\s*requestCursor" -or
    $service -notmatch '"cursor_expired"' -or
    $service -notmatch '"cursor_rejected"') {
    Fail "server-rejected/expired cursors must probe an empty authoritative boundary without destructive state change"
} else {
    Pass "expired/rejected cursors probe a controlled non-destructive full-refresh boundary"
}
$catalogApplyIndex = $service.IndexOf("var applyStats = await ApplyCatalogAsync(")
$ambiguityGuardIndex = $service.IndexOf("var paginationSafety = CatalogPaginationSafetyPolicy.EvaluateTerminalPage(")
$compatibilityIndex = $service.IndexOf("var compatibilityError = PosOnlineCompatibilityValidator.ValidateCatalogPull")
$responseShopIndex = $service.IndexOf("var stagedResponseShopError = OutboxShopBinding.GetMismatchCode(")
$stageAppendIndex = $service.IndexOf("fullStage.AppendAsync(")
$promotionMarkerIndex = $service.IndexOf("Only a completely drained and protocol-validated full chain")
$promotionResetIndex = if ($promotionMarkerIndex -ge 0) {
    $service.IndexOf("RequestFullRepairWhileBarrierHeldAsync(", $promotionMarkerIndex)
} else { -1 }
$stagedApplyIndex = $service.IndexOf("var stagedStats = await ApplyCatalogAsync(")
if ($catalogApplyIndex -lt 0 -or
    $ambiguityGuardIndex -lt 0 -or
    $compatibilityIndex -lt 0 -or
    $ambiguityGuardIndex -le $compatibilityIndex -or
    $responseShopIndex -le $ambiguityGuardIndex -or
    $stageAppendIndex -lt $responseShopIndex -or
    $promotionMarkerIndex -lt $stageAppendIndex -or
    $promotionResetIndex -lt $promotionMarkerIndex -or
    $stagedApplyIndex -lt $promotionResetIndex -or
    $service -notmatch "var\s+requestCursor\s*=\s*networkCursor" -or
    $service -notmatch "networkCursor\s*=\s*result\.Value\.SyncCursor") {
    Fail "full_refresh must validate every response, advance the network cursor, stage the drained chain, then reset/apply"
} else {
    Pass "full_refresh validates and stages the drained cursor chain before reset/apply"
}
$preStageValidationBlock = if ($stageAppendIndex -gt $ambiguityGuardIndex -and $ambiguityGuardIndex -ge 0) {
    $service.Substring($ambiguityGuardIndex, $stageAppendIndex - $ambiguityGuardIndex)
} else { "" }
if ($preStageValidationBlock -match "ApplyCatalogAsync") {
    Fail "full response may be applied before pagination/compatibility/shop validation and durable staging"
} else {
    Pass "full response is not applied before validation and durable staging"
}
if ($fullStage -notmatch "MaximumPageBytes\s*=\s*8\s*\*\s*1024\s*\*\s*1024" -or
    $fullStage -notmatch "MaximumRunBytes\s*=\s*512L\s*\*\s*1024L\s*\*\s*1024L" -or
    $fullStage -notmatch "DataContractJsonSerializer" -or
    $fullStage -notmatch "DELETE FROM app_settings WHERE key GLOB @pattern" -or
    $fullStage -notmatch "LoadPageAsync" -or
    $service -notmatch "fullStage\.BeginAsync" -or
    $service -notmatch "fullStage\.AppendAsync" -or
    $service -notmatch "fullStage\.LoadPageAsync" -or
    $service -notmatch "fullStage\.ClearAsync") {
    Fail "bounded durable full-chain staging contract is incomplete"
} else {
    Pass "full-chain staging is durable, generation-scoped and bounded"
}
$ambiguityFailureBlock = [regex]::Match(
    $service,
    'if \(!paginationSafety\.Allowed\)[\s\S]*?(?=\r?\n\s*if \(page == 1 && pageIsFullRefresh\))').Value
if ($paginationPolicy -notmatch "server_catalog_pagination_ambiguous" -or
    $ambiguityFailureBlock -notmatch "StoreCatalogFailureAsync" -or
    $ambiguityFailureBlock -notmatch "BootstrapStatusFailedRetryable" -or
    $ambiguityFailureBlock -notmatch "PosCatalogPullOutcome\.Failure" -or
    $ambiguityFailureBlock -match "RequestFullRepair|ApplyCatalogAsync|StoreCatalogDiagnosticsAsync") {
    Fail "ambiguous terminal page must fail visibly without reset/apply/cursor persistence"
} else { Pass "ambiguous terminal page fails visibly without changing catalog generation" }
$skippedRowsIndex = $service.IndexOf("applyStats.RowsSkipped > 0")
$diagnosticsIndex = $service.IndexOf("StoreCatalogDiagnosticsAsync(")
if ($batchRepository -notmatch "RowsSkipped" -or
    $batchRepository -notmatch "ProductsSkipped" -or
    $batchRepository -notmatch "CategoriesSkipped" -or
    $batchRepository -notmatch "SuppliersSkipped" -or
    $batchRepository -notmatch "TombstonesSkipped" -or
    $skippedRowsIndex -lt $catalogApplyIndex -or
    $diagnosticsIndex -lt $skippedRowsIndex) {
    Fail "catalog row skips must be counted and rejected before cursor/diagnostic persistence"
} else {
    Pass "all catalog entity skips fail before cursor persistence"
}
if ($service -notmatch "if\s*\(applyStats\.RowsSkipped\s*>\s*0\)[\s\S]{0,600}RequestFullRepairWhileBarrierHeldAsync[\s\S]{0,600}StoreCatalogFailureAsync\(skippedRowsCode\)") {
    Fail "a catalog apply conflict/skip must force a full repair before the cursor can resume"
} else {
    Pass "catalog apply conflicts/skips force a full-repair boundary"
}
$reconcileIndex = $service.IndexOf(".ReconcileAndVerifyStagedAsync(")
$storeExactnessIndex = $service.IndexOf(".StoreExactnessAsync(")
$authoritativeCursorIndex = $service.IndexOf("authoritativeSnapshotCommitted: true")
$saleSafeIndex = $service.IndexOf("StoreCatalogSaleSafeAsync(")
if ($reconcileIndex -lt 0 -or
    $storeExactnessIndex -lt $reconcileIndex -or
    $authoritativeCursorIndex -lt $storeExactnessIndex -or
    $saleSafeIndex -lt $authoritativeCursorIndex) {
    Fail "full_refresh must reconcile, persist exactness, then commit cursor and sale-safe state in order"
} else {
    Pass "exactness is persisted before authoritative cursor and sale-safe state"
}
$activeProductsIndex = $service.IndexOf("var activeRemoteProducts")
if ($activeProductsIndex -lt $storeExactnessIndex -or
    $activeProductsIndex -gt $authoritativeCursorIndex -or
    $service -notmatch "catalog_partial_delta_no_active_products") {
    Fail "zero-active catalogs must fail and request repair before final/full cursor or partial-delta continuation"
} else {
    Pass "zero-active full and partial-delta states fail closed before sale-safe/cursor completion"
}
if ($service -notmatch "AuditCurrentAsync" -or
    $service -notmatch "FindInvariantError\(deltaAudit\)" -or
    $service -notmatch "RequestFullRepairWhileBarrierHeldAsync") {
    Fail "completed delta does not audit structural invariants and force repair on integrity failure"
} else {
    Pass "completed delta audits structural invariants before sale-safe completion"
}
if ($service -notmatch "exactness\.Status\s*!=\s*CatalogCompletenessStatus\.Verified" -or
    $service -notmatch "exactness\.RepairRequired" -or
    $service.IndexOf("exactness.RepairRequired") -lt $storeExactnessIndex -or
    $service.IndexOf("exactness.RepairRequired") -gt $authoritativeCursorIndex) {
    Fail "every non-verified/repair-required exactness result must fail closed before authoritative cursor commit"
} else {
    Pass "non-verified exactness fails closed before cursor commit"
}
if ($service -notmatch "snapshotCatalogVersion" -or
    $service -notmatch "catalog_version_changed_mid_pull" -or
    $service -notmatch "string\.Equals\(snapshotCatalogVersion, responseCatalogVersion") {
    Fail "multi-page pull does not pin catalogVersion"
} else {
    Pass "multi-page pull pins catalogVersion"
}
if ($service -notmatch "snapshotSummary" -or
    $service -notmatch "CatalogSummariesEqual" -or
    $service -notmatch "catalog_summary_changed_mid_pull") {
    Fail "multi-page pull does not pin catalog summary"
} else {
    Pass "multi-page pull pins catalog summary"
}
if ($shopState -notmatch "SummaryPinned\s*&&\s*!summaryPresent[\s\S]{0,200}catalog_summary_missing_across_runs" -or
    $service -notmatch "snapshotSummaryPinned\s*&&\s*result\.Value\.CatalogSummary\s*==\s*null" -or
    $service -notmatch 'const string summaryMissingCode\s*=\s*"catalog_summary_missing_mid_pull"[\s\S]{0,700}RequestFullRepairWhileBarrierHeldAsync' -or
    $catalogExactnessTests -notmatch "catalog_summary_missing_across_runs") {
    Fail "a pinned catalog summary may disappear without forcing repair"
} else {
    Pass "pinned catalog summary presence is fail-closed within and across runs"
}
if ($service -notmatch "const string versionChangedCode[\s\S]{0,600}RequestFullRepairWhileBarrierHeldAsync[\s\S]{0,600}StoreCatalogFailureAsync\(versionChangedCode\)" -or
    $service -notmatch "const string summaryChangedCode[\s\S]{0,600}RequestFullRepairWhileBarrierHeldAsync[\s\S]{0,600}StoreCatalogFailureAsync\(summaryChangedCode\)" -or
    $service -notmatch "const string cursorProgressCode[\s\S]{0,600}RequestFullRepairWhileBarrierHeldAsync[\s\S]{0,600}StoreCatalogFailureAsync\(cursorProgressCode\)" -or
    $service -notmatch "page\s*>\s*1\s*&&\s*pageIsFullRefresh\s*!=\s*fullRefresh[\s\S]{0,600}RequestFullRepairWhileBarrierHeldAsync[\s\S]{0,600}catalog_sync_mode_changed") {
    Fail "snapshot pin, mode or cursor violations can resume from a mixed checkpoint"
} else {
    Pass "snapshot pin, mode and cursor violations reset the resumable cursor and require full repair"
}
if ($service -notmatch "seenCursorFingerprints" -or
    $service -notmatch "responseCursor\.Length\s*==\s*0" -or
    $service -notmatch "responseCursorFingerprint" -or
    $service -notmatch "responseCursorAlreadySeen" -or
    $service -notmatch "seenCursorFingerprints\.Contains\(responseCursorFingerprint\)" -or
    $service -notmatch "seenCursorFingerprints\.Add\(responseCursorFingerprint\)" -or
    $service -notmatch "sameCursor" -or
    $service -notmatch "allowsDeltaNoOpCursor" -or
    $service -notmatch "CatalogHasMutations" -or
    $service -notmatch "catalog_cursor_not_progressing") {
    Fail "pull must reject empty/repeated/non-progressing cursors except a final mutation-free delta no-op"
} else {
    Pass "cursor progress is enforced and only a final mutation-free delta no-op may retain its cursor"
}
$cursorLimitIndex = $service.IndexOf("seenCursorFingerprints.Count >=")
if ($cursorLimitIndex -lt 0 -or
    $cursorLimitIndex -gt $catalogApplyIndex -or
    $service -notmatch "MaxDeltaChainCursorFingerprints" -or
    $service -notmatch "DeltaChainCursorLimitCode[\s\S]{0,800}RequestFullRepairWhileBarrierHeldAsync" -or
    $shopState -notmatch "fingerprints\.Count\s*>\s*MaxDeltaChainCursorFingerprints" -or
    $shopState -match "\.Take\(256\)") {
    Fail "delta cursor-cycle evidence must fail closed before apply instead of evicting fingerprints"
} else {
    Pass "delta cursor-cycle evidence has a fail-closed bounded limit before apply and store"
}
if ($service -notmatch "LoadDeltaChainAsync" -or
    $service -notmatch "CatalogDeltaChainCheckpoint" -or
    $service -notmatch "GetSnapshotMismatchCode" -or
    $service -notmatch "persistedDeltaChain\.IsValid" -or
    $service -notmatch "persistedDeltaChain\.Code" -or
    $service -notmatch "DeltaChainCursorMismatchCode" -or
    $service -notmatch "CursorFingerprints\s*=\s*seenCursorFingerprints\.ToArray\(\)" -or
    $shopState -notmatch "StoreDeltaChainAsync" -or
    $shopState -notmatch "ClearDeltaChainAsync" -or
    $shopState -notmatch "presentValues\s*!=\s*rawValues\.Length" -or
    $shopState -notmatch "CatalogDeltaChainState\.Invalid" -or
    $shopState -notmatch "public bool IsValid" -or
    $shopState -notmatch "public string Code") {
    Fail "resumable delta pulls must strictly validate and persist all checkpoint evidence across run boundaries"
} else {
    Pass "resumable delta pulls reject partial/corrupt checkpoints before network apply"
}
if ($service -notmatch "ValidateCatalogPull" -or $compatibility -notmatch "catalog_schema_not_supported" -or $compatibility -notmatch "catalog_capability_not_supported" -or $compatibility -notmatch "policy_contract_not_supported") { Fail "catalog schema/policy/capability fail-closed validation missing" } else { Pass "catalog schema, policy and capabilities fail closed" }
if ($compatibility -notmatch "ValidateCatalogVersion\(response\.CatalogVersion\)" -or
    $compatibility -notmatch "IsRequiredCanonicalText" -or
    $compatibility -notmatch "CatalogVersionMaximumLength" -or
    $catalogContentPolicy -notmatch "CatalogVersionMaximumLength\s*=\s*128" -or
    $catalogContentPolicy -notmatch "IsRequiredCanonicalText" -or
    $catalogContentPolicy -notmatch "char\.IsHighSurrogate" -or
    $catalogContentPolicy -notmatch "char\.IsLowSurrogate" -or
    $compatibility -notmatch "catalog_version_invalid" -or
    $shopState -match "SafeOpaque\(checkpoint\.CatalogVersion" -or
    $shopState -notmatch "ValidateCatalogVersion\(checkpoint\.CatalogVersion\)") {
    Fail "catalogVersion must be rejected before apply unless it round-trips the persisted checkpoint domain"
} else {
    Pass "catalogVersion is validated consistently before apply and checkpoint persistence"
}
if ($restoreTests -notmatch "CatalogDeltaChainCheckpoint" -or
    $restoreTests -notmatch "pos\.catalog\.delta_chain\." -or
    $restoreTests -notmatch "ResetForRestoreReviewAsync") {
    Fail "restore reset regression must seed and clear an active delta checkpoint"
} else {
    Pass "restore reset regression clears active delta checkpoint state"
}
if ($compatibility -notmatch "ValidateCatalogRows" -or
    $compatibility -notmatch "catalog_product_row_invalid" -or
    $compatibility -notmatch "catalog_price_row_invalid" -or
    $compatibility -notmatch "catalog_category_row_invalid" -or
    $compatibility -notmatch "catalog_supplier_row_invalid" -or
    $compatibility -notmatch "catalog_product_tombstone_invalid" -or
    $compatibility.IndexOf("ValidateCatalogRows(response.Catalog)") -gt $compatibility.IndexOf("ValidateCatalogSummary(response.CatalogSummary)")) {
    Fail "catalog payload rows must be validated fail-closed before apply/summary verification"
} else {
    Pass "catalog products, references, prices and tombstones are validated before apply"
}
$compatibilityIndex = $service.IndexOf(
    "ValidateCatalogPull(result.Value)",
    [System.StringComparison]::Ordinal)
$evidenceIndex = $service.IndexOf(
    ".StageAuthoritativePageAsync(",
    [System.StringComparison]::Ordinal)
$terminalIndex = $service.IndexOf(
    "CatalogPaginationSafetyPolicy.EvaluateTerminalPage(",
    [System.StringComparison]::Ordinal)
$budgetIndex = $service.IndexOf(
    "CatalogPaginationSafetyPolicy.CalculatePageBudget(",
    [System.StringComparison]::Ordinal)
$stageAppendIndex = $service.IndexOf(
    "fullStage.AppendAsync(",
    [System.StringComparison]::Ordinal)
$applyIndex = $service.IndexOf(
    "var applyStats = await ApplyCatalogAsync(",
    [System.StringComparison]::Ordinal)
if ($compatibilityIndex -lt 0 -or
    $evidenceIndex -le $compatibilityIndex -or
    $terminalIndex -le $compatibilityIndex -or
    $budgetIndex -le $compatibilityIndex -or
    $stageAppendIndex -le $compatibilityIndex -or
    $applyIndex -le $compatibilityIndex) {
    Fail "catalog compatibility validation must precede evidence accumulation, pagination, staging and apply"
} else {
    Pass "catalog compatibility validation is the first response normalization boundary"
}
$cliHarnessStart = $cli.IndexOf(
    "private static async Task PullAndApplyCatalogAsync",
    [System.StringComparison]::Ordinal)
$cliHarnessEnd = if ($cliHarnessStart -ge 0) {
    $cli.IndexOf(
        "private static async Task ApplyTask081CatalogResponseAsync",
        $cliHarnessStart,
        [System.StringComparison]::Ordinal)
} else { -1 }
$cliHarness = if ($cliHarnessStart -ge 0 -and $cliHarnessEnd -gt $cliHarnessStart) {
    $cli.Substring($cliHarnessStart, $cliHarnessEnd - $cliHarnessStart)
} else { "" }
$cliValidationIndex = $cliHarness.IndexOf(
    "EnsureCompatibleCatalogResponse(response);",
    [System.StringComparison]::Ordinal)
$cliDiagnosticsIndex = $cliHarness.IndexOf(
    "LastTask081CatalogDiagnostics =",
    [System.StringComparison]::Ordinal)
$cliApplyIndex = $cliHarness.IndexOf(
    "await ApplyTask081CatalogResponseAsync(factory, response)",
    [System.StringComparison]::Ordinal)
$cliCheckpointIndex = $cliHarness.IndexOf(
    'SetStringAsync("pos.catalog.last_sync_at"',
    [System.StringComparison]::Ordinal)
if ($cliValidationIndex -lt 0 -or
    $cliDiagnosticsIndex -le $cliValidationIndex -or
    $cliApplyIndex -le $cliValidationIndex -or
    $cliCheckpointIndex -le $cliValidationIndex -or
    $cli -notmatch "EnsureCompatibleCatalogResponse[\s\S]{0,300}PosOnlineCompatibilityValidator\.ValidateCatalogPull") {
    Fail "CLI catalog harness must validate before diagnostics, normalization, apply and checkpoint persistence"
} else {
    Pass "CLI catalog harness shares the fail-closed compatibility ingress"
}
if ($compatibility -notmatch "HasDuplicateCatalogIds" -or
    $compatibility -notmatch "catalog_duplicate_product_ids" -or
    $compatibility -notmatch "catalog_duplicate_category_ids" -or
    $compatibility -notmatch "catalog_duplicate_supplier_ids" -or
    $catalogExactnessTests -notmatch "CatalogRowValidator_RejectsDuplicateRemoteIdsWithinOnePage") {
    Fail "duplicate product/category/supplier remote IDs can collapse within one response page"
} else {
    Pass "duplicate product/category/supplier remote IDs fail before page apply"
}
if ($compatibility -notmatch "row\.ProductName" -or
    $compatibility -notmatch "row\.SecondProductName" -or
    $compatibility -notmatch "row\.ItemNumber" -or
    $compatibility -notmatch "response\.GeneratedAt" -or
    $catalogContentPolicy -notmatch "NameMaximumLength\s*=\s*512" -or
    $catalogContentPolicy -notmatch "ItemNumberMaximumLength\s*=\s*128" -or
    $catalogContentPolicy -notmatch "CatalogVersionMaximumLength\s*=\s*128" -or
    $catalogContentPolicy -notmatch "SyncCursorMaximumLength\s*=\s*512" -or
    $catalogContentPolicy -notmatch "IsRequiredCanonicalText" -or
    $catalogContentPolicy -notmatch "IsOptionalTimestamp" -or
    $catalogContentPolicy -notmatch "DateTimeOffset\.TryParse" -or
    $catalogContentPolicy -notmatch "char\.IsHighSurrogate" -or
    $catalogContentPolicy -notmatch "char\.IsLowSurrogate" -or
    $catalogExactnessTests -notmatch "CatalogRowValidator_BoundsProductReceiptTextAndRejectsMalformedUnicode" -or
    $catalogExactnessTests -notmatch "CatalogGeneratedAt_MustBeABoundedSemanticTimestamp" -or
    $catalogExactnessTests -notmatch "CatalogCursorValidator_RejectsMalformedUnicodeBeforeFingerprintingOrPersistence" -or
    $catalogExactnessTests -notmatch "CatalogRowValidator_RejectsMalformedSemanticTimestamps") {
    Fail "remote catalog persisted text must be bounded and Unicode-valid before normalization or rendering"
} else {
    Pass "remote catalog persisted text is bounded and Unicode-valid at ingress"
}
if ($batchRepository -notmatch "ValidateBatchContent\(batch\)" -or
    $batchRepository.IndexOf("ValidateBatchContent(batch)") -gt $batchRepository.IndexOf("CatalogMutationGate.Instance.WaitAsync") -or
    $batchRepositoryTests -notmatch "ApplyAsync_RejectsOversizedProductTextBeforeAnyMutation" -or
    $batchRepositoryTests -notmatch "ApplyAsync_RejectsMalformedTimestampBeforeAnyMutation" -or
    $fullRefresh -notmatch "EnsureOptionalTimestamp\([\s\S]{0,160}generatedAt" -or
    $catalogExactnessTests -notmatch "ReconcilerRejectsOversizedGeneratedAtBeforeCatalogMutation") {
    Fail "remote catalog persistence must reject unsafe text before acquiring the write gate or mutating SQLite"
} else {
    Pass "remote catalog persistence rejects unsafe text before any mutation"
}
if ($categoryRepository -match "string\.CompareOrdinal\(normalizedIncoming" -or
    $supplierRepository -match "string\.CompareOrdinal\(normalizedIncoming" -or
    $referenceTombstoneTests -notmatch "RemoteReferenceTimestamps_RejectMalformedInputAndRepairMalformedLegacyState") {
    Fail "reference timestamp ordering must reject malformed input and repair poisoned legacy state without lexical fallback"
} else {
    Pass "reference timestamp ordering has no lexical poisoning fallback"
}
if ($heartbeatPolicy.IndexOf("raw.Length > MaximumRevisionLength") -lt 0 -or
    $heartbeatPolicy.IndexOf("raw.Trim()") -lt 0 -or
    $heartbeatPolicy.IndexOf("raw.Length > MaximumRevisionLength") -gt $heartbeatPolicy.IndexOf("raw.Trim()") -or
    $heartbeatPolicy -notmatch "char\.IsHighSurrogate" -or
    $heartbeatPolicy -notmatch "char\.IsLowSurrogate" -or
    $heartbeatTests -notmatch "bad\\uD800revision" -or
    $heartbeatTests -notmatch "bad\\uDC00revision") {
    Fail "catalog heartbeat revisions must be bounded and Unicode-valid before trim"
} else {
    Pass "catalog heartbeat revisions are bounded before normalization"
}
if ($shopState -notmatch "EnsureOptionalCanonicalText\([\s\S]{0,160}syncCursor" -or
    $shopState -notmatch "suppliedContext\.CatalogVersion" -or
    $catalogExactnessTests -notmatch "InvalidNewCursor_IsRejectedBeforeCatalogCheckpointMutation" -or
    $catalogExactnessTests -notmatch "LegacyUnsafeCursorAndMode_CanBeAtomicallyReplacedByValidState") {
    Fail "catalog state sinks must reject new unsafe cursors while allowing atomic legacy-state repair"
} else {
    Pass "catalog state sinks reject unsafe new state without blocking legacy repair"
}
if ($compatibility -match '"incremental"' -or
    $compatibility -notmatch '"delta"' -or
    $compatibility -notmatch '"full_refresh"' -or
    $compatibility -notmatch 'rawSyncMode[\s\S]{0,180}StringComparison\.Ordinal' -or
    $catalogExactnessTests -notmatch 'CatalogSyncMode_RejectsWhitespaceThatWouldChangeFullRefreshClassification') {
    Fail "catalog compatibility must accept only canonical Admin sync modes delta/full_refresh"
} else { Pass "catalog compatibility accepts only canonical Admin delta/full_refresh modes" }
if ($compatibility -notmatch "SalesSync \?\? string\.Empty" -or $compatibility -notmatch "sales_sync_contract_not_supported") { Fail "SalesSync capability must be mandatory and exact" } else { Pass "SalesSync capability is mandatory and exact" }
if ($shopState -notmatch "StoreLastSyncAsync" -or $shopState -notmatch "StoreSaleSafeAsync" -or $shopState -notmatch "BeginTransaction") { Fail "catalog cursor/sale-safe writes are not transactionally shop-bound" } else { Pass "catalog cursor/sale-safe writes are transactionally shop-bound" }
if ($shopState -notmatch "requireEvidence:\s*true" -or
    $shopState -notmatch "Catalog exactness evidence is required before authoritative cursor commit") {
    Fail "authoritative full-refresh cursor commit is not protected by repository-level exactness evidence"
} else {
    Pass "repository rejects authoritative full-refresh cursor commit without safe exactness evidence"
}
if ($client -notmatch 'DataMember\(Name = "syncCursor"') { Fail "catalog pull syncCursor wire field missing" } else { Pass "catalog pull syncCursor wire field present" }
if ($client -notmatch 'DataMember\(Name = "catalogVersion"') { Fail "catalog version wire field missing" } else { Pass "catalog version wire field present" }
if ($client -notmatch 'DataMember\(Name = "hasMore"') { Fail "hasMore wire field missing" } else { Pass "hasMore wire field present" }
if ($client -notmatch 'DataMember\(Name = "tombstones"') { Fail "tombstones wire field missing" } else { Pass "tombstones wire field present" }
if ($remoteCatalogProductWriter -notmatch "remote_product_id") { Fail "remote product id column missing in remote product writer" } else { Pass "remote product id used in remote product writer" }
if ($remotePriceHistoryRepository -notmatch "remote_catalog_pending_prices" -or $remotePriceHistoryRepository -notmatch "QueuePendingRemotePriceAsync" -or $remotePriceHistoryRepository -notmatch "ApplyPendingRemotePricesAsync") { Fail "pending remote price replay missing" } else { Pass "pending remote price replay present" }
if ($remotePriceHistoryRepository -notmatch "PendingRemotePriceReplayBatchSize" -or $remotePriceHistoryRepository -notmatch "while\s*\(true\)" -or $remotePriceHistoryRepository -notmatch "canonical" -or $remotePriceHistoryRepository -notmatch "GROUP BY remote_product_id") { Fail "pending remote price replay must drain all resolvable batches against a canonical remote product per remote_product_id" } else { Pass "pending remote price replay drains all resolvable batches against canonical remote product" }
if ($remotePriceHistoryRepository -notmatch "remote_price_id" -or $initializer -notmatch "remote_price_id" -or $service -notmatch "RemotePriceId\s*=\s*Normalize\(row\.PriceId\)" -or $batchRepository -notmatch "price\.RemotePriceId") { Fail "remote priceId idempotency missing" } else { Pass "remote priceId idempotency present" }
if ($repository -notmatch "private readonly RemotePriceHistoryRepository _remotePriceHistory" -or
    $repository -notmatch "_remotePriceHistory\.UpsertRemotePriceHistoryAsync" -or
    $repository -notmatch "_remotePriceHistory\.UpsertOrQueueRemotePriceHistoryAsync" -or
    $repository -notmatch "_remotePriceHistory\.ApplyPendingRemotePricesAsync") {
    Fail "ProductRepository must preserve its remote price facade through the extracted collaborator"
} else {
    Pass "ProductRepository delegates remote price operations through the extracted collaborator"
}
$localFacadeMethods = @(
    "UpsertAsync",
    "UpsertMetaAsync",
    "UpsertMetaFullAsync",
    "DeleteByBarcodeAsync",
    "UpsertProductAndMetaInTransactionAsync",
    "UpdateProductAndMetaInTransactionAsync",
    "UpdateProductAndMetaWithPriceHistoryAsync",
    "InsertPriceHistoryAsync",
    "UpdateProductPricesAsync",
    "UpdateAsync"
)
$missingLocalFacadeMethods = @($localFacadeMethods | Where-Object {
    $repository -notmatch ("_localProductWriter\s*\.\s*{0}\s*\(" -f [regex]::Escape($_))
})
if ($repository -notmatch "private readonly LocalProductWriter _localProductWriter" -or
    $missingLocalFacadeMethods.Count -gt 0) {
    Fail "ProductRepository must preserve every local mutation through LocalProductWriter: $($missingLocalFacadeMethods -join ', ')"
} else {
    Pass "ProductRepository delegates every local mutation through LocalProductWriter"
}
if ($repository -notmatch "string\.IsNullOrWhiteSpace\(remoteProductId\)" -or
    $repository -notmatch "_localProductWriter\.UpsertProductAndMetaInTransactionAsync" -or
    $repository -notmatch "_remoteProductWriter\.UpsertProductAndMetaInTransactionAsync" -or
    $repository -notmatch "_remoteProductWriter\.ApplyRemoteProductTombstoneAsync" -or
    $repository -notmatch "private readonly RemoteCatalogProductWriter _remoteProductWriter") {
    Fail "ProductRepository must preserve explicit local/remote writer delegation"
} else {
    Pass "ProductRepository keeps blank-vs-remote product ownership explicit"
}
if ($localProductWriter -notmatch "SalesReceiptContentPolicy\.EnsureValidProductIdentity" -or
    $localProductWriter -notmatch "ProductIdentityPolicy\.IsReservedBarcode" -or
    $localProductWriter -notmatch "product_price_history" -or
    $localProductWriter -notmatch "remote_deleted_at" -or
    $localProductWriter -notmatch "sales_sync_outbox") {
    Fail "LocalProductWriter must retain local identity validation, soft-delete, price history and pending-stock safeguards"
} else {
    Pass "LocalProductWriter retains local write safeguards"
}
if ($productIdentityPolicy -notmatch "IsReservedBarcode" -or
    $productIdentityPolicy -notmatch '"DISC:"' -or
    $productIdentityPolicy -notmatch '"MANUAL:"' -or
    $productIdentityPolicy -notmatch "StringComparison\.Ordinal") {
    Fail "ProductIdentityPolicy must retain the exact reserved barcode rules shared by local and remote writers"
} else {
    Pass "ProductIdentityPolicy retains shared reserved barcode rules"
}
if ($productMetaReference -notmatch "internal sealed class ProductMetaReference" -or
    $productMetaReference -notmatch "int\? Id" -or
    $productMetaReference -notmatch "string Name") {
    Fail "ProductMetaReference must remain a shared metadata value object outside ProductRepository"
} else {
    Pass "ProductMetaReference is shared outside ProductRepository"
}
if ($productMetaResolver -notmatch "ResolveSupplierReferenceAsync" -or
    $productMetaResolver -notmatch "ResolveCategoryReferenceAsync" -or
    $productMetaResolver -notmatch "FindSupplierByNormalizedNameAsync" -or
    $productMetaResolver -notmatch "FindCategoryByNormalizedNameAsync" -or
    $productMetaResolver -notmatch "NormalizeCatalogName" -or
    $productMetaResolver -notmatch "StringComparison\.OrdinalIgnoreCase") {
    Fail "ProductMetaResolver must own normalized supplier/category resolution"
} else {
    Pass "ProductMetaResolver owns normalized supplier/category resolution"
}
if ($repository -match "internal\s+sealed\s+class\s+ProductMetaReference" -or
    $repository -match "private\s+static\s+(async\s+)?Task<ProductMetaReference>\s+ResolveSupplierReferenceAsync" -or
    $repository -match "private\s+static\s+(async\s+)?Task<ProductMetaReference>\s+ResolveCategoryReferenceAsync" -or
    $repository -match "private\s+static\s+Task<ProductMetaReference>\s+FindSupplierByNormalizedNameAsync" -or
    $repository -match "private\s+static\s+Task<ProductMetaReference>\s+FindCategoryByNormalizedNameAsync") {
    Fail "ProductRepository must not retain duplicate local metadata resolver ownership"
} else {
    Pass "ProductRepository has no duplicate local metadata resolver ownership"
}
if ($localProductWriter -notmatch "UpsertProductAndMetaInTransactionCoreAsync\s*\(\s*SqliteConnection\s+conn\s*,\s*SqliteTransaction\s+tx" -or
    $localProductWriterTests -notmatch "LocalProductWriter_CallerTransactionRollbackLeavesNoLocalRows" -or
    $localProductWriterTests -notmatch "LocalProductWriter_AndProductFacade_KeepLocalMutationParity" -or
    $localProductWriterTests -notmatch "LocalProductWriter_AndProductFacade_RejectReservedBarcodeWithoutMutation" -or
    $localProductWriterTests -notmatch "LocalProductWriter_ConcurrentFacadeReads_LeaveLocalStateUnchanged") {
    Fail "local writer must retain caller-owned transaction semantics and direct parity/read/negative regressions"
} else {
    Pass "local writer transaction ownership and direct parity/read/negative regressions are present"
}
if ($remoteCatalogProductWriter -notmatch "UpsertProductAndMetaInTransactionCoreAsync\s*\(\s*SqliteConnection\s+conn\s*,\s*SqliteTransaction\s+tx" -or
    $remoteCatalogProductWriter -notmatch "ApplyRemoteProductTombstoneInTransactionAsync\s*\(\s*SqliteConnection\s+conn\s*,\s*SqliteTransaction\s+tx" -or
    $remoteCatalogProductWriter -notmatch "ProductIdentityPolicy\.IsReservedBarcode" -or
    $remoteCatalogProductWriter -notmatch "CanonicalizeRemoteProductBeforeUpsertAsync" -or
    $remoteCatalogProductWriter -notmatch "DeactivateRemoteProductDuplicatesAsync" -or
    $remoteCatalogProductWriter -notmatch "hasPendingLocalStock" -or
    $remoteCatalogProductWriter -notmatch "sales_sync_outbox" -or
    $remoteCatalogProductWriter -notmatch "stockQtyToWrite = existingStock" -or
    $remoteCatalogProductWriterTests -notmatch "RemoteCatalogProductWriter_CallerTransactionRollbackLeavesNoRemoteRows" -or
    $remoteCatalogProductWriterTests -notmatch "RemoteCatalogProductWriter_AndProductFacade_KeepRemoteMutationParity" -or
    $remoteCatalogProductWriterTests -notmatch "RemoteCatalogProductWriter_AndProductFacade_RejectReservedBarcodeWithoutMutation" -or
    $remoteCatalogProductWriterTests -notmatch "RemoteCatalogProductWriter_AndProductFacade_KeepTombstoneParityAndIdempotence" -or
    $remoteCatalogProductWriterTests -notmatch "RemoteCatalogProductWriter_PreservesPendingLocalStockAcrossCanonicalBarcodeChange") {
    Fail "remote product writer must retain caller transactions, identity safeguards and direct parity regressions"
} else {
    Pass "remote product writer retains transaction ownership, identity safeguards and direct parity regressions"
}
if ($remoteCatalogProductWriter -notmatch "internal sealed class CatalogProductPreparedCommands" -or
    $remoteCatalogProductWriter -notmatch "internal sealed class CatalogProductBatchContext" -or
    $batchRepository -notmatch "RemoteCatalogProductWriter\.CatalogProductPreparedCommands" -or
    $batchRepository -notmatch "RemoteCatalogProductWriter\.CatalogProductBatchContext" -or
    $batchRepository -match "ProductRepository\.(CatalogProductPreparedCommands|CatalogProductBatchContext|ProductMetaReference)") {
    Fail "remote catalog batch must consume extracted remote writer types directly"
} else {
    Pass "remote catalog batch consumes extracted remote writer types directly"
}
if ($repository -match "CanonicalizeRemoteProductBeforeUpsertAsync|DeactivateRemoteProductDuplicatesAsync|ApplyRemoteProductTombstoneInTransactionAsync|UpsertProductAndMetaInTransactionCoreAsync|CatalogProductPreparedCommands|CatalogProductBatchContext|ProductMetaReference|CatalogMutationGate\.Instance|remote_product_id|remote_deleted_at") {
    Fail "ProductRepository must remain a façade and must not retain remote writer implementation ownership"
} else {
    Pass "ProductRepository has no remote writer implementation ownership"
}
if ($catalogMutationGate -notmatch "SemaphoreSlim\s+Instance\s*=\s*new\s+SemaphoreSlim\(1\s*,\s*1\)" -or
    $localProductWriter -notmatch "CatalogMutationGate\.Instance\.WaitAsync" -or
    $remoteCatalogProductWriter -notmatch "CatalogMutationGate\.Instance\.WaitAsync" -or
    $batchRepository -notmatch "CatalogMutationGate\.Instance\.WaitAsync" -or
    $fullRefresh -notmatch "CatalogMutationGate\.Instance\.WaitAsync") {
    Fail "local writes, remote products, catalog batches and full refresh must share CatalogMutationGate.Instance"
} else {
    Pass "local writes, remote products, catalog batches and full refresh share one mutation gate"
}
$transactionHelpersValid = $true
foreach ($transactionMethod in @(
    "UpsertOrQueueRemotePriceHistoryInTransactionAsync",
    "ApplyPendingRemotePricesInTransactionAsync",
    "PrepareAuthoritativeRemotePriceRepairAsync")) {
    if ($remotePriceHistoryRepository -notmatch ("{0}\s*\(\s*SqliteConnection\s+conn\s*,\s*SqliteTransaction\s+tx" -f $transactionMethod)) {
        Fail "remote price transaction helper $transactionMethod must accept the caller connection and transaction"
        $transactionHelpersValid = $false
    }
}
if ($transactionHelpersValid) { Pass "remote price transaction helpers retain the caller transaction boundary" }
if ($initializer -notmatch "remote_catalog_price_evidence_quarantine" -or
    $remotePriceHistoryRepository -notmatch "PrepareAuthoritativeRemotePriceRepairAsync" -or
    $remotePriceHistoryRepository -notmatch "StoreRemotePriceOwnershipAsync" -or
    $batchRepository -notmatch "AuthoritativeFullRefresh" -or
    $service -notmatch "AuthoritativeFullRefresh\s*=\s*authoritativeFullRefresh" -or
    $catalogImportOutbox -notmatch "EnsureAssignedRemotePriceOwnershipAsync" -or
    $catalogImportOutbox -notmatch "RemotePriceHistoryRepository\s*\.\s*StoreRemotePriceOwnershipAsync") {
    Fail "remote price ownership must be atomic for pull/import writers and legacy evidence must be quarantined only on authoritative full refresh"
} else {
    Pass "remote price ownership is atomic and authoritative repair preserves legacy evidence in quarantine"
}
if ($batchRepository -notmatch "PricesSkipped" -or
    $service -notmatch "PriceRowsSkipped\s*=\s*applied\.PricesSkipped" -or
    $service -notmatch "PriceRowsAccepted\s*=\s*totalStats\.PriceRowsApplied\s*\+\s*totalStats\.PriceRowsQueued" -or
    $service -notmatch "InvalidPriceRows\s*=\s*totalStats\.PriceRowsSkipped" -or
    $fullRefresh -notmatch "runContext\.DuplicatePriceRows\s*=\s*evidence\.DuplicatePrices" -or
    $fullRefresh -notmatch "catalog_invalid_price_rows" -or
    $fullRefresh -notmatch "catalog_duplicate_price_rows" -or
    $fullRefresh -notmatch "catalog_prices_not_fully_applied") {
    Fail "price exactness evidence is incomplete or not fail-closed"
} else {
    Pass "price received/accepted/skipped/duplicate evidence is verified fail-closed"
}
if ($remoteCatalogProductWriter -notmatch "remote_deleted_at") { Fail "remote tombstone column missing in remote product writer" } else { Pass "remote tombstone column used in remote product writer" }
if ($repository -notmatch "ApplyRemoteProductTombstoneAsync") { Fail "remote product tombstone facade missing" } else { Pass "remote product tombstone facade present" }
if ($remoteCatalogProductWriter -notmatch "CanonicalizeRemoteProductBeforeUpsertAsync" -or $remoteCatalogProductWriter -notmatch "DeactivateRemoteProductDuplicatesAsync" -or $remoteCatalogProductWriter -notmatch "remote_product_id\s*=\s*@remoteProductId[\s\S]{0,160}barcode\s*<>\s*@barcode") { Fail "remote product upsert must canonicalize remote_product_id and deactivate duplicate active barcodes" } else { Pass "remote product upsert canonicalizes remote_product_id duplicates" }
if ($remoteCatalogProductWriter -notmatch "hasPendingLocalStock" -or $remoteCatalogProductWriter -notmatch "sales_sync_outbox" -or $remoteCatalogProductWriter -notmatch "'pending', 'retry', 'in_progress', 'failed_blocked'" -or $remoteCatalogProductWriter -notmatch "stockQtyToWrite = existingStock") { Fail "catalog upsert must preserve stock_qty when unresolved local stock movements exist" } else { Pass "catalog upsert preserves unresolved local stock" }
$tombstoneMethod = [regex]::Match($remoteCatalogProductWriter, "ApplyRemoteProductTombstoneInTransactionAsync[\s\S]*?return rows > 0;").Value
if ($tombstoneMethod -notmatch "is_active\s*=\s*0" -or $tombstoneMethod -notmatch "remote_deleted_at") { Fail "product tombstone must soft-delete/inactivate products" } else { Pass "product tombstone is soft-delete/inactive" }
if ($tombstoneMethod -match "DELETE\s+FROM") { Fail "product tombstone must not purge rows" } else { Pass "product tombstone does not purge rows" }
if ($batchRepository -notmatch "CategoryRepository\.UpsertRemoteInTransactionAsync" -or $batchRepository -notmatch "SupplierRepository\.UpsertRemoteInTransactionAsync") { Fail "remote category/supplier identities are not persisted by the batch repository" } else { Pass "remote category/supplier identities persisted by the batch repository" }
if ($initializer -notmatch "remote_catalog_product_references" -or
    $batchRepository -notmatch "RemoteProductReferencePreparedCommand" -or
    $batchRepository -notmatch "RelinkRemoteProductReferencesAsync" -or
    $batchRepository -notmatch "ProductIdentityConflicts" -or
    $fullRefresh -notmatch "RemoteProductsWithoutReferenceMap" -or
    $fullRefresh -notmatch "ReferenceMapsWithoutProduct" -or
    $fullRefresh -notmatch "catalog_authoritative_products_not_applied" -or
    $fullRefresh -notmatch "InvalidCategoryReferenceMappings" -or
    $fullRefresh -notmatch "InvalidSupplierReferenceMappings") {
    Fail "remote product identities/references are not collision-safe, persisted, relinked and audited"
} else {
    Pass "remote identities/references survive page ordering without barcode collapse and are relinked/audited"
}
$relinkMethod = [regex]::Match(
    $batchRepository,
    'private static Task<int> RelinkRemoteProductReferencesAsync[\s\S]*?private static string NormalizeBarcode').Value
if ($relinkMethod -notmatch "temp_catalog_relink_product_ids" -or
    $relinkMethod -notmatch "temp_catalog_relink_category_ids" -or
    $relinkMethod -notmatch "temp_catalog_relink_supplier_ids" -or
    $relinkMethod -notmatch "temp_catalog_relink_barcodes" -or
    $relinkMethod -notmatch "WHERE barcode IN\s*\(\s*SELECT barcode\s*FROM temp_catalog_relink_barcodes" -or
    $relinkMethod -match "UPDATE product_meta[\s\S]{0,3000}WHERE EXISTS" -or
    $initializer -notmatch "idx_remote_product_refs_category" -or
    $initializer -notmatch "idx_remote_product_refs_supplier" -or
    $batchRepositoryTests -notmatch "product_meta_relink_touch_log" -or
    $batchRepositoryTests -notmatch "WHERE barcode = 'UNRELATED-REF'") {
    Fail "reference relink must target only page-affected products through indexed temporary sets"
} else {
    Pass "reference relink targets only page-affected products and preserves unrelated rows"
}
$referenceCleanup = [regex]::Match(
    $batchRepository,
    'DELETE FROM remote_catalog_product_references[\s\S]{0,500}?\);", transaction').Value
if ($referenceCleanup -notmatch "p\.remote_product_id\s*=\s*remote_catalog_product_references\.remote_product_id" -or
    $referenceCleanup -match "TRIM\(COALESCE\(p\.remote_product_id") {
    Fail "reference-map cleanup must use the canonical indexed remote_product_id instead of a per-row TRIM scan"
} else {
    Pass "reference-map cleanup uses the canonical indexed remote product identity"
}
if ($batchRepository -notmatch "CategoryRepository\.ApplyRemoteTombstoneInTransactionAsync" -or $batchRepository -notmatch "SupplierRepository\.ApplyRemoteTombstoneInTransactionAsync") { Fail "category/supplier tombstones are not applied by the batch repository" } else { Pass "category/supplier tombstones applied by the batch repository" }
if ($categoryRepository -notmatch "remote_category_id" -or $categoryRepository -notmatch "remote_deleted_at" -or $categoryRepository -notmatch "is_active\s*=\s*0") { Fail "category remote identity/tombstone state missing" } else { Pass "category remote identity/tombstone state present" }
if ($supplierRepository -notmatch "remote_supplier_id" -or $supplierRepository -notmatch "remote_deleted_at" -or $supplierRepository -notmatch "is_active\s*=\s*0") { Fail "supplier remote identity/tombstone state missing" } else { Pass "supplier remote identity/tombstone state present" }
if ($categoryRepository -match "DELETE\s+FROM\s+categories" -or $supplierRepository -match "DELETE\s+FROM\s+suppliers") { Fail "category/supplier tombstones must not purge reference rows" } else { Pass "category/supplier tombstones are non-destructive" }
if ($initializer -notmatch "remote_category_id" -or $initializer -notmatch "remote_supplier_id" -or $initializer -notmatch "idx_categories_remote_category_id" -or $initializer -notmatch "idx_suppliers_remote_supplier_id") { Fail "category/supplier remote identity migration or indexes missing" } else { Pass "category/supplier remote identity migration present" }
$localReferenceWriters = @($categorySupplierResolver, $productImportApply, $productDbImporter, $productMetaResolver) -join "`n"
if ($localReferenceWriters -match "INSERT\s+OR\s+REPLACE\s+INTO\s+(categories|suppliers)") { Fail "local import can destructively replace remote category/supplier identity" } else { Pass "local import does not replace remote category/supplier identity" }
if ($categorySupplierResolver -notmatch "COALESCE\(is_active, 1\) = 1" -or $productMetaResolver -notmatch "COALESCE\(is_active, 1\) = 1") { Fail "local reference resolution can reuse remote tombstones" } else { Pass "local reference resolution excludes remote tombstones" }
if ($productImportApply -notmatch "remote_supplier_id" -or $productImportApply -notmatch "remote_category_id" -or $productDbImporter -notmatch "remote_supplier_id" -or $productDbImporter -notmatch "remote_category_id") { Fail "local import remote-identity collision guards missing" } else { Pass "local import remote-identity collision guards present" }
if ($initializer -notmatch "remote_product_id") { Fail "remote product id column missing in db initializer" } else { Pass "remote product id column present" }
if ($initializer -notmatch "remote_deleted_at") { Fail "remote tombstone column missing in db initializer" } else { Pass "remote tombstone column present" }
if ($initializer -notmatch "is_active") { Fail "active product column missing in db initializer" } else { Pass "active product column present" }
if ($startupCoordinator -notmatch "OnlineSyncLane\.CatalogDelta" -or
    $syncHost -notmatch "TryPullCatalogForSupervisorAsync") { Fail "startup catalog supervisor lane missing" } else { Pass "startup catalog supervisor lane present" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
if ($combined -match "payment_sync|cash_close") { Fail "payment sync/cash close scope detected" } else { Pass "TASK-081 sales sync scope allowed" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($combined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
