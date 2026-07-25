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
        [string]$DiagnosticsDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Mode,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactName,
        [switch]$SeedTrustedSession,
        [switch]$RequirePreparedData
    )

    $DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
        .TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $DiagnosticsDirectory =
        [System.IO.Path]::GetFullPath($DiagnosticsDirectory)
            .TrimEnd(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
    $dataWithSeparator =
        $DataDirectory + [System.IO.Path]::DirectorySeparatorChar
    $diagnosticsWithSeparator =
        $DiagnosticsDirectory +
        [System.IO.Path]::DirectorySeparatorChar
    if ([string]::Equals(
            $DataDirectory,
            $DiagnosticsDirectory,
            [StringComparison]::OrdinalIgnoreCase) -or
        $DiagnosticsDirectory.StartsWith(
            $dataWithSeparator,
            [StringComparison]::OrdinalIgnoreCase) -or
        $DataDirectory.StartsWith(
            $diagnosticsWithSeparator,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Authorization lease diagnostics must be outside WIN7POS_DATA_DIR."
    }

    New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force | Out-Null
    if ($SeedTrustedSession) {
        New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
        $unexpectedEntries = @(
            Get-ChildItem -LiteralPath $DataDirectory -Force |
                Sort-Object -Property Name
        )
        if ($unexpectedEntries.Count -gt 0) {
            $entryNames = ($unexpectedEntries | ForEach-Object { $_.Name }) -join ", "
            throw "--seed-trusted-session requires a new or empty QA data directory. Found: $entryNames"
        }
    }
    elseif ($RequirePreparedData) {
        if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) {
            throw "Restart verification requires the prepared restart data directory: $DataDirectory"
        }
        $preparedEntries = @(Get-ChildItem -LiteralPath $DataDirectory -Force)
        if ($preparedEntries.Count -eq 0) {
            throw "Restart verification requires a non-empty prepared restart data directory."
        }
    }
    else {
        New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    }

    $quotedDataDirectory = '"' + $DataDirectory.Replace('"', '\"') + '"'
    $quotedDiagnosticsDirectory =
        '"' + $DiagnosticsDirectory.Replace('"', '\"') + '"'
    $arguments = @(
        "--data-dir",
        $quotedDataDirectory,
        "--diagnostics-dir",
        $quotedDiagnosticsDirectory,
        $Mode
    )
    if ($SeedTrustedSession) {
        $arguments += "--seed-trusted-session"
    }
    $standardOutputPath =
        Join-Path $DiagnosticsDirectory ($ArtifactName + ".stdout.txt")
    $standardErrorPath =
        Join-Path $DiagnosticsDirectory ($ArtifactName + ".stderr.txt")
    $process = Start-Process `
        -FilePath $harnessExe `
        -ArgumentList $arguments `
        -RedirectStandardOutput $standardOutputPath `
        -RedirectStandardError $standardErrorPath `
        -PassThru `
        -WindowStyle Hidden
    $artifact = Join-Path $DiagnosticsDirectory $ArtifactName
    $harnessErrorPath = Join-Path $DiagnosticsDirectory "harness-error.txt"
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(10000)
        $standardOutput = if (Test-Path -LiteralPath $standardOutputPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($standardOutputPath).Trim()
        } else {
            "stdout was not produced"
        }
        $standardError = if (Test-Path -LiteralPath $standardErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($standardErrorPath).Trim()
        } else {
            "stderr was not produced"
        }
        $harnessError = if (Test-Path -LiteralPath $harnessErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($harnessErrorPath).Trim()
        } else {
            "harness-error.txt was not produced"
        }
        throw "Authorization lease smoke mode $Mode exceeded its 180-second timeout. Harness error: $harnessError. Stdout: $standardOutput. Stderr: $standardError. Artifact: $artifact"
    }
    if ($process.ExitCode -ne 0) {
        $harnessError = if (Test-Path -LiteralPath $harnessErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($harnessErrorPath).Trim()
        } else {
            "harness-error.txt was not produced"
        }
        $standardOutput = if (Test-Path -LiteralPath $standardOutputPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($standardOutputPath).Trim()
        } else {
            "stdout was not produced"
        }
        $standardError = if (Test-Path -LiteralPath $standardErrorPath -PathType Leaf) {
            [System.IO.File]::ReadAllText($standardErrorPath).Trim()
        } else {
            "stderr was not produced"
        }
        throw "Authorization lease smoke mode $Mode failed with exit code $($process.ExitCode). Harness error: $harnessError. Stdout: $standardOutput. Stderr: $standardError. Artifact: $artifact"
    }
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Authorization lease smoke mode $Mode did not produce its result artifact: $artifact"
    }

    $result = [System.IO.File]::ReadAllText($artifact).Trim()
    if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal)) {
        throw "Authorization lease smoke mode $Mode did not report PASS. Result: $result. Artifact: $artifact"
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

$runRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLease." + [Guid]::NewGuid().ToString("N"))))
$mainDataDir = Join-Path $runRoot "main-data"
$mainDiagnosticsDir = Join-Path $runRoot "main-diagnostics"
$restartDataDir = Join-Path $runRoot "restart-data"
$restartDiagnosticsDir = Join-Path $runRoot "restart-diagnostics"
$capacityDataDir = Join-Path $runRoot "capacity-data"
$capacityDiagnosticsDir = Join-Path $runRoot "capacity-diagnostics"

try {
    $smoke = Invoke-AuthorizationHarness `
        -DataDirectory $mainDataDir `
        -DiagnosticsDirectory $mainDiagnosticsDir `
        -Mode "--authorization-lease-smoke" `
        -ArtifactName "authorization-lease-smoke.txt" `
        -SeedTrustedSession
    $prepare = Invoke-AuthorizationHarness `
        -DataDirectory $restartDataDir `
        -DiagnosticsDirectory $restartDiagnosticsDir `
        -Mode "--authorization-lease-restart-prepare" `
        -ArtifactName "authorization-lease-restart-prepare.txt" `
        -SeedTrustedSession
    $verify = Invoke-AuthorizationHarness `
        -DataDirectory $restartDataDir `
        -DiagnosticsDirectory $restartDiagnosticsDir `
        -Mode "--authorization-lease-restart-verify" `
        -ArtifactName "authorization-lease-restart-verify.txt" `
        -RequirePreparedData
    $capacity = Invoke-AuthorizationHarness `
        -DataDirectory $capacityDataDir `
        -DiagnosticsDirectory $capacityDiagnosticsDir `
        -Mode "--authorization-lease-clock-capacity-smoke" `
        -ArtifactName "authorization-lease-clock-capacity.txt" `
        -SeedTrustedSession

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
    (Read-ResultValue -Text $smoke.Result -Name "saleRevocationDemandCount") -ne "4" -or
    (Read-ResultValue -Text $smoke.Result -Name "denialCallbacksAfterGateRelease") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleGenerationRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleGenerationRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleGenerationDemandCount") -ne "4" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitExpiryRaceSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitExpiryRaceOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitExpiryDemandCount") -ne "5" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderExpiryDenied") -ne "True" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderSinkRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $smoke.Result -Name "saleCommitBlockedReaderDemandCount") -ne "2" -or
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
    Write-Host "AUTHORIZATION_LEASE_DATA_DIAGNOSTICS_SEPARATED=True"
    Write-Host "AUTHORIZATION_LEASE_RESTART_SEEDED_ONCE=True"
    Write-Host "AUTHORIZATION_LEASE_DIAGNOSTICS_COLLECTED=True"
}
finally {
    $qaRootWithSeparator =
        $qaRoot.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    $runRootName = [System.IO.Path]::GetFileName($runRoot)
    if (-not $runRoot.StartsWith(
            $qaRootWithSeparator,
            [StringComparison]::OrdinalIgnoreCase) -or
        $runRootName -notmatch '^AuthorizationLease\.[0-9a-f]{32}$') {
        throw "Refusing to clean an invalid authorization lease run root: $runRoot"
    }
    if (Test-Path -LiteralPath $runRoot -PathType Container) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force
    }
}
