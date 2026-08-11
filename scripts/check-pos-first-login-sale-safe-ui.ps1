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

function Require-File([string]$relativePath) {
    if (-not (Test-Path (Join-Path $repoRoot $relativePath))) {
        Fail "$relativePath missing"
    }
}

function Index-OrFail([string]$text, [string]$needle, [string]$message) {
    $index = $text.IndexOf($needle, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        Fail $message
    }
    return $index
}

$required = @(
    "src/Win7POS.Wpf/MainWindow.xaml",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs",
    "src/Win7POS.Core/Online/PosStartupCoordinatorPolicy.cs",
    "src/Win7POS.Wpf/Pos/PosView.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
)

foreach ($path in $required) {
    Require-File $path
}

if ($fail) {
    exit 1
}

$mainXaml = Read-Text "src/Win7POS.Wpf/MainWindow.xaml"
$mainCode = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$startupCoordinator = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs"
$startupCoordinatorPolicy = Read-Text "src/Win7POS.Core/Online/PosStartupCoordinatorPolicy.cs"
$posView = Read-Text "src/Win7POS.Wpf/Pos/PosView.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$dialogCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$catalogPull = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"

if ($mainXaml -match "<pos:PosView") {
    Fail "MainWindow must not instantiate PosView in XAML before sale-safe gate"
} else {
    Pass "MainWindow does not instantiate PosView in XAML"
}

if ($mainXaml -notmatch 'x:Name="PosTabHost"' -or $mainCode -notmatch "private\s+PosView\s+PosViewControl" -or $mainCode -notmatch "EnsurePosViewCreated") {
    Fail "POS view lazy host/creation guard missing"
} else {
    Pass "POS view lazy host/creation guard present"
}

$saleSafeGateIndex = Index-OrFail $startupCoordinator ".IsCatalogSaleSafeAsync(factory)" "sale-safe gate missing from startup coordinator"
$ensurePosIndex = Index-OrFail $mainCode "EnsurePosViewCreated();" "POS lazy creation call missing"
$accessDialogIndex = Index-OrFail $mainCode "POS access dialog opening" "unified POS access startup marker missing"
$accessAcceptanceIndex = Index-OrFail $mainCode "AcceptAuthenticatedAccessAsync(login.AccessMode)" "startup coordinator access acceptance missing"
if ($accessDialogIndex -gt $accessAcceptanceIndex -or $ensurePosIndex -lt $accessAcceptanceIndex -or
    $startupCoordinator -notmatch 'AcceptAuthenticatedAccessAsync[\s\S]{0,700}IsCatalogSaleSafeAsync\(factory\)') {
    Fail "unified access must precede the sale-safe decision and PosView must follow it"
} else {
    Pass "unified access precedes sale-safe decision; PosView creation follows it"
}

if ($startupCoordinator -notmatch "PosStartupCoordinatorPolicy\.DetermineShellMode" -or
    $startupCoordinatorPolicy -notmatch "PosShellStartupPolicy\.Determine" -or
    $mainCode -notmatch "PosShellMode\.Recovery" -or
    $mainCode -notmatch "EnterRecoveryModeAsync" -or
    $mainCode -notmatch "if\s*\(shellMode\s*==\s*PosShellMode\.Pos\)[\s\S]{0,180}EnsurePosViewCreated") {
    Fail "catalog-unsafe startup must enter recovery without creating PosView"
} else {
    Pass "catalog-unsafe startup enters recovery and blocks normal PosView"
}

if ($mainCode -notmatch "RecoveryModeBanner\.Visibility\s*=\s*Visibility\.Visible" -or
    $mainCode -notmatch "OnVerifyRecoveryCatalogClick" -or
    $startupCoordinator -notmatch "TryApproveLocalCatalogAsync[\s\S]{0,450}CatalogRecoveryRepository") {
    Fail "recovery banner or controlled catalog re-verification is missing"
} else {
    Pass "recovery banner and controlled re-verification are present"
}

if ($posView -notmatch "StartInitialize\(\)") {
    Fail "PosView startup initialize marker missing"
} else {
    Pass "PosView startup initialize marker present"
}

if ($dialogXaml -notmatch "ProgressPanel" -or $dialogXaml -notmatch "SetupProgressBar" -or $dialogXaml -notmatch "RetryDownloadButton") {
    Fail "blocking setup progress/retry UI missing"
} else {
    Pass "blocking setup progress/retry UI present"
}

if ($dialogCode -notmatch "_busy" -or $dialogCode -notmatch "BeginBusySetup[\s\S]*SetInputEnabled\(false\)") {
    Fail "dialog must disable inputs and guard double-submit while setup runs"
} else {
    Pass "dialog disables inputs and guards double-submit"
}

