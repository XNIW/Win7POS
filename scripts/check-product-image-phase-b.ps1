$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Read-Required([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing required Phase B file: $relativePath")
        return ""
    }
    return [System.IO.File]::ReadAllText($path)
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { $failures.Add($message) }
}

$contract = Read-Required "src/Win7POS.Core/Online/PosProductImageContract.cs"
$client = Read-Required "src/Win7POS.Data/Online/PosProductImageClient.cs"
$outbox = Read-Required "src/Win7POS.Data/Online/ProductImageOperationOutboxRepository.cs"
$migration = Read-Required "src/Win7POS.Data/Migrations/SchemaMigrationRegistry.cs"
$restore = Read-Required "src/Win7POS.Data/Online/RestoreShopSafetyRepository.cs"
$supervisor = Read-Required "src/Win7POS.Data/Online/OnlineSyncSupervisor.cs"
$flags = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImageFeatureFlags.cs"
$imageRuntime = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImageRuntime.cs"
$cacheScopeStore = Read-Required "src/Win7POS.Data/Online/ProductImageCacheScopeStore.cs"
$syncHost = Read-Required "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$editor = Read-Required "src/Win7POS.Wpf/Products/ProductEditDialog.xaml"
$translations = Read-Required "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$attributes = Read-Required ".gitattributes"
$stagingAcceptance = Read-Required "tests/Win7POS.Wpf.UiSmokeHarness/ProductImageStagingAcceptance.cs"
$stagingRunner = Read-Required "scripts/qa/Invoke-Win7PosProductImageStagingAcceptance.ps1"
$shopTransition = Read-Required "src/Win7POS.Data/Online/PosShopTransitionGuard.cs"

Require ($flags -match 'IsPhaseAEnabled\s*=>\s*true') `
    "Product image UI must be enabled only after the complete Phase B implementation is present."
Require ($contract -match 'MaximumJsonBodyBytes\s*=\s*16\s*\*\s*1024') `
    "Trusted image request bodies must remain bounded at 16 KiB."
Require ($attributes -match 'tests/fixtures/pos-product-image-v1/\*\*\s+-text' -and
         $attributes -match 'tests/fixtures/product-image-v1/\*\*\s+-text') `
    "Byte-identical product image fixtures must be exempt from checkout EOL conversion."
Require ($contract -match 'ReadUrlTimeToLiveSeconds\s*=\s*300' -and
         $contract -match 'ReadUrlSafetyWindowSeconds\s*=\s*30' -and
         $contract -match 'UploadCapabilitySeconds\s*=\s*7200') `
    "Signed URL and upload capability TTL pins changed."
foreach ($path in @(
    '/api/pos/catalog/product-images/intent',
    '/api/pos/catalog/product-images/finalize',
    '/api/pos/catalog/product-images/read-urls',
    '/api/pos/catalog/product-images/remove')) {
    Require ($client.Contains($path)) "Missing exact trusted image endpoint: $path"
}
Require ($client -match 'AllowAutoRedirect\s*=\s*false') `
    "Image HTTP transports must keep automatic redirects disabled."
Require ($client -match 'TryAddWithoutValidation\("Cache-Control",\s*"no-store"\)') `
    "Trusted image requests must remain no-store."
Require ($client -match 'MaximumJsonBodyBytes' -and $client -match 'MaximumReadResponseBytes') `
    "Image transport request/response bounds are not enforced."
foreach ($state in @(
    'waiting_dependency', 'pending_intent', 'pending_upload', 'pending_finalize',
    'pending_remove', 'in_progress', 'retry_wait', 'failed_blocked',
    'completed', 'cleanup_pending')) {
    Require ($outbox.Contains($state) -and $migration.Contains($state)) `
        "Durable product image state is missing from outbox or migration: $state"
}
Require ($outbox -notmatch '(?i)INSERT[\s\S]{0,500}(signed.?url|session.?token|device.?token|authorization)') `
    "Image outbox SQL must not persist signed URLs or trusted credentials."
Require ($shopTransition -match 'product_image_operation_outbox' -and
         $shopTransition -match 'pending_upload' -and
         $shopTransition -match 'cleanup_pending') `
    "Shop transitions must fail closed while durable product image work is unresolved."
Require ($restore -match 'restore_live_product_image_outbox_unresolved' -and
         $restore -match 'restore_(candidate|review)_outbox_unresolved' -and
         $restore -match 'product_image_operation_outbox') `
    "Restore safety must fail closed on unresolved image work."
Require ($supervisor -match 'OnlineSyncLane\.ProductImageOutbox' -and
         $supervisor -match 'waiterCancellationToken') `
    "Product image sync must use the supervised bounded lane."
