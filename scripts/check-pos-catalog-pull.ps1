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
    "src/Win7POS.Data/Repositories/RemoteCatalogBatchRepository.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "tests/Win7POS.Core.Tests/Data/CatalogExactnessTests.cs",
    "tests/Win7POS.Core.Tests/Data/RemoteCatalogBatchRepositoryTests.cs",
    "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs"
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
$shopState = Read-Text "src/Win7POS.Data/Online/CatalogShopStateRepository.cs"
$catalogImportOutbox = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$fullRefresh = Read-Text "src/Win7POS.Data/Online/CatalogFullRefreshReconciler.cs"
$compatibility = Read-Text "src/Win7POS.Data/Online/PosOnlineCompatibilityValidator.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$repository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
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
$restoreTests = Read-Text "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($client -notmatch "/api/pos/catalog/pull") { Fail "catalog pull path missing" } else { Pass "catalog pull path present" }
if ($client -notmatch "CatalogPullAsync") { Fail "CatalogPullAsync missing" } else { Pass "CatalogPullAsync present" }
if ($service -notmatch "PosTrustedDeviceStore") { Fail "trusted device store not used for catalog pull" } else { Pass "trusted device store used" }
if ($service -notmatch "RemoteCatalogBatchRepository" -or $service -notmatch "ApplyAsync\(batch, cancellationToken\)") { Fail "catalog pages are not delegated to the batch repository with cancellation" } else { Pass "catalog pages use the cancellation-aware batch repository" }
if ($batchRepository -notmatch "class RemoteCatalogBatchRepository" -or $batchRepository -notmatch "Task<RemoteCatalogBatchApplyResult>\s+ApplyAsync") { Fail "remote catalog batch repository contract missing" } else { Pass "remote catalog batch repository contract present" }
$batchCancellationIndex = $batchRepository.LastIndexOf("cancellationToken.ThrowIfCancellationRequested")
$batchTransactionIndex = $batchRepository.IndexOf("conn.BeginTransaction")
$batchCommitIndex = $batchRepository.IndexOf("tx.Commit()")
$batchRollbackIndex = $batchRepository.IndexOf("tx.Rollback()")
if ($batchRepository -notmatch "WaitAsync\(cancellationToken\)" -or
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
    $batchRepository.Substring($batchTransactionIndex) -match "ThrowIfCancellationRequested|WaitAsync\(cancellationToken\)") {
    Fail "catalog batch must not abandon a partially written page on mid-transaction cancellation"
} else {
    Pass "catalog batch cancellation cannot split a page transaction"
}
if ($batchRepository -notmatch "UpsertRemoteInTransactionAsync" -or
    $batchRepository -notmatch "UpsertProductAndMetaInTransactionCoreAsync" -or
    $batchRepository -notmatch "UpsertOrQueueRemotePriceHistoryInTransactionAsync" -or
    $batchRepository -notmatch "ApplyRemoteProductTombstoneInTransactionAsync") {
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
$bootstrapCapMatch = [regex]::Match($service, "MaxBootstrapCatalogPullPages\s*=\s*(\d+)")
if (-not $bootstrapCapMatch.Success) {
    Fail "bootstrap catalog pull cap missing"
} elseif ([int]$bootstrapCapMatch.Groups[1].Value -lt 60) {
    Fail "bootstrap catalog pull cap too low for large first sync"
} else {
    Pass "bootstrap catalog pull cap supports large first sync"
}
if ($service -notmatch "effectiveMaxPages" -or
    $service -notmatch "Math\.Max\([\s\S]*MaxBootstrapCatalogPullPages" -or
    $service -notmatch "for\s*\(var page = 1; page <= effectiveMaxPages; page\+\+\)") {
    Fail "a server-selected full_refresh on the background path can remain trapped by the delta page cap"
} else {
    Pass "server-selected full_refresh dynamically uses the bootstrap drain cap"
}
if ($service -notmatch "TryPullInitialCatalogAsync") { Fail "initial catalog pull path missing" } else { Pass "initial catalog pull path present" }
if ($service -notmatch "completed" -or $service -notmatch "partial_has_more" -or $service -notmatch "failed_retryable" -or $service -notmatch "failed_auth_denied") { Fail "catalog bootstrap status values incomplete" } else { Pass "catalog bootstrap status values present" }
if ($service -notmatch "pos\.catalog\.sale_safe_at" -or $service -notmatch "pos\.catalog\.initial_completed_at" -or $service -notmatch "CountActiveRemoteProductsAsync") { Fail "catalog sale-safe readiness markers missing" } else { Pass "catalog sale-safe readiness markers present" }
if ($service -notmatch "has_more_not_drained") { Fail "catalog HasMore cap error code missing" } else { Pass "catalog HasMore cap error code present" }
if ($service -notmatch "lastResponse\.HasMore[\s\S]*StoreCatalogFailureAsync\(CatalogHasMoreNotDrainedCode\)[\s\S]*PosCatalogPullOutcome\.Failure") { Fail "catalog HasMore after cap is not treated as failure/partial outcome" } else { Pass "catalog HasMore after cap fails visibly" }
if ($service -notmatch "cursorSaved=" -or $service -notmatch "!fullRefresh") { Fail "catalog partial HasMore log must distinguish resumable delta from restartable full refresh" } else { Pass "catalog partial HasMore log distinguishes delta/full-refresh cursor persistence" }
if ($service -notmatch "CatalogPullWithRetryAsync") { Fail "catalog pull retry helper missing" } else { Pass "catalog pull retry helper present" }
if ($service -notmatch "Task.Delay") { Fail "catalog pull backoff missing" } else { Pass "catalog pull backoff present" }
if ($service -notmatch "var\s+requestCursor\s*=\s*page\s*==\s*1\s*\?\s*binding\.Cursor\s*:\s*cursor" -or $service -notmatch "SyncCursor\s*=\s*requestCursor" -or $service -notmatch "EnsureAndLoadCursorAsync") { Fail "catalog pull syncCursor is not loaded from shop-bound state" } else { Pass "catalog pull syncCursor uses shop-bound state" }
$capturedSessionCheckIndex = $service.IndexOf("ValidateCapturedSessionAsync")
$bindingIndex = $service.IndexOf("EnsureAndLoadCursorAsync")
$networkIndex = $service.IndexOf("new PosAdminWebClient")
if ($capturedSessionCheckIndex -lt 0 -or $bindingIndex -lt 0 -or $networkIndex -lt 0 -or $capturedSessionCheckIndex -gt $bindingIndex -or $capturedSessionCheckIndex -gt $networkIndex -or $shopState -notmatch "catalog_session_shop_changed") { Fail "captured catalog session must be revalidated inside the transition barrier before bind/network" } else { Pass "captured catalog session is revalidated before bind/network" }
if ($shopState -notmatch "pos\.catalog\.bound_shop_id" -or $shopState -notmatch "pos\.catalog\.bound_shop_code" -or $shopState -notmatch "Catalog state shop binding mismatch") { Fail "persistent catalog shop binding missing" } else { Pass "persistent catalog shop binding present" }
if ($service -notmatch "responseShopError[\s\S]*response_shop_mismatch[\s\S]*ApplyCatalogAsync") { Fail "catalog response shop must be validated before local apply" } else { Pass "catalog response shop validated before local apply" }
if ($service -notmatch "var\s+authoritativeProductIds\s*=\s*new List<string>" -or
    $service -notmatch "var\s+authoritativeCategoryIds\s*=\s*new List<string>" -or
    $service -notmatch "var\s+authoritativeSupplierIds\s*=\s*new List<string>") {
    Fail "full refresh must retain raw authoritative ID rows for duplicate/invalid evidence"
} else {
    Pass "full refresh retains raw authoritative ID rows"
}
$rawIdCollector = [regex]::Match($service, "private static void AddRemoteIds[\s\S]*?(?=\r?\n\s*private static)").Value
if ($rawIdCollector -notmatch "ICollection<string>" -or
    $rawIdCollector -notmatch "target\.Add\(Normalize\(selector\(value\)\)\)" -or
    $rawIdCollector -match "IsNullOrWhiteSpace|Distinct\(|HashSet") {
    Fail "authoritative ID collector must preserve empty and duplicate raw rows for exactness audit"
} else {
    Pass "authoritative ID collector preserves invalid/duplicate evidence"
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
    $service -notmatch "request\.SyncCursor\s*=\s*requestCursor" -or
    $service -notmatch "seenCursorFingerprints\.Clear\(\)" -or
    $service -notmatch '"cursor_expired"' -or
    $service -notmatch '"cursor_rejected"') {
    Fail "server-rejected/expired cursors must reset shop-bound state and retry once from the authoritative boundary"
} else {
    Pass "expired/rejected cursors trigger a controlled full-refresh boundary retry"
}
$catalogApplyIndex = $service.IndexOf("ApplyCatalogAsync(result.Value, fullRefresh, cancellationToken)")
$pageOnePolicyIndex = $service.IndexOf("if (page == 1)")
$repairBeforeApplyIndex = if ($catalogApplyIndex -gt 0) {
    $service.Substring(0, $catalogApplyIndex).LastIndexOf("RequestFullRepairWhileBarrierHeldAsync")
} else {
    -1
}
if ($catalogApplyIndex -lt 0 -or
    $pageOnePolicyIndex -lt 0 -or
    $repairBeforeApplyIndex -lt $pageOnePolicyIndex -or
    $service.Substring($pageOnePolicyIndex, $catalogApplyIndex - $pageOnePolicyIndex) -notmatch "fullRefresh\s*&&\s*!forceFullRepair") {
    Fail "automatic full_refresh must reset repair/cursor state before the first page is applied"
} else {
    Pass "full_refresh resets repair/cursor state before local apply"
}
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
$reconcileIndex = $service.IndexOf(".ReconcileAndVerifyAsync(")
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
if ($service -notmatch "exactness\.Status\s*==\s*CatalogCompletenessStatus\.Mismatch" -or
    $service -notmatch "exactness\.RepairRequired" -or
    $service.IndexOf("exactness.RepairRequired") -lt $storeExactnessIndex -or
    $service.IndexOf("exactness.RepairRequired") -gt $authoritativeCursorIndex) {
    Fail "mismatch/repair-required exactness must fail closed before authoritative cursor commit"
} else {
    Pass "exactness mismatch fails closed before cursor commit"
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
    $compatibility -notmatch "value\.Length\s*>\s*128" -or
    $compatibility -notmatch "value\.Any\(char\.IsControl\)" -or
    $compatibility -notmatch "string\.Equals\(value, value\.Trim\(\), StringComparison\.Ordinal\)" -or
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
if ($compatibility -notmatch "HasDuplicateCatalogIds" -or
    $compatibility -notmatch "catalog_duplicate_product_ids" -or
    $compatibility -notmatch "catalog_duplicate_category_ids" -or
    $compatibility -notmatch "catalog_duplicate_supplier_ids" -or
    $catalogExactnessTests -notmatch "CatalogRowValidator_RejectsDuplicateRemoteIdsWithinOnePage") {
    Fail "duplicate product/category/supplier remote IDs can collapse within one response page"
} else {
    Pass "duplicate product/category/supplier remote IDs fail before page apply"
}
if ($compatibility -match '"incremental"' -or $compatibility -notmatch '"delta"' -or $compatibility -notmatch '"full_refresh"') { Fail "catalog compatibility must accept only actual Admin sync modes delta/full_refresh" } else { Pass "catalog compatibility accepts only Admin delta/full_refresh modes" }
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
if ($repository -notmatch "remote_product_id") { Fail "remote product id column missing in repository" } else { Pass "remote product id used in repository" }
if ($repository -notmatch "remote_catalog_pending_prices" -or $repository -notmatch "QueuePendingRemotePriceAsync" -or $repository -notmatch "ApplyPendingRemotePricesAsync") { Fail "pending remote price replay missing" } else { Pass "pending remote price replay present" }
if ($repository -notmatch "PendingRemotePriceReplayBatchSize" -or $repository -notmatch "while\s*\(true\)" -or $repository -notmatch "canonical" -or $repository -notmatch "GROUP BY remote_product_id") { Fail "pending remote price replay must drain all resolvable batches against a canonical product per remote_product_id" } else { Pass "pending remote price replay drains all resolvable batches against canonical remote product" }
if ($repository -notmatch "remote_price_id" -or $initializer -notmatch "remote_price_id" -or $service -notmatch "RemotePriceId\s*=\s*Normalize\(row\.PriceId\)" -or $batchRepository -notmatch "price\.RemotePriceId") { Fail "remote priceId idempotency missing" } else { Pass "remote priceId idempotency present" }
if ($initializer -notmatch "remote_catalog_price_evidence_quarantine" -or
    $repository -notmatch "PrepareAuthoritativeRemotePriceRepairAsync" -or
    $repository -notmatch "StoreRemotePriceOwnershipAsync" -or
    $batchRepository -notmatch "AuthoritativeFullRefresh" -or
    $service -notmatch "AuthoritativeFullRefresh\s*=\s*authoritativeFullRefresh" -or
    $catalogImportOutbox -notmatch "EnsureAssignedRemotePriceOwnershipAsync" -or
    $catalogImportOutbox -notmatch "StoreRemotePriceOwnershipAsync") {
    Fail "remote price ownership must be atomic for pull/import writers and legacy evidence must be quarantined only on authoritative full refresh"
} else {
    Pass "remote price ownership is atomic and authoritative repair preserves legacy evidence in quarantine"
}
if ($batchRepository -notmatch "PricesSkipped" -or
    $service -notmatch "PriceRowsSkipped\s*=\s*applied\.PricesSkipped" -or
    $service -notmatch "PriceRowsAccepted\s*=\s*totalStats\.PriceRowsApplied\s*\+\s*totalStats\.PriceRowsQueued" -or
    $service -notmatch "InvalidPriceRows\s*=\s*totalStats\.PriceRowsSkipped" -or
    $service -notmatch "DuplicatePriceRows\s*=\s*duplicatePriceRows" -or
    $fullRefresh -notmatch "catalog_invalid_price_rows" -or
    $fullRefresh -notmatch "catalog_duplicate_price_rows" -or
    $fullRefresh -notmatch "catalog_prices_not_fully_applied") {
    Fail "price exactness evidence is incomplete or not fail-closed"
} else {
    Pass "price received/accepted/skipped/duplicate evidence is verified fail-closed"
}
if ($repository -notmatch "remote_deleted_at") { Fail "remote tombstone column missing in repository" } else { Pass "remote tombstone column used in repository" }
if ($repository -notmatch "ApplyRemoteProductTombstoneAsync") { Fail "remote product tombstone apply missing" } else { Pass "remote product tombstone apply present" }
if ($repository -notmatch "CanonicalizeRemoteProductBeforeUpsertAsync" -or $repository -notmatch "DeactivateRemoteProductDuplicatesAsync" -or $repository -notmatch "remote_product_id\s*=\s*@remoteProductId[\s\S]{0,160}barcode\s*<>\s*@barcode") { Fail "remote product upsert must canonicalize remote_product_id and deactivate duplicate active barcodes" } else { Pass "remote product upsert canonicalizes remote_product_id duplicates" }
if ($repository -notmatch "hasPendingLocalStock" -or $repository -notmatch "sales_sync_outbox" -or $repository -notmatch "'pending', 'retry', 'in_progress', 'failed_blocked'" -or $repository -notmatch "stockQtyToWrite = existingStock") { Fail "catalog upsert must preserve stock_qty when unresolved local stock movements exist" } else { Pass "catalog upsert preserves unresolved local stock" }
$tombstoneMethod = [regex]::Match($repository, "ApplyRemoteProductTombstoneAsync[\s\S]*?UpsertRemotePriceHistoryAsync").Value
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
$localReferenceWriters = @($categorySupplierResolver, $productImportApply, $productDbImporter, $repository) -join "`n"
if ($localReferenceWriters -match "INSERT\s+OR\s+REPLACE\s+INTO\s+(categories|suppliers)") { Fail "local import can destructively replace remote category/supplier identity" } else { Pass "local import does not replace remote category/supplier identity" }
if ($categorySupplierResolver -notmatch "COALESCE\(is_active, 1\) = 1" -or $repository -notmatch "COALESCE\(is_active, 1\) = 1") { Fail "local reference resolution can reuse remote tombstones" } else { Pass "local reference resolution excludes remote tombstones" }
if ($productImportApply -notmatch "remote_supplier_id" -or $productImportApply -notmatch "remote_category_id" -or $productDbImporter -notmatch "remote_supplier_id" -or $productDbImporter -notmatch "remote_category_id") { Fail "local import remote-identity collision guards missing" } else { Pass "local import remote-identity collision guards present" }
if ($initializer -notmatch "remote_product_id") { Fail "remote product id column missing in db initializer" } else { Pass "remote product id column present" }
if ($initializer -notmatch "remote_deleted_at") { Fail "remote tombstone column missing in db initializer" } else { Pass "remote tombstone column present" }
if ($initializer -notmatch "is_active") { Fail "active product column missing in db initializer" } else { Pass "active product column present" }
if ($mainWindow -notmatch "TryPullCatalogAsync") { Fail "startup catalog pull foundation missing" } else { Pass "startup catalog pull foundation present" }

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
