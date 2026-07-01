param(
    [string]$ReleasePackSource = ""
)

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

function Test-TranslationEntry(
    [string]$Text,
    [string]$Key,
    [string[]]$RequiredFragments = @()
) {
    $pattern = 'new\s+TranslationEntry\("' + [regex]::Escape($Key) + '"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        return $false
    }

    $values = @(
        $match.Groups[1].Value,
        $match.Groups[2].Value,
        $match.Groups[3].Value,
        $match.Groups[4].Value
    )

    foreach ($value in $values) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            return $false
        }
    }

    foreach ($fragment in $RequiredFragments) {
        $found = $false
        foreach ($value in $values) {
            if ($value.Contains($fragment)) {
                $found = $true
                break
            }
        }
        if (-not $found) {
            return $false
        }
    }

    return $true
}

function Get-MethodBody([string]$Text, [string]$SignaturePattern, [string]$NextSignaturePattern) {
    $match = [regex]::Match($Text, $SignaturePattern + "[\s\S]*?" + $NextSignaturePattern)
    if (-not $match.Success) {
        return ""
    }

    return $match.Value
}

function Test-ReleasePackSource([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        Pass "ReleasePack completeness validator available; pass -ReleasePackSource to validate an artifact/drop"
        return
    }

    $resolved = $source
    if (-not [System.IO.Path]::IsPathRooted($resolved)) {
        $resolved = Join-Path $repoRoot $resolved
    }

    if (-not (Test-Path $resolved)) {
        Fail "ReleasePack source missing: $source"
        return
    }

    $root = $resolved
    $tempDir = $null
    if (-not (Test-Path $resolved -PathType Container)) {
        if ([System.IO.Path]::GetExtension($resolved) -notmatch "\.zip") {
            Fail "ReleasePack source must be a folder or zip: $source"
            return
        }

        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-pack-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Expand-Archive -Path $resolved -DestinationPath $tempDir -Force
        $root = $tempDir
    }

    try {
        $required = @(
            "Win7POS.Wpf.exe",
            "Win7POS.Core.dll",
            "Win7POS.Data.dll",
            "Microsoft.Data.Sqlite.dll",
            "SQLitePCLRaw.core.dll",
            "SQLitePCLRaw.batteries_v2.dll",
            "SQLitePCLRaw.provider.e_sqlite3.dll",
            "e_sqlite3.dll"
        )

        foreach ($name in $required) {
            $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -eq $found) {
                Fail "ReleasePack missing required runtime file: $name"
            }
            else {
                Pass "ReleasePack contains $name"
            }
        }

        $docs = @("README_RUN.txt", "VERSION.txt")
        foreach ($name in $docs) {
            $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -eq $found) {
                Fail "ReleasePack missing required metadata file: $name"
            }
            else {
                Pass "ReleasePack contains $name"
            }
        }
    }
    finally {
        if ($tempDir -and (Test-Path $tempDir)) {
            Remove-Item -Path $tempDir -Recurse -Force
        }
    }
}

$requiredFiles = @(
    "src/Win7POS.Wpf/App.xaml.cs",
    "src/Win7POS.Wpf/App.xaml",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    ".github/workflows/release-pack.yml",
    "scripts/check-release-pack-completeness.ps1",
    "scripts/win7pos/collect-win7-startup-diagnostics.bat"
)

foreach ($path in $requiredFiles) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$app = Read-Text "src/Win7POS.Wpf/App.xaml.cs"
$appXaml = Read-Text "src/Win7POS.Wpf/App.xaml"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$startupTrace = Read-Text "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs"
$catalogPull = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$workflow = Read-Text ".github/workflows/release-pack.yml"

if ($translations -match 'private\s+static\s+readonly\s+Dictionary<[^;]+>\s+Translations\s*=\s*CreateTranslations\(\)\s*;') {
    Fail "PosLocalization translations must not be built by an eager field initializer before static entries"
}
else {
    $hasLazyTranslationCatalog =
        $translations -match 'private\s+static\s+readonly\s+Lazy<[\s\S]*?>\s+TranslationCatalog\s*=\s*new\s+Lazy<[\s\S]*?>\s*\(\s*CreateTranslations\s*\)\s*;' -and
        $translations -match 'private\s+static\s+Dictionary<[\s\S]*?>\s+Translations\s*\{[\s\S]*?get\s*\{\s*return\s+TranslationCatalog\.Value\s*;?\s*\}[\s\S]*?\}' -and
        $translations -match 'public\s+static\s+PosLocalization\s+Current\s*\{\s*get\s*;\s*\}\s*=\s*new\s+PosLocalization\(\)\s*;'

    $hasOrderedStaticConstructor =
        $translations -match 'static\s+PosLocalization\s*\(\)\s*\{[\s\S]*?Translations\s*=\s*CreateTranslations\(\);[\s\S]*?Current\s*=\s*new\s+PosLocalization\(\);[\s\S]*?\}'

    if (-not $hasLazyTranslationCatalog -and -not $hasOrderedStaticConstructor) {
        Fail "PosLocalization must initialize translations lazily or before Current"
    }
    else {
        Pass "PosLocalization static initialization order is safe"
    }
}

