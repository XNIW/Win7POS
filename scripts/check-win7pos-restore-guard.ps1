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

. (Join-Path $PSScriptRoot "sales-sync-outbox-gate-helpers.ps1")

function Test-SalesOutboxFacadeDelegation(
    [string]$text,
    [string]$methodName,
    [string]$writerName,
    [string[]]$forwarding) {
    $slices = @(Get-CSharpMethodSlices $text "public" $methodName)
    if ($slices.Count -eq 0 -or $slices.Count -ne $forwarding.Count) {
        return $false
    }

    for ($index = 0; $index -lt $slices.Count; $index++) {
        $slice = $slices[$index].Text
        $delegation = "_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($writerName) + "\s*\("
        $exactForwarding = "(?s)_salesSyncOutbox\s*\.\s*" +
            [regex]::Escape($writerName) + "\s*\(\s*" +
            $forwarding[$index] + "\s*\)"
        if ([regex]::Matches($slice, $delegation).Count -ne 1 -or
            [regex]::Matches($slice, $exactForwarding).Count -ne 1 -or
            $slice -match "\bsales_sync_outbox\b|\b(?:conn|_factory)\s*\.\s*(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async\b|\b(?:Open|BeginTransaction|Commit|Rollback)\s*\(") {
            return $false
        }
    }

    return $true
}

function Index-OrFail([string]$text, [string]$needle, [string]$message) {
    $index = $text.IndexOf($needle, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        Fail $message
    }
    return $index
}

$required = @(
    "src/Win7POS.Wpf/Pos/PosWorkflowService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs",
    "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSignalBus.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs",
    "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs",
    "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs",
    "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs",
    "src/Win7POS.Wpf/MainWindow.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs",
    "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs",
    "src/Win7POS.Data/Repositories/SaleRepository.cs",
    "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs",
    "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs",
    "src/Win7POS.Data/Online/AtomicRestoreInstaller.cs",
    "src/Win7POS.Data/Online/SqliteRestoreCoordinator.cs",
    "src/Win7POS.Data/Backup/SqliteOnlineBackup.cs",
    "src/Win7POS.Data/Backup/SqliteSourceInspector.cs",
    "src/Win7POS.Data/Backup/BackupRestoreProtocol.cs",
    "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs",
    "src/Win7POS.Data/SqliteConnectionFactory.cs",
    "tests/Win7POS.Core.Tests/Data/PersistenceFoundationTests.cs",
    "tests/Win7POS.Core.Tests/Data/PersistenceFoundationHardeningTests.cs",
    "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs",
    "tests/Win7POS.Core.Tests/Online/PosOnlineSyncSignalBusTests.cs",
    "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj"
)

foreach ($path in $required) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if ($fail) {
    exit 1
}

$workflow = Read-Text "src/Win7POS.Wpf/Pos/PosWorkflowService.cs"
$startOfDay = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartOfDaySyncService.cs"
$statusReader = Read-Text "src/Win7POS.Wpf/Pos/Online/PosSyncStatusReader.cs"
$translations = Read-Text "src/Win7POS.Wpf/Localization/PosTranslations.Secondary.cs"
$saleRepo = Read-Text "src/Win7POS.Data/Repositories/SaleRepository.cs"
$salesOutboxRepo = Read-Text "src/Win7POS.Data/Repositories/SalesSyncOutboxRepository.cs"
$catalogOutboxRepo = Read-Text "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs"
$maintenance = Read-Text "src/Win7POS.Data/Repositories/DbMaintenanceRepository.cs"
$restoreSafety = Read-Text "src/Win7POS.Data/Online/RestoreShopSafetyRepository.cs"
$restoreSafetyTests = Read-Text "tests/Win7POS.Core.Tests/Data/RestoreShopSafetyTests.cs"
$atomicInstaller = Read-Text "src/Win7POS.Data/Online/AtomicRestoreInstaller.cs"
$restoreCoordinator = Read-Text "src/Win7POS.Data/Online/SqliteRestoreCoordinator.cs"
$onlineBackup = Read-Text "src/Win7POS.Data/Backup/SqliteOnlineBackup.cs"
$sourceInspector = Read-Text "src/Win7POS.Data/Backup/SqliteSourceInspector.cs"
$backupRestoreProtocol = Read-Text "src/Win7POS.Data/Backup/BackupRestoreProtocol.cs"
$connectionFactory = Read-Text "src/Win7POS.Data/SqliteConnectionFactory.cs"
$persistenceTests = Read-Text "tests/Win7POS.Core.Tests/Data/PersistenceFoundationTests.cs"
$persistenceHardeningTests = Read-Text "tests/Win7POS.Core.Tests/Data/PersistenceFoundationHardeningTests.cs"
$connectionPolicyTests = Read-Text "tests/Win7POS.Core.Tests/Data/SqliteConnectionPolicyTests.cs"
$syncSignalBusTests = Read-Text "tests/Win7POS.Core.Tests/Online/PosOnlineSyncSignalBusTests.cs"
$testProject = Read-Text "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj"
$mainWindow = Read-Text "src/Win7POS.Wpf/MainWindow.xaml.cs"
$startupCoordinator = Read-Text "src/Win7POS.Wpf/Pos/Online/PosStartupCoordinator.cs"
$maintenanceDialog = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs"
$maintenanceViewModel = Read-Text "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs"
$syncSignalBus = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSignalBus.cs"
$revocationLatch = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncRevocationLatch.cs"
$syncHost = Read-Text "src/Win7POS.Wpf/Pos/Online/PosOnlineSyncSupervisorHost.cs"
$unresolvedFacadeDelegates = Test-SalesOutboxFacadeDelegation $saleRepo "HasUnresolvedSalesSyncOutboxAsync" "HasUnresolvedAsync" @("\s*")
$combined = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) } |
    Out-String

