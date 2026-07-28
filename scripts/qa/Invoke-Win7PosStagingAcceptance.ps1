[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [string]$DataDirectory = 'C:\POSData\Win7POSAutomatedStagingAcceptance',
    [ValidateRange(15, 60)][int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Win7PosAcceptanceProcessRunner.psm1') -Force

$runnerExit = @{
    ProfileMissingOrInvalid = 2
    CompleteFailure = 20
    ResultMissingOrInvalid = 21
    Timeout = 22
    AlreadyRunning = 23
    LaunchFailure = 24
}

$evidenceDirectory = Join-Path 'C:\Dev\_codex-evidence' (
    'win7pos-staging-acceptance-' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null

function Complete-Win7PosAcceptanceRunner {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][bool]$Passed,
        $ProcessResult = $null
    )

    $runnerResult = [ordered]@{
        code = $Code
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        evidenceDirectory = $evidenceDirectory
        harnessExitCode = if ($null -ne $ProcessResult) {
            $ProcessResult.ExitCode
        } else {
            $null
        }
        logicalRuns = if ($null -ne $ProcessResult -and $ProcessResult.Started) {
            1
        } else {
            0
        }
        orphanRemaining = if ($null -ne $ProcessResult) {
            [bool]$ProcessResult.OrphanRemaining
        } else {
            $false
        }
        passed = $Passed
        processId = if ($null -ne $ProcessResult) {
            $ProcessResult.ProcessId
        } else {
            $null
        }
        timedOut = if ($null -ne $ProcessResult) {
            [bool]$ProcessResult.TimedOut
        } else {
            $false
        }
    }
    $runnerResult |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (
            Join-Path $evidenceDirectory 'staging-acceptance-runner-result.json'
        ) -Encoding UTF8

    Write-Output ('WIN7POS_STAGING_ACCEPTANCE_EVIDENCE=' + $evidenceDirectory)
    if ($Passed) {
        Write-Output ('WIN7POS_AUTOMATED_STAGING_ACCEPTANCE=PASS evidence=' +
            $evidenceDirectory)
    }
    else {
        Write-Output ('WIN7POS_AUTOMATED_STAGING_ACCEPTANCE=FAIL code=' +
            $Code + ' evidence=' + $evidenceDirectory)
    }
    exit $ExitCode
}

$lease = Enter-Win7PosAcceptanceLock
if (-not $lease.Acquired) {
    $lease.Mutex.Dispose()
    Complete-Win7PosAcceptanceRunner `
        -ExitCode $runnerExit.AlreadyRunning `
        -Code 'acceptance_already_running' `
        -Passed $false
}

try {
    if (Test-Win7PosAcceptanceProcessActive) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.AlreadyRunning `
            -Code 'acceptance_process_active' `
            -Passed $false
    }

    $profileState = Test-Win7PosQaCredentialProfile -Profile $Profile
    if (-not $profileState.Exists -or -not $profileState.Valid) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ProfileMissingOrInvalid `
            -Code 'qa_credential_profile_missing_or_invalid' `
            -Passed $false
    }

    $fullDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
    $expectedDataDirectory = [System.IO.Path]::GetFullPath(
        'C:\POSData\Win7POSAutomatedStagingAcceptance')
    if (-not [string]::Equals(
        $fullDataDirectory,
        $expectedDataDirectory,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_data_directory_invalid' `
            -Passed $false
    }

    $fullHarnessPath = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot (
            '..\..\tests\Win7POS.Wpf.UiSmokeHarness\' +
            'bin\x86\Release\net48\Win7POS.Wpf.UiSmokeHarness.exe')))
    if (-not (Test-Path -LiteralPath $fullHarnessPath -PathType Leaf)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_harness_not_built' `
            -Passed $false
    }

    if (Test-Path -LiteralPath $fullDataDirectory) {
        $backup = $fullDataDirectory + '.backup-' +
            [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
        Move-Item `
            -LiteralPath $fullDataDirectory `
            -Destination $backup `
            -ErrorAction Stop
    }
    New-Item -ItemType Directory -Path $fullDataDirectory -Force | Out-Null

    $processResult = Invoke-Win7PosWaitedProcess `
        -FilePath $fullHarnessPath `
        -ArgumentList @(
            '--data-dir', $fullDataDirectory,
            '--staging-acceptance',
            '--profile', $Profile,
            '--acceptance-output', $evidenceDirectory
        ) `
        -TimeoutMilliseconds ($TimeoutMinutes * 60 * 1000) `
        -EvidenceDirectory $evidenceDirectory

    if (-not $processResult.Started) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_harness_launch_failed' `
            -Passed $false `
            -ProcessResult $processResult
    }
    if ($processResult.TimedOut -or $processResult.OrphanRemaining) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.Timeout `
            -Code 'acceptance_harness_timeout' `
            -Passed $false `
            -ProcessResult $processResult
    }

    $resultPath = Join-Path $evidenceDirectory 'staging-acceptance-result.json'
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ResultMissingOrInvalid `
            -Code 'acceptance_result_missing' `
            -Passed $false `
            -ProcessResult $processResult
    }

    try {
        $acceptanceResult = Get-Content -Raw -LiteralPath $resultPath |
            ConvertFrom-Json -ErrorAction Stop
        $completePass = $processResult.ExitCode -eq 0 -and
            $acceptanceResult.passed -eq $true -and
            [int]$acceptanceResult.logicalRuns -eq 1
    }
    catch {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ResultMissingOrInvalid `
            -Code 'acceptance_result_invalid' `
            -Passed $false `
            -ProcessResult $processResult
    }

    if (-not $completePass) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.CompleteFailure `
            -Code 'acceptance_complete_failure' `
            -Passed $false `
            -ProcessResult $processResult
    }

    Complete-Win7PosAcceptanceRunner `
        -ExitCode 0 `
        -Code 'success' `
        -Passed $true `
        -ProcessResult $processResult
}
finally {
    Exit-Win7PosAcceptanceLock -Lease $lease
}