$loadedBody = Get-MethodBody `
    $mainWindow `
    "private\s+async\s+void\s+OnLoadedAsync\s*\([^\)]*\)" `
    "private\s+async\s+Task<bool>\s+TryOnlineBootstrapFirstRunAsync"

if ([string]::IsNullOrWhiteSpace($loadedBody)) {
    Fail "MainWindow.OnLoadedAsync body not found"
}
else {
    $loginIndex = $loadedBody.IndexOf("OperatorLoginDialog", [StringComparison]::Ordinal)
    $refreshIndex = $loadedBody.IndexOf("TryRefreshTrustedPosSessionAsync", [StringComparison]::Ordinal)
    $queueIndex = $loadedBody.IndexOf("QueueBackgroundOnlineRefresh", [StringComparison]::Ordinal)

    if ($loginIndex -lt 0) {
        Fail "operator login dialog missing from startup path"
    }
    else {
        Pass "operator login dialog present in startup path"
    }

    if ($refreshIndex -ge 0 -and $refreshIndex -lt $loginIndex) {
        Fail "trusted session refresh runs before operator login"
    }
    else {
        Pass "no trusted session refresh before operator login"
    }

    if ($queueIndex -lt 0 -or $loginIndex -lt 0 -or $queueIndex -lt $loginIndex) {
        Fail "background online refresh is not queued after operator login"
    }
    else {
        Pass "background online refresh queued after operator login"
    }
}

if ($mainWindow -match "CancellationToken\.None") {
    Fail "MainWindow startup online path uses CancellationToken.None"
}
else {
    Pass "MainWindow startup online path uses bounded cancellation tokens"
}

if ($mainWindow -notmatch "StartupHeartbeatTimeout\s*=\s*TimeSpan\.FromSeconds\(3\)") {
    Fail "startup heartbeat timeout must be 3 seconds"
}
else {
    Pass "startup heartbeat timeout bounded"
}

if ($mainWindow -notmatch "StartupSalesSyncTimeout\s*=\s*TimeSpan\.FromSeconds\(5\)") {
    Fail "startup sales sync timeout must be 5 seconds"
}
else {
    Pass "startup sales sync timeout bounded"
}

if ($mainWindow -notmatch "StartupCatalogPullTimeout\s*=\s*TimeSpan\.FromSeconds\(8\)") {
    Fail "startup catalog pull timeout must be 8 seconds"
}
else {
    Pass "startup catalog pull timeout bounded"
}

if ($mainWindow -notmatch "Task\.Run[\s\S]*RunBackgroundOnlineRefreshAsync") {
    Fail "online refresh is not moved to background task"
}
else {
    Pass "online refresh runs in a background task"
}

if ($mainWindow -notmatch "Dispatcher\.BeginInvoke[\s\S]*RefreshSyncStatusStripAsync") {
    Fail "background sync status updates must go through Dispatcher"
}
else {
    Pass "background sync status updates use Dispatcher"
}

if ($mainWindow -notmatch "WaitForContentRenderedOrTimeoutAsync" -or
    $mainWindow -notmatch "ContentRendered fired") {
    Fail "operator login is not gated behind visible/rendered shell trace"
}
else {
    Pass "operator login waits for rendered shell signal or timeout"
}

$refreshBody = Get-MethodBody `
    $mainWindow `
    "private\s+async\s+Task\s+TryRefreshTrustedPosSessionAsync\s*\([^\)]*\)" `
    "private\s+async\s+Task\s+TryPullCatalogAsync"
if ($refreshBody -match "ModernMessageDialog\.Show") {
    Fail "background trusted session refresh shows a blocking dialog"
}
else {
    Pass "background trusted session refresh does not show blocking dialogs"
}