$restoreStart = Index-OrFail $workflow "public async Task<DbRestoreResult> RestoreDbAsync" "RestoreDbAsync missing"
$restoreEnd = Index-OrFail $workflow "public async Task<string> IntegrityCheckAsync" "RestoreDbAsync end marker missing"
$restoreBody = $workflow.Substring($restoreStart, $restoreEnd - $restoreStart)

$outboxCheck = Index-OrFail $restoreBody "HasUnresolvedSalesSyncOutboxAsync" "restore must check unresolved sales outbox"
$catalogOutboxCheck = Index-OrFail $restoreBody "_catalogImportOutbox.HasUnresolvedAsync" "restore must check unresolved catalog import outbox"
$coordinatorConstruction = Index-OrFail $restoreBody "new SqliteRestoreCoordinator(" "restore must delegate data installation to the restore coordinator"
$coordinatorCall = Index-OrFail $restoreBody "coordinator.RestoreAsync(" "restore coordinator call missing"
$reviewFlag = Index-OrFail $restoreBody "SetBoolAsync(KeyRestoreNeedsSyncReview, true)" "restore must mark sync review required"

if ($outboxCheck -gt $coordinatorConstruction -or
    $catalogOutboxCheck -gt $coordinatorConstruction -or
    $coordinatorConstruction -gt $coordinatorCall -or
    $coordinatorCall -gt $reviewFlag) {
    Fail "WPF restore must block unresolved outboxes before the data coordinator and set review state only in its post-swap callback"
} else {
    Pass "WPF restore remains a thin safety/UI layer around the data coordinator"
}

$stageCandidate = Index-OrFail $restoreCoordinator "StageAndValidateCandidateAsync(" "restore source staging missing"
$exclusiveMaintenance = Index-OrFail $restoreCoordinator "RunExclusiveMaintenanceAsync(async () =>" "restore must acquire the process-wide connection fence"
$fencedLiveValidation = Index-OrFail $restoreCoordinator ".ValidateLivePreSwapAsync(" "restore must revalidate live state after the connection drain"
$fencedCandidateValidation = Index-OrFail $restoreCoordinator ".ValidateAndSealAsync(cancellationToken)" "restore must revalidate and seal the candidate inside the fence"
$preBackup = Index-OrFail $restoreCoordinator ".CreateVerifiedAsync(preBackupPath, cancellationToken)" "restore must create a verified online pre-restore backup"
$installValidated = Index-OrFail $restoreCoordinator "installer.InstallAsync(" "restore must atomically install the sealed candidate"
if ($stageCandidate -gt $exclusiveMaintenance -or
    $exclusiveMaintenance -gt $fencedLiveValidation -or
    $fencedLiveValidation -gt $fencedCandidateValidation -or
    $fencedCandidateValidation -gt $preBackup -or
    $preBackup -gt $installValidated) {
    Fail "data restore order must be stage/validate -> fence -> authoritative live/candidate revalidation -> verified pre-backup -> sealed candidate install"
} else {
    Pass "data restore ordering preserves validation, fence and verified pre-backup invariants"
}

$authorizationMaintenance = Index-OrFail $restoreBody "EnterAuthorizationMaintenance" "restore must enter authorization maintenance"
$syncStop = Index-OrFail $restoreBody "PosOnlineSyncSignalBus.StopAsync" "restore must stop the sync host"
$trustedCapture = Index-OrFail $restoreBody "trustedDeviceStore.TryRead" "restore must capture the invalidated trust only after stop"
$catalogBarrier = Index-OrFail $restoreBody "new CatalogShopTransitionBarrier" "restore must acquire the catalog transition barrier"
$generationTombstone = Index-OrFail $restoreBody ".ResetForRestoreAsync(" "restore must install an inactive generation tombstone"
$catalogReviewReset = Index-OrFail $restoreBody ".ResetForRestoreReviewWhileBarrierHeldAsync(" "restore must reset catalog review state"
$restoreAudit = Index-OrFail $restoreBody "_audit.AppendAsync(" "restore must persist its audit record"
$authorizationInvalidate = Index-OrFail $restoreBody ".InvalidateAuthorizationStateWhileMaintenanceHeld();" "restore must invalidate process authorization state"
$installedMarker = Index-OrFail $restoreBody "restoreInstalled = true" "restore success marker missing"
$generationRevoke = Index-OrFail $restoreBody "PosOnlineSyncRevocationLatch.RevokeWhileMaintenanceHeld(" "restore must latch the invalidated generation"
$trustedClear = Index-OrFail $restoreBody "trustedDeviceStore.Clear()" "restore must clear all persisted trust"
if ($authorizationMaintenance -gt $syncStop -or $syncStop -gt $trustedCapture -or
    $trustedCapture -gt $catalogBarrier -or $catalogBarrier -gt $generationTombstone -or
    $generationTombstone -gt $catalogReviewReset -or $catalogReviewReset -gt $restoreAudit -or
    $restoreAudit -gt $authorizationInvalidate -or $authorizationInvalidate -gt $installedMarker -or
    $installedMarker -gt $generationRevoke -or $generationRevoke -gt $trustedClear) {
    Fail "restore auth boundary must be maintenance -> stop -> capture -> barrier -> tombstone/review/audit -> epoch -> revoke/clear"
} else {
    Pass "restore authorization boundary is ordered and fail-closed"
}

