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

. (Join-Path $PSScriptRoot "sales-sync-outbox-gate-helpers.ps1")

function Test-SalesOutboxFacadeDelegation(
    [string]$text,
    [string]$methodName,
    [string]$writerName,
    [string[]]$forwarding) {
    $slices = @(Get-CSharpMethodSlices $text "public" $methodName)
    if ($slices.Count -eq 0 -or $slices.Count -ne $forwarding.Count) {
        return $false
    }

    for ($index = 0; $index -lt $slices.Count; $index++) {
        $slice = $slices[$index].Text
        $delegation = "_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($writerName) + "\s*\("
        $exactForwarding = "(?s)_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($writerName) + "\s*\(\s*" +
            $forwarding[$index] + "\s*\)"
        if ([regex]::Matches($slice, $delegation).Count -ne 1 -or
            [regex]::Matches($slice, $exactForwarding).Count -ne 1 -or
            $slice -match "\bsales_sync_outbox\b|\b(?:conn|_factory)\s*\.\s*(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b|\b(?:Open|BeginTransaction|Commit|Rollback)\s*\(") {
            return $false
        }
    }

    return $true
}

function Test-SalesOutboxEnqueueThroughTransactionWriter(
    [string]$saleRepository,
    [string]$transactionWriter) {
    $repositorySlices = @(Get-CSharpMethodSlices $saleRepository "public" "EnqueueSalesSyncOutboxAsync")
    $writerSlices = @(Get-CSharpMethodSlices $transactionWriter "internal" "EnqueueSalesSyncOutboxAsync")
    if ($repositorySlices.Count -ne 1 -or $writerSlices.Count -ne 1) {
        return $false
    }

    $repositorySlice = $repositorySlices[0].Text
    $writerSlice = $writerSlices[0].Text
    return $repositorySlice -match "(?s)_transactionWriter\s*\.\s*EnqueueSalesSyncOutboxAsync\(\s*conn\s*,\s*tx\s*,\s*saleId\s*,\s*clientSaleId\s*\)" -and
        $repositorySlice -notmatch "_salesSyncOutbox\s*\.\s*EnqueueAsync|BuildClientSaleId|clientSaleId\.Trim\(\)|\.Open(?:Async)?\s*\(|BeginTransaction|\.Commit\s*\(|\.Rollback\s*\(" -and
        $writerSlice -match "string\.IsNullOrWhiteSpace\(clientSaleId\)" -and
        $writerSlice -match "BuildClientSaleId\(saleId\)" -and
        $writerSlice -match "clientSaleId\.Trim\(\)" -and
        $writerSlice -match "(?s)_salesSyncOutbox\s*\.\s*EnqueueAsync\(\s*conn\s*,\s*tx\s*,\s*saleId\s*,\s*normalizedClientSaleId\s*\)" -and
        $writerSlice -notmatch "INSERT\s+OR\s+IGNORE\s+INTO\s+sales_sync_outbox|SerializeCanonical|Sha256Hex|\.Open(?:Async)?\s*\(|BeginTransaction|\.Commit\s*\(|\.Rollback\s*\("
}

$required = @(
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs",
    "src/Win7POS.Data/Repositories/SaleReversalWriter.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "src/Win7POS.Data/Online/CatalogShopStateRepository.cs",
    "src/Win7POS.Core/Online/CatalogSaleSafetyPolicy.cs",
    "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs",
    "src/Win7POS.Data/DbInitializer.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$sync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$saleRepo = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$transactionWriter = Read-Text "src/Win7POS.Data/Repositories/SaleTransactionWriter.cs"
$reversalWriter = Read-Text "src/Win7POS.Data/Repositories/SaleReversalWriter.cs"
$salesOutbox = Read-Text "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs"
$catalogState = Read-Text "src/Win7POS.Data/Online/CatalogShopStateRepository.cs"
$catalogSaleSafetyPolicy = Read-Text "src/Win7POS.Core/Online/CatalogSaleSafetyPolicy.cs"
$builder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$pendingFacadeDelegates = Test-SalesOutboxFacadeDelegation $saleRepo "GetPendingSalesSyncOutboxAsync" "GetPendingAsync" @("take\s*,\s*nowMs")
$releaseFacadeDelegates = Test-SalesOutboxFacadeDelegation $saleRepo "ReleaseSalesSyncAttemptAsync" "ReleaseAttemptAsync" @(
    "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount",
    "outboxId\s*,\s*saleId\s*,\s*errorCode\s*,\s*nextRetryAt\s*,\s*nowMs\s*,\s*expectedAttemptCount\s*,\s*fence")
$enqueueFacadeDelegates = Test-SalesOutboxEnqueueThroughTransactionWriter $saleRepo $transactionWriter
$summaryFacadeDelegates = Test-SalesOutboxFacadeDelegation $saleRepo "GetSalesSyncOutboxSummaryAsync" "GetSummaryAsync" @("\s*")
$salesScope = @($sync, $saleRepo, $transactionWriter, $reversalWriter, $salesOutbox, $builder, $initializer, $statusReader) -join "`n"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($sync -notmatch "MaxOutboxItemsPerRun\s*=\s*25") { Fail "sales sync per-run outbox cap missing or changed" } else { Pass "sales sync per-run outbox cap present" }
if (-not $pendingFacadeDelegates -or
    $salesOutbox -notmatch "GetPendingAsync\(int take, long nowMs\)" -or
    $salesOutbox -notmatch "if \(take > 50\) take = 50") { Fail "outbox repository must bound caller-requested take through the F4 collaborator" } else { Pass "outbox repository bounds caller-requested take through the F4 collaborator" }
if ($sync -notmatch "Interlocked\.CompareExchange" -or $sync -notmatch "Sales sync skipped: already running") { Fail "sales sync in-flight guard missing" } else { Pass "sales sync in-flight guard present" }
if ($sync -notmatch "StoreSalesSyncInProgressAsync\(\s*true,\s*generation\s*\)" -or
    $sync -notmatch "finally[\s\S]{0,240}StoreSalesSyncInProgressAsync\(\s*false,\s*generation\s*\)" -or
    $sync -notmatch "StoreSalesSyncInProgressAsync[\s\S]{0,700}SetBoolIfGenerationCurrentAsync\(\s*SalesSyncInProgressSettingKey,\s*inProgress,\s*generation") {
    Fail "sales sync in-progress status must be fenced to the active generation"
} else { Pass "sales sync in-progress status is generation-fenced" }

if ($sync -notmatch "MaxAttemptsBeforeBlocked\s*=\s*12") { Fail "max attempts before blocked missing" } else { Pass "max attempts before blocked present" }
if ($sync -notmatch "OperationCanceledException[\s\S]{0,900}ReleaseSalesSyncAttemptAsync" -or
    -not $releaseFacadeDelegates -or
    $salesOutbox -notmatch "ReleaseAttemptAsync[\s\S]{0,2400}attempt_count\s*=\s*attempt_count\s*-\s*1") {
    Fail "cancelled sales claims must release their CAS attempt without consuming retry budget"
} else { Pass "cancelled sales claims release their attempt budget" }
if ($sync -notmatch "Math\.Min\(300,\s*10\s*\*\s*attempts\)" -or $sync -notmatch "MarkSalesSyncRetryAsync") { Fail "retry backoff scheduling missing" } else { Pass "retry/backoff scheduling present" }
if ($sync -notmatch "MarkSalesSyncBlockedAsync" -or $sync -notmatch "failed_blocked") { Fail "blocked sales status missing" } else { Pass "blocked sales status present" }
if ($sync -notmatch "validation_failed" -or $sync -notmatch "conflict") { Fail "validation/conflict failures must be blocked" } else { Pass "validation/conflict failures are handled" }
if ($sync -notmatch "duplicate" -or $sync -notmatch "idempotent" -or $sync -notmatch "acked" -or $sync -notmatch "synced") { Fail "duplicate/idempotent ack statuses missing" } else { Pass "duplicate/idempotent ack statuses accepted" }

if ($transactionWriter -notmatch "InsertSaleAsync" -or $transactionWriter -notmatch "ApplyLocalStockMovementsAsync" -or $transactionWriter -notmatch "EnqueueSalesSyncOutboxAsync" -or $transactionWriter -notmatch "tx\.Commit\(\)") { Fail "sale save must persist sale, stock movement and outbox in one transaction through F6" } else { Pass "sale save persists sale, stock and outbox together through F6" }
if ($transactionWriter -notmatch "sale\.Kind\s*==\s*\(int\)SaleKind\.Sale" -or
    $transactionWriter -notmatch "RequireSaleSafeForOrdinarySaleAsync\(conn, tx\)" -or
    $catalogSaleSafetyPolicy -notmatch "catalog_sale_blocked_binding_partial" -or
    $catalogSaleSafetyPolicy -notmatch "catalog_sale_blocked_repair_required" -or
    $catalogSaleSafetyPolicy -notmatch "catalog_sale_blocked_not_sale_safe" -or
    $catalogSaleSafetyPolicy -notmatch "catalog_sale_blocked_exactness_shop_mismatch" -or
    $transactionWriter.IndexOf("RequireSaleSafeForOrdinarySaleAsync(conn, tx)") -gt $transactionWriter.IndexOf("ApplyLocalStockMovementsAsync")) {
    Fail "ordinary sale persistence must enforce catalog sale-safe state inside the sale transaction before stock/outbox writes"
} else {
    Pass "ordinary sale persistence enforces catalog sale-safe state atomically before stock/outbox writes"
}
if (-not $enqueueFacadeDelegates -or
    $salesOutbox -notmatch "INSERT OR IGNORE INTO sales_sync_outbox" -or
    $salesOutbox -notmatch "idempotency_key") { Fail "outbox enqueue/idempotency ownership or facade delegation missing" } else { Pass "outbox enqueue/idempotency is owned by F4 with façade delegation" }
if ($initializer -notmatch "sales_sync_outbox" -or $initializer -notmatch "sale_id\s+INTEGER NOT NULL UNIQUE" -or $initializer -notmatch "client_sale_id TEXT NOT NULL UNIQUE" -or $initializer -notmatch "idempotency_key TEXT NOT NULL UNIQUE") { Fail "new DB outbox uniqueness constraints missing" } else { Pass "new DB outbox uniqueness constraints present" }
if ($initializer -notmatch "idx_sales_client_sale_id_unique") { Fail "legacy sales client_sale_id unique index missing" } else { Pass "legacy sales client_sale_id unique index present" }

if (-not $summaryFacadeDelegates -or
    $salesOutbox -notmatch 'CountFor\("failed_blocked"\)' -or
    $statusReader -notmatch "PendingOrRetry" -or $statusReader -notmatch "Blocked") { Fail "status strip outbox summary missing" } else { Pass "status strip outbox summary is delegated to F4" }
if ($statusReader -notmatch "last_error" -or $statusReader -notmatch "SalesErrorText") { Fail "sales sync errors must surface in status reader" } else { Pass "sales sync errors surface in status reader" }

if ($builder -notmatch "SerializeRedacted" -or $builder -notmatch "DeviceToken = null" -or $builder -notmatch "SessionToken = null") { Fail "sales sync payload persistence must redact tokens" } else { Pass "sales sync payload persistence redacts tokens" }
if (-not $enqueueFacadeDelegates -or
    $salesOutbox -notmatch "SerializeCanonical" -or
    $salesOutbox -notmatch "payload_hash" -or
    $salesOutbox -notmatch "payload_json IS @payloadJson" -or
    $sync -notmatch "Sha256Hex\(item\.PayloadJson\)" -or $sync -notmatch "payload_hash_mismatch") { Fail "sales payload/hash immutability missing" } else { Pass "sales payload/hash are immutable from F4 enqueue through retry" }
if ($saleRepo -notmatch "_reversalWriter\s*\.\s*EvaluateReversalDependencyAsync\s*\(\s*saleId\s*\)" -or
    $saleRepo -notmatch "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\s*\(\s*conn\s*,\s*tx\s*,\s*sale\s*,\s*lines\s*\)" -or
    $transactionWriter -notmatch "_reversalWriter\s*\.\s*ValidateReversalBoundaryAsync\s*\(\s*conn\s*,\s*tx\s*,\s*sale\s*,\s*lines\s*\)" -or
    $reversalWriter -notmatch "ReversalDependencyState\.PermanentBlock" -or
    $reversalWriter -notmatch "prior_reversal_blocked" -or
    $reversalWriter -notmatch "original_sale_blocked" -or
    $sync -notmatch "dependency\.State\s*==\s*ReversalDependencyState\.Wait" -or
    $sync -notmatch "dependency\.State\s*==\s*ReversalDependencyState\.PermanentBlock") {
    Fail "refund/void dependency must distinguish bounded wait from permanent block through the F5 façade"
} else { Pass "refund/void dependency distinguishes wait from permanent block through the F5 façade" }
if ($salesScope -match "DELETE\s+FROM\s+sales_sync_outbox") { Fail "destructive sales_sync_outbox cleanup detected" } else { Pass "no destructive outbox cleanup detected" }
if ($salesScope -match "DROP\s+TABLE\s+sales_sync_outbox") { Fail "destructive sales_sync_outbox drop detected" } else { Pass "no outbox drop detected" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password|payloadJson)"
if ($combined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
