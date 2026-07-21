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
    "src/Win7POS.Data/Online/PosAdminWebClient.cs",
    "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs",
    "src/Win7POS.Core/Online/PosAdminWebOptions.cs",
    "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs",
    "src/Win7POS.Data/Online/OnlineSyncGenerationRepository.cs",
    "src/Win7POS.Data/Online/OnlineSyncSupervisor.cs",
    "tests/Win7POS.Core.Tests/Online/OnlineSyncSupervisorTests.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml",
    "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs",
    "src/Win7POS.Data/Repositories/UserRepository.cs",
    "tests/Win7POS.Core.Tests/Data/OnlineSyncGenerationRepositoryTests.cs"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$client = Read-Text "src/Win7POS.Data/Online/PosAdminWebClient.cs"
$streamingTests = Read-Text "tests/Win7POS.Core.Tests/Online/HttpBoundedStreamingTests.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$options = Read-Text "src/Win7POS.Core/Online/PosAdminWebOptions.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$syncHost = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$revocationLatch = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$generationRepo = Read-Text "src/Win7POS.Data/Online/OnlineSyncGenerationRepository.cs"
$generationTests = Read-Text "tests/Win7POS.Core.Tests/Data/OnlineSyncGenerationRepositoryTests.cs"
$supervisor = Read-Text "src/Win7POS.Data/Online/OnlineSyncSupervisor.cs"
$supervisorTests = Read-Text "tests/Win7POS.Core.Tests/Online/OnlineSyncSupervisorTests.cs"
$dialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.LegacyReachable.cs"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$taskCombined = @(
    $client,
    $store,
    $options,
    $bootstrap,
    $syncHost,
    (Read-Text "src/Win7POS.Wpf/Pos/Online/PosDeviceIdentity.cs"),
    $dialogXaml,
    $dialog,
    $mainWindow
) -join "`n"
$baseUrlScope = @(
    $client,
    $options,
    $bootstrap,
    $syncHost,
    $dialog,
    $mainWindow
) -join "`n"
$forbiddenRuntimeUrlScope = @(
    $client,
    $bootstrap,
    $syncHost,
    $dialog,
    $dialogXaml,
    $mainWindow
) -join "`n"
$combined = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml,*.csproj |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String
$defaultUrlMatch = [regex]::Match($wpfProject, '<AdminWebDefaultBaseUrl[^>]*>([^<]+)</AdminWebDefaultBaseUrl>')

if ($client -notmatch "HttpClient") { Fail "HttpClient missing" } else { Pass "HttpClient present" }
if ($client -notmatch "SecurityProtocolType\.Tls12") { Fail "TLS 1.2 enforcement missing" } else { Pass "TLS 1.2 present" }
if ($client -notmatch "Timeout\s*=") { Fail "explicit timeout missing" } else { Pass "timeout present" }
if ($client -notmatch "SharedTransportEntry" -or
    $client -notmatch "AcquireSharedTransport" -or
    $client -notmatch "ReleaseSharedTransport" -or
    $client -notmatch "CreateTransportKey") { Fail "endpoint/config-scoped HttpClient reuse missing" } else { Pass "endpoint/config-scoped HttpClient reuse present" }
if ($client -notmatch "HttpCompletionOption\.ResponseHeadersRead") { Fail "streaming response headers mode missing" } else { Pass "ResponseHeadersRead present" }
if ($client -notmatch "BoundedCountingReadStream" -or
    $client -notmatch "MaxResponseBodyBytes\s*=\s*8\s*\*\s*1024\s*\*\s*1024" -or
    $client -notmatch "content\.Headers\.ContentLength") { Fail "bounded response stream missing" } else { Pass "declared and chunked response limits present" }
if ($client -notmatch "serializer\.ReadObject\(bounded\)" -or
    $client -match "StringContent|Encoding\.UTF8\.Get(String|Bytes)") { Fail "online transport still uses a byte/string/byte JSON round trip" } else { Pass "request and response DTOs stream without a byte/string/byte round trip" }