$successBranch = $restoreBody.IndexOf("if (restoreInstalled)", [System.StringComparison]::Ordinal)
$noResumeExit = $restoreBody.IndexOf("ExitMaintenanceWithoutResumeAsync", [System.StringComparison]::Ordinal)
$failedLeaseDispose = if ($noResumeExit -ge 0) {
    $restoreBody.IndexOf("authorizationMaintenanceLease?.Dispose()", $noResumeExit, [System.StringComparison]::Ordinal)
} else { -1 }
$failedResume = $restoreBody.IndexOf("PosOnlineSyncSignalBus.ResumeAsync", [System.StringComparison]::Ordinal)
$finalLeaseDispose = $restoreBody.LastIndexOf("authorizationMaintenanceLease?.Dispose()", [System.StringComparison]::Ordinal)
$restoreGateRelease = $restoreBody.LastIndexOf("_gate.Release()", [System.StringComparison]::Ordinal)
if ($successBranch -lt 0 -or $noResumeExit -le $successBranch -or
    $failedLeaseDispose -le $noResumeExit -or $failedResume -le $failedLeaseDispose -or
    $finalLeaseDispose -le $failedResume -or $restoreGateRelease -le $finalLeaseDispose) {
    Fail "successful restore must suppress resume and failed restore must release auth maintenance before resume"
} else {
    Pass "restore success suppresses resume while failure can safely resume"
}

$signalStart = $syncSignalBus.IndexOf("public static void Signal(", [System.StringComparison]::Ordinal)
$triggerStart = $syncSignalBus.IndexOf("public static Task<OnlineSyncLaneOutcome> TriggerAsync", [System.StringComparison]::Ordinal)
$stopStart = $syncSignalBus.IndexOf("public static async Task StopAsync()", [System.StringComparison]::Ordinal)
$resumeStart = $syncSignalBus.IndexOf("public static async Task ResumeAsync", [System.StringComparison]::Ordinal)
$exitStart = $syncSignalBus.IndexOf("public static async Task ExitMaintenanceWithoutResumeAsync", [System.StringComparison]::Ordinal)
$endStart = $syncSignalBus.IndexOf("private static async Task EndMaintenanceAsync", [System.StringComparison]::Ordinal)
$pauseStart = $syncSignalBus.IndexOf("private static async Task PauseRegistrationDuringMaintenanceAsync", [System.StringComparison]::Ordinal)
if ($signalStart -lt 0 -or $triggerStart -le $signalStart -or $stopStart -le $triggerStart -or
    $resumeStart -le $stopStart -or $exitStart -le $resumeStart -or
    $endStart -le $exitStart -or $pauseStart -le $endStart) {
    Fail "sync maintenance bus method boundaries are missing"
} else {
    $signalBody = $syncSignalBus.Substring($signalStart, $triggerStart - $signalStart)
    $triggerBody = $syncSignalBus.Substring($triggerStart, $stopStart - $triggerStart)
    $stopBody = $syncSignalBus.Substring($stopStart, $resumeStart - $stopStart)
    $exitBody = $syncSignalBus.Substring($exitStart, $endStart - $exitStart)
    $endBody = $syncSignalBus.Substring($endStart, $pauseStart - $endStart)
    $pauseEnd = $syncSignalBus.IndexOf("private sealed class Registration", [System.StringComparison]::Ordinal)
    $pauseBody = if ($pauseEnd -gt $pauseStart) {
        $syncSignalBus.Substring($pauseStart, $pauseEnd - $pauseStart)
    } else { "" }
    $captureStopHandler = $stopBody.IndexOf("stopHandler = _registration?.StopHandler", [System.StringComparison]::Ordinal)
    $invokeOutsideGate = $stopBody.IndexOf("stopHandler()", [System.StringComparison]::Ordinal)
    $publishSharedStop = $stopBody.IndexOf("_maintenanceStopTask = stopTask", [System.StringComparison]::Ordinal)
    $awaitSharedStop = $stopBody.IndexOf("await stopTask", [System.StringComparison]::Ordinal)
    $nestedDepth = $endBody.IndexOf("if (_maintenanceDepth > 0)", [System.StringComparison]::Ordinal)
    $decrementDepth = if ($nestedDepth -ge 0) {
        $endBody.IndexOf("_maintenanceDepth--", $nestedDepth, [System.StringComparison]::Ordinal)
    } else { -1 }
    $nestedReturn = if ($decrementDepth -ge 0) {
        $endBody.IndexOf("return", $decrementDepth, [System.StringComparison]::Ordinal)
    } else { -1 }
    $awaitResume = $endBody.IndexOf("await handler(cancellationToken)", [System.StringComparison]::Ordinal)
    $successfulPendingReset = if ($awaitResume -ge 0) {
        $endBody.IndexOf("_resumePending = false", $awaitResume, [System.StringComparison]::Ordinal)
    } else { -1 }
    if ($signalBody -notmatch '_maintenanceDepth\s*>\s*0\s*\|\|\s*_resumePending[\s\S]{0,100}\?\s*null' -or
        $triggerBody -notmatch '_maintenanceDepth\s*>\s*0\s*\|\|\s*_resumePending[\s\S]{0,220}"sync_maintenance_active"[\s\S]{0,100}terminal:\s*true' -or
        $stopBody -notmatch '_maintenanceDepth\+\+' -or
        $stopBody -notmatch '_maintenanceDepth\s*>\s*1[\s\S]{0,120}stopTask\s*=\s*_maintenanceStopTask' -or
        $stopBody -notmatch '_resumePending\s*=\s*false[\s\S]{0,180}stopHandler\s*=\s*_registration\?\.StopHandler' -or
        $stopBody -notmatch 'MaintenanceTransitionGate\.WaitAsync[\s\S]*MaintenanceTransitionGate\.Release' -or
        $captureStopHandler -lt 0 -or $invokeOutsideGate -le $captureStopHandler -or
        $publishSharedStop -le $invokeOutsideGate -or $awaitSharedStop -le $publishSharedStop -or
        $exitBody -notmatch 'resume:\s*false' -or
        $nestedDepth -lt 0 -or $decrementDepth -le $nestedDepth -or
        $nestedReturn -le $decrementDepth -or
        $endBody -notmatch 'MaintenanceTransitionGate\.WaitAsync[\s\S]*MaintenanceTransitionGate\.Release' -or
        $endBody -notmatch '_maintenanceDepth\s*<=\s*0\s*&&\s*!_resumePending' -or
        $endBody -notmatch 'if\s*\(!resume\)[\s\S]{0,100}_resumeSuppressed\s*=\s*true' -or
        $endBody -notmatch 'resume\s*&&\s*!_resumeSuppressed' -or
        $endBody -notmatch 'if\s*\(handler\s*==\s*null\)[\s\S]{0,220}_resumePending\s*=\s*false[\s\S]{0,220}return' -or
        $endBody -notmatch '_resumePending\s*=\s*true[\s\S]{0,400}await\s+handler\(cancellationToken\)' -or
        $awaitResume -lt 0 -or $successfulPendingReset -le $awaitResume -or
        $endBody -notmatch 'retry marker without corrupting nested-scope depth' -or
        $syncSignalBusTests -notmatch 'FailedResume_RemainsInMaintenanceAndSecondResumeRetries' -or
        $syncSignalBusTests -notmatch 'NestedNoResume_SuppressesTheFinalResumeHandler' -or
        $syncSignalBusTests -notmatch 'FailedResume_ThenSuccessfulRestoreClearsPendingWithoutRestart' -or
        $syncSignalBusTests -notmatch 'FailedResume_DirectNoResumeExitCancelsPendingRetry' -or
        $testProject -notmatch 'PosOnlineSyncSignalBus\.cs' -or
        $syncSignalBus -notmatch 'pauseForMaintenance\s*=\s*_maintenanceDepth\s*>\s*0\s*\|\|\s*_resumePending[\s\S]{0,180}PauseRegistrationDuringMaintenanceAsync' -or
        $pauseBody -notmatch 'MaintenanceTransitionGate\.WaitAsync[\s\S]*MaintenanceTransitionGate\.Release' -or
        $pauseBody -notmatch 'lock\s*\(Gate\)[\s\S]{0,180}_maintenanceDepth\s*<=\s*0\s*&&\s*!_resumePending[\s\S]{0,180}!ReferenceEquals\(_registration,\s*registration\)' -or
        $pauseBody -notmatch 'await\s+registration\.StopHandler\(\)') {
        Fail "sync maintenance bus must suppress traffic, nest stops and honor no-resume"
    } else {
        Pass "sync maintenance bus suppresses traffic and nests stop/resume safely"
    }
}