if ($app -notmatch "Mutex" -or $app -notmatch "SingleInstance") {
    Fail "single-instance mutex guard missing"
}
else {
    Pass "single-instance mutex guard present"
}

if ($app -notmatch "SecurityProtocolType\.Tls12") {
    Fail "App startup does not enable TLS 1.2"
}
else {
    Pass "App startup enables TLS 1.2"
}

if ($appXaml -match "StartupUri" -or
    $appXaml -notmatch 'ShutdownMode="OnMainWindowClose"' -or
    $app -notmatch "new MainWindow\(\)" -or
    $app -notmatch "mainWindow\.Show\(\)") {
    Fail "WPF startup must explicitly create/show MainWindow without StartupUri"
}
else {
    Pass "WPF startup explicitly creates and shows MainWindow"
}

$hasSafeStartTranslations =
    (Test-TranslationEntry $translations "sync.safeStart" @("Safe start: online sync disabled")) -and
    (Test-TranslationEntry $translations "sync.safeStartTooltip" @("heartbeat", "sales sync", "catalog pull", "trusted-session refresh"))
$hasSafeStartStatus =
    $mainWindow -match 'PosLocalization\.Current\.Text\("sync\.safeStart"\)' -and
    $mainWindow -match 'PosLocalization\.Current\.Text\("sync\.safeStartTooltip"\)'
if ($app -notmatch "--safe-start" -or
    $app -notmatch "WIN7POS_SAFE_START" -or
    -not $hasSafeStartStatus -or
    -not $hasSafeStartTranslations -or
    $mainWindow -notmatch "online refresh skipped: safe-start") {
    Fail "safe-start mode is missing or incomplete"
}
else {
    Pass "safe-start mode present"
}

if ($startupTrace -notmatch "startup-trace\.log" -or
    $startupTrace -notmatch "CommonApplicationData" -or
    $startupTrace -notmatch "BaseDirectory") {
    Fail "pre-AppPaths startup trace with fallback missing"
}
else {
    Pass "pre-AppPaths startup trace with fallback present"
}

if ($mainWindow -notmatch "StartupWatchdogTimeout\s*=\s*TimeSpan\.FromSeconds\(5\)" -or
    $mainWindow -notmatch "startup watchdog warning") {
    Fail "startup watchdog warning missing"
}
else {
    Pass "startup watchdog warning present"
}

if ($mainWindow -notmatch "TryOnlineBootstrapFirstRunAsync\(factory\)[\s\S]{0,120}ConfigureAwait\(true\)" -or
    $mainWindow -notmatch "needFirstRun && !App\.IsSafeStart" -or
    $mainWindow -notmatch "first-run online bootstrap skipped: safe-start") {
    Fail "safe-start must skip first-run online bootstrap"
}
else {
    Pass "safe-start skips first-run online bootstrap"
}

if ($app -notmatch "App\.OnStartup entered" -or
    $mainWindow -notmatch "MainWindow constructor entered" -or
    $mainWindow -notmatch "DbInitializer start" -or
    $mainWindow -notmatch "OperatorLogin dialog opening" -or
    $mainWindow -notmatch "BackgroundOnlineRefresh queued") {
    Fail "startup trace markers missing"
}
else {
    Pass "startup trace markers present"
}

$uiPathPatterns = @(
    "AppPaths\.LogPath",
    "options\.DbPath",
    "ex\.Message"
)
foreach ($pattern in $uiPathPatterns) {
    if ($app -match "ModernMessageDialog\.Show[\s\S]{0,220}$pattern" -or
        $mainWindow -match "ModernMessageDialog\.Show[\s\S]{0,220}$pattern") {
        Fail "startup UI may expose raw exception/path via pattern: $pattern"
    }
}
if (-not $fail) {
    Pass "startup UI avoids raw exception and absolute path copy"
}

if ($catalogPull -notmatch 'StoreCatalogFailureAsync\("timeout"\)') {
    Fail "catalog pull cancellation does not persist timeout status"
}
else {
    Pass "catalog pull cancellation persists timeout status"
}

if ($workflow -notmatch "check-pos-startup-win7-safe\.ps1" -or
    $workflow -notmatch "check-release-pack-completeness\.ps1[\s\S]*-ReleasePackSource\s+dist/Win7POS") {
    Fail "ReleasePack workflow does not run startup and package validators"
}
else {
    Pass "ReleasePack workflow runs startup and package validators"
}

Test-ReleasePackSource $ReleasePackSource

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
