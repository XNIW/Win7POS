[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("x86")]
    [string]$Platform = "x86"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$candidatePaths = @(
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe"),
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe")
)
$harnessExe = $candidatePaths |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($harnessExe)) {
    throw "Authorization lease WPF smoke harness is missing; build the x86 net48 harness first."
}

function Invoke-AuthorizationHarness {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Mode,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactName
    )

    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    $quotedDataDirectory = '"' + $DataDirectory.Replace('"', '\"') + '"'
    $standardOutputPath = Join-Path $DataDirectory ($ArtifactName + ".stdout.txt")
    $standardErrorPath = Join-Path $DataDirectory ($ArtifactName + ".stderr.txt")
    $process = Start-Process `
        -FilePath $harnessExe `
        -ArgumentList @("--data-dir", $quotedDataDirectory, $Mode) `
        -RedirectStandardOutput $standardOutputPath `
        -RedirectStandardError $standardErrorPath `
        -PassThru `
        -WindowStyle Hidden
    $artifact = Join-Path $DataDirectory $ArtifactName
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Authorization lease smoke mode $Mode exceeded its 180-second timeout. Artifact: $artifact"
    }
    if ($process.ExitCode -ne 0) {
        $harnessErrorPath = Join-Path $DataDirectory "harness-error.txt"
        $harnessError = if (Test-Path -LiteralPath $harnessErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($harnessErrorPath).Trim()
        } else {
            "harness-error.txt was not produced"
        }
        $standardError = if (Test-Path -LiteralPath $standardErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($standardErrorPath).Trim()
        } else {
            "stderr was not produced"
        }
        throw "Authorization lease smoke mode $Mode failed with exit code $($process.ExitCode). Harness error: $harnessError. Stderr: $standardError. Artifact: $artifact"
    }
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Authorization lease smoke mode $Mode did not produce its result artifact: $artifact"
    }

    $result = [System.IO.File]::ReadAllText($artifact).Trim()
    if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal)) {
        throw "Authorization lease smoke mode $Mode did not report PASS. Artifact: $artifact"
    }

    return [pscustomobject]@{
        Artifact = $artifact
        ProcessId = $process.Id
        Result = $result
    }
}

function Read-ResultValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $pattern = "(?m)^" + [regex]::Escape($Name) + "=([^\r\n]+)\r?$"
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Authorization lease smoke result is missing $Name."
    }
    return $match.Groups[1].Value.Trim()
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$qaRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "Win7POS-QA"))
if (-not $qaRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create authorization lease smoke data outside the system temp directory."
}

$dataDir = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLease." + [Guid]::NewGuid().ToString("N"))))
$restartDataDir = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLeaseRestart." + [Guid]::NewGuid().ToString("N"))))
$capacityDataDir = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLeaseClockCapacity." + [Guid]::NewGuid().ToString("N"))))

$smoke = Invoke-AuthorizationHarness `
    -DataDirectory $dataDir `
    -Mode "--authorization-lease-smoke" `
    -ArtifactName "authorization-lease-smoke.txt"
$prepare = Invoke-AuthorizationHarness `
    -DataDirectory $restartDataDir `
    -Mode "--authorization-lease-restart-prepare" `
    -ArtifactName "authorization-lease-restart-prepare.txt"
$verify = Invoke-AuthorizationHarness `
    -DataDirectory $restartDataDir `
    -Mode "--authorization-lease-restart-verify" `
    -ArtifactName "authorization-lease-restart-verify.txt"
$capacity = Invoke-AuthorizationHarness `
    -DataDirectory $capacityDataDir `
    -Mode "--authorization-lease-clock-capacity-smoke" `
    -ArtifactName "authorization-lease-clock-capacity.txt"

