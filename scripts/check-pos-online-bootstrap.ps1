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

function Has-Literal([string]$text, [string]$literal) {
    return $text.Contains($literal)
}

function Has-VisibleCopyOrLocKey([string]$text, [string]$visibleCopy, [string]$locKey) {
    return (Has-Literal $text $visibleCopy) -or (Has-Literal $text $locKey)
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

$required = @(
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Core/Online/PosAdminWebOptions.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs",
    "src/Win7POS.Data/DbInitializer.cs",
    "src/Win7POS.Data/Repositories/UserRepository.cs"
    "src/Win7POS.Core/Security/PosAccessRecoveryPolicy.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$firstRun = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml"
$firstRunCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$catalogPull = Read-Text "src/Win7POS.Wpf/Pos/Online/PosCatalogPullService.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$initializer = Read-Text "src/Win7POS.Data/DbInitializer.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$recoveryPolicy = Read-Text "src/Win7POS.Core/Security/PosAccessRecoveryPolicy.cs"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String
$forbiddenRuntimeUrlScope = @(
    $mainWindow,
    $dialogXaml,
    $dialog,
    $bootstrap,
    (Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs")
) -join "`n"
$defaultUrlMatch = [regex]::Match($wpfProject, '<AdminWebDefaultBaseUrl[^>]*>([^<]+)</AdminWebDefaultBaseUrl>')

if ([regex]::Matches($mainWindow, 'new\s+PosOnlineFirstLoginDialog\b').Count -lt 1) { Fail "startup unified POS access dialog missing" } else { Pass "startup uses unified POS access dialog" }
if ($mainWindow -match "FirstRunSetupDialog|TryOnlineBootstrapFirstRunAsync") { Fail "MainWindow must not recreate the legacy first-run dialog chain" } else { Pass "MainWindow has no legacy first-run dialog chain" }
if ($dialog -notmatch "new\s+FirstRunSetupDialog\(_factory\)" -or $dialog -notmatch "OnRecoveryClick") { Fail "FirstRunSetupDialog is not reachable as an explicit child recovery action" } else { Pass "explicit child recovery entry point retained" }
if ($dialog -notmatch "PosAccessRecoveryPolicy\.Evaluate" -or
    $recoveryPolicy -notmatch "IsDenied\(failureKind\)" -or
    $recoveryPolicy -notmatch "PosAccessNextStep\.Denied") { Fail "anti-denial recovery policy missing" } else { Pass "anti-denial recovery policy present" }
if (-not ((Has-VisibleCopyOrLocKey $firstRun "Recovery/dev" "firstRun.title") -and (Test-TranslationEntry $translations "firstRun.title" @("Recovery/dev")))) { Fail "FirstRunSetupDialog is not labelled as recovery/dev" } else { Pass "FirstRunSetupDialog labelled recovery/dev" }
if ($dialogXaml -match "Indirizzo pannello") { Fail "online bootstrap must not expose the panel URL in the normal operator flow" } else { Pass "panel URL removed from normal operator flow" }
if (-not ((Has-Literal $dialogXaml "AdvancedExpander") -and (Has-Literal $dialogXaml 'x:Name="BaseUrlBox"') -and (Has-VisibleCopyOrLocKey $dialogXaml "Impostazioni avanzate / Server" "access.login.advancedSettings") -and (Has-VisibleCopyOrLocKey $dialogXaml "Server Admin Web configurato" "onlineFirstLogin.serverConfigured") -and (Test-TranslationEntry $translations "access.login.advancedSettings" @("Advanced settings / Server", "Configuracion avanzada / Servidor", "Impostazioni avanzate / Server")) -and (Test-TranslationEntry $translations "onlineFirstLogin.serverConfigured" @("Admin Web server configured", "Servidor Admin Web configurado", "Server Admin Web configurato")))) { Fail "online bootstrap must keep Admin Web URL under advanced server settings" } else { Pass "advanced server settings present" }
if ($options -notmatch "TryLoadPackagedDefault" -or $options -notmatch "TryReadPackagedDefaultBaseUrl" -or $options -notmatch "PosAdminWebBaseUrlSource") { Fail "packaged default URL resolver/source model missing" } else { Pass "packaged default URL resolver/source model present" }
if ($wpfProject -notmatch "AdminWebEnvironment" -or $wpfProject -notmatch "AdminWebDefaultBaseUrl" -or $wpfProject -notmatch "AssemblyMetadataAttribute") { Fail "MSBuild packaged default metadata missing" } else { Pass "MSBuild packaged default metadata present" }
if (-not $defaultUrlMatch.Success) {
    Fail "packaged default base URL missing"
} else {
    try {
        $defaultUri = [Uri]$defaultUrlMatch.Groups[1].Value.Trim()
        if ($defaultUri.Scheme -ne "https" -or $defaultUri.UserInfo -or $defaultUri.AbsolutePath -ne "/" -or $defaultUri.Query -or $defaultUri.Fragment) {
            Fail "packaged default base URL must be HTTPS and base-only"
        } else {
            Pass "packaged default base URL is HTTPS and base-only"
        }
    } catch {
        Fail "packaged default base URL is not a valid absolute URI"
    }
}
if ($forbiddenRuntimeUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1|\.\.\.|schemas\.microsoft\.com))") { Fail "Admin Web URL hardcoded outside centralized config/sample/docs" } else { Pass "no Admin Web URL hardcoded in UI/bootstrap/client runtime" }
if ($dialogXaml -match "Shop code|Staff code|Nome device") { Fail "online bootstrap still exposes technical/English labels" } else { Pass "online bootstrap labels are operator-facing" }
if ($dialog -notmatch "PosOnlineBootstrapService") { Fail "online dialog does not use bootstrap service" } else { Pass "online dialog uses bootstrap service" }
if ($dialog -notmatch "finally[\s\S]*CredentialBox\.Clear\(\)") { Fail "online dialog must clear PIN/password in a finally block" } else { Pass "online dialog clears PIN/password in finally" }
if ($options -notmatch "SaveBaseUrl") { Fail "Admin Web base URL cannot be saved from bootstrap" } else { Pass "Admin Web base URL save present" }
if ($bootstrap -notmatch "FirstLoginAsync") { Fail "bootstrap does not call first-login" } else { Pass "bootstrap calls first-login" }
if ($bootstrap -match "shop code|staff code") { Fail "bootstrap service error copy must be operator-facing Italian" } else { Pass "bootstrap service error copy is operator-facing" }
if ($bootstrap -notmatch "SaveFirstLogin") { Fail "bootstrap does not save trusted device with DPAPI store" } else { Pass "trusted device save present" }
if ($bootstrap -notmatch "UpsertRemoteStaffMirrorAsync") { Fail "bootstrap does not create/sync local staff mirror" } else { Pass "local staff mirror present" }
$validationIndex = $bootstrap.IndexOf("ValidateFirstLoginResponse(")
$mirrorIndex = $bootstrap.IndexOf("UpsertRemoteStaffMirrorAsync")
if ($validationIndex -lt 0 -or $mirrorIndex -lt 0 -or $validationIndex -gt $mirrorIndex) {
    Fail "bootstrap must validate first-login trusted/session payload before local staff mirror"
} else {
    Pass "bootstrap validates trusted/session payload before local staff mirror"
}
$validationPattern = "ValidateFirstLoginResponse[\s\S]*TrustedDeviceToken[\s\S]*Session[\s\S]*SessionToken[\s\S]*PosSessionId[\s\S]*ShopDeviceId[\s\S]*StaffId[\s\S]*StaffCode[\s\S]*ShopId[\s\S]*ShopCode"
if ($bootstrap -notmatch $validationPattern) { Fail "bootstrap first-login validation must cover tokens, session, device, staff and shop fields" } else { Pass "bootstrap first-login validation covers required fields" }
if ($bootstrap -notmatch "response\.Ok" -or $bootstrap -notmatch "Device\.Trusted" -or $bootstrap -notmatch "Device\.Status" -or $bootstrap -notmatch "Policy" -or $bootstrap -notmatch "ContractVersion") { Fail "bootstrap first-login validation must require ok, trusted active device and policy contract" } else { Pass "bootstrap validates ok/trusted device/policy contract" }
$shopSnapshotIndex = $bootstrap.IndexOf("PosOnlineShopSnapshot.SaveAsync")
$policySnapshotIndex = $bootstrap.IndexOf("PosOnlinePolicySnapshot.SaveAsync")
$saveTrustIndex = $bootstrap.IndexOf("SaveFirstLogin")
if ($shopSnapshotIndex -lt 0 -or $policySnapshotIndex -lt 0 -or $saveTrustIndex -lt 0 -or $mirrorIndex -lt 0 -or $shopSnapshotIndex -gt $saveTrustIndex -or $policySnapshotIndex -gt $saveTrustIndex -or $saveTrustIndex -gt $mirrorIndex) {
    Fail "bootstrap must save shop/policy and trust before creating local staff mirror"
} else {
    Pass "bootstrap persists shop/policy/trust before local staff mirror"
}
$transitionCheckIndex = $bootstrap.IndexOf(".EvaluateAsync(")
$transitionBlockIndex = $bootstrap.IndexOf("if (!shopTransition.Allowed)")
$transitionResetIndex = $bootstrap.IndexOf("ApplyAuthorizedTransitionAndHoldAsync")
if ($transitionCheckIndex -lt 0 -or $transitionBlockIndex -lt 0 -or $transitionResetIndex -lt 0 -or
    $transitionCheckIndex -gt $transitionBlockIndex -or $transitionBlockIndex -gt $shopSnapshotIndex -or
    $transitionResetIndex -gt $shopSnapshotIndex -or $transitionResetIndex -gt $saveTrustIndex) {
    Fail "shop transition/outbox guard must fail closed before snapshot and trusted-device persistence"
} else {
    Pass "shop transition/outbox guard precedes snapshot and trusted-device persistence"
}
if ($bootstrap -notmatch "_trustedDeviceStore\.Clear\(\)" -or $bootstrap -notmatch "local_persistence_failed") { Fail "bootstrap must clear trust when local trust/mirror persistence fails" } else { Pass "bootstrap clears trust on local persistence failure" }
if ($bootstrap -notmatch "TryPullInitialCatalogAsync" -or $catalogPull -notmatch "TryPullInitialCatalogAsync") { Fail "bootstrap does not use initial catalog pull path" } else { Pass "initial catalog pull path used" }
if ($catalogPull -notmatch "pos.catalog.bootstrap_status" -or $catalogPull -notmatch "partial_has_more" -or $catalogPull -notmatch "failed_auth_denied") { Fail "initial catalog pull must persist bootstrap catalog status" } else { Pass "initial catalog pull persists bootstrap catalog status" }
if ($bootstrap -notmatch "Bootstrap catalog pull incomplete" -or $bootstrap -notmatch "catalogOutcome\.Completed") { Fail "bootstrap must log incomplete catalog pull outcome" } else { Pass "bootstrap logs incomplete catalog outcome" }
if ($bootstrap -notmatch "CanOpenPos" -or $bootstrap -notmatch "CatalogSaleSafe" -or $bootstrap -notmatch "CatalogCompleted" -or $bootstrap -notmatch "RequiresRetry") { Fail "bootstrap result must expose catalog readiness and POS open decision" } else { Pass "bootstrap result exposes catalog readiness" }
if ($bootstrap -notmatch "IProgress<PosCatalogPullProgress>" -or $catalogPull -notmatch "IProgress<PosCatalogPullProgress>" -or $dialog -notmatch "UpdateSetupProgress") { Fail "catalog/bootstrap progress callback missing" } else { Pass "catalog/bootstrap progress callback present" }
if ($dialogXaml -notmatch "ProgressPanel" -or $dialogXaml -notmatch "SetupProgressBar" -or $dialogXaml -notmatch "RetryDownloadButton" -or $dialog -notmatch "RunCatalogRetryAsync") { Fail "blocking preparation progress/retry UI missing" } else { Pass "blocking preparation progress/retry UI present" }
if ($dialog -match "result\.Success[\s\S]{0,180}DialogResult\s*=\s*true") { Fail "online dialog closes on generic Success instead of CanOpenPos/sale-safe" } else { Pass "online dialog does not close on generic Success" }
if ($dialog -notmatch "if\s*\(result\.CanOpenPos\)" -or $dialog -notmatch "AccessMode\s*=\s*PosAuthenticatedAccessMode\.Normal") { Fail "online dialog must distinguish normal sale-safe access from recovery" } else { Pass "online dialog distinguishes sale-safe access from recovery" }
if ($dialog -notmatch "result\.CanOpenPos[\s\S]{0,220}CompleteOnlineSignInAsync") { Fail "online dialog must route CanOpenPos through completion gate" } else { Pass "CanOpenPos routes through completion gate" }
$completeSignInMethod = [regex]::Match($dialog, "private\s+async\s+Task<bool>\s+CompleteOnlineSignInAsync[\s\S]*?private\s+async\s+Task<bool>\s+TryOfflineSignInAsync").Value
if ($completeSignInMethod -notmatch "EnsureCatalogSaleSafeForAccessAsync[\s\S]*DialogResult\s*=\s*true") { Fail "online completion must recheck sale-safe catalog before closing" } else { Pass "online completion rechecks sale-safe catalog before closing" }
if ($catalogPull -notmatch "pos\.catalog\.sale_safe_at" -or $catalogPull -notmatch "pos\.catalog\.initial_completed_at") { Fail "catalog sale-safe completion settings missing" } else { Pass "catalog sale-safe completion settings present" }
if ($catalogPull -notmatch "result\.Denied\s*&&\s*clearStoredStateOnDenied[\s\S]{0,120}_store\.Clear\(\)") { Fail "catalog trust clear must be guarded by auth denied" } else { Pass "catalog trust clear guarded by auth denied" }
if ($dialog -notmatch "new\s+CancellationTokenSource\(TimeSpan\.FromMinutes\(6\)\)") { Fail "online first-login/catalog timeout must allow the large initial catalog" } else { Pass "online first-login/catalog uses the long bootstrap timeout" }
if ($userRepo -notmatch "UpsertRemoteStaffMirrorAsync") { Fail "UserRepository remote staff upsert missing" } else { Pass "UserRepository remote staff upsert present" }
if ($initializer -notmatch "remote_staff_id") { Fail "remote staff id column missing" } else { Pass "remote staff id column present" }
if ($initializer -notmatch "remote_credential_version") { Fail "remote credential version column missing" } else { Pass "remote credential version column present" }
if ($store -notmatch "ProtectedData\.Protect") { Fail "trusted device store does not use DPAPI" } else { Pass "DPAPI trusted device store present" }
if ($mainWindow -match "ModernMessageDialog\.Show[\s\S]{0,180}ex\.Message") { Fail "startup/user error dialogs must not expose raw exception messages" } else { Pass "startup/user error dialogs are user-facing" }
if ($firstRunCode -match "ShowError\(msg\)") { Fail "recovery/dev setup must not show raw exception messages" } else { Pass "recovery/dev setup errors are user-facing" }
if ($firstRunCode -notmatch "finally[\s\S]*PinBox\.Clear\(\)[\s\S]*ConfirmPinBox\.Clear\(\)") { Fail "recovery/dev setup must clear PIN fields in a finally block" } else { Pass "recovery/dev setup clears PIN fields in finally" }
if ((Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs") -notmatch "MaxResponseBodyBytes|ReadResponseBodyAsync") { Fail "Admin Web client must bound response body reads" } else { Pass "Admin Web client bounds response body reads" }

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
if ($combined -match "NEXT_PUBLIC_SUPABASE|supabase\.co") { Fail "Supabase direct client/config reference found" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($combined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
