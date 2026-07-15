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
$permission = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/PermissionService.cs"
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

$guardConstructionFiles = @(Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf") -Recurse -File -Filter "*.cs" |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    Where-Object { Select-String -Path $_.FullName -Pattern "new PosOfflineAuthorizationLeaseGuard\(" -Quiet } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') })
if ($guardConstructionFiles.Count -eq 1 -and
    $guardConstructionFiles[0] -eq "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs") {
    Pass "operator session is the single runtime guard composition point"
}
else {
    Fail "unexpected runtime guard composition point(s): $($guardConstructionFiles -join ', ')"
}

$leaseIndex = $session.IndexOf("var authorization = EvaluateAuthorizationLease();", [System.StringComparison]::Ordinal)
$pinIndex = $session.IndexOf("_userRepo.VerifyPinAsync", [System.StringComparison]::Ordinal)
if ($leaseIndex -ge 0 -and $pinIndex -gt $leaseIndex) {
    Pass "operator login checks lease before local PIN verification"
}
else {
    Fail "operator login must check lease before local PIN verification"
}

Require-Pattern "permission service checks lease before cached permissions" $permission 'EnsureAuthorizationValid\(\)[\s\S]*CurrentUser'
Require-Pattern "override verifies active lease before authorizer PIN" $override 'EnsureAuthorizationValid\(\)[\s\S]*VerifyPinAsync'
Require-Pattern "POS access exposes a distinct lease-expired result" $accessDialog 'LoginResult\.AuthorizationExpired[\s\S]*access\.login\.authorizationExpired'
$denialIndex = $accessDialog.IndexOf("if (IsAuthorizationDenied(result))", [System.StringComparison]::Ordinal)
$denialClearIndex = $accessDialog.IndexOf("_trustedDeviceStore.Clear();", $denialIndex, [System.StringComparison]::Ordinal)
$denialReturnIndex = $accessDialog.IndexOf("return;", $denialClearIndex, [System.StringComparison]::Ordinal)
$nextFallbackIndex = $accessDialog.IndexOf("if (IsOfflineFallbackAllowed(result.Code))", $denialIndex, [System.StringComparison]::Ordinal)
if ($denialIndex -ge 0 -and $denialClearIndex -gt $denialIndex -and
    $denialReturnIndex -gt $denialClearIndex -and $nextFallbackIndex -gt $denialReturnIndex) {
    Pass "explicit online denial clears trust and never enters fallback"
}
else {
    Fail "explicit online denial must clear trust and return before fallback"
}
Require-Pattern "operator switch cannot bypass an expired lease" $operatorSwitch 'LoginResult\.AuthorizationExpired[\s\S]*authorization_lease_denied'
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
    ForEach-Object { $_.FullName.Substring($repoRoot.Length).TrimStart('/', '\') } |
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
