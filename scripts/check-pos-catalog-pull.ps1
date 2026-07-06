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
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$repository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
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
if ($service -notmatch "cursorSaved=true") { Fail "catalog partial HasMore log must state cursor is preserved" } else { Pass "catalog partial HasMore log states cursor preserved" }
if ($service -notmatch "CatalogPullWithRetryAsync") { Fail "catalog pull retry helper missing" } else { Pass "catalog pull retry helper present" }
if ($service -notmatch "Task.Delay") { Fail "catalog pull backoff missing" } else { Pass "catalog pull backoff present" }
if ($service -notmatch "SyncCursor = await LoadLastCursorAsync") { Fail "catalog pull syncCursor request missing" } else { Pass "catalog pull syncCursor request present" }
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
if ($repository -notmatch "hasPendingLocalStock" -or $repository -notmatch "sales_sync_outbox" -or $repository -notmatch "'pending', 'retry'" -or $repository -notmatch "failed_blocked" -or $repository -notmatch "stockQtyToWrite = existingStock") { Fail "catalog upsert must preserve stock_qty when local pending/retry/blocked stock movements exist" } else { Pass "catalog upsert preserves pending local stock" }
$tombstoneMethod = [regex]::Match($repository, "ApplyRemoteProductTombstoneAsync[\s\S]*?UpsertRemotePriceHistoryAsync").Value
if ($tombstoneMethod -notmatch "is_active\s*=\s*0" -or $tombstoneMethod -notmatch "remote_deleted_at") { Fail "product tombstone must soft-delete/inactivate products" } else { Pass "product tombstone is soft-delete/inactive" }
if ($tombstoneMethod -match "DELETE\s+FROM") { Fail "product tombstone must not purge rows" } else { Pass "product tombstone does not purge rows" }
if ($service -notmatch "category/supplier tombstones are diagnostic-only") { Fail "category/supplier tombstone limitation must be logged/diagnostic" } else { Pass "category/supplier tombstone limitation documented in diagnostics" }
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
