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

function Resolve-DotnetExe {
    if ($env:DOTNET_EXE -and (Test-Path -LiteralPath $env:DOTNET_EXE)) {
        return $env:DOTNET_EXE
    }

    $repoLocal = Join-Path $repoRoot ".dotnet_home\dotnet.exe"
    if (Test-Path -LiteralPath $repoLocal) {
        return $repoLocal
    }

    $codexDotnet10 = "C:\Dev\dotnet10\dotnet.exe"
    if (Test-Path -LiteralPath $codexDotnet10) {
        return $codexDotnet10
    }

    return "dotnet"
}

$initializerPath = "src/Win7POS.Data/DbInitializer.cs"
$cliPath = "src/Win7POS.Cli/Program.cs"

foreach ($path in @($initializerPath, $cliPath)) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$initializer = Read-Text $initializerPath
$cli = Read-Text $cliPath

if ($cli -notmatch "--task083-legacy-db-startup-harness") {
    Fail "TASK-083 legacy DB CLI harness missing"
} else {
    Pass "TASK-083 legacy DB CLI harness present"
}

$salesClientColumnMatch = [regex]::Match($initializer, 'EnsureColumn\s*\(\s*conn\s*,\s*(?:tx\s*,\s*)?"sales"\s*,\s*"client_sale_id"')
$salesClientColumnIndex = if ($salesClientColumnMatch.Success) { $salesClientColumnMatch.Index } else { -1 }
$salesClientIndexIndex = $initializer.IndexOf('CREATE INDEX IF NOT EXISTS idx_sales_client_sale_id ON sales(client_sale_id)', [StringComparison]::Ordinal)
$salesClientUniqueIndexIndex = $initializer.IndexOf('CREATE UNIQUE INDEX IF NOT EXISTS idx_sales_client_sale_id_unique ON sales(client_sale_id)', [StringComparison]::Ordinal)
$salesSyncColumnMatch = [regex]::Match($initializer, 'EnsureColumn\s*\(\s*conn\s*,\s*(?:tx\s*,\s*)?"sales"\s*,\s*"sync_status"')
$salesSyncColumnIndex = if ($salesSyncColumnMatch.Success) { $salesSyncColumnMatch.Index } else { -1 }
$salesSyncIndexIndex = $initializer.IndexOf('CREATE INDEX IF NOT EXISTS idx_sales_sync_status ON sales(sync_status, createdAt)', [StringComparison]::Ordinal)

if ($salesClientColumnIndex -lt 0) {
    Fail "sales.client_sale_id EnsureColumn missing"
} elseif ($salesClientIndexIndex -ge 0 -and $salesClientIndexIndex -lt $salesClientColumnIndex) {
    Fail "idx_sales_client_sale_id is created before sales.client_sale_id exists"
} else {
    Pass "idx_sales_client_sale_id is not before sales.client_sale_id migration"
}

if ($salesClientColumnIndex -lt 0 -or $salesClientUniqueIndexIndex -lt 0) {
    Fail "idx_sales_client_sale_id_unique or sales.client_sale_id migration missing"
} elseif ($salesClientUniqueIndexIndex -lt $salesClientColumnIndex) {
    Fail "idx_sales_client_sale_id_unique is created before sales.client_sale_id exists"
} else {
    Pass "idx_sales_client_sale_id_unique follows sales.client_sale_id migration"
}

if ($salesSyncColumnIndex -lt 0 -or $salesSyncIndexIndex -lt 0) {
    Fail "idx_sales_sync_status or sales.sync_status migration missing"
} elseif ($salesSyncIndexIndex -lt $salesSyncColumnIndex) {
    Fail "idx_sales_sync_status is created before sales.sync_status exists"
} else {
    Pass "idx_sales_sync_status follows sales.sync_status migration"
}

if ($initializer -notmatch "sales_sync_outbox" -or $initializer -notmatch "local_stock_movements") {
    Fail "dependent sales sync/local stock tables missing from initializer"
} else {
    Pass "dependent sales sync/local stock tables present"
}

if ($initializer -match "(?i)DROP\s+TABLE|ALTER\s+TABLE\s+\w+\s+DROP|DELETE\s+FROM\s+sales|DELETE\s+FROM\s+products") {
    Fail "destructive migration statement detected"
} else {
    Pass "no destructive sales/products migration statement detected"
}

if (-not $fail) {
    Push-Location $repoRoot
    try {
        $dotnetExe = Resolve-DotnetExe
        & $dotnetExe run --project src/Win7POS.Cli/Win7POS.Cli.csproj -- --task083-legacy-db-startup-harness
        if ($LASTEXITCODE -ne 0) {
            Fail "TASK-083 legacy DB harness failed"
        } else {
            Pass "TASK-083 legacy DB harness passed"
        }
    }
    finally {
        Pop-Location
    }
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