if ($dialogCode -match "result\.Success[\s\S]{0,220}DialogResult\s*=\s*true") {
    Fail "dialog closes on generic Success instead of sale-safe readiness"
} else {
    $completeSignInMethod = [regex]::Match($dialogCode, "private\s+async\s+Task<bool>\s+CompleteOnlineSignInAsync[\s\S]*?private\s+async\s+Task<bool>\s+TryOfflineSignInAsync").Value
    $catalogRetryMethod = [regex]::Match($dialogCode, "private\s+async\s+Task\s+RunCatalogRetryAsync[\s\S]*?private\s+void\s+BeginBusySetup").Value
    $initialReadyRoutesThroughGate = $dialogCode -match "result\.CanOpenPos[\s\S]{0,220}CompleteOnlineSignInAsync"
    $completionRechecksSaleSafe = $completeSignInMethod -match "EnsureCatalogSaleSafeForAccessAsync[\s\S]*DialogResult\s*=\s*true"
    $retryRequiresSaleSafe = $catalogRetryMethod -match "outcome\.Completed\s*&&\s*outcome\.CatalogSaleSafe"
    $resumeOnlyClosesInsideReadyBranch = $catalogRetryMethod -match "outcome\.Completed\s*&&\s*outcome\.CatalogSaleSafe[\s\S]*_resumeCatalogOnly[\s\S]*DialogResult\s*=\s*true"
    $normalRetryRoutesThroughGate = $catalogRetryMethod -match "CompleteOnlineSignInAsync"
    if (-not $initialReadyRoutesThroughGate -or -not $completionRechecksSaleSafe -or -not $retryRequiresSaleSafe -or
        -not $resumeOnlyClosesInsideReadyBranch -or -not $normalRetryRoutesThroughGate) {
        Fail "dialog must close only after CanOpenPos or completed sale-safe retry"
    } else {
        Pass "dialog closes only after sale-safe readiness"
    }
}

if ($dialogCode -notmatch "AccessMode\s*=\s*PosAuthenticatedAccessMode\.Normal") {
    Fail "dialog must distinguish normal sale-safe access from recovery"
} else {
    Pass "dialog distinguishes normal sale-safe access from recovery"
}

if ($dialogCode -notmatch "finally[\s\S]*request\.Credential\s*=\s*string\.Empty[\s\S]*CredentialBox\.Clear\(\)") {
    Fail "dialog must clear raw credential/PIN in finally"
} else {
    Pass "dialog clears raw credential/PIN in finally"
}

if ($dialogCode -notmatch "_activeCts\??\.Cancel\(\)" -or $dialogCode -notmatch "RunCatalogRetryAsync") {
    Fail "dialog cancel/retry path missing"
} else {
    Pass "dialog cancel/retry path present"
}

if ($bootstrap -notmatch "CanOpenPos" -or $bootstrap -notmatch "CatalogSaleSafe" -or $bootstrap -notmatch "RequiresRetry") {
    Fail "bootstrap result must expose sale-safe POS-open decision"
} else {
    Pass "bootstrap result exposes sale-safe POS-open decision"
}

$hasMoreIndex = Index-OrFail $catalogPull "lastResponse.HasMore" "catalog HasMore guard missing"
$saleSafeWriteIndex = Index-OrFail $catalogPull "StoreCatalogSaleSafeAsync" "catalog sale-safe write missing"
if ($saleSafeWriteIndex -lt $hasMoreIndex) {
    Fail "catalog sale-safe marker can be written before HasMore is handled"
} else {
    Pass "catalog sale-safe marker is after HasMore guard"
}

if ($catalogPull -notmatch "authenticationDenied\s*&&\s*clearStoredStateOnDenied[\s\S]{0,180}_store\.Clear\(\)" -or
    $catalogPull -notmatch "SharedAuthStopPolicy\.IsAuthenticationDenied\(resultCode\)") {
    Fail "catalog retry/partial errors must not clear trust unless auth is denied"
} else {
    Pass "catalog clears trust only on auth denied"
}

$retryIndex = $statusReader.IndexOf("if (outbox.Retry > 0 || catalogOutbox.Retry > 0)", [System.StringComparison]::Ordinal)
$catalogUpdatingIndex = $statusReader.IndexOf('"updating"', [System.StringComparison]::Ordinal)
if ($retryIndex -lt 0 -or $catalogUpdatingIndex -lt 0 -or $retryIndex -gt $catalogUpdatingIndex) {
    Fail "status summary must surface sales retry before catalog updating/ready text"
} else {
    Pass "status summary prioritizes sales retry over catalog updating"
}

if ($translations -notmatch "onlineFirstLogin\.catalogIncomplete" -or $translations -notmatch "onlineFirstLogin\.downloadRetry" -or $translations -notmatch "onlineFirstLogin\.retryDownload") {
    Fail "first-login sale-safe retry localization missing"
} else {
    Pass "first-login sale-safe retry localization present"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