if ($client -notmatch "MaxErrorResponseBodyBytes" -or
    $streamingTests -notmatch "ChunkedBodyOverEightMiBIsStoppedByCountingStream" -or
    $streamingTests -notmatch "OversizedUnauthorizedErrorRemainsAuthenticationDenied") { Fail "bounded streaming regression tests missing" } else { Pass "bounded success/error streaming tests present" }
if ($client -notmatch "/api/pos/auth/first-login") { Fail "first-login path missing" } else { Pass "first-login path present" }
if ($client -notmatch "/api/pos/session/heartbeat") { Fail "heartbeat path missing" } else { Pass "heartbeat path present" }
if ($store -notmatch "ProtectedData\.Protect" -or $store -notmatch "ProtectedData\.Unprotect") { Fail "DPAPI storage missing" } else { Pass "DPAPI storage present" }
if ($store -notmatch "WriteAllTextAtomic" -or $store -notmatch "File\.Replace" -or $store -notmatch "File\.Move") { Fail "trusted-device atomic write missing" } else { Pass "trusted-device atomic write present" }
if ($options -notmatch "WIN7POS_ADMIN_WEB_BASE_URL" -or $options -notmatch "pos-admin-web\.config") { Fail "base URL config sources missing" } else { Pass "base URL config present" }
if ($options -notmatch "PosAdminWebBaseUrlSource" -or $options -notmatch "EnvironmentVariable" -or $options -notmatch "ConfigFile" -or $options -notmatch "PackagedDefault") { Fail "base URL source model missing" } else { Pass "base URL source model present" }
if ($options -notmatch "TryLoadPackagedDefault" -or $options -notmatch "TryReadPackagedDefaultBaseUrl" -or $options -notmatch "AssemblyMetadataAttribute") { Fail "packaged default URL resolver missing" } else { Pass "packaged default URL resolver present" }
if ($wpfProject -notmatch "AdminWebEnvironment" -or $wpfProject -notmatch "AdminWebDefaultBaseUrl" -or $wpfProject -notmatch "AssemblyMetadataAttribute") { Fail "MSBuild Admin Web metadata missing" } else { Pass "MSBuild Admin Web metadata present" }
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
if ($options -notmatch "WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB" -or $options -notmatch "AllowInsecureLanAdminWeb") { Fail "insecure LAN override guard missing" } else { Pass "insecure LAN override guard present" }
if ($options -notmatch "parsed\.UserInfo" -or $options -notmatch "senza username o password") { Fail "base URL credentials guard missing" } else { Pass "base URL credentials rejected" }
if ($dialogXaml -match "Indirizzo pannello") { Fail "normal online link dialog still exposes URL field copy" } else { Pass "normal online link dialog hides URL field copy" }
if (-not ((Has-Literal $dialogXaml "AdvancedExpander") -and (Has-Literal $dialogXaml 'x:Name="BaseUrlBox"') -and (Has-VisibleCopyOrLocKey $dialogXaml "Impostazioni avanzate / Server" "access.login.advancedSettings") -and (Has-VisibleCopyOrLocKey $dialogXaml "URL Admin Web" "onlineFirstLogin.adminWebUrl") -and (Test-TranslationEntry $translations "access.login.advancedSettings" @("Advanced settings / Server", "Configuracion avanzada / Servidor", "Impostazioni avanzate / Server")) -and (Test-TranslationEntry $translations "onlineFirstLogin.adminWebUrl" @("Admin Web URL", "URL Admin Web")))) { Fail "advanced server URL settings missing" } else { Pass "advanced server URL settings present" }
if ($dialog -notmatch "PosDeviceIdentity\.GetStableDisplayName") { Fail "device display name is not generated automatically" } else { Pass "device display name generated automatically" }
if ($dialog -notmatch "PosOnlineBootstrapService") { Fail "first-login dialog does not use bootstrap service" } else { Pass "first-login dialog uses bootstrap service" }
if ($dialog -notmatch "finally[\s\S]*request\.Credential\s*=\s*string\.Empty[\s\S]*credential\s*=\s*string\.Empty[\s\S]*CredentialBox\.Clear\(\)") { Fail "first-login dialog does not clear PIN/password in finally" } else { Pass "first-login dialog clears PIN/password in finally" }
if ($bootstrap -notmatch "new\s+PosAdminWebClient" -or $bootstrap -notmatch "FirstLoginAsync") { Fail "bootstrap service does not call first-login through online client" } else { Pass "bootstrap service calls first-login through online client" }
if ($bootstrap -notmatch "ActivateAuthenticatedTrustAsync" -or
    $syncHost -notmatch "_store\.SaveFirstLogin" -or
    $store -notmatch "ProtectedDeviceSecret" -or
    $store -notmatch "ProtectedSessionSecret") { Fail "bootstrap does not save trusted tokens through the serialized protected store" } else { Pass "trusted tokens saved through serialized protected store" }