$vmRestoreStart = $maintenanceViewModel.IndexOf("private async Task RestoreBackupAsync()", [System.StringComparison]::Ordinal)
$vmRestoreEnd = $maintenanceViewModel.IndexOf("private async Task IntegrityCheckAsync()", [System.StringComparison]::Ordinal)
$vmRestoreBody = if ($vmRestoreStart -ge 0 -and $vmRestoreEnd -gt $vmRestoreStart) {
    $maintenanceViewModel.Substring($vmRestoreStart, $vmRestoreEnd - $vmRestoreStart)
} else { "" }
$vmRestoreCall = $vmRestoreBody.IndexOf("RestoreDbAsync", [System.StringComparison]::Ordinal)
$vmAudit = $vmRestoreBody.IndexOf("LogSecurityEvent", [System.StringComparison]::Ordinal)
$vmLogout = $vmRestoreBody.IndexOf("LogoutForced", [System.StringComparison]::Ordinal)
$vmCatch = $vmRestoreBody.IndexOf("catch (Exception ex)", [System.StringComparison]::Ordinal)
if ($vmRestoreCall -lt 0 -or $vmAudit -le $vmRestoreCall -or
    $vmLogout -le $vmAudit -or $vmCatch -le $vmLogout) {
    Fail "successful restore must audit then force logout before returning control"
} else {
    Pass "successful restore audits and forces logout"
}

$registrationStart = $startupCoordinator.IndexOf("PosOnlineSyncSignalBus.Register(", [System.StringComparison]::Ordinal)
$registrationEnd = $startupCoordinator.IndexOf("public IOperatorSession EnsureSession", [System.StringComparison]::Ordinal)
$registrationSetupBody = if ($registrationStart -ge 0 -and $registrationEnd -gt $registrationStart) {
    $startupCoordinator.Substring($registrationStart, $registrationEnd - $registrationStart)
} else { "" }
$stopForMaintenanceStart = $startupCoordinator.IndexOf("private async Task StopForMaintenanceAsync", [System.StringComparison]::Ordinal)
$resumeAfterMaintenanceStart = $startupCoordinator.IndexOf("private async Task ResumeAfterMaintenanceAsync", [System.StringComparison]::Ordinal)
$resumeAfterMaintenanceBody = if ($resumeAfterMaintenanceStart -ge 0 -and $stopForMaintenanceStart -gt $resumeAfterMaintenanceStart) {
    $startupCoordinator.Substring($resumeAfterMaintenanceStart, $stopForMaintenanceStart - $resumeAfterMaintenanceStart)
} else { "" }
$stopForMaintenanceBody = if ($stopForMaintenanceStart -ge 0) {
    $startupCoordinator.Substring($stopForMaintenanceStart)
} else { "" }
# Keep the causal order explicit: registration setup, stop-state capture, then resume/re-arm.
$registrationBody = $registrationSetupBody + $stopForMaintenanceBody + $resumeAfterMaintenanceBody
$hostStop = $registrationBody.IndexOf(".StopAsync()", [System.StringComparison]::Ordinal)
$hadGeneration = $registrationBody.IndexOf("stoppedState.HadGeneration", [System.StringComparison]::Ordinal)
$wasContinuous = $registrationBody.IndexOf("stoppedState.WasContinuous", [System.StringComparison]::Ordinal)
$resumeRead = $registrationBody.IndexOf("Interlocked.CompareExchange", [System.StringComparison]::Ordinal)
$attachTrust = $registrationBody.IndexOf("AttachCurrentTrustAsync(cancellationToken)", [System.StringComparison]::Ordinal)
$continuousStart = $registrationBody.IndexOf("StartContinuous()", [System.StringComparison]::Ordinal)
$resumeCatch = $registrationBody.IndexOf("catch", $continuousStart, [System.StringComparison]::Ordinal)
$resumeRearm = if ($resumeCatch -ge 0) {
    $registrationBody.IndexOf("_maintenanceResumeRequested", $resumeCatch, [System.StringComparison]::Ordinal)
} else { -1 }
if ($hostStop -lt 0 -or $hadGeneration -le $hostStop -or
    $wasContinuous -le $hadGeneration -or $resumeRead -le $wasContinuous -or
    $attachTrust -le $resumeRead -or $continuousStart -le $attachTrust -or
    $resumeCatch -le $continuousStart -or $resumeRearm -le $resumeCatch -or
    $registrationBody -notmatch 'shouldResume\s*\?\s*1\s*:\s*0' -or
    $registrationSetupBody -notmatch 'StopForMaintenanceAsync\(registeredHost\)' -or
    $registrationSetupBody -notmatch 'ResumeAfterMaintenanceAsync\(registeredHost,\s*token\)' -or
    $registrationBody.Substring($resumeCatch) -notmatch '_maintenanceResumeRequested,[\s\S]{0,80}1') {
    Fail "startup coordinator maintenance resume must use actual stop state and re-arm after failure"
} else {
    Pass "startup coordinator maintenance resume is stateful and retry-safe"
}

