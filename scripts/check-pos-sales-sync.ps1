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
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
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
$builder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$salesScope = @($sync, $saleRepo, $builder, $initializer, $statusReader) -join "`n"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

if ($sync -notmatch "MaxOutboxItemsPerRun\s*=\s*25") { Fail "sales sync per-run outbox cap missing or changed" } else { Pass "sales sync per-run outbox cap present" }
if ($saleRepo -notmatch "GetPendingSalesSyncOutboxAsync\(int take" -or $saleRepo -notmatch "if \(take > 50\) take = 50") { Fail "outbox repository must bound caller-requested take" } else { Pass "outbox repository bounds take" }
if ($sync -notmatch "Interlocked\.CompareExchange" -or $sync -notmatch "Sales sync skipped: already running") { Fail "sales sync in-flight guard missing" } else { Pass "sales sync in-flight guard present" }
if ($sync -notmatch "StoreSalesSyncInProgressAsync\(true\)" -or $sync -notmatch "StoreSalesSyncInProgressAsync\(false\)") { Fail "sales sync in-progress status missing" } else { Pass "sales sync in-progress status persisted" }

if ($sync -notmatch "MaxAttemptsBeforeBlocked\s*=\s*12") { Fail "max attempts before blocked missing" } else { Pass "max attempts before blocked present" }
if ($sync -notmatch "Math\.Min\(300,\s*10\s*\*\s*attempts\)" -or $sync -notmatch "MarkSalesSyncRetryAsync") { Fail "retry backoff scheduling missing" } else { Pass "retry/backoff scheduling present" }
if ($sync -notmatch "MarkSalesSyncBlockedAsync" -or $sync -notmatch "failed_blocked") { Fail "blocked sales status missing" } else { Pass "blocked sales status present" }
if ($sync -notmatch "validation_failed" -or $sync -notmatch "conflict") { Fail "validation/conflict failures must be blocked" } else { Pass "validation/conflict failures are handled" }
if ($sync -notmatch "duplicate" -or $sync -notmatch "idempotent" -or $sync -notmatch "acked" -or $sync -notmatch "synced") { Fail "duplicate/idempotent ack statuses missing" } else { Pass "duplicate/idempotent ack statuses accepted" }

if ($saleRepo -notmatch "InsertSaleAsync" -or $saleRepo -notmatch "ApplyLocalStockMovementsAsync" -or $saleRepo -notmatch "EnqueueSalesSyncOutboxAsync" -or $saleRepo -notmatch "tx\.Commit\(\)") { Fail "sale save must persist sale, stock movement and outbox in one transaction" } else { Pass "sale save persists sale, stock and outbox together" }
if ($saleRepo -notmatch "INSERT OR IGNORE INTO sales_sync_outbox" -or $saleRepo -notmatch "idempotency_key") { Fail "outbox enqueue/idempotency missing" } else { Pass "outbox enqueue/idempotency present" }
if ($initializer -notmatch "sales_sync_outbox" -or $initializer -notmatch "sale_id\s+INTEGER NOT NULL UNIQUE" -or $initializer -notmatch "client_sale_id TEXT NOT NULL UNIQUE" -or $initializer -notmatch "idempotency_key TEXT NOT NULL UNIQUE") { Fail "new DB outbox uniqueness constraints missing" } else { Pass "new DB outbox uniqueness constraints present" }
if ($initializer -notmatch "idx_sales_client_sale_id_unique") { Fail "legacy sales client_sale_id unique index missing" } else { Pass "legacy sales client_sale_id unique index present" }

if ($saleRepo -notmatch "GetSalesSyncOutboxSummaryAsync" -or $statusReader -notmatch "PendingOrRetry" -or $statusReader -notmatch "Blocked") { Fail "status strip outbox summary missing" } else { Pass "status strip outbox summary present" }
if ($statusReader -notmatch "last_error" -or $statusReader -notmatch "SalesErrorText") { Fail "sales sync errors must surface in status reader" } else { Pass "sales sync errors surface in status reader" }

if ($builder -notmatch "SerializeRedacted" -or $builder -notmatch "DeviceToken = null" -or $builder -notmatch "SessionToken = null") { Fail "sales sync payload persistence must redact tokens" } else { Pass "sales sync payload persistence redacts tokens" }
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