if ($store -match 'DataMember\(Name\s*=\s*"(trustedDeviceToken|deviceToken|sessionToken)"') { Fail "trusted store may persist raw token fields" } else { Pass "trusted store does not persist raw token field names" }
if ($userRepo -notmatch "PinHelper\.HashPin\(input\.Credential") { Fail "remote staff credential is not hashed for local mirror" } else { Pass "remote staff credential hashed for local mirror" }
if ($mainWindow -notmatch "new\s+PosOnlineFirstLoginDialog" -or $dialog -notmatch "PosOnlineBootstrapService") { Fail "unified POS access flow is not wired to the online client" } else { Pass "unified POS access flow uses online bootstrap client" }
if ($syncHost -notmatch "RunHeartbeatAsync[\s\S]{0,1200}_store\.TryReadGeneration\(\s*context\.Generation" -or
    $syncHost -notmatch "ExecuteCredentialedRequestAsync[\s\S]{0,7000}_store\.TrySaveHeartbeat\(\s*context\.Generation\.GenerationId,\s*expectedSession" -or
    $syncHost -notmatch "_store\.TrySaveHeartbeat[\s\S]{0,500}if\s*\(!await\s+context\.IsCurrentAsync\(\)") { Fail "supervised startup heartbeat missing or generation-fence incomplete" } else { Pass "supervised startup heartbeat is credential- and generation-fenced" }