if ((Read-ResultValue -Text $smoke.Result -Name "frozenClockMonotonicExpiry") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "frozenClockUnauthorizedSaleSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "frozenClockUnauthorizedPublicationOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "monotonicCounterRegressionDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "monotonicProviderFailureDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "invalidMonotonicFrequencyDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "monotonicElapsedOverflowDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "preflightDelayExpiryDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "activationDelayCountedFromReceipt") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "firstUseReceiptClockExpiryDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "firstUseUnauthorizedSaleSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "firstUseUnauthorizedPublicationOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "firstLoginRetryClockNotReset") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "heartbeatClockNotReset") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "staleHeartbeatClockNotReset") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "betweenPreflightsExpiryDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayG2Denied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayG3AfterClearDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayG3AfterTryClearDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplaySinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayLineRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayStockMovementRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationReplayOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossGenerationFreshResponseRecovers") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "trustedClockSaveFailureNotPublished") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitDurabilityHeadroomDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "crossPreflightRegressionDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "oversizedTrustedStateDenied") -ne "True") {
    throw "Authorization lease smoke did not prove monotonic expiry and fail-closed continuity."
}

if ((Read-ResultValue -Text $capacity.Result -Name "trustedClockCapacityFailClosed") -ne "True" -or
    (Read-ResultValue -Text $capacity.Result -Name "trustedClockCapacityNoEviction") -ne "True" -or
    (Read-ResultValue -Text $capacity.Result -Name "trustedClockDomainMismatchDenied") -ne "True" -or
    (Read-ResultValue -Text $capacity.Result -Name "trustedClockInvalidKeyDenied") -ne "True") {
    throw "Authorization lease clock-capacity smoke did not prove bounded fail-closed continuity."
}

if ((Read-ResultValue -Text $smoke.Result -Name "saleExpiryRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "loginRevocationRaceDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "staleLoginCleanupDoesNotClearNewAuthority") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "staleDenialDoesNotClearNewAuthority") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "permissionSnapshotRejectsReplacementAdmin") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "permissionSnapshotRejectsConcurrentRevocation") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "localRecoveryCannotInheritPosAuthority") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleExpiryRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleRevocationRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleRevocationRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "denialCallbacksAfterGateRelease") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleGenerationRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleGenerationRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitExpiryRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitExpiryRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderExpiryDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitFenceReleased") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitRevocationLinearized") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleExactRetryIdempotent") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleAmbiguousCommitRetryIdempotent") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleAmbiguousCartMutationStartsNewIdentity") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleAmbiguousAuthorityMismatchDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "concurrentAuthorizedSalesRows") -ne "2" -or
    (Read-ResultValue -Text $smoke.Result -Name "concurrentAuthorizedSalesOutboxRows") -ne "2") {
    throw "Authorization lease smoke did not prove the repository-native sale boundary."
}

$prepareInstance = Read-ResultValue `
    -Text $prepare.Result `
    -Name "processInstance"
$verifyInstance = Read-ResultValue `
    -Text $verify.Result `
    -Name "processInstance"
if ([string]::Equals(
        $prepareInstance,
        $verifyInstance,
        [StringComparison]::Ordinal)) {
    throw "Authorization lease restart regression did not use two distinct process instances."
}
if ((Read-ResultValue -Text $verify.Result -Name "offlineAttestationAfterRestart") -ne "False" -or
    (Read-ResultValue -Text $verify.Result -Name "offlineDenial") -ne "offline_attestation_required" -or
    (Read-ResultValue -Text $verify.Result -Name "unauthorizedSaleSinkRows") -ne "0" -or
    (Read-ResultValue -Text $verify.Result -Name "unauthorizedPublicationOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $verify.Result -Name "freshOnlineRecovery") -ne "True") {
    throw "Authorization lease restart regression did not prove fail-closed authorization and online recovery."
}

Write-Host $smoke.Result
Write-Host $prepare.Result
Write-Host $verify.Result
Write-Host $capacity.Result
Write-Host "AUTHORIZATION_LEASE_ARTIFACT=$($smoke.Artifact)"
Write-Host "AUTHORIZATION_LEASE_RESTART_PREPARE_ARTIFACT=$($prepare.Artifact)"
Write-Host "AUTHORIZATION_LEASE_RESTART_VERIFY_ARTIFACT=$($verify.Artifact)"
Write-Host "AUTHORIZATION_LEASE_CLOCK_CAPACITY_ARTIFACT=$($capacity.Artifact)"