if ($connectionFactory -notmatch "RunExclusiveMaintenanceAsync" -or
    $connectionFactory -notmatch "_maintenancePending" -or
    $connectionFactory -notmatch "_maintenanceEntered" -or
    $connectionFactory -notmatch "_activeConnections" -or
    $connectionFactory -notmatch "RegisterConnection" -or
    $connectionFactory -notmatch "DefaultMaintenanceDrainTimeout" -or
    $connectionFactory -notmatch "Timed out waiting for active SQLite connections to drain" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_DrainsActiveConnectionsBlocksNewOpenAndAllowsOwnerReentry" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_AllowsConnectionNeededToCompleteDrainWithoutDeadlock" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_DrainTimeoutAbortsBeforeActionAndReleasesFence" -or
    $connectionPolicyTests -notmatch "ExclusiveMaintenance_LeakedOwnerConnectionFailsButReleasesGlobalFence") {
    Fail "restore must complete or safely time out the connection drain, avoid nested-open deadlock, block opens at the zero boundary, and allow maintenance-owner reentry"
} else {
    Pass "process-wide connection fence is covered by concurrency tests"
}

if ($restoreSafety -notmatch "ValidateLivePreSwapAsync" -or
    $restoreSafety -notmatch "restore_live_sales_outbox_unresolved" -or
    $restoreSafety -notmatch "restore_live_catalog_outbox_unresolved" -or
    $restoreSafety -notmatch "restore_live_catalog_epoch_changed" -or
    $restoreSafetyTests -notmatch "LivePreSwapValidation_RejectsOutboxCommittedAfterPreliminaryCheck") {
    Fail "restore must close the pre-fence outbox/shop/epoch TOCTOU before backup or live swap"
} else {
    Pass "fenced pre-swap validation closes the outbox/shop/epoch TOCTOU"
}

$stageStart = Index-OrFail $restoreCoordinator "private async Task<RestoreCandidatePreparation> StageAndValidateCandidateAsync" "candidate staging method missing"
$stageEnd = Index-OrFail $restoreCoordinator "private string AllocateCandidatePath" "candidate staging end marker missing"
$stageBody = $restoreCoordinator.Substring($stageStart, $stageEnd - $stageStart)
$sourceSnapshot = Index-OrFail $stageBody "CreateReadOnlySnapshot(" "restore must create a SQLite snapshot"
$candidateMigration = Index-OrFail $stageBody "DbInitializer.EnsureCreated(PosDbOptions.ForPath(candidatePath))" "candidate migration missing"
$candidateIntegrity = Index-OrFail $stageBody ".ValidateAsync(cancellationToken)" "candidate integrity/foreign-key validation missing"
$candidateValidation = Index-OrFail $stageBody ".ValidateCandidateAsync(expectedShopId, expectedShopCode)" "candidate shop/outbox validation missing"
if ($sourceSnapshot -gt $candidateMigration -or
    $candidateMigration -gt $candidateIntegrity -or
    $candidateIntegrity -gt $candidateValidation -or
    $restoreSafety -notmatch "restore_shop_mismatch" -or
    $restoreSafety -notmatch "restore_catalog_shop_mismatch" -or
    $restoreSafety -notmatch "restore_candidate_outbox_unresolved" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCrossShopSnapshotAndAnyUnresolvedOutbox" -or
    $restoreSafetyTests -notmatch "CandidateValidation_RejectsCatalogBindingMismatch") {
    Fail "restore must reject incoherent official/catalog binding and every unresolved candidate outbox before live DB installation"
} else {
    Pass "restore validates official/catalog binding and rejects every unresolved candidate outbox before installation"
}

if ($restoreBody -match "File\.Copy\(\s*backupDbPath" -or
    $restoreCoordinator -match "File\.Copy\(\s*source" -or
    $restoreCoordinator -notmatch "source\.BackupDatabase\(destination\)" -or
    $restoreCoordinator -notmatch "Mode\s*=\s*SqliteOpenMode\.ReadOnly" -or
    $restoreCoordinator -notmatch "Cache\s*=\s*SqliteCacheMode\.Private" -or
    $restoreCoordinator -notmatch "Pooling\s*=\s*false" -or
    $restoreCoordinator -notmatch "FileShare\.ReadWrite" -or
    $persistenceHardeningTests -notmatch "RestoreSourceIdentityGuard_BlocksDeleteAndReplaceDuringSnapshot") {
    Fail "shipping restore must stage a read-only SQLite snapshot, never raw-copy the source, and guard source identity"
} else {
    Pass "WAL-safe SQLite source snapshot replaces the raw restore-source copy"
}

