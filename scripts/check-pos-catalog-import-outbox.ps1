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

function Assert-Contains([string]$text, [string]$needle, [string]$message) {
    if ($text.Contains($needle)) { Pass $message } else { Fail $message }
}

$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$contract = Read-Text "src/Win7POS.Core/Online/PosOnlineContract.cs"
$dto = Read-Text "src/Win7POS.Core/Online/PosCatalogImportContract.cs"
$repository = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$syncService = Read-Text "src/Win7POS.Data/Online/CatalogImportSyncService.cs"
$client = Read-Text "src/Win7POS.Core/Online/PosAdminWebClient.cs"
$builder = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxPayloadBuilder.cs"
$applier = Read-Text "src/Win7POS.Data/Import/SupplierExcelImportApplier.cs"
$cli = Read-Text "src/Win7POS.Cli/Program.cs"
$viewModel = Read-Text "src/Win7POS.Wpf/Import/SupplierExcelImportViewModel.cs"
$workflow = Read-Text "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs"
$productRepository = Read-Text "src/Win7POS.Data/Repositories/ProductRepository.cs"
$restoreGuard = Read-Text "scripts/check-win7pos-restore-guard.ps1"
$combinedSrc = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

Assert-Contains $contract 'CatalogImportSchemaVersion = "pos-catalog-import-v1"' "catalog import schema version present"
Assert-Contains $dto "PosCatalogImportRequest" "catalog import DTO request present"
Assert-Contains $dto "PosCatalogImportResponse" "catalog import DTO response present"
Assert-Contains $dto "deviceToken" "catalog import DTO carries device auth for send-time only"
Assert-Contains $dto "sessionToken" "catalog import DTO carries session auth for send-time only"
Assert-Contains $initializer "CREATE TABLE IF NOT EXISTS catalog_import_outbox" "catalog_import_outbox table present"
Assert-Contains $initializer "idx_catalog_import_outbox_status_next" "catalog import status/next index present"
Assert-Contains $initializer "idx_catalog_import_outbox_idempotency" "catalog import idempotency index present"
Assert-Contains $repository "GetPendingAsync" "catalog import pending query present"
Assert-Contains $repository "MarkAckedAsync" "catalog import ack transition present"
Assert-Contains $repository "MarkRetryAsync" "catalog import retry transition present"
Assert-Contains $repository "MarkBlockedAsync" "catalog import blocked transition present"
Assert-Contains $repository "CatalogImportInProgressLeaseMilliseconds" "catalog import in_progress lease present"
Assert-Contains $repository "COALESCE(last_attempt_at, updated_at, 0) <= @staleInProgressBefore" "catalog import stale in_progress recovery is lease-gated"
Assert-Contains $repository "attempt_count = @expectedAttemptCount" "catalog import final transitions are attempt-token guarded"
Assert-Contains $repository "status IN ('pending', 'retry', 'in_progress', 'failed_blocked')" "catalog import unresolved guard includes in_progress"
Assert-Contains $repository "idempotency conflict" "catalog import enqueue detects idempotency conflict"
Assert-Contains $builder "Path.GetFileName" "catalog import payload redacts source file path"
Assert-Contains $builder "Sha256Hex(payloadJson)" "catalog import payload hash present"
Assert-Contains $client "/api/pos/catalog/import-sync" "catalog import Admin Web endpoint present"
Assert-Contains $client "CatalogImportAsync" "catalog import Admin Web client method present"
Assert-Contains $syncService "CatalogImportOutboxPayloadValidator" "catalog import sync validates persisted payload"
Assert-Contains $syncService "payload_contains_auth" "catalog import sync rejects persisted auth payload"
Assert-Contains $syncService "MarkAckedAsync" "catalog import sync can ack remote acceptance"
Assert-Contains $syncService "MarkRetryAsync" "catalog import sync can retry transient failures"
Assert-Contains $syncService "MarkBlockedAsync" "catalog import sync can block validation/conflict"
Assert-Contains $cli "--catalog-import-sync-http-harness" "catalog import HTTP harness present"
Assert-Contains $applier "CatalogImportOutboxEntry" "supplier apply accepts catalog outbox entry"
Assert-Contains $applier "CatalogImportOutboxRepository" "supplier apply enqueues catalog outbox"
Assert-Contains $applier ".EnqueueAsync(conn, tx" "catalog outbox enqueue uses supplier apply transaction"
Assert-Contains $workflow "CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry" "supplier workflow builds catalog import payload"
Assert-Contains $viewModel "_service.ApplyAsync(SyncPreview, false, SelectedFileName)" "supplier apply passes redacted file name"
Assert-Contains $workflow "ListDetailsByBarcodesAsync" "supplier workflow uses targeted catalog lookup"
Assert-Contains $productRepository "ListDetailsByBarcodesAsync" "targeted product details lookup present"
Assert-Contains $productRepository "is_active = 0" "product delete uses soft delete"
Assert-Contains $productRepository "remote_deleted_at = @deletedAt" "product soft delete records timestamp"
Assert-Contains $restoreGuard "catalog_import_outbox" "restore guard covers catalog import outbox"

$deleteMethod = [regex]::Match($productRepository, "public\s+async\s+Task<bool>\s+DeleteByBarcodeAsync[\s\S]*?^\s*}\s*$", [System.Text.RegularExpressions.RegexOptions]::Multiline)
if ($deleteMethod.Success -and $deleteMethod.Value -match "(?i)\bDELETE\s+FROM\s+(products|product_meta)\b") {
    Fail "DeleteByBarcodeAsync must not hard-delete products/product_meta"
} else {
    Pass "DeleteByBarcodeAsync has no product hard-delete"
}

if ($combinedSrc -match "SUPABASE_SERVICE_ROLE_KEY|service_role|NEXT_PUBLIC_SUPABASE|supabase\.co|createClient\s*\(|supabaseUrl|supabaseKey") {
    Fail "direct Supabase/service-role marker found in POS source"
} else {
    Pass "no direct Supabase/service-role marker found in POS source"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