Require ($imageRuntime -match 'BindWithTransitionAsync' -and
         $imageRuntime -match 'PurgeAllAsync' -and
         $imageRuntime -match 'AcknowledgePurgeAsync' -and
         $imageRuntime -match 'SameReadIdentity\(session, responseSession\)' -and
         $imageRuntime -match 'Decoder\.TrimMemoryCache') `
    "Account, shop and server cache-scope transitions must purge the prior disk and decode partitions."
Require ($imageRuntime -match 'ReadProductCacheGeneration\(product\.RemoteProductId\)' -and
         @([regex]::Matches($imageRuntime, 'IncrementProductCacheGeneration\(product\.RemoteProductId\)')).Count -ge 2 -and
         $imageRuntime -match 'ReadProductCacheGeneration\(product\.RemoteProductId\)\s*==\s*productCacheGeneration') `
    "Product removal must fence stale in-flight cache commits with a product generation."
Require ($cacheScopeStore -match 'ActiveBindingKey' -and
         $cacheScopeStore -match 'PendingPurgeKey' -and
         $cacheScopeStore -match 'ObserveTrustedIdentityAsync' -and
         $cacheScopeStore -match 'win7pos-cache-binding-v1' -and
         $cacheScopeStore -notmatch 'server-scope-one|server-scope-two') `
    "The durable active cache binding must remain opaque and transition-aware."
Require ($syncHost -match 'ProductImageRuntime\.ReconcileTrustedIdentityAsync' -and
         $syncHost.IndexOf('ProductImageRuntime.ReconcileTrustedIdentityAsync') -lt
         $syncHost.IndexOf('_store.SaveFirstLogin') -and
         @([regex]::Matches($syncHost, 'forcePurge:\s*true')).Count -ge 2) `
    "Trusted account/shop activation must durably purge image caches before the trust commit."
Require ($editor -match 'ChooseImageCommand' -and
         $editor -match 'RemoveImageCommand' -and
         $editor -match 'AutomationProperties\.Name') `
    "Product editor image commands/accessibility bindings are incomplete."
foreach ($key in @(
    'productImage.choose', 'productImage.replace', 'productImage.remove',
    'productImage.queued', 'productImage.uploading', 'productImage.finalizing',
    'productImage.unavailable', 'productImage.corrupt', 'productImage.conflict')) {
    Require ($translations.Contains($key)) "Missing localized product image key: $key"
}
Require ($stagingAcceptance -match 'DataProtectionScope\.CurrentUser' -and
         $stagingAcceptance -match 'AllowAutoRedirect\s*=\s*false' -and
         $stagingAcceptance -match 'RestartAfterOfflineQueue' -and
         $stagingAcceptance -match 'VerifyExpiredCapabilitiesAsync' -and
         $stagingAcceptance -match 'result_issue' -and
         $stagingAcceptance -match 'CountForbiddenPersistenceMarkers' -and
         $stagingAcceptance -match 'UseTrustedProfileForAcceptance' -and
         $stagingAcceptance -match 'Phase = "begin_pending"' -and
         $stagingAcceptance -match 'state\.BeginRequestId' -and
         $stagingAcceptance -match 'state\.RunMarker' -and
         $stagingAcceptance -match 'boundary_begin_recovery_failed' -and
         $stagingAcceptance -match 'staging_list_runtime_image_not_loaded' -and
         $stagingAcceptance -match 'staging_editor_runtime_image_not_loaded' -and
         $stagingAcceptance -notmatch '\.SetLoaded\(' -and
         $stagingAcceptance -notmatch 'Product\s*=\s*null' -and
         $stagingAcceptance -match 'new ProductsView' -and
         $stagingAcceptance -match 'new ProductEditDialog' -and
         $stagingAcceptance -notmatch 'runMarker[^\r\n]*DataMember[^\r\n]*SafeReport') `
    "Real staging acceptance must preserve DPAPI, no-redirect, restart and capability-expiry coverage."
Require ($stagingAcceptance.IndexOf('Phase = "begin_pending"') -lt
             $stagingAcceptance.IndexOf(
                 'armed = await boundary.PostTrustedAsync') -and
         $stagingAcceptance -match
             'TrustedRequest\.Begin\(\s*sharedSession,\s*state\.BeginRequestId,\s*state\.RunMarker\)' -and
         $stagingAcceptance -match
             'state\.Phase = "armed";\s*SaveState\(state\)') `
    "Begin response-loss recovery must be checkpointed before the remote call and replayable during cleanup."
Require ($stagingRunner -match 'branch -show-current|branch --show-current' -and
         $stagingRunner -match 'rev-parse origin/main' -and
         $stagingRunner -match 'AddHours\(2\)\.AddMinutes\(5\)' -and
         $stagingRunner -match 'AddHours\(3\)' -and
         $stagingRunner -match 'priorCheckpoint' -and
         $stagingRunner -match 'Invoke-CheckpointCleanup' -and
         $stagingRunner -match 'evidence_overlaps_(data|repo)' -and
         $stagingRunner -match "Phase 'cleanup'" -and
         $stagingRunner -notmatch 'TASK150_QA_HMAC_KEY|SUPABASE_SERVICE_ROLE_KEY') `
    "Staging runner must require exact main, wait the fence and avoid server secrets."

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Product image Phase B static gate passed."
Write-Host "Contract, transport, outbox, restore, supervised lane, UI and localization pins verified."