if ($restoreCoordinator -notmatch "finally[\s\S]{0,180}sealedCandidate\?\.Dispose\(\)[\s\S]{0,120}preparation\?\.Dispose\(\)" -or
    $restoreCoordinator -notmatch "DeleteSqliteFiles\(candidatePath\)" -or
    $restoreCoordinator -notmatch 'DeleteIfPresent\(path \+ "-wal"\)' -or
    $restoreCoordinator -notmatch 'DeleteIfPresent\(path \+ "-shm"\)') {
    Fail "staged candidates and SQLite sidecars must have deterministic pre-marker cleanup"
} else {
    Pass "unpublished candidate and sidecars have deterministic cleanup"
}

if ($atomicInstaller -notmatch "File\.Replace\(candidatePath, liveDatabasePath, rollbackPath\)" -or
    $atomicInstaller -notmatch "File\.Replace\(rollbackPath, liveDatabasePath, null\)" -or
    $atomicInstaller -notmatch "PhasePrepared" -or
    $atomicInstaller -notmatch "PhaseCommitted" -or
    $atomicInstaller -notmatch "RecoverInterruptedInstallCore" -or
    $atomicInstaller -notmatch "Flush\(true\)" -or
    $atomicInstaller -notmatch 'databasePath \+ "-journal"' -or
    $restoreSafetyTests -notmatch "AtomicInstaller_RollsBackOnPostSwapFailure" -or
    $persistenceTests -notmatch "Recovery_PreparedBeforeSwapKeepsOldLiveAndRemovesPartialCandidate" -or
    $persistenceTests -notmatch "Recovery_PreparedAfterAtomicSwapRestoresOldLive" -or
    $persistenceTests -notmatch "Recovery_CommittedAfterSwapKeepsNewLiveAndCleansRollback") {
    Fail "restore must use same-directory atomic replacement with durable prepared/committed recovery"
} else {
    Pass "restore swap is fail-atomic and crash recovery is covered"
}

$preparedWrite = Index-OrFail $atomicInstaller "WriteMarker(markerPath, marker);" "durable prepared marker missing"
$atomicReplace = Index-OrFail $atomicInstaller "File.Replace(candidatePath, liveDatabasePath, rollbackPath);" "atomic replace missing"
$postMigration = Index-OrFail $atomicInstaller "DbInitializer.EnsureCreated(PosDbOptions.ForPath(liveDatabasePath));" "post-swap migration missing"
$postValidation = Index-OrFail $atomicInstaller ".ValidateAsync(" "post-swap integrity/foreign-key validation missing"
$committedPhase = Index-OrFail $atomicInstaller "marker.Phase = PhaseCommitted;" "committed marker transition missing"
if ($preparedWrite -gt $atomicReplace -or
    $atomicReplace -gt $postMigration -or
    $postMigration -gt $postValidation -or
    $postValidation -gt $committedPhase) {
    Fail "atomic protocol order must be prepared -> replace -> migration -> integrity/FK -> committed"
} else {
    Pass "atomic marker and post-swap validation order is durable and fail-closed"
}

if ($persistenceTests -notmatch "Recovery_CommittedCorruptLiveRestoresValidatedRollback" -or
    $atomicInstaller -notmatch "IsDatabaseValidAsync" -or
    $atomicInstaller -notmatch "TryCleanupCommittedRestore") {
    Fail "committed recovery must validate the live DB, preserve rollback until validation, and defer cleanup safely"
} else {
    Pass "committed recovery validates live state before idempotent cleanup"
}

if ($onlineBackup -notmatch "BackupDatabase" -or
    $onlineBackup -notmatch "ValidateAsync" -or
    [regex]::Matches($onlineBackup, "EnsureDestinationIsPublishable\(finalPath\)").Count -lt 2 -or
    $onlineBackup -notmatch "File\.Move\(temporaryPath, finalPath\)" -or
    $workflow -notmatch "_onlineBackup\.CreateVerifiedAsync\(outputPath, cancellationToken\)" -or
    $persistenceTests -notmatch "OnlineBackup_ProducesValidatedSnapshotWhileWriterContinues" -or
    $persistenceHardeningTests -notmatch "BackupRejectsPreexistingFinalSidecarWithoutPublishingOrDeletingIt" -or
    $persistenceHardeningTests -notmatch "BackupRejectsFinalSidecarCreatedImmediatelyBeforePublish") {
    Fail "manual and pre-restore backups must use the verified SQLite online-backup path"
} else {
    Pass "verified online backup is used and covered under concurrent writes"
}

if (-not $unresolvedFacadeDelegates -or
    $salesOutboxRepo -notmatch "WHERE status IN \('pending', 'retry', 'in_progress', 'failed_blocked'\)") {
    Fail "unresolved outbox guard must include pending, retry, in_progress and failed_blocked"
} else {
    Pass "unresolved outbox guard includes pending/retry/in_progress/failed_blocked"
}

if ($catalogOutboxRepo -notmatch "WHERE status IN \('pending', 'retry', 'in_progress', 'failed_blocked'\)") {
    Fail "catalog import unresolved guard must include pending, retry, in_progress and failed_blocked"
} else {
    Pass "catalog import unresolved guard includes pending/retry/in_progress/failed_blocked"
}

