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
    "src/Win7POS.Data/Online/CatalogShopStateRepository.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs"
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
$fullRefresh = Read-Text "src/Win7POS.Data/Online/CatalogFullRefreshReconciler.cs"
$compatibility = Read-Text "src/Win7POS.Data/Online/PosOnlineCompatibilityValidator.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$repository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
$categoryRepository = Read-Text "src/Win7POS.Data/Repositories/CategoryRepository.cs"
$supplierRepository = Read-Text "src/Win7POS.Data/Repositories/SupplierRepository.cs"
$categorySupplierResolver = Read-Text "src/Win7POS.Data/Import/CategorySupplierResolver.cs"
$productImportApply = Read-Text "src/Win7POS.Data/Import/ProductImportApplyService.cs"
$productDbImporter = Read-Text "src/Win7POS.Data/ImportDb/ProductDbImporter.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($client -notmatch "/api/pos/catalog/pull") { Fail "catalog pull path missing" } else { Pass "catalog pull path present" }
if ($client -notmatch "CatalogPullAsync") { Fail "CatalogPullAsync missing" } else { Pass "CatalogPullAsync present" }
if ($service -notmatch "PosTrustedDeviceStore") { Fail "trusted device store not used for catalog pull" } else { Pass "trusted device store used" }
if ($service -notmatch "ProductRepository") { Fail "local product repository not used" } else { Pass "local product repository used" }
if ($service -notmatch "UpsertProductAndMetaInTransactionAsync") { Fail "catalog rows are not persisted locally" } else { Pass "catalog rows persisted locally" }
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
if ($service -notmatch "TryPullInitialCatalogAsync") { Fail "initial catalog pull path missing" } else { Pass "initial catalog pull path present" }
if ($service -notmatch "completed" -or $service -notmatch "partial_has_more" -or $service -notmatch "failed_retryable" -or $service -notmatch "failed_auth_denied") { Fail "catalog bootstrap status values incomplete" } else { Pass "catalog bootstrap status values present" }
if ($service -notmatch "pos\.catalog\.sale_safe_at" -or $service -notmatch "pos\.catalog\.initial_completed_at" -or $service -notmatch "CountActiveRemoteProductsAsync") { Fail "catalog sale-safe readiness markers missing" } else { Pass "catalog sale-safe readiness markers present" }
if ($service -notmatch "has_more_not_drained") { Fail "catalog HasMore cap error code missing" } else { Pass "catalog HasMore cap error code present" }
if ($service -notmatch "lastResponse\.HasMore[\s\S]*StoreCatalogFailureAsync\(CatalogHasMoreNotDrainedCode\)[\s\S]*PosCatalogPullOutcome\.Failure") { Fail "catalog HasMore after cap is not treated as failure/partial outcome" } else { Pass "catalog HasMore after cap fails visibly" }
if ($service -notmatch "cursorSaved=" -or $service -notmatch "!fullRefresh") { Fail "catalog partial HasMore log must distinguish resumable delta from restartable full refresh" } else { Pass "catalog partial HasMore log distinguishes delta/full-refresh cursor persistence" }
if ($service -notmatch "CatalogPullWithRetryAsync") { Fail "catalog pull retry helper missing" } else { Pass "catalog pull retry helper present" }
if ($service -notmatch "Task.Delay") { Fail "catalog pull backoff missing" } else { Pass "catalog pull backoff present" }
if ($service -notmatch "SyncCursor\s*=\s*page\s*==\s*1\s*\?\s*binding\.Cursor" -or $service -notmatch "EnsureAndLoadCursorAsync") { Fail "catalog pull syncCursor is not loaded from shop-bound state" } else { Pass "catalog pull syncCursor uses shop-bound state" }
$capturedSessionCheckIndex = $service.IndexOf("ValidateCapturedSessionAsync")
$bindingIndex = $service.IndexOf("EnsureAndLoadCursorAsync")
$networkIndex = $service.IndexOf("new PosAdminWebClient")
if ($capturedSessionCheckIndex -lt 0 -or $bindingIndex -lt 0 -or $networkIndex -lt 0 -or $capturedSessionCheckIndex -gt $bindingIndex -or $capturedSessionCheckIndex -gt $networkIndex -or $shopState -notmatch "catalog_session_shop_changed") { Fail "captured catalog session must be revalidated inside the transition barrier before bind/network" } else { Pass "captured catalog session is revalidated before bind/network" }
if ($shopState -notmatch "pos\.catalog\.bound_shop_id" -or $shopState -notmatch "pos\.catalog\.bound_shop_code" -or $shopState -notmatch "Catalog state shop binding mismatch") { Fail "persistent catalog shop binding missing" } else { Pass "persistent catalog shop binding present" }
if ($service -notmatch "responseShopError[\s\S]*response_shop_mismatch[\s\S]*ApplyCatalogAsync") { Fail "catalog response shop must be validated before local apply" } else { Pass "catalog response shop validated before local apply" }
if ($service -notmatch "CatalogFullRefreshReconciler" -or $fullRefresh -notmatch "NOT EXISTS" -or $fullRefresh -notmatch "remote_product_id" -or $fullRefresh -notmatch "remote_category_id" -or $fullRefresh -notmatch "remote_supplier_id") { Fail "full_refresh reconciliation missing" } else { Pass "full_refresh reconciles remote rows absent from authoritative snapshot" }
if ($service -notmatch "ValidateCatalogPull" -or $compatibility -notmatch "catalog_schema_not_supported" -or $compatibility -notmatch "catalog_capability_not_supported" -or $compatibility -notmatch "policy_contract_not_supported") { Fail "catalog schema/policy/capability fail-closed validation missing" } else { Pass "catalog schema, policy and capabilities fail closed" }
if ($compatibility -match '"incremental"' -or $compatibility -notmatch '"delta"' -or $compatibility -notmatch '"full_refresh"') { Fail "catalog compatibility must accept only actual Admin sync modes delta/full_refresh" } else { Pass "catalog compatibility accepts only Admin delta/full_refresh modes" }
if ($compatibility -notmatch "SalesSync \?\? string\.Empty" -or $compatibility -notmatch "sales_sync_contract_not_supported") { Fail "SalesSync capability must be mandatory and exact" } else { Pass "SalesSync capability is mandatory and exact" }
if ($shopState -notmatch "StoreLastSyncAsync" -or $shopState -notmatch "StoreSaleSafeAsync" -or $shopState -notmatch "BeginTransaction") { Fail "catalog cursor/sale-safe writes are not transactionally shop-bound" } else { Pass "catalog cursor/sale-safe writes are transactionally shop-bound" }
if ($client -notmatch 'DataMember\(Name = "syncCursor"') { Fail "catalog pull syncCursor wire field missing" } else { Pass "catalog pull syncCursor wire field present" }
if ($client -notmatch 'DataMember\(Name = "catalogVersion"') { Fail "catalog version wire field missing" } else { Pass "catalog version wire field present" }
if ($client -notmatch 'DataMember\(Name = "hasMore"') { Fail "hasMore wire field missing" } else { Pass "hasMore wire field present" }
if ($client -notmatch 'DataMember\(Name = "tombstones"') { Fail "tombstones wire field missing" } else { Pass "tombstones wire field present" }
if ($repository -notmatch "remote_product_id") { Fail "remote product id column missing in repository" } else { Pass "remote product id used in repository" }
if ($repository -notmatch "remote_catalog_pending_prices" -or $repository -notmatch "QueuePendingRemotePriceAsync" -or $repository -notmatch "ApplyPendingRemotePricesAsync") { Fail "pending remote price replay missing" } else { Pass "pending remote price replay present" }
if ($repository -notmatch "PendingRemotePriceReplayBatchSize" -or $repository -notmatch "while\s*\(true\)" -or $repository -notmatch "canonical" -or $repository -notmatch "GROUP BY remote_product_id") { Fail "pending remote price replay must drain all resolvable batches against a canonical product per remote_product_id" } else { Pass "pending remote price replay drains all resolvable batches against canonical remote product" }
if ($repository -notmatch "remote_price_id" -or $initializer -notmatch "remote_price_id" -or $service -notmatch "Normalize\(price\.PriceId\)") { Fail "remote priceId idempotency missing" } else { Pass "remote priceId idempotency present" }
if ($repository -notmatch "remote_deleted_at") { Fail "remote tombstone column missing in repository" } else { Pass "remote tombstone column used in repository" }
if ($repository -notmatch "ApplyRemoteProductTombstoneAsync") { Fail "remote product tombstone apply missing" } else { Pass "remote product tombstone apply present" }
if ($repository -notmatch "CanonicalizeRemoteProductBeforeUpsertAsync" -or $repository -notmatch "DeactivateRemoteProductDuplicatesAsync" -or $repository -notmatch "remote_product_id\s*=\s*@remoteProductId[\s\S]{0,160}barcode\s*<>\s*@barcode") { Fail "remote product upsert must canonicalize remote_product_id and deactivate duplicate active barcodes" } else { Pass "remote product upsert canonicalizes remote_product_id duplicates" }
if ($repository -notmatch "hasPendingLocalStock" -or $repository -notmatch "sales_sync_outbox" -or $repository -notmatch "'pending', 'retry', 'in_progress', 'failed_blocked'" -or $repository -notmatch "stockQtyToWrite = existingStock") { Fail "catalog upsert must preserve stock_qty when unresolved local stock movements exist" } else { Pass "catalog upsert preserves unresolved local stock" }
$tombstoneMethod = [regex]::Match($repository, "ApplyRemoteProductTombstoneAsync[\s\S]*?UpsertRemotePriceHistoryAsync").Value
if ($tombstoneMethod -notmatch "is_active\s*=\s*0" -or $tombstoneMethod -notmatch "remote_deleted_at") { Fail "product tombstone must soft-delete/inactivate products" } else { Pass "product tombstone is soft-delete/inactive" }
if ($tombstoneMethod -match "DELETE\s+FROM") { Fail "product tombstone must not purge rows" } else { Pass "product tombstone does not purge rows" }
if ($service -notmatch "categoryRepository\.UpsertRemoteAsync" -or $service -notmatch "supplierRepository\.UpsertRemoteAsync") { Fail "remote category/supplier identities are not persisted" } else { Pass "remote category/supplier identities persisted" }
if ($service -notmatch "categoryRepository\.ApplyRemoteTombstoneAsync" -or $service -notmatch "supplierRepository\.ApplyRemoteTombstoneAsync") { Fail "category/supplier tombstones are not applied" } else { Pass "category/supplier tombstones applied" }
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
