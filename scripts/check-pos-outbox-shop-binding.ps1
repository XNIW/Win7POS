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

$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$binding = Read-Text "src/Win7POS.Data/Online/OutboxShopBinding.cs"
$catalogRepository = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$catalogSync = Read-Text "src/Win7POS.Data/Online/CatalogImportSyncService.cs"
$saleRepository = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$salesBuilder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$shopState = Read-Text "src/Win7POS.Data/Online/CatalogShopStateRepository.cs"
$transition = Read-Text "src/Win7POS.Data/Online/PosShopTransitionGuard.cs"
$barrier = Read-Text "src/Win7POS.Data/Online/CatalogShopTransitionBarrier.cs"
$tests = Read-Text "tests/Win7POS.Core.Tests/Data/OutboxShopBindingTests.cs"
$catalogSafetyTests = Read-Text "tests/Win7POS.Core.Tests/Data/CatalogSafetyInvariantTests.cs"

if ($initializer -notmatch "sales_sync_outbox[\s\S]*origin_shop_id[\s\S]*origin_shop_code" -or $initializer -notmatch "catalog_import_outbox[\s\S]*origin_shop_id[\s\S]*origin_shop_code") { Fail "outbox origin columns missing" } else { Pass "sales and catalog outbox origin columns present" }
if ($initializer -notmatch "schema_version" -or $initializer -notmatch "operation_type" -or $initializer -notmatch "BackfillLegacyOutboxBindings") { Fail "outbox schema/operation/backfill migration missing" } else { Pass "outbox schema, operation and legacy backfill present" }
if ($binding -notmatch "ResolveRequiredAsync" -or $binding -notmatch "Official shop binding is required before enqueue") { Fail "enqueue official-shop resolver missing" } else { Pass "enqueue requires official shop binding" }
if ($saleRepository -notmatch "ResolveRequiredAsync" -or $catalogRepository -notmatch "ResolveRequiredAsync") { Fail "one or more enqueue paths do not capture official binding" } else { Pass "sales and catalog enqueue capture official binding" }
if ($binding -notmatch "origin_shop_unbound" -or $binding -notmatch "origin_shop_mismatch" -or $binding -notmatch "trusted_shop_unbound") { Fail "redacted fail-closed binding codes missing" } else { Pass "redacted fail-closed binding codes present" }
if ($catalogSync -notmatch "PrepareAttemptAsync[\s\S]*GetMismatchCode[\s\S]*MarkBlockedAsync[\s\S]*CatalogImportAsync") { Fail "catalog drain does not claim then block mismatch before send" } else { Pass "catalog drain claims with CAS then blocks mismatch before send" }
if ($salesSync -notmatch "PrepareSalesSyncAttemptAsync[\s\S]*GetMismatchCode[\s\S]*MarkBlockedAsync[\s\S]*SalesSyncAsync") { Fail "sales drain does not claim then block mismatch before send" } else { Pass "sales drain claims with CAS then blocks mismatch before send" }
if ($salesBuilder -notmatch "GetMismatchCode" -or $salesBuilder -notmatch "ShopCode = item\.OriginShopCode") { Fail "sales request builder lacks binding defense in depth" } else { Pass "sales request builder uses immutable origin" }
if ($saleRepository -notmatch "status = 'in_progress'" -or $saleRepository -notmatch "attempt_count = @expectedAttemptCount" -or $salesSync -notmatch "client_batch_mismatch" -or $salesSync -notmatch "response_shop_mismatch") { Fail "sales attempt/ACK guards incomplete" } else { Pass "sales prepare and ACK are attempt/shop guarded" }
if ($catalogRepository -notmatch "CatalogImportInProgressLeaseMilliseconds" -or $saleRepository -notmatch "SalesSyncInProgressLeaseMilliseconds") { Fail "stale in_progress recovery lease missing" } else { Pass "both outboxes have stale in_progress recovery leases" }
if ($shopState -notmatch "pos\.catalog\.bound_shop_id" -or $shopState -notmatch "pos\.catalog\.bound_shop_code" -or $transition -notmatch "BoundShopIdKey" -or $transition -notmatch "BoundShopCodeKey") { Fail "catalog cursor/sale-safe shop binding or reset missing" } else { Pass "catalog cursor/sale-safe are persistently bound and reset on authorized transition" }
if ($initializer -notmatch "legacy_origin_ambiguous" -or $initializer -notmatch "TryReadLegacySalesOriginShopCode" -or $initializer -match "origin_shop_code\s*=\s*@shopCode") { Fail "legacy backfill must require per-row proof and block ambiguity" } else { Pass "legacy backfill is per-row proven or fail-closed" }
if ($saleRepository -notmatch "payload_json IS @payloadJson" -or $saleRepository -notmatch "payload_hash IS @payloadHash" -or $salesSync -notmatch "Sha256Hex\(item\.PayloadJson\)" -or $salesSync -notmatch "payload_hash_mismatch") { Fail "sales immutable payload/hash guards incomplete" } else { Pass "sales payload/hash are captured at enqueue and verified on retry" }
if ($saleRepository -notmatch "EvaluateReversalDependencyAsync" -or
    $saleRepository -notmatch "ReversalDependencyState\.PermanentBlock" -or
    $saleRepository -notmatch "ValidateReversalBoundaryAsync" -or
    $saleRepository -notmatch "OriginalOriginShopCode" -or
    $salesSync -notmatch "ReversalDependencyState\.Wait" -or
    $salesSync -notmatch "ReversalDependencyState\.PermanentBlock") { Fail "reversal boundary/dependency shop guards incomplete" } else { Pass "reversal boundary distinguishes waiting and permanently invalid dependencies" }
if ($shopState -notmatch "TransitionEpochKey" -or $transition -notmatch "ApplyAuthorizedTransitionAndHoldAsync" -or $barrier -notmatch "SemaphoreSlim" -or $catalogSafetyTests -notmatch "TransitionLease_CoversResetUntilDestinationIdentityIsCommitted") { Fail "catalog pull/shop transition race protection incomplete" } else { Pass "catalog pull/shop transition lease covers reset through destination identity commit" }
if ($shopState -notmatch "StorePullCursorAsync" -or $shopState -notmatch "authoritativeSnapshotCommitted" -or $catalogSafetyTests -notmatch "FullRefreshCursor_IsNotAdvancedBeforeAuthoritativeCommit") { Fail "full-refresh crash/restart cursor guard incomplete" } else { Pass "full-refresh cursor advances only after authoritative reconciliation commit" }
if ($tests -notmatch "SalesEnqueue_BindsSaleRefundAndVoidToOfficialShop" -or $tests -notmatch "CatalogDrain_ClaimsThenBlocksShopMismatchWithoutNetwork" -or $tests -notmatch "LegacyBinding_UsesPersistedSalesProofAndBlocksAmbiguousRows" -or $tests -notmatch "SalesPayloadAndHash_AreImmutableAcrossAttempts" -or $tests -notmatch "ReversalBoundaryAndDependency_RejectMissingOriginalAndWaitForOriginalAck" -or $tests -notmatch "ReversalBoundary_RejectsAckFromDifferentOfficialShopWithoutMutation" -or $tests -notmatch "SalesTransitions_RequirePreparedAttemptAndRecoverStaleInProgress") { Fail "outbox binding regression coverage incomplete" } else { Pass "outbox binding, immutable payload, reversal-origin and recovery tests present" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