if ($workflow -notmatch "KeyRestoreLastPreBackupPath" -or
    $workflow -notmatch "KeyRestoreLastSourcePath" -or
    $workflow -notmatch "KeyRestoreLastIntegrityCheck") {
    Fail "restore audit settings for source/pre-backup/integrity missing"
} else {
    Pass "restore records source, pre-backup and integrity"
}

if ($maintenance -notmatch "PRAGMA integrity_check" -or
    $maintenance -notmatch "PRAGMA foreign_key_check" -or
    $maintenance -notmatch "ValidateAsync" -or
    $maintenance -notmatch "PRAGMA wal_checkpoint\(FULL\)" -or
    $persistenceTests -notmatch "CandidateForeignKeyViolation_IsRejectedBeforeLiveSwap") {
    Fail "DB maintenance must expose combined integrity and foreign-key validation while preserving WAL checkpoint support"
} else {
    Pass "DB maintenance validates integrity and foreign keys and preserves WAL checkpoint support"
}

if ($connectionFactory -notmatch 'ExpectedJournalMode\s*=\s*"delete"' -or
    $connectionFactory -notmatch 'ExpectedLockingMode\s*=\s*"normal"' -or
    $connectionFactory -notmatch 'ExpectedSynchronous\s*=\s*2' -or
    $connectionFactory -notmatch 'ExpectedForeignKeys\s*=\s*1' -or
    $connectionFactory -notmatch 'ExpectedBusyTimeoutMilliseconds\s*=\s*5000' -or
    $connectionFactory -notmatch 'ExpectedTempStore\s*=\s*1' -or
    $connectionFactory -notmatch 'ExpectedCacheSizeKiB\s*=\s*-2048' -or
    $connectionFactory -match 'ExpectedJournalMode\s*=\s*"wal"' -or
    $connectionFactory -notmatch 'journal_mode=DELETE' -or
    $connectionFactory -notmatch 'synchronous=FULL') {
    Fail "production SQLite policy must remain DELETE/NORMAL/FULL/FK/5000/FILE/-2048"
} else {
    Pass "production SQLite policy remains DELETE/NORMAL/FULL/FK/5000/FILE/-2048"
}

if ($sourceInspector -notmatch "missing its required WAL sidecar" -or
    $sourceInspector -notmatch "missing its shared-memory sidecar" -or
    $sourceInspector -notmatch "SQLite WAL sidecar page size does not match" -or
    $restoreCoordinator -match "wal_checkpoint|journal_mode\s*=|synchronous\s*=" -or
    $restoreCoordinator -match "File\.Copy\([^\r\n]*(?:-wal|-shm)" -or
    $onlineBackup -notmatch "BuildSourceConnectionString" -or
    $onlineBackup -notmatch "Mode\s*=\s*SqliteOpenMode\.ReadOnly" -or
    $onlineBackup -notmatch "Pooling\s*=\s*false" -or
    $persistenceHardeningTests -notmatch "RestoreWalSource_PreservesCommittedFramesWithConcurrentWriter" -or
    $persistenceHardeningTests -notmatch "RestoreSourceSidecarMatrix_IsDeterministicAndFailClosed") {
    Fail "WAL sources must be inspected fail-closed and snapshotted without mutating source policy or copying sidecars"
} else {
    Pass "WAL means read-only committed-frame snapshot; stale or incomplete sidecars fail closed"
}

$startupRecovery = Index-OrFail $startupCoordinator "RecoverInterruptedInstallAsync(options.DbPath)" "startup atomic recovery missing"
$startupInitializer = Index-OrFail $startupCoordinator "DbInitializer.EnsureCreated(options)" "startup database initializer missing"
if ($startupRecovery -gt $startupInitializer -or
    $persistenceHardeningTests -notmatch "StartupRecoveryHook_IsInvokedAndSecondRecoveryIsIdempotent" -or
    $persistenceHardeningTests -notmatch "RecoveryMalformedUnsafeAmbiguousAndCorruptRollback_FailsClosed") {
    Fail "startup recovery must precede normal initialization and fail closed on ambiguous markers"
} else {
    Pass "startup recovery precedes migrations/login/sync and is idempotent/fail-closed"
}

if ($backupRestoreProtocol -notmatch "internal enum BackupFailurePoint" -or
    $backupRestoreProtocol -notmatch "internal enum RestoreFailurePoint" -or
    $backupRestoreProtocol -notmatch "internal sealed class BackupRestoreTestHooks" -or
    $backupRestoreProtocol -match "Environment\.GetEnvironmentVariable|ConfigurationManager|AppSettings" -or
    $restoreCoordinator -notmatch "CancellationToken cancellationToken" -or
    $onlineBackup -notmatch "CancellationToken cancellationToken" -or
    $persistenceHardeningTests -notmatch "RestoreFailureInjection_AlwaysLeavesOldOrNewValidAndRetryable" -or
    $persistenceHardeningTests -notmatch "RestoreCancellation_StopsBeforePreparedOrFinishesAtomicProtocol") {
    Fail "fault hooks must stay internal/test-only and cancellation must preserve the atomic boundary"
} else {
    Pass "fault hooks are internal/test-only and cancellation is cooperatively bounded"
}

$hardeningSources = $backupRestoreProtocol + $sourceInspector + $onlineBackup + $restoreCoordinator + $atomicInstaller
if ($backupRestoreProtocol -notmatch "Path\.GetFileName" -or
    $backupRestoreProtocol -notmatch "operation_id=" -or
    $backupRestoreProtocol -notmatch "source_kind=" -or
    $backupRestoreProtocol -notmatch "cancel_requested=" -or
    $workflow -notmatch "GetBackupRestoreFailureCode" -or
    $workflow -match '_logger\.LogError\(ex,\s*"POS DB (?:backup|restore) failed' -or
    $hardeningSources -match "Windows\.Storage|Windows\.System|OperatingSystem\.IsWindowsVersionAtLeast|RandomAccess\.") {
    Fail "backup/restore diagnostics must be filename-only and hardening code must remain Win7-compatible"
} else {
    Pass "diagnostics are structured/filename-only and no Win10-only API is introduced"
}