$beginStart = $syncHost.IndexOf("BeginAuthenticatedTrustTransitionAsync", [System.StringComparison]::Ordinal)
$activateStart = $syncHost.IndexOf("internal async Task<OnlineSyncGeneration> ActivateAuthenticatedTrustAsync", [System.StringComparison]::Ordinal)
$attachStart = $syncHost.IndexOf("private async Task<OnlineSyncGeneration> AttachCurrentTrustCoreAsync", [System.StringComparison]::Ordinal)
$rejectStart = $syncHost.IndexOf("internal async Task<bool> RejectAuthenticatedTrustTransitionAsync", [System.StringComparison]::Ordinal)
$publicRevokeStart = $syncHost.IndexOf("public async Task RevokeCurrentTrustAsync", [System.StringComparison]::Ordinal)
if ($beginStart -lt 0 -or $activateStart -le $beginStart -or $attachStart -le $activateStart -or
    $rejectStart -lt 0 -or $publicRevokeStart -le $rejectStart) {
    Fail "authenticated transition method boundaries are missing"
} else {
    $beginBody = $syncHost.Substring($beginStart, $activateStart - $beginStart)
    $activateBody = $syncHost.Substring($activateStart, $attachStart - $activateStart)
    $rejectBody = $syncHost.Substring($rejectStart, $publicRevokeStart - $rejectStart)
    $beginCapture = $beginBody.IndexOf("TryCaptureAuthorizationEpoch", [System.StringComparison]::Ordinal)
    $beginRead = $beginBody.IndexOf("ReadCurrentPredecessorAsync", [System.StringComparison]::Ordinal)
    $beginEpochRecheck = if ($beginRead -ge 0) {
        $beginBody.IndexOf("IsAuthorizationEpochCurrent", $beginRead, [System.StringComparison]::Ordinal)
    } else { -1 }
    $beginMaintenanceRecheck = if ($beginRead -ge 0) {
        $beginBody.IndexOf("PosOnlineSyncSignalBus.IsMaintenanceActive", $beginRead, [System.StringComparison]::Ordinal)
    } else { -1 }
    $attemptCheck = $activateBody.IndexOf("transition.AttemptId", [System.StringComparison]::Ordinal)
    $epochCheck = $activateBody.IndexOf("IsAuthorizationEpochCurrent", [System.StringComparison]::Ordinal)
    $epochInvalidate = $activateBody.IndexOf("TryInvalidateAuthorizationState", [System.StringComparison]::Ordinal)
    $generationCas = $activateBody.IndexOf("ActivateAndRecoverAsync", [System.StringComparison]::Ordinal)
    $catalogMutation = $activateBody.IndexOf("applyAuthorizedLocalTransitionAsync()", [System.StringComparison]::Ordinal)
    $trustedSave = $activateBody.IndexOf("_store.SaveFirstLogin", [System.StringComparison]::Ordinal)
    $localPersistence = $activateBody.IndexOf("await persistAuthenticatedLocalStateAsync", [System.StringComparison]::Ordinal)
    if ($beginCapture -lt 0 -or $beginRead -le $beginCapture -or
        $beginBody -match '_store\.TryRead|AttachOrInitializeCurrentAsync' -or
        $beginEpochRecheck -le $beginRead -or $beginMaintenanceRecheck -le $beginRead -or
        $activateBody -notmatch 'ActivateAndRecoverAsync\(\s*nextGeneration,\s*NextActivationTimestamp\(\),\s*transition\.ExpectedCurrentState\)' -or
        $attemptCheck -lt 0 -or $epochCheck -lt 0 -or $epochInvalidate -lt 0 -or
        $generationCas -lt 0 -or $catalogMutation -lt 0 -or $trustedSave -lt 0 -or
        $localPersistence -lt 0 -or $attemptCheck -gt $epochInvalidate -or
        $epochCheck -gt $epochInvalidate -or $epochInvalidate -gt $generationCas -or
        $generationCas -gt $catalogMutation -or $catalogMutation -gt $localPersistence -or
        $localPersistence -gt $trustedSave) {
        Fail "authenticated commit must validate attempt/epoch and commit the full predecessor CAS before local mutation"
    } else {
        Pass "authenticated commit is attempt-, epoch-, predecessor- and mutation-ordered"
    }

    $rejectAttempt = $rejectBody.IndexOf("transition.AttemptId", [System.StringComparison]::Ordinal)
    $rejectEpoch = $rejectBody.IndexOf("IsAuthorizationEpochCurrent", [System.StringComparison]::Ordinal)
    $rejectRead = $rejectBody.IndexOf("ReadCurrentPredecessorAsync", [System.StringComparison]::Ordinal)
    $rejectSecondAttempt = if ($rejectRead -ge 0) {
        $rejectBody.IndexOf("transition.AttemptId", $rejectRead, [System.StringComparison]::Ordinal)
    } else { -1 }
    $rejectSecondEpoch = if ($rejectRead -ge 0) {
        $rejectBody.IndexOf("IsAuthorizationEpochCurrent", $rejectRead, [System.StringComparison]::Ordinal)
    } else { -1 }
    $rejectSecondMaintenance = if ($rejectRead -ge 0) {
        $rejectBody.IndexOf("PosOnlineSyncSignalBus.IsMaintenanceActive", $rejectRead, [System.StringComparison]::Ordinal)
    } else { -1 }
    $rejectPredecessor = $rejectBody.IndexOf("PredecessorStatesMatch", [System.StringComparison]::Ordinal)
    $rejectRevoke = $rejectBody.IndexOf("RevokeCurrentTrustCoreAsync", [System.StringComparison]::Ordinal)
    if ($rejectAttempt -lt 0 -or $rejectEpoch -lt 0 -or $rejectRead -le $rejectEpoch -or
        $rejectSecondAttempt -le $rejectRead -or $rejectSecondEpoch -le $rejectRead -or
        $rejectSecondMaintenance -le $rejectRead -or $rejectPredecessor -le $rejectSecondAttempt -or
        $rejectSecondEpoch -ge $rejectPredecessor -or
        $rejectSecondMaintenance -ge $rejectPredecessor -or
        $rejectRevoke -le $rejectPredecessor -or
        $rejectBody -notmatch 'RevokeCurrentTrustCoreAsync\(\s*reason,\s*transition\.ExpectedCurrentState\)') {
        Fail "authoritative first-login denial is not scoped to its captured transition"
    } else {
        Pass "authoritative first-login denial is transition-scoped"
    }
}

