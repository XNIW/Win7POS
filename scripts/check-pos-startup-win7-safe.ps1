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

function Get-MethodBody([string]$Text, [string]$SignaturePattern, [string]$NextSignaturePattern = "") {
    if (-not [string]::IsNullOrWhiteSpace($NextSignaturePattern)) {
        $match = [regex]::Match($Text, $SignaturePattern + "[\s\S]*?" + $NextSignaturePattern)
        if ($match.Success) {
            return $match.Value
        }
    }

    $signature = [regex]::Match($Text, $SignaturePattern)
    if (-not $signature.Success) {
        return ""
    }

    $bodyStart = $Text.IndexOf("{", $signature.Index + $signature.Length, [StringComparison]::Ordinal)
    if ($bodyStart -lt 0) {
        return ""
    }

    $depth = 0
    for ($i = $bodyStart; $i -lt $Text.Length; $i++) {
        $ch = $Text[$i]
        if ($ch -eq "{") {
            $depth++
        }
        elseif ($ch -eq "}") {
            $depth--
            if ($depth -eq 0) {
                return $Text.Substring($signature.Index, ($i - $signature.Index) + 1)
            }
        }
    }

    return ""
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
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PaymentViewModel.cs",
    "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    ".github/workflows/release-pack.yml",
    "installer/Win7POS.iss",
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
$posWorkflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$paymentViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PaymentViewModel.cs"
$startupTrace = Read-Text "src/Win7POS.Wpf/Infrastructure/StartupTrace.cs"
$catalogPull = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosLocalization.cs"
$workflow = Read-Text ".github/workflows/release-pack.yml"
$installer = Read-Text "installer/Win7POS.iss"

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
    "private\s+async\s+Task<StartOfDaySyncResult>\s+RunStartOfDaySyncAsync"

if ([string]::IsNullOrWhiteSpace($loadedBody)) {
    Fail "MainWindow.OnLoadedAsync body not found"
}
else {
    $loginIndex = $loadedBody.IndexOf("PosOnlineFirstLoginDialog", [StringComparison]::Ordinal)
    $shownIndex = $loadedBody.IndexOf("POS access dialog shown", [StringComparison]::Ordinal)
    $acceptedIndex = $loadedBody.IndexOf("POS access dialog accepted", [StringComparison]::Ordinal)
    $showDialogIndex = $loadedBody.IndexOf("login.ShowDialog()", [StringComparison]::Ordinal)
    $refreshIndex = $loadedBody.IndexOf("TryRefreshTrustedPosSessionAsync", [StringComparison]::Ordinal)
    $queueIndex = $loadedBody.IndexOf("QueueBackgroundOnlineRefresh", [StringComparison]::Ordinal)

    if ($loginIndex -lt 0) {
        Fail "POS access dialog missing from startup path"
    }
    else {
        Pass "POS access dialog present in startup path"
    }

    if ($loadedBody -notmatch "login\.ShowDialog\(\)\s*!=\s*true" -or
        $loadedBody -notmatch "OperatorSessionHolder\.Current[\s\S]{0,160}!OperatorSessionHolder\.Current\.IsLoggedIn") {
        Fail "POS access dialog result/authentication guard missing"
    }
    else {
        Pass "POS access dialog result/authentication guard present"
    }

    if ($refreshIndex -ge 0 -and $refreshIndex -lt $loginIndex) {
        Fail "trusted session refresh runs before POS access"
    }
    else {
        Pass "no trusted session refresh before POS access"
    }

    if ($queueIndex -lt 0 -or $acceptedIndex -lt 0 -or $queueIndex -lt $acceptedIndex) {
        Fail "background online refresh is not queued after POS access acceptance"
    }
    else {
        Pass "background online refresh queued after POS access acceptance"
    }

    if ($shownIndex -lt 0 -or $showDialogIndex -lt 0 -or $shownIndex -gt $showDialogIndex) {
        Fail "POS access shown marker must be before ShowDialog"
    }
    else {
        Pass "POS access shown marker precedes ShowDialog"
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

if ($mainWindow -notmatch "StartAdaptiveOnlineScheduler" -or
    $mainWindow -notmatch "Task\.Run[\s\S]*RunAdaptiveOnlineSchedulerAsync") {
    Fail "online refresh is not moved to background task"
}
else {
    Pass "adaptive online refresh runs in a background task"
}

if ($mainWindow -notmatch "Dispatcher\.BeginInvoke[\s\S]*RefreshSyncStatusStripAsync") {
    Fail "background sync status updates must go through Dispatcher"
}
else {
    Pass "background sync status updates use Dispatcher"
}

if ($mainWindow -notmatch "WaitForContentRenderedOrTimeoutAsync" -or
    $mainWindow -notmatch "ContentRendered fired") {
    Fail "POS access is not gated behind visible/rendered shell trace"
}
else {
    Pass "POS access waits for rendered shell signal or timeout"
}

$refreshBody = Get-MethodBody `
    $mainWindow `
    "private\s+async\s+Task<CatalogSyncRunResult>\s+RunCoordinatedOnlineRefreshAsync\s*\([^\)]*\)" `
    "private\s+static\s+bool\s+IsSameTrustedSession"
if ($refreshBody -match "ModernMessageDialog\.Show") {
    Fail "coordinated background refresh shows a blocking dialog"
}
else {
    Pass "coordinated background refresh does not show blocking dialogs"
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

$queueRefreshBody = Get-MethodBody `
    $mainWindow `
    "private\s+void\s+QueueBackgroundOnlineRefresh\s*\([^\)]*\)" `
    "private\s+void\s+StartAdaptiveOnlineScheduler"
if ($mainWindow -match "TryOnlineBootstrapFirstRunAsync" -or
    $loadedBody -notmatch "if\s*\(\s*!App\.IsSafeStart\s*\)[\s\S]{0,220}RunStartOfDaySyncAsync\(factory\)" -or
    [string]::IsNullOrWhiteSpace($queueRefreshBody) -or
    $queueRefreshBody -notmatch "if\s*\(\s*App\.IsSafeStart\s*\)[\s\S]{0,160}online refresh skipped: safe-start[\s\S]{0,160}return" -or
    $mainWindow -notmatch "online refresh skipped: safe-start") {
    Fail "safe-start must skip automatic startup online refresh"
}
else {
    Pass "safe-start skips automatic startup online refresh"
}

if ($posWorkflow -notmatch 'TrySyncSalesOutboxNoThrowAsync[\s\S]{0,260}if\s*\(App\.IsSafeStart\)[\s\S]{0,180}return' -or
    $paymentViewModel -notmatch '_autoPrintPdfSii\s*=\s*!App\.IsSafeStart' -or
    $paymentViewModel -notmatch 'effectiveValue\s*=\s*!App\.IsSafeStart\s*&&\s*value' -or
    $paymentViewModel -notmatch 'TriggerAutoPrintPdfIfEnabledAsync[\s\S]{0,180}if\s*\(App\.IsSafeStart\)[\s\S]{0,80}return\s+false' -or
    $paymentViewModel -notmatch 'StampaPdfAsync\(\)[\s\S]{0,180}if\s*\(App\.IsSafeStart\)[\s\S]{0,80}return\s+false' -or
    $paymentViewModel -notmatch '_generateFiscalPdf\s*!=\s*null\s*&&\s*!App\.IsSafeStart') {
    Fail "safe-start must block post-sale sync and automatic fiscal printing"
}
else {
    Pass "safe-start blocks post-sale sync and all fiscal PDF output paths"
}

if ($mainWindow -notmatch 'TriggerAdaptiveOnlineRefreshAsync[\s\S]{0,450}App\.IsSafeStart[\s\S]{0,900}authorization_lease_denied' -or
    $mainWindow -notmatch 'ShowSyncCenterDialog\(Window owner = null\)[\s\S]{0,260}App\.IsSafeStart' -or
    $mainWindow -notmatch 'new SettingsHubDialog\([\s\S]{0,180}App\.IsSafeStart') {
    Fail "safe-start must block manual catalog sync and hide Sync Center"
}
else {
    Pass "safe-start blocks manual catalog sync and hides Sync Center"
}

if ($app -notmatch "App\.OnStartup entered" -or
    $mainWindow -notmatch "MainWindow constructor entered" -or
    $mainWindow -notmatch "DB init start" -or
    $mainWindow -notmatch "POS access dialog opening" -or
    $mainWindow -notmatch "POS access dialog shown" -or
    $mainWindow -notmatch "POS access dialog accepted" -or
    $mainWindow -notmatch "adaptive online scheduler start") {
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

if ($catalogPull -notmatch 'catch\s*\(OperationCanceledException\)\s*when\s*\(cancellationToken\.IsCancellationRequested\)[\s\S]{0,120}throw' -or
    $catalogPull -notmatch 'StoreCatalogFailureForGenerationAsync\([\s\S]{0,240}"timeout"') {
    Fail "catalog pull cancellation does not distinguish caller cancellation from fenced timeout status"
}
else {
    Pass "catalog pull cancellation rethrows caller cancellation and persists fenced timeout status"
}

if ($workflow -notmatch "check-required-gates\.ps1" -or
    $workflow -notmatch "check-release-pack-completeness\.ps1[\s\S]*-ReleasePackSource\s+dist/Win7POS" -or
    $workflow -notmatch "check-required-gates\.ps1\s+-ReleasePackSource\s+dist/Win7POS") {
    Fail "ReleasePack workflow does not run startup and package validators"
}
else {
    Pass "ReleasePack workflow runs canonical startup and package validators"
}

if ($installer -notmatch "(?m)^MinVersion=6\.1sp1\r?$") {
    Fail "Installer must require Windows 7 SP1 or later"
}
else {
    Pass "Installer requires Windows 7 SP1 or later"
}

Test-ReleasePackSource $ReleasePackSource

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