$backupStart = Index-OrFail $workflow "public async Task<string> BackupDbAsync" "BackupDbAsync missing"
$backupEnd = Index-OrFail $workflow "private string AllocateDbBackupPath" "BackupDbAsync end marker missing"
$backupBody = $workflow.Substring($backupStart, $backupEnd - $backupStart)
if ($backupBody -notmatch "_onlineBackup\.CreateVerifiedAsync\(outputPath, cancellationToken\)" -or
    $backupBody -match "File\.Copy\(_options\.DbPath") {
    Fail "manual DB backup must use SQLite online backup and validate the snapshot"
} else {
    Pass "manual DB backup uses a verified SQLite online snapshot"
}

if ($onlineBackup -notmatch "NativeSnapshotMaximumAttempts\s*=\s*5" -or
    $onlineBackup -notmatch "NativeSnapshotRetryWindowMilliseconds\s*=\s*5000" -or
    $onlineBackup -notmatch "IsNativeSnapshotBusy" -or
    $onlineBackup -notmatch "DeleteSqliteSidecars\(temporaryPath\)" -or
    $restoreCoordinator -notmatch "NativeSnapshotMaximumAttempts\s*=\s*5" -or
    $restoreCoordinator -notmatch "NativeSnapshotRetryWindowMilliseconds\s*=\s*5000" -or
    $restoreCoordinator -notmatch "IsNativeSnapshotBusy" -or
    $restoreCoordinator -notmatch "DeleteSqliteFiles\(candidatePath\)" -or
    $onlineBackup -notmatch "DeleteSqliteSidecars\(temporaryPath\);\s*cancellationToken\.ThrowIfCancellationRequested\(\)" -or
    $restoreCoordinator -notmatch "DeleteSqliteFiles\(candidatePath\);\s*cancellationToken\.ThrowIfCancellationRequested\(\)" -or
    $persistenceHardeningTests -notmatch "TransientNativeBusy_RetriesFromCleanPartialSnapshot" -or
    $persistenceHardeningTests -notmatch "PersistentBusyFailsClosedAndCleanRetryPasses" -or
    $persistenceHardeningTests -notmatch "CancellationAfterBusyStopsBeforeRetryAndCleansPartial") {
    Fail "native backup/restore snapshot BUSY retry must be bounded, clean partial state and remain tested"
} else {
    Pass "native backup/restore snapshot BUSY retry is bounded and fail-closed"
}

if ($startOfDay -notmatch "RestoreNeedsReviewSettingKey" -or
    $startOfDay -notmatch "restore_needs_review" -or
    $startOfDay -notmatch "RunStartOfDayAsync\(\s*result\.RestoreNeedsReview" -or
    $startOfDay -notmatch "catalogComplete\s*=\s*!catalogRequired\s*\|\|[\s\S]{0,240}catalogLane\?\.Success\s*==\s*true\s*&&\s*catalogLane\.CatalogHasMore\s*==\s*false" -or
    $startOfDay -notmatch "if\s*\(result\.RestoreNeedsReview\)[\s\S]{0,700}!catalogComplete\s*\|\|\s*!result\.CatalogSaleSafe[\s\S]{0,600}CompleteReviewAsync" -or
    $mainWindow -notmatch "ShowRestoreReviewRecoveryAsync" -or
    $maintenanceDialog -notmatch "restoreReviewOnly") {
    Fail "start-of-day must expose a restricted restore reconciliation/review path before MainWindow closes"
} else {
    Pass "start-of-day exposes reconciliation and restricted maintenance review before MainWindow closes"
}

if ($statusReader -notmatch "RestoreNeedsReviewSettingKey" -or
    $statusReader -notmatch "restoreNeedsReview" -or
    $statusReader -notmatch "RestoreReviewText") {
    Fail "sync status must surface restore-needs-review"
} else {
    Pass "sync status surfaces restore-needs-review"
}

if ($translations -notmatch "dbMaintenance.restoreBlockedUnresolvedSales" -or
    $translations -notmatch "dbMaintenance.restoreBlockedUnresolvedCatalogImports" -or
    $translations -notmatch "dbMaintenance.restoreSyncReview" -or
    $translations -notmatch "dbMaintenance.restoreTrustedShopRequired") {
    Fail "restore user-facing safety messages missing"
} else {
    Pass "restore user-facing safety messages present"
}

if ($workflow -notmatch "CompleteRestoreSyncReviewAsync" -or
    $restoreSafety -notmatch "CompleteReviewAsync" -or
    $restoreSafety -notmatch "restore_review_catalog_not_reconciled" -or
    $restoreSafetyTests -notmatch "CompleteReview_RequiresPostRestoreCatalogAndNoUnresolvedOutbox") {
    Fail "safe restore review completion path missing"
} else {
    Pass "restore review completion requires reconciled catalog and resolved outboxes"
}

if ($combined -match "(?i)\b(TRUNCATE\s+TABLE\s+sales_sync_outbox|DROP\s+TABLE\s+sales_sync_outbox|DELETE\s+FROM\s+sales_sync_outbox)\b") {
    Fail "destructive sales_sync_outbox cleanup detected"
} else {
    Pass "no destructive sales_sync_outbox cleanup detected"
}

if ($combined -match "(?i)\b(TRUNCATE\s+TABLE\s+catalog_import_outbox|DROP\s+TABLE\s+catalog_import_outbox|DELETE\s+FROM\s+catalog_import_outbox)\b") {
    Fail "destructive catalog_import_outbox cleanup detected"
} else {
    Pass "no destructive catalog_import_outbox cleanup detected"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
