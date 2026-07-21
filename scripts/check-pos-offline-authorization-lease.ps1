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
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $path)) {
        Fail "$relativePath missing"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require-Pattern([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) {
        Pass $label
    }
    else {
        Fail $label
    }
}

$contract = Read-Text "src/Win7POS.Core/Online/PosOnlineContract.cs"
$policy = Read-Text "src/Win7POS.Core/Online/PosOfflineAuthorizationLeasePolicy.cs"
$contracts = Read-Text "src/Win7POS.Core/Online/PosOnlineTransportContracts.cs"
$store = Read-Text "src/Win7POS.Wpf/Pos/Online/PosTrustedDeviceStore.cs"
$guard = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOfflineAuthorizationLeaseGuard.cs"
$session = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs"
$userRepo = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$permission = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/PermissionService.cs"
$recoveryPermission = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/LocalRecoveryPermissionService.cs"
$override = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/OverrideAuthService.cs"
$accessDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
$operatorSwitch = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml.cs"
$main = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$posViewModel = Read-Text "src/Win7POS.Wpf/Pos/PosViewModel.cs"
$salesBuilder = Read-Text "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"
$salesSync = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSalesSyncService.cs"
$syncHost = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$revocationLatch = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs"
$bootstrap = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineBootstrapService.cs"
$wpfProject = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
$uiHarness = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$authorizationSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/AuthorizationLeaseWpfSmoke.cs"

Require-Pattern "offline lease maximum is the 12-hour POS session contract" $contract 'OfflineAuthorizationMaxAgeSeconds\s*=\s*12\s*\*\s*60\s*\*\s*60'
Require-Pattern "policy fails closed on missing legacy receipt timestamp" $policy 'local_receipt_time_invalid'
Require-Pattern "policy rejects rollback and exact expiry" $policy 'clock_rollback[\s\S]*estimatedServerNow\s*>=\s*effectiveExpiry'
Require-Pattern "first-login DTO consumes authenticated serverTime" $contracts 'class\s+PosFirstLoginResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "heartbeat DTO consumes authenticated serverTime" $contracts 'class\s+PosHeartbeatResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "trusted store persists server and local receipt clocks" $store 'LastOkLocalAt\s*=\s*candidate\.LastOkLocalAt[\s\S]*LastOkServerAt\s*=\s*candidate\.LastOkServerAt'
Require-Pattern "only the non-shipping UI harness receives WPF internal test access" $wpfProject 'InternalsVisibleToAttribute[\s\S]{0,180}Win7POS\.Wpf\.UiSmokeHarness'
Require-Pattern "authorization lease dynamic smoke is wired into the UI harness" $uiHarness '--authorization-lease-smoke[\s\S]*AuthorizationLeaseWpfSmoke\.RunAsync'
Require-Pattern "authorization lease dynamic smoke is restricted to an empty QA data root" $uiHarness 'restrictedSeed\s*=\s*physicalPrinterQa[\s\S]{0,300}--authorization-lease-smoke[\s\S]{0,700}EnsureSyntheticTrustedSessionSeedPath'
Require-Pattern "wrong PIN dynamic regression leaves the generation uncommitted" $authorizationSmoke 'LoginAsync\(username,\s*WrongPin\)[\s\S]{0,900}sync_generation_inactive'
Require-Pattern "epoch and generation changes are denied dynamically" $authorizationSmoke 'InvalidateAuthorizationState\(\)[\s\S]{0,900}sync_generation_changed[\s\S]{0,1500}qa-auth-generation-2[\s\S]{0,900}sync_generation_changed'
Require-Pattern "successful PIN primes a monotonic authorization high-water" $authorizationSmoke 'LoginAsync\(username,\s*CorrectPin\)[\s\S]{0,1400}successful PIN did not prime[\s\S]{0,3200}clock_rollback'
Require-Pattern "cancelled operator switch rejects durable authority changes dynamically" $authorizationSmoke 'IsSessionBoundToCurrentTrustedIdentityAsync[\s\S]{0,1400}users\.UpdateAsync[\s\S]{0,900}durable authority change left the cached operator session bound[\s\S]{0,900}durableAuthorityChangeDenied=True'
Require-Pattern "authorization lease smoke has an explicit zero-hardware boundary" $authorizationSmoke 'hardwareEffects=0'
Require-Pattern "cancelled operator switch reloads and compares durable authority" $main 'IsSessionBoundToCurrentTrustedIdentityAsync[\s\S]{0,1400}GetByUsernameAsync[\s\S]{0,500}HasSameDurableAuthority'
Require-Pattern "durable authority comparison covers role, status, limits and permissions" $main 'HasSameDurableAuthority[\s\S]{0,1700}RoleId[\s\S]{0,500}RoleCode[\s\S]{0,500}IsActive[\s\S]{0,500}RequirePinChange[\s\S]{0,500}MaxDiscountPercent[\s\S]{0,500}CanOverride[\s\S]{0,500}SequenceEqual'
Require-Pattern "runtime guard is internal and cannot be composed ad hoc" $guard 'internal\s+sealed\s+class\s+PosOfflineAuthorizationLeaseGuard'
$syncEvaluateStart = $guard.IndexOf("public PosOfflineAuthorizationLeaseDecision Evaluate(out", [System.StringComparison]::Ordinal)
$preflightStart = $guard.IndexOf("public async Task<PosOfflineAuthorizationLeaseEvaluation> PreflightAsync()", [System.StringComparison]::Ordinal)
$commitStart = $guard.IndexOf("public async Task<PosOfflineAuthorizationLeaseEvaluation> CommitAuthenticationAsync", [System.StringComparison]::Ordinal)
$evaluationClassStart = $guard.IndexOf("internal sealed class PosOfflineAuthorizationLeaseEvaluation", [System.StringComparison]::Ordinal)
if ($syncEvaluateStart -lt 0 -or $preflightStart -le $syncEvaluateStart -or
    $commitStart -le $preflightStart -or $evaluationClassStart -le $commitStart) {
    Fail "authorization guard method boundaries are missing"
} else {
    $syncEvaluateBody = $guard.Substring($syncEvaluateStart, $preflightStart - $syncEvaluateStart)
    $preflightBody = $guard.Substring($preflightStart, $commitStart - $preflightStart)
    $commitBody = $guard.Substring($commitStart, $evaluationClassStart - $commitStart)
    $syncCapture = $syncEvaluateBody.IndexOf("TryCaptureAuthorizationEpoch", [System.StringComparison]::Ordinal)
    $syncStore = $syncEvaluateBody.IndexOf("_store.TryRead", [System.StringComparison]::Ordinal)
    $preflightCapture = $preflightBody.IndexOf("TryCaptureAuthorizationEpoch", [System.StringComparison]::Ordinal)
    $preflightStore = $preflightBody.IndexOf("_store.TryRead", [System.StringComparison]::Ordinal)
    $preflightDurable = $preflightBody.IndexOf("await _generationIsActive", [System.StringComparison]::Ordinal)
    $preflightLock = $preflightBody.IndexOf("lock (_sync)", $preflightDurable, [System.StringComparison]::Ordinal)
    $preflightEpoch = $preflightBody.IndexOf("IsAuthorizationEpochCurrent", $preflightLock, [System.StringComparison]::Ordinal)
    $preflightReread = $preflightBody.IndexOf("_store.TryReadGeneration", $preflightLock, [System.StringComparison]::Ordinal)
    $commitDurable = $commitBody.IndexOf("await _generationIsActive", [System.StringComparison]::Ordinal)
    $commitLock = $commitBody.IndexOf("lock (_sync)", $commitDurable, [System.StringComparison]::Ordinal)
    $commitReread = $commitBody.IndexOf("_store.TryReadGeneration", $commitLock, [System.StringComparison]::Ordinal)
    $epochAssignment = $commitBody.IndexOf("_validatedAuthorizationEpoch = first.Token.AuthorizationEpoch", [System.StringComparison]::Ordinal)
    $fingerprintAssignment = $commitBody.IndexOf("_validatedGenerationFingerprint = generation.Fingerprint", [System.StringComparison]::Ordinal)
    $finalEpoch = $commitBody.LastIndexOf("IsAuthorizationEpochCurrent", [System.StringComparison]::Ordinal)
    if ($syncCapture -lt 0 -or $syncStore -le $syncCapture -or
        $syncEvaluateBody -notmatch '_validatedAuthorizationEpoch\s*==\s*authorizationEpoch' -or
        $syncEvaluateBody -notmatch '_validatedGenerationFingerprint[\s\S]{0,180}generation\.Fingerprint' -or
        $syncEvaluateBody -notmatch 'IsAuthorizationEpochCurrent' -or
        $preflightCapture -lt 0 -or $preflightStore -le $preflightCapture -or
        $preflightDurable -le $preflightStore -or $preflightLock -le $preflightDurable -or
        $preflightEpoch -le $preflightLock -or $preflightReread -le $preflightEpoch -or
        $preflightBody -match '_validatedAuthorizationEpoch\s*=\s*authorizationEpoch' -or
        $preflightBody -match '_validatedGenerationFingerprint\s*=\s*generation\.Fingerprint' -or
        $commitDurable -lt 0 -or $commitLock -le $commitDurable -or
        $commitReread -le $commitLock -or $epochAssignment -le $commitReread -or
        $fingerprintAssignment -le $commitReread -or
        $finalEpoch -le $epochAssignment -or
        $commitBody -notmatch 'first\.Token\.AuthorizationEpoch\s*==\s*second\.Token\.AuthorizationEpoch' -or
        $commitBody -notmatch 'first\.Token\.GenerationFingerprint[\s\S]{0,180}second\.Token\.GenerationFingerprint') {
        Fail "runtime guard must keep preflight non-mutating and commit only a token-matched epoch plus generation"
    } else {
        Pass "runtime guard uses non-mutating preflight and atomic token-matched commit"
    }
}

if ($revocationLatch -notmatch 'TryCaptureAuthorizationEpoch[\s\S]{0,260}_authorizationMaintenanceDepth\s*==\s*0' -or
    $revocationLatch -notmatch 'IsAuthorizationEpochCurrent[\s\S]{0,300}_authorizationMaintenanceDepth\s*==\s*0[\s\S]{0,100}_authorizationEpoch\s*==\s*epoch' -or
    $revocationLatch -notmatch 'TryInvalidateAuthorizationState\(long expectedEpoch\)[\s\S]{0,420}_authorizationMaintenanceDepth\s*>\s*0[\s\S]{0,120}_authorizationEpoch\s*!=\s*expectedEpoch[\s\S]{0,140}_authorizationEpoch\+\+' -or
    $revocationLatch -notmatch 'InvalidateAuthorizationState[\s\S]{0,180}_authorizationEpoch\+\+' -or
    $revocationLatch -notmatch 'Revoke\(OnlineSyncGeneration generation\)[\s\S]{0,120}RevokeFingerprint\(generation\?\.Fingerprint\)' -or
    $revocationLatch -notmatch 'RevokeFingerprint\(string fingerprint\)[\s\S]{0,220}_authorizationEpoch\+\+[\s\S]{0,220}RevokedFingerprints\.Add') {
    Fail "authorization epoch latch is not maintenance-aware and monotonic"
} else {
    Pass "authorization epoch latch is maintenance-aware and monotonic"
}

$guardConstructionFiles = @(Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "new PosOfflineAuthorizationLeaseGuard\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') -replace '\\', '/' })
if ($guardConstructionFiles.Count -eq 1 -and
    $guardConstructionFiles[0] -eq "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs") {
    Pass "operator session is the single runtime guard composition point"
}
else {
    Fail "unexpected runtime guard composition point(s): $($guardConstructionFiles -join ', ')"
}

$leaseIndex = $session.IndexOf("await _authorizationLeaseGuard.PreflightAsync()", [System.StringComparison]::Ordinal)
$pinIndex = $session.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
if ($leaseIndex -ge 0 -and $pinIndex -gt $leaseIndex) {
    Pass "operator login checks lease before local PIN verification"
}
else {
    Fail "operator login must check lease before local PIN verification"
}

$loginStart = $session.IndexOf("private async Task<LoginResult> LoginInternalAsync", [System.StringComparison]::Ordinal)
$loginEnd = $session.IndexOf("public PosOfflineAuthorizationLeaseDecision EvaluateAuthorizationLease()", [System.StringComparison]::Ordinal)
if ($loginStart -lt 0 -or $loginEnd -le $loginStart) {
    Fail "operator login method boundaries are missing"
} else {
    $loginBody = $session.Substring($loginStart, $loginEnd - $loginStart)
    $initialLease = $loginBody.IndexOf("initialEvaluation = await _authorizationLeaseGuard.PreflightAsync()", [System.StringComparison]::Ordinal)
    $pinVerify = $loginBody.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
    $finalLease = $loginBody.IndexOf("var finalEvaluation = await _authorizationLeaseGuard", [System.StringComparison]::Ordinal)
    $leaseCommit = $loginBody.IndexOf(".CommitAuthenticationAsync(initialEvaluation, finalEvaluation)", [System.StringComparison]::Ordinal)
    $sameGeneration = $loginBody.IndexOf("IsSameTrustedGeneration", [System.StringComparison]::Ordinal)
    $commitUser = $loginBody.IndexOf("_currentUser = result.User", [System.StringComparison]::Ordinal)
    if ($initialLease -lt 0 -or $pinVerify -le $initialLease -or
        $finalLease -le $pinVerify -or $leaseCommit -le $finalLease -or
        $sameGeneration -le $leaseCommit -or
        $commitUser -le $sameGeneration) {
        Fail "operator login must revalidate and atomically commit the exact trusted generation after PIN"
    } else {
        Pass "operator login revalidates and atomically commits the exact trusted generation after PIN"
    }
}

Require-Pattern "normal operator login requires lease and no local-only classification" $session 'LoginAsync[\s\S]{0,500}requireAuthorizationLease:\s*true[\s\S]{0,180}requireLocalRecoveryUser:\s*false'
Require-Pattern "local recovery login bypasses only lease and requires local identity" $session 'LoginLocalRecoveryAsync[\s\S]{0,500}requireAuthorizationLease:\s*false[\s\S]{0,180}requireLocalRecoveryUser:\s*true'
Require-Pattern "normal operator login resolves the exact lease-bound remote mirror" $session '_authorizationLeaseGuard\.PreflightAsync\(\)[\s\S]{0,500}trustedSession\s*=\s*initialEvaluation\.TrustedSession[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync\([\s\S]{0,500}trustedSession\.StaffCredentialVersion'
Require-Pattern "normal operator login rejects a different username before PIN verification" $session 'string\.Equals\(username,\s*trustedUsername,\s*StringComparison\.Ordinal\)[\s\S]{0,450}return\s+LoginResult\.Failed[\s\S]{0,900}VerifyPinAsync\(username,\s*pin\)'
Require-Pattern "local recovery identity is checked before PIN" $session 'IsLocalRecoveryUserAsync\(username\)[\s\S]{0,700}VerifyPinAsync\(username,\s*pin\)'

Require-Pattern "permission service checks lease before cached permissions" $permission 'EnsureAuthorizationValid\(\)[\s\S]*CurrentUser'
Require-Pattern "recovery permission service uses only explicit recovery policy" $recoveryPermission 'LocalRecoveryPermissionPolicy\.IsGranted'
Require-Pattern "override verifies active lease before authorizer PIN" $override 'EnsureAuthorizationValid\(\)[\s\S]*VerifyPinAsync'
Require-Pattern "override resolves only the lease-bound remote identity" $override 'ResolveLeaseBoundAuthorizerAsync[\s\S]{0,5000}FindTrustedRemoteStaffUsernameAsync'
Require-Pattern "override rechecks lease binding after PIN verification" $override 'VerifyPinAsync[\s\S]{0,900}ResolveLeaseBoundAuthorizerAsync'
if ($override -match 'ListUsersWithPermissionAsync' -or
    $override -match 'ListAdminUsersAsync') {
    Fail "override must not enumerate local recovery or stale mirror identities"
}
else {
    Pass "override has no local/stale identity enumeration"
}
Require-Pattern "POS access exposes a distinct lease-expired result" $accessDialog 'LoginResult\.AuthorizationExpired[\s\S]*access\.login\.authorizationExpired'
$denialIndex = $accessDialog.IndexOf("IsAuthorizationDenied(result)", [System.StringComparison]::Ordinal)
$denialReturnIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("return;", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$nextFallbackIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("if (IsOfflineFallbackAllowed(result.Code))", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$rejectStart = $syncHost.IndexOf("internal async Task<bool> RejectAuthenticatedTrustTransitionAsync", [System.StringComparison]::Ordinal)
$publicRevokeStart = $syncHost.IndexOf("public async Task RevokeCurrentTrustAsync", [System.StringComparison]::Ordinal)
$rejectBody = if ($rejectStart -ge 0 -and $publicRevokeStart -gt $rejectStart) {
    $syncHost.Substring($rejectStart, $publicRevokeStart - $rejectStart)
} else { "" }
$firstAttemptCheck = $rejectBody.IndexOf("transition.AttemptId", [System.StringComparison]::Ordinal)
$stateRead = $rejectBody.IndexOf("ReadCurrentPredecessorAsync", [System.StringComparison]::Ordinal)
$secondAttemptCheck = if ($stateRead -ge 0) {
    $rejectBody.IndexOf("transition.AttemptId", $stateRead, [System.StringComparison]::Ordinal)
} else { -1 }
$predecessorCheck = $rejectBody.IndexOf("PredecessorStatesMatch", [System.StringComparison]::Ordinal)
$scopedRevoke = $rejectBody.IndexOf("RevokeCurrentTrustCoreAsync", [System.StringComparison]::Ordinal)
if ($denialIndex -ge 0 -and $denialReturnIndex -gt $denialIndex -and
    $nextFallbackIndex -gt $denialReturnIndex -and
    $accessDialog -notmatch '\.RevokeCurrentTrustAsync\(' -and
    $bootstrap -match 'result\.Denied\s*\|\|[\s\S]{0,180}SharedAuthStopPolicy\.IsAuthenticationDenied\(result\.Code\)[\s\S]{0,500}RejectAuthenticatedTrustTransitionAsync\(\s*authenticatedTransition,\s*"auth_denied"' -and
    $firstAttemptCheck -ge 0 -and $stateRead -gt $firstAttemptCheck -and
    $secondAttemptCheck -gt $stateRead -and $predecessorCheck -gt $secondAttemptCheck -and
    $scopedRevoke -gt $predecessorCheck -and
    $rejectBody -match 'IsAuthorizationEpochCurrent' -and
    $rejectBody -match 'PosOnlineSyncSignalBus\.IsMaintenanceActive') {
    Pass "explicit online denial is transition-scoped and never enters fallback"
}
else {
    Fail "explicit online denial must be scoped to its attempt and return before fallback"
}
Require-Pattern "operator switch cannot bypass an expired lease" $operatorSwitch 'LoginResult\.AuthorizationExpired[\s\S]*authorization_lease_denied'
Require-Pattern "trusted mirror lookup binds opaque shop and staff ids" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,1800}remote_shop_id[\s\S]{0,500}remote_staff_id'
Require-Pattern "trusted mirror lookup binds the credential version" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,2200}remote_credential_version[\s\S]{0,300}staffCredentialVersion'
Require-Pattern "offline login binds requested staff to the trusted lease" $accessDialog 'staff_identity_mismatch[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync'
Require-Pattern "online completion resolves the lease-bound mirror" $accessDialog 'CompleteOnlineSignInAsync[\s\S]{0,700}FindLeaseBoundRemoteStaffUsernameAsync'
$unsafeCatalogBranch = [regex]::Match(
    $accessDialog,
    'if\s*\(result\.Success\s*&&\s*!result\.CanOpenPos\)[\s\S]*?(?=\r?\n\s*var\s+failureKind)').Value
if ($unsafeCatalogBranch -match 'FindLeaseBoundRemoteStaffUsernameAsync' -and
    $unsafeCatalogBranch -notmatch 'LoginLocalUsernameAsync|LoginRemoteMirrorForRecoveryAsync|AccessMode\s*=|DialogResult\s*=') {
    Pass "unsafe catalog prepares recovery without authenticating or committing access"
}
else {
    Fail "unsafe catalog must not authenticate or commit before explicit recovery acceptance"
}
Require-Pattern "explicit remote recovery re-challenge uses normal lease-bound login" $accessDialog 'RunRemoteRecoveryLoginAsync[\s\S]{0,1800}LoginRemoteMirrorForRecoveryAsync[\s\S]{0,1800}AccessMode\s*=\s*PosAuthenticatedAccessMode\.Normal'
if ($accessDialog -match 'OperatorSessionHolder\.Current\s*=') {
    Fail "access dialog must not replace the shared operator session"
}
else {
    Pass "access dialog preserves the shared operator session object"
}
$catalogRetryMethod = [regex]::Match(
    $accessDialog,
    'private\s+async\s+Task\s+RunCatalogRetryAsync\(\)[\s\S]*?(?=\r?\n\s*private\s+)').Value
if ($catalogRetryMethod -match 'OperatorSessionHolder\.Current[\s\S]{0,120}IsLoggedIn') {
    Fail "catalog retry must not accept an unrelated already-logged-in operator"
}
else {
    Pass "catalog retry requires the pending shop/staff credential path"
}
Require-Pattern "operator switch resolves only the staff bound to the trusted lease" $operatorSwitch 'PosTrustedDeviceStore[\s\S]{0,2200}FindTrustedRemoteStaffUsernameAsync'
if ($operatorSwitch -match 'GetByUsernameAsync\(normalized\)' -or
    $operatorSwitch -match 'FindRemoteStaffUsernameAsync\(_shopCode,\s*normalized\)') {
    Fail "operator switch must not fall back to local or stale same-shop identities"
}
else {
    Pass "operator switch has no local/stale mirror fallback"
}
Require-Pattern "active window schedules exact lease expiry" $main 'RefreshAuthorizationLeaseSchedule[\s\S]*_authorizationLeaseTimer\.Interval\s*=\s*remaining'

$paymentReturnIndex = $posViewModel.IndexOf("if (!ok)", [System.StringComparison]::Ordinal)
$commitDemandIndex = $posViewModel.IndexOf("_permissionService.Demand(PermissionCodes.PosPay", $paymentReturnIndex, [System.StringComparison]::Ordinal)
$completeSaleIndex = $posViewModel.IndexOf("_service.CompleteSaleAsync", [System.StringComparison]::Ordinal)
if ($paymentReturnIndex -ge 0 -and $commitDemandIndex -gt $paymentReturnIndex -and $completeSaleIndex -gt $commitDemandIndex) {
    Pass "payment revalidates authorization immediately before sale commit"
}
else {
    Fail "payment must revalidate authorization immediately before sale commit"
}

Require-Pattern "sales contract emits clientOriginalLineId additively" $contracts 'DataMember\(Name\s*=\s*"clientOriginalLineId",\s*EmitDefaultValue\s*=\s*false\)'
Require-Pattern "new reversal payload binds original line" $salesBuilder 'reversal_original_line_missing[\s\S]*ClientOriginalLineId\s*=\s*isReversal'
Require-Pattern "legacy reversal payload is blocked after CAS claim and before network" $salesSync 'PrepareSalesSyncAttemptAsync[\s\S]*HasCompleteReversalBindings\(request\)[\s\S]*MarkBlockedAsync\(item,\s*"reversal_original_line_missing"[\s\S]*SalesSyncAsync'

$directPinCallFiles = Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "\.VerifyPinAsync\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') -replace '\\', '/' } |
    Sort-Object
$allowedPinCallFiles = @(
    "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs",
    "src/Win7POS.Wpf/Infrastructure/Security/OverrideAuthService.cs"
) | Sort-Object
if (($directPinCallFiles -join "|") -eq ($allowedPinCallFiles -join "|")) {
    Pass "raw local PIN verification has only guarded call sites"
}
else {
    Fail "unexpected raw VerifyPinAsync call site(s): $($directPinCallFiles -join ', ')"
}

if (($guard + $policy) -match '(?i)(outbox|catalog|mirror).*?(delete|clear|remove)') {
    Fail "authorization guard/policy must not delete outbox, catalog or mirror state"
}
else {
    Pass "authorization denial does not delete outbox/catalog/mirror state"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
