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

function Require([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Pass $label } else { Fail $label }
}

$client = Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs"
$dto = Read-Text "src/Win7POS.Core/Online/PosCatalogImportContract.cs"
$repository = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$reconciliation = Read-Text "src/Win7POS.Data/Online/CatalogImportReconciliationService.cs"
$service = Read-Text "src/Win7POS.Data/Online/CatalogImportSyncService.cs"
$cli = Read-Text "src/Win7POS.Cli/Program.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"

Require "catalog import endpoint path present" $client "/api/pos/catalog/import-sync"
Require "catalog import client method present" $client "CatalogImportAsync"
Require "catalog import posts DTO request/response" $client "PostJsonAsync<PosCatalogImportRequest,\s*PosCatalogImportResponse>"
foreach ($field in @("deviceToken", "sessionToken", "posSessionId", "shopDeviceId", "shopCode")) {
    Require "catalog import DTO field $field present" $dto $field
}

Require "sync service pending query present" $service "GetPendingAsync"
Require "sync service prepare transition present" $service "PrepareAttemptAsync"
Require "sync service calls Admin Web client" $service "CatalogImportAsync"
Require "sync service ack transition present" $service "MarkAckedAsync"
Require "sync service retry transition present" $service "MarkRetryAsync"
Require "sync service blocked transition present" $service "MarkBlockedAsync"
Require "sync service validates payload hash" $service "hash_mismatch"
Require "sync service rejects persisted auth payload" $service "payload_contains_auth"
Require "sync service validates remote batch id before ack" $service "IsExpectedRemoteBatch"
Require "sync service validates remote idempotency key before ack" $service "batch\.IdempotencyKey[\s\S]*item\.IdempotencyKey"
Require "sync service validates optional remote payload hash before ack" $service "payload_hash_mismatch"
Require "sync service blocks remote client import mismatch" $service "client_import_mismatch"
Require "sync service prefers local client item barcode for ACK id mapping" $service "ResolveAckBarcode[\s\S]*BarcodeFor"
Require "sync service builds ack result with remote ids" $service "BuildAckResult[\s\S]*RemoteProductIds[\s\S]*RemotePriceIds"
Require "accepted maps to ack" $service "accepted"
Require "duplicate maps to ack" $service "duplicate"
Require "idempotent maps to ack" $service "idempotent"
Require "validation failed maps to block" $service "validation_failed"
Require "conflict maps to block" $service "conflict"
Require "auth denied maps to retry/trust clear" $service "auth_denied"
Require "bounded retry attempts present" $service "MaxAttemptsBeforeBlocked"

Require "repository has in_progress lease window" $repository "CatalogImportInProgressLeaseMilliseconds"
Require "repository stale in_progress recovery is lease-gated" $repository "COALESCE\(last_attempt_at, updated_at, 0\) <= @staleInProgressBefore"
Require "repository unresolved includes in_progress" $repository "status IN \('pending', 'retry', 'in_progress', 'failed_blocked'\)"
Require "repository prepare sets in_progress" $repository "SET status = 'in_progress'"
Require "repository final transitions are attempt-token guarded" $repository "expectedAttemptCount <= 0[\s\S]*attempt_count = @expectedAttemptCount"
Require "repository ack stores server request id" $repository "server_request_id"
Require "repository ack applies remote product ids" $repository "ApplyRemoteProductIdsAsync"
Require "repository ack applies remote price ids" $repository "ApplyRemotePriceIdsAsync"
Require "repository remote price ack is client-item scoped" $repository "catalog_import_client_item_id = @ClientItemId[\s\S]*catalog_import_idempotency_key = @IdempotencyKey"
Require "repository remote price ack keeps legacy fallback guarded" $repository "importRows > 0[\s\S]*continue"
Require "repository summary exposes InProgress" $repository "InProgress"
Require "repository idempotency conflict guard present" $repository "idempotency conflict"
Require "reconciliation recovers expired in_progress" $reconciliation "RecoverExpiredInProgressAsync[\s\S]*status = 'retry'"
Require "reconciliation signals failed_blocked" $reconciliation "GetFailedBlockedCountAsync[\s\S]*failed_blocked"
Require "reconciliation applies remote product ids by barcode" $reconciliation "ReconcileRemoteProductIdAsync[\s\S]*remote_product_id"

Require "CLI catalog import outbox selftest flag present" $cli "--catalog-import-outbox-selftest"
Require "CLI catalog import reconciliation selftest flag present" $cli "--catalog-import-reconciliation-selftest"
Require "CLI catalog import sync HTTP harness flag present" $cli "--catalog-import-sync-http-harness"
Require "CLI fake HTTP server present" $cli "CatalogImportFakeServer"
Require "CLI fake server emits idempotency key" $cli "IdempotencyKeyFromBody"
Require "CLI harness asserts remote ids saved" $cli "ReadProductRemoteProductIdAsync[\s\S]*ReadLatestRemotePriceIdAsync"
Require "WPF background catalog import sync wired" $mainWindow "TrySyncCatalogImportOutboxAsync"

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
