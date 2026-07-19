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
        Fail "missing file: $relativePath"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Assert-Contains([string]$label, [string]$text, [string]$needle) {
    if ($text.Contains($needle)) {
        Pass $label
    }
    else {
        Fail "$label missing: $needle"
    }
}

$srcRoot = Join-Path $repoRoot "src"
$codeFiles = Get-ChildItem -Path $srcRoot -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

$operatorNewHits = $codeFiles | Select-String -Pattern 'new\s+OperatorLoginDialog\b'
if ($operatorNewHits) {
    $operatorNewHits | ForEach-Object {
        Write-Host ("  {0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim())
    }
    Fail "new OperatorLoginDialog must not be used"
}
else {
    Pass "no new OperatorLoginDialog in src"
}

if (Test-Path (Join-Path $repoRoot "src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml")) {
    Fail "OperatorLoginDialog.xaml should be removed"
}
else {
    Pass "OperatorLoginDialog.xaml removed"
}

if (Test-Path (Join-Path $repoRoot "src/Win7POS.Wpf/Pos/Dialogs/OperatorLoginDialog.xaml.cs")) {
    Fail "OperatorLoginDialog.xaml.cs should be removed"
}
else {
    Pass "OperatorLoginDialog.xaml.cs removed"
}

$main = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$loadedMatch = [regex]::Match(
    $main,
    'private\s+async\s+void\s+OnLoadedAsync[\s\S]*?(?=\r?\n\s*private\s+async\s+Task<StartOfDaySyncResult>)')
if (-not $loadedMatch.Success) {
    Fail "MainWindow.OnLoadedAsync body not found"
}
else {
    $loaded = $loadedMatch.Value
    $loginDialogCount = [regex]::Matches($loaded, 'new\s+PosOnlineFirstLoginDialog\b').Count
    if ($loginDialogCount -eq 1) {
        Pass "startup opens one unified access dialog"
    }
    else {
        Fail "startup should open exactly one PosOnlineFirstLoginDialog, found $loginDialogCount"
    }

    if ($loaded -match 'OperatorLoginDialog|FirstRunSetupDialog|TryOnlineBootstrapFirstRunAsync') {
        Fail "startup still contains legacy login/setup dialog flow"
    }
    else {
        Pass "startup has no legacy login/setup dialog flow"
    }

    if ($loaded -match 'OperatorSwitchDialog') {
        Fail "startup must not open OperatorSwitchDialog"
    }
    else {
        Pass "startup does not open OperatorSwitchDialog"
    }

    if ($loaded -match 'RunStartOfDaySyncAsync') {
        Pass "startup keeps start-of-day sync after access"
    }
    else {
        Fail "startup does not run start-of-day sync after access"
    }
}

$translations = ""
Get-ChildItem -Path (Join-Path $repoRoot "src/Win7POS.Wpf/Localization") -Recurse -File -Filter *.cs |
    ForEach-Object { $translations += [System.IO.File]::ReadAllText($_.FullName) + "`n" }

$requiredKeys = @(
    "access.login.title",
    "access.login.shopCode",
    "access.login.staffCode",
    "access.login.credential",
    "access.login.deviceName",
    "access.login.networkOnline",
    "access.login.networkOffline",
    "access.login.offlineNoticeWifiUnavailable",
    "access.login.offlineNoticeServerUnavailable",
    "access.login.offlineMirrorMissing",
    "access.login.onlineDeniedNoOfflineFallback",
    "access.login.onlineDeniedLocalRecoveryAvailable",
    "access.login.shopSwitchBlockedOutbox",
    "access.login.recoveryOnlineRequired",
    "access.login.invalidCredentials",
    "access.login.signIn",
    "access.login.advancedSettings"
)

foreach ($key in $requiredKeys) {
    if ($translations -match [regex]::Escape('TranslationEntry("' + $key + '"')) {
        Pass "localization key $key"
    }
    else {
        Fail "missing localization key $key"
    }
}

$networkPath = "src/Win7POS.Wpf/Infrastructure/NetworkStatusService.cs"
$network = Read-Text $networkPath
Assert-Contains "NetworkStatusService exists" $network "public static class NetworkStatusService"
Assert-Contains "NetworkStatusService uses NetworkInterface" $network "NetworkInterface.GetAllNetworkInterfaces"
Assert-Contains "NetworkStatusService uses Win7-safe availability" $network "NetworkInterface.GetIsNetworkAvailable"

$dialogXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml"
Assert-Contains "dialog has ShopCodeBox" $dialogXaml 'x:Name="ShopCodeBox"'
Assert-Contains "dialog has StaffCodeBox" $dialogXaml 'x:Name="StaffCodeBox"'
Assert-Contains "dialog has CredentialBox" $dialogXaml 'x:Name="CredentialBox"'
Assert-Contains "dialog has network badge" $dialogXaml 'x:Name="NetworkStatusBadge"'
Assert-Contains "dialog has network status text" $dialogXaml 'x:Name="NetworkStatusText"'

$dialogCode = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/PosOnlineFirstLoginDialog.xaml.cs"
Assert-Contains "dialog attempts offline fallback" $dialogCode "TryOfflineSignInAsync"
Assert-Contains "dialog blocks denied fallback" $dialogCode "onlineDeniedNoOfflineFallback"
Assert-Contains "dialog explains existing local recovery after online denial" $dialogCode "onlineDeniedLocalRecoveryAvailable"
Assert-Contains "dialog explains blocked shop switch with unresolved sales" $dialogCode "shopSwitchBlockedOutbox"
Assert-Contains "dialog localizes the exact blocked shop-switch result" $dialogCode 'ShowError(LocalizeOnlineBootstrapFailure(result.Code, result.Message))'
Assert-Contains "dialog logs in session before credential clear path" $dialogCode "LoginLocalUsernameAsync"
Assert-Contains "local recovery uses its dedicated credential verifier" $dialogCode "LoginLocalRecoveryAsync"
Assert-Contains "full offline sign-in verifies the trusted shop binding" $dialogCode "IsOfflineShopAuthorizedAsync"
Assert-Contains "local recovery always remains restricted" $dialogCode "AccessMode = PosAuthenticatedAccessMode.LocalRecovery"
Assert-Contains "dialog exposes explicit recovery action" $dialogXaml 'x:Name="RecoveryButton"'
Assert-Contains "dialog keeps recovery inside unified access" $dialogCode "OnRecoveryClick"
Assert-Contains "dialog reaches first-run child only from recovery" $dialogCode "new FirstRunSetupDialog(_factory)"

$recoveryPolicy = Read-Text "src/Win7POS.Core/Security/PosAccessRecoveryPolicy.cs"
$recoveryPermissions = Read-Text "src/Win7POS.Core/Security/LocalRecoveryPermissionPolicy.cs"
$recoveryPermissionService = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/LocalRecoveryPermissionService.cs"
$operatorSession = Read-Text "src/Win7POS.Wpf/Infrastructure/Security/OperatorSession.cs"
$userRepository = Read-Text "src/Win7POS.Data/Repositories/UserRepository.cs"
$dbMaintenance = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs"
$firstRunSetup = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/FirstRunSetupDialog.xaml.cs"
Assert-Contains "recovery policy classifies online denial" $recoveryPolicy "IsDenied(failureKind)"
Assert-Contains "recovery policy returns denied state" $recoveryPolicy "PosAccessNextStep.Denied"
Assert-Contains "online denial preserves only existing local recovery" $recoveryPolicy 'failureKind == PosAccessFailureKind.AuthenticationDenied &&'
Assert-Contains "local recovery has an explicit trusted count" $recoveryPolicy "ActiveLocalRecoveryUsers"
Assert-Contains "local recovery mode cannot elevate itself to POS" $recoveryPolicy 'accessMode == PosAuthenticatedAccessMode.LocalRecovery'
if ($dialogCode.Contains("PosLocalRecoveryElevationPolicy") -or
    $recoveryPolicy.Contains("PosLocalRecoveryElevationPolicy")) {
    Fail "local recovery must never promote itself to normal POS access"
} else {
    Pass "local recovery cannot promote itself to normal POS access"
}
Assert-Contains "local recovery identities are classified explicitly" $userRepository "IsLocalRecoveryUserAsync"
if ($userRepository -match 'IsLocalRecoveryUserAsync[\s\S]{0,900}remote_staff_id[\s\S]{0,300}remote_staff_code[\s\S]{0,300}remote_shop_id[\s\S]{0,300}remote_shop_code') {
    Pass "local recovery identity requires every remote binding field to be empty"
} else {
    Fail "local recovery classification must reject every partially linked remote identity"
}
Assert-Contains "local recovery RBAC uses an explicit allowlist" $recoveryPermissions "public static bool IsAllowed"
Assert-Contains "local recovery RBAC denies security override by omission" $recoveryPermissions "PermissionCodes.DbRestore"
if ($recoveryPermissions.Contains("PermissionCodes.SecurityOverride")) {
    Fail "local recovery allowlist must not include security override"
} else {
    Pass "local recovery allowlist excludes security override"
}
Assert-Contains "Products recovery uses a lease-free restricted permission service" $recoveryPermissionService "LocalRecoveryPermissionPolicy.IsGranted"
Assert-Contains "recovery DB backup rechecks its granular permission" $dbMaintenance "_hasBackupPermission"
Assert-Contains "recovery catalog import rechecks its granular permission" $dbMaintenance "_hasCatalogImportPermission"
if ($recoveryPermissionService -match 'public\s+bool\s+CanOverride\([^)]*\)\s*\{\s*return\s+false;\s*\}') {
    Pass "local recovery cannot perform permission override"
} else {
    Fail "local recovery permission override must always be disabled"
}
if ($operatorSession -match 'LoginLocalRecoveryAsync[\s\S]{0,500}requireAuthorizationLease:\s*false[\s\S]{0,160}requireLocalRecoveryUser:\s*true') {
    Pass "dedicated local recovery login has the narrow lease bypass"
} else {
    Fail "dedicated local recovery login must bypass only the lease and require a local identity"
}
if ($operatorSession -match 'IsLocalRecoveryUserAsync\(username\)[\s\S]{0,700}VerifyPinAsync\(username,\s*pin\)') {
    Pass "local identity classification precedes PIN verification"
} else {
    Fail "local recovery must classify the identity before PIN verification"
}
Assert-Contains "first-run recovery authenticates with the dedicated recovery verifier" $firstRunSetup "LoginLocalRecoveryAsync"
if ($dialogCode -match 'onlineDeniedNoOfflineFallback[\s\S]{0,500}CanCreateLocalAdmin') {
    Fail "online denied path must not expose local admin recovery"
} else {
    Pass "online denied path does not expose local admin recovery"
}

$uiSmoke = Read-Text "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
Assert-Contains "recovery access dialog has 20-cycle lifecycle coverage" $uiSmoke "recoveryAccessCycles=20"
Assert-Contains "recovery lifecycle opens the real access dialog" $uiSmoke "new PosOnlineFirstLoginDialog()"

Assert-Contains "shell evaluates catalog before PosView" $main "PosShellStartupPolicy.Determine"
Assert-Contains "shell has controlled recovery mode" $main "EnterRecoveryModeAsync"
Assert-Contains "local recovery explains that online access is required for POS" $main 'localRecoveryAccess ? "access.login.recoveryOnlineRequired"'
Assert-Contains "recovery shell suppresses authorization lease shutdown" $main 'if (_recoveryMode)'
Assert-Contains "recovery shell stops the normal sync status timer" $main '_syncStatusTimer?.Stop()'
if ($main -match 'TriggerAdaptiveOnlineRefreshAsync[\s\S]{0,450}_authenticatedAccessMode\s*==\s*PosAuthenticatedAccessMode\.LocalRecovery' -and
    $main -match 'ShowSyncCenterDialog\(Window owner = null\)[\s\S]{0,260}_authenticatedAccessMode\s*==\s*PosAuthenticatedAccessMode\.LocalRecovery') {
    Pass "recovery shell blocks manual and coordinated online sync"
} else {
    Fail "recovery shell must block every Sync Center execution path"
}
Assert-Contains "recovery shell uses restricted product permissions" $main "new LocalRecoveryPermissionService(session)"
Assert-Contains "recovery shell suspends an existing POS view without discarding its cart" $main "SuspendPosViewForRecovery"
if ($main -match 'OpenPosAccessForOperatorChangeAsync[\s\S]{0,2400}AccessMode\s*==\s*PosAuthenticatedAccessMode\.LocalRecovery[\s\S]{0,900}await\s+EnterRecoveryModeAsync\(factory\)') {
    Pass "operator change downgrades a local recovery login"
} else {
    Fail "operator change must enter recovery for LocalRecovery access"
}
if ($main -match 'MainTabControl_SelectionChanged[\s\S]{0,500}!IsRecoveryTab[\s\S]{0,300}ClampRecoveryTabSelection') {
    Pass "recovery shell clamps hidden tabs"
} else {
    Fail "recovery shell must clamp hidden tabs"
}
$permissionMethod = [regex]::Match(
    $main,
    'private\s+bool\s+HasCurrentPermission\(string\s+permissionCode\)[\s\S]*?(?=\r?\n\s*private\s+)').Value
$recoveryPermissionIndex = $permissionMethod.IndexOf(
    'LocalRecoveryPermissionPolicy.IsGranted(user, permissionCode)',
    [System.StringComparison]::Ordinal)
$normalLeaseIndex = $permissionMethod.IndexOf(
    'session.EnsureAuthorizationValid()',
    [System.StringComparison]::Ordinal)
if ($recoveryPermissionIndex -ge 0 -and $normalLeaseIndex -gt $recoveryPermissionIndex) {
    Pass "recovery permission branch bypasses the normal lease check"
} else {
    Fail "recovery permissions must be resolved before and without the normal lease check"
}
Assert-Contains "recovery blocks payment surface activation" $main "CancelActivePaymentForRecovery"
if ($main -match '!accessAccepted[\s\S]{0,300}AccessMode\s*==\s*PosAuthenticatedAccessMode\.LocalRecovery[\s\S]{0,600}EnterRecoveryModeAsync\(factory\)') {
    Pass "cancel after authenticated unsafe catalog still downgrades the shell"
} else {
    Fail "authenticated unsafe catalog must downgrade the shell even when access dialog is cancelled"
}
if ($main -match 'private\s+async\s+Task<bool>\s+ExitRecoveryModeAsync\(\)[\s\S]{0,500}HasNormalAuthorizedAccessForRecoveryExit\(\)[\s\S]{0,3500}_recoveryMode\s*=\s*false') {
    Pass "recovery exit validates normal authorized access before opening POS"
} else {
    Fail "recovery exit must validate normal authorized access before clearing recovery mode"
}
if ($main -notmatch 'if\s*\(shellMode\s*==\s*PosShellMode\.Pos\)[\s\S]{0,180}EnsurePosViewCreated') {
    Fail "PosView creation must remain inside the sale-safe POS branch"
} else {
    Pass "PosView creation remains inside the sale-safe POS branch"
}

$operatorSwitchXaml = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/OperatorSwitchDialog.xaml"
Assert-Contains "operator switch dialog exists" $operatorSwitchXaml 'x:Class="Win7POS.Wpf.Pos.Dialogs.OperatorSwitchDialog"'
Assert-Contains "operator switch dialog uses safe title" $operatorSwitchXaml 'operator.switch.title'
Assert-Contains "operator switch uses manual staff code" $operatorSwitchXaml 'x:Name="StaffCodeBox"'
if ($operatorSwitchXaml.Contains('OperatorCombo')) {
    Fail "operator switch must not force selecting from OperatorCombo"
}
else {
    Pass "operator switch does not force operator list selection"
}

Assert-Contains "Change/Lock uses operator switch" $main "new OperatorSwitchDialog"
Assert-Contains "permission denied switch prompt wired" $main "PermissionDeniedDialog.ShowSwitchPrompt"
Assert-Contains "operator switch can request full POS access" $main "PosAccessRequested"

if ($fail) {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}

Write-Host "`nRESULT: PASS" -ForegroundColor Green
exit 0