$bootstrapBegin = $bootstrap.IndexOf("BeginAuthenticatedTrustTransitionAsync", [System.StringComparison]::Ordinal)
$bootstrapRequest = $bootstrap.IndexOf("FirstLoginAsync", [System.StringComparison]::Ordinal)
$bootstrapActivation = $bootstrap.IndexOf("ActivateAuthenticatedTrustAsync", [System.StringComparison]::Ordinal)
$bootstrapMutation = $bootstrap.IndexOf("ApplyAuthorizedTransitionAndHoldAsync", [System.StringComparison]::Ordinal)
if ($bootstrapBegin -lt 0 -or $bootstrapRequest -le $bootstrapBegin -or
    $bootstrapActivation -le $bootstrapRequest -or $bootstrapMutation -le $bootstrapActivation -or
    $bootstrap -notmatch "RejectAuthenticatedTrustTransitionAsync" -or
    $bootstrap -notmatch 'authenticatedTransition\.ExpectedCurrentState\.Active[\s\S]{0,360}storedGeneration\.Fingerprint[\s\S]{0,220}authenticatedTransition\.ExpectedCurrentState\.Fingerprint' -or
    $bootstrap -match "\.RevokeCurrentTrustAsync\(" -or
    $dialog -match "\.RevokeCurrentTrustAsync\(") {
    Fail "bootstrap request/commit/denial flow is not stale-attempt safe"
} else {
    Pass "bootstrap begins before I/O, commits before mutation, and has no broad denial revoke"
}

$repoActivateStart = $generationRepo.IndexOf("public async Task ActivateAndRecoverAsync", [System.StringComparison]::Ordinal)
$repoReadStart = $generationRepo.IndexOf("ReadCurrentPredecessorAsync", [System.StringComparison]::Ordinal)
if ($repoActivateStart -lt 0 -or $repoReadStart -le $repoActivateStart) {
    Fail "generation predecessor CAS method is missing"
} else {
    $repoActivateBody = $generationRepo.Substring($repoActivateStart, $repoReadStart - $repoActivateStart)
    if ($repoActivateBody -notmatch "BeginTransaction\(deferred:\s*false\)" -or
        $repoActivateBody -notmatch "current == null && expectedCurrentState\.Exists" -or
        $repoActivateBody -notmatch "current != null" -or
        $repoActivateBody -notmatch "!expectedCurrentState\.Exists" -or
        $repoActivateBody -notmatch '!string\.Equals\(\s*current\.Fingerprint,\s*expectedCurrentState\.Fingerprint,\s*StringComparison\.Ordinal\)' -or
        $repoActivateBody -notmatch '\(current\.Active\s*==\s*1\)\s*!=\s*expectedCurrentState\.Active' -or
        $repoActivateBody -notmatch 'current\.ActivatedAt\s*==\s*long\.MaxValue[\s\S]{0,180}Math\.Max\(activatedAt,\s*current\.ActivatedAt\s*\+\s*1\)' -or
        $repoActivateBody -notmatch 'sameActiveGeneration[\s\S]*current\.Active\s*!=\s*1[\s\S]*cannot be reactivated') {
        Fail "generation activation does not atomically compare presence, fingerprint and active state"
    } else {
        Pass "generation activation uses a full immediate predecessor CAS"
    }
}

