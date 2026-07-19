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

Require-Pattern "offline lease maximum is the 12-hour POS session contract" $contract 'OfflineAuthorizationMaxAgeSeconds\s*=\s*12\s*\*\s*60\s*\*\s*60'
Require-Pattern "policy fails closed on missing legacy receipt timestamp" $policy 'local_receipt_time_invalid'
Require-Pattern "policy rejects rollback and exact expiry" $policy 'clock_rollback[\s\S]*estimatedServerNow\s*>=\s*effectiveExpiry'
Require-Pattern "first-login DTO consumes authenticated serverTime" $contracts 'class\s+PosFirstLoginResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "heartbeat DTO consumes authenticated serverTime" $contracts 'class\s+PosHeartbeatResponse[\s\S]*DataMember\(Name\s*=\s*"serverTime"\)'
Require-Pattern "trusted store persists server and local receipt clocks" $store 'LastOkLocalAt\s*=\s*candidate\.LastOkLocalAt[\s\S]*LastOkServerAt\s*=\s*candidate\.LastOkServerAt'
Require-Pattern "runtime guard keeps a process high-water" $guard '_estimatedServerHighWater[\s\S]*minimumEstimatedServerNow|_estimatedServerHighWater[\s\S]*PosOfflineAuthorizationLeasePolicy\.Evaluate'
Require-Pattern "runtime guard is internal and cannot be composed ad hoc" $guard 'internal\s+sealed\s+class\s+PosOfflineAuthorizationLeaseGuard'
Require-Pattern "runtime guard returns only the lease session evaluated under its lock" $guard 'Evaluate\(out\s+PosTrustedDeviceSession\s+trustedSession\)[\s\S]{0,1200}lock\s*\(_sync\)[\s\S]{0,1200}if\s*\(!decision\.Allowed\)[\s\S]{0,300}trustedSession\s*=\s*session'

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

$leaseIndex = $session.IndexOf("var authorization = EvaluateAuthorizationLease(out trustedSession);", [System.StringComparison]::Ordinal)
$pinIndex = $session.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
if ($leaseIndex -ge 0 -and $pinIndex -gt $leaseIndex) {
    Pass "operator login checks lease before local PIN verification"
}
else {
    Fail "operator login must check lease before local PIN verification"
}

Require-Pattern "normal operator login requires lease and no local-only classification" $session 'LoginAsync[\s\S]{0,500}requireAuthorizationLease:\s*true[\s\S]{0,180}requireLocalRecoveryUser:\s*false'
Require-Pattern "local recovery login bypasses only lease and requires local identity" $session 'LoginLocalRecoveryAsync[\s\S]{0,500}requireAuthorizationLease:\s*false[\s\S]{0,180}requireLocalRecoveryUser:\s*true'
Require-Pattern "normal operator login resolves the exact lease-bound remote mirror" $session 'EvaluateAuthorizationLease\(out\s+trustedSession\)[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync\([\s\S]{0,500}trustedSession\.StaffCredentialVersion'
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
$denialClearIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("_trustedDeviceStore.Clear();", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$denialReturnIndex = if ($denialClearIndex -ge 0) {
    $accessDialog.IndexOf("return;", $denialClearIndex, [System.StringComparison]::Ordinal)
} else { -1 }
$nextFallbackIndex = if ($denialIndex -ge 0) {
    $accessDialog.IndexOf("if (IsOfflineFallbackAllowed(result.Code))", $denialIndex, [System.StringComparison]::Ordinal)
} else { -1 }
if ($denialIndex -ge 0 -and $denialClearIndex -gt $denialIndex -and
    $denialReturnIndex -gt $denialClearIndex -and $nextFallbackIndex -gt $denialReturnIndex) {
    Pass "explicit online denial clears trust and never enters fallback"
}
else {
    Fail "explicit online denial must clear trust and return before fallback"
}
Require-Pattern "operator switch cannot bypass an expired lease" $operatorSwitch 'LoginResult\.AuthorizationExpired[\s\S]*authorization_lease_denied'
Require-Pattern "trusted mirror lookup binds opaque shop and staff ids" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,1800}remote_shop_id[\s\S]{0,500}remote_staff_id'
Require-Pattern "trusted mirror lookup binds the credential version" $userRepo 'FindTrustedRemoteStaffUsernameAsync[\s\S]{0,2200}remote_credential_version[\s\S]{0,300}staffCredentialVersion'
Require-Pattern "offline login binds requested staff to the trusted lease" $accessDialog 'staff_identity_mismatch[\s\S]{0,900}FindTrustedRemoteStaffUsernameAsync'
Require-Pattern "online completion and authenticated recovery resolve the lease-bound mirror" $accessDialog 'CompleteOnlineSignInAsync[\s\S]{0,700}FindLeaseBoundRemoteStaffUsernameAsync[\s\S]*LoginRemoteMirrorForRecoveryAsync[\s\S]{0,700}FindLeaseBoundRemoteStaffUsernameAsync'
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
$commitDemandIndex = $posViewModel.IndexOf("_permissionService?.Demand(PermissionCodes.PosPay", $paymentReturnIndex, [System.StringComparison]::Ordinal)
$completeSaleIndex = $posViewModel.IndexOf("_service.CompleteSaleAsync", [System.StringComparison]::Ordinal)
if ($paymentReturnIndex -ge 0 -and $commitDemandIndex -gt $paymentReturnIndex -and $completeSaleIndex -gt $commitDemandIndex) {
    Pass "payment revalidates authorization immediately before sale commit"
}
else {
    Fail "payment must revalidate authorization immediately before sale commit"
}

Require-Pattern "sales contract emits clientOriginalLineId additively" $contracts 'DataMember\(Name\s*=\s*"clientOriginalLineId",\s*EmitDefaultValue\s*=\s*false\)'
Require-Pattern "new reversal payload binds original line" $salesBuilder 'reversal_original_line_missing[\s\S]*ClientOriginalLineId\s*=\s*isReversal'
Require-Pattern "legacy reversal payload is blocked before network" $salesSync 'HasCompleteReversalBindings\(request\)[\s\S]*MarkPreflightBlockedAsync\(item,\s*"reversal_original_line_missing"'

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