$repoAttachStart = $generationRepo.IndexOf("AttachOrInitializeCurrentAsync", [System.StringComparison]::Ordinal)
$repoStopStart = $generationRepo.IndexOf("public async Task<bool> StopIfCurrentAsync", [System.StringComparison]::Ordinal)
if ($repoAttachStart -lt 0 -or $repoStopStart -le $repoAttachStart) {
    Fail "generation attach method boundaries are missing"
} else {
    $repoAttachBody = $generationRepo.Substring($repoAttachStart, $repoStopStart - $repoAttachStart)
    if ($repoAttachBody -match 'INSERT\s+INTO\s+pos_sync_session_generation' -or
        $repoAttachBody -notmatch 'current\s*!=\s*null[\s\S]{0,1200}return\s+matches' -or
        $repoAttachBody -notmatch 'transaction\.Rollback\(\);\s*return\s+false') {
        Fail "retained trust must never create a missing generation singleton"
    } else {
        Pass "missing generation singleton requires authenticated activation"
    }
}

if ($syncHost -notmatch "AuthorizationEpoch" -or
    $revocationLatch -notmatch "TryCaptureAuthorizationEpoch" -or
    $generationTests -notmatch "generation-expected-empty" -or
    $generationTests -notmatch "generation-missing-predecessor" -or
    $generationTests -notmatch "generation-after-inactive-predecessor" -or
    $generationTests -notmatch "ConcurrentAuthenticatedRelinks_FromSamePredecessor_CommitExactlyOne" -or
    $generationTests -notmatch "DeletedSingleton_CannotBeRecreatedFromRetainedTrustAfterRestart" -or
    $generationTests -notmatch "PredecessorScopedStop_TombstonesWithoutTrustedFileState" -or
    $generationTests -notmatch "preRestoreState" -or
    $generationTests -notmatch "postRestoreState") {
    Fail "stale login/restore predecessor regressions are not covered"
} else {
    Pass "stale login and inactive/absent predecessor regressions are covered"
}

$authStopStart = $supervisor.IndexOf("private Task StopAuthenticationAsync", [System.StringComparison]::Ordinal)
$authStopEnd = $supervisor.IndexOf("private async Task RunAuthenticationStopCallbackAsync", [System.StringComparison]::Ordinal)
$authStopBody = if ($authStopStart -ge 0 -and $authStopEnd -gt $authStopStart) {
    $supervisor.Substring($authStopStart, $authStopEnd - $authStopStart)
} else { "" }
if ($authStopBody -match 'if\s*\([^)]*_stopping' -or
    $authStopBody -notmatch 'if\s*\(_authenticationStopped\)' -or
    $supervisorTests -notmatch 'ShutdownRace_AwaitsAuthenticationDenialAlreadyObservedByRequest') {
    Fail "shutdown must persist and await an authentication denial already observed by a request"
} else {
    Pass "shutdown preserves an observed authentication denial and its durable stop"
}

$rollbackRevoke = $activateBody.IndexOf("PosOnlineSyncRevocationLatch.Revoke(nextGeneration)", [System.StringComparison]::Ordinal)
$rollbackStop = $activateBody.IndexOf("StopIfCurrentAsync(", $rollbackRevoke, [System.StringComparison]::Ordinal)
$rollbackRecheck = $activateBody.IndexOf("IsCurrentAndActiveAsync(nextGeneration)", $rollbackStop, [System.StringComparison]::Ordinal)
$rollbackClear = $activateBody.IndexOf("_store.TryClear(nextGeneration.GenerationId)", $rollbackStop, [System.StringComparison]::Ordinal)
if ($rollbackRevoke -lt 0 -or $rollbackStop -le $rollbackRevoke -or
    $rollbackRecheck -le $rollbackStop -or $rollbackClear -le $rollbackRecheck -or
    $activateBody -notmatch 'StopIfCurrentAsync\(\s*nextGeneration,[\s\S]{0,180}"bootstrap_local_persistence_failed"' -or
    $activateBody -notmatch 'ReadCurrentPredecessorAsync[\s\S]{0,500}PredecessorStatesMatch[\s\S]{0,700}if\s*\(!preserveResidentSupervisor\)' -or
    $activateBody -notmatch 'PosOnlineSyncRevocationLatch\.Revoke\(nextGeneration\)' -or
    $syncHost -notmatch 'RevokeCurrentTrustCoreAsync\(\s*reason,\s*transition\.ExpectedCurrentState\)' -or
    $syncHost -notmatch 'StopIfCurrentPredecessorAsync\(\s*expectedCurrentState' -or
    $syncHost -notmatch 'RevokeFingerprint\(\s*expectedCurrentState\.Fingerprint\)' -or
    $bootstrap -notmatch '_trustedDeviceStore\.TryClear\(activatedGenerationId\)' -or
    $bootstrap -match 'RevokeCurrentTrustAsync') {
    Fail "authenticated local-failure rollback is not generation-scoped"
} else {
    Pass "authenticated local-failure rollback is generation-scoped"
}

if ($combined -match "SUPABASE_SERVICE_ROLE_KEY|service_role") { Fail "service-role reference found" }
$salesShopPolicyIndex = $salesSync.IndexOf("ReceiptShopMetadataPolicy.EnsureValidRemoteShop(result.Value.Shop)", [System.StringComparison]::Ordinal)
$salesShopBindingIndex = if ($salesShopPolicyIndex -ge 0) {
    $salesSync.IndexOf("OutboxShopBinding.GetMismatchCode(", $salesShopPolicyIndex, [System.StringComparison]::Ordinal)
} else { -1 }
if ($salesShopPolicyIndex -lt 0 -or $salesShopBindingIndex -lt 0 -or $salesShopPolicyIndex -gt $salesShopBindingIndex) {
    Fail "sales response shop metadata must be bounded before normalization/binding and ACK mutation"
} else { Pass "sales response shop metadata is bounded before normalization/binding and ACK mutation" }
if ($combined -match "mcpos_(device|session)_[A-Za-z0-9_-]+") { Fail "literal POS token found" }
if ($forbiddenRuntimeUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1|\.\.\.|schemas\.microsoft\.com))") { Fail "Admin Web URL hardcoded outside centralized config/sample/docs" } else { Pass "no Admin Web URL hardcoded in UI/bootstrap/client runtime" }
if ($baseUrlScope -match "https?://(?!(localhost|127\.0\.0\.1|::1|\.\.\.))") { Fail "production-like Admin Web URL hardcoded in resolver/runtime code" } else { Pass "no production Admin Web URL hardcoded in resolver/runtime code" }
if ($combined -match "sync_batch") { Fail "legacy sync batch marker detected" } else { Pass "TASK-081 sales sync scope allowed" }
$sensitiveLogPattern = "(?i)Log(?:Info|Warning|Error)\s*\([^\r\n)]*(trustedDeviceToken|sessionToken|deviceToken|CredentialBox|PinBox|credential|pin|password)"
if ($taskCombined -match $sensitiveLogPattern) { Fail "sensitive POS online value may be logged" } else { Pass "no sensitive POS online logs" }

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
