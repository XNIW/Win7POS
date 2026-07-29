[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [string]$DataDirectory = 'C:\POSData\Win7POSArticleMutationAcceptance',
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

$runId = 'ASUSART_' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') +
    '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant()
$evidenceDirectory = Join-Path 'C:\Dev\_codex-evidence' (
    'win7pos-pos-article-sync-v1-' + $runId)
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
        runId = $runId
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
    @(
        '# WIN7POS POS ARTICLE SYNC V1 STAGING RESULT'
        ''
        '- status: ' + $(if ($Passed) { 'PASS' } else { 'FAIL' })
        '- code: ' + $Code
        '- runId: ' + $runId
        '- exactMain: validated before build'
        '- logicalRuns: ' + $runnerResult.logicalRuns
        '- automaticRetry: false'
        '- hardwareActions: 0'
        '- evidenceDirectory: ' + $evidenceDirectory
    ) | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory 'FINAL-RESULT.md'
    ) -Encoding UTF8

    Write-Output ('WIN7POS_STAGING_ACCEPTANCE_EVIDENCE=' + $evidenceDirectory)
    Write-Output ('WIN7POS_STAGING_ACCEPTANCE_RUN_ID=' + $runId)
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
        'C:\POSData\Win7POSArticleMutationAcceptance')
    if (-not [string]::Equals(
        $fullDataDirectory,
        $expectedDataDirectory,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_data_directory_invalid' `
            -Passed $false
    }

    $repoRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..'))
    $headSha = (& git -C $repoRoot rev-parse HEAD).Trim()
    $originMainSha = (& git -C $repoRoot rev-parse origin/main).Trim()
    $trackedChanges = @(& git -C $repoRoot status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($headSha) -or
        -not [string]::Equals(
            $headSha,
            $originMainSha,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $trackedChanges.Count -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_exact_main_required' `
            -Passed $false
    }

    @(
        'runId=' + $runId
        'profile=' + $Profile
        'dataDirectory=' + $fullDataDirectory
        'head=' + $headSha
        'originMain=' + $originMainSha
        'exactMain=True'
        'trackedWorktreeClean=True'
        'logicalRuns=1'
        'automaticRetry=False'
        'hardwareActions=0'
    ) | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory 'preflight.txt'
    ) -Encoding UTF8

    $fixtureDirectory = Join-Path $repoRoot (
        'tests\fixtures\POS-ARTICLE-MUTATION-V1')
    @(
        'request=' + (
            Get-FileHash -Algorithm SHA256 -LiteralPath (
                Join-Path $fixtureDirectory 'article-mutation-v1.request.json'
            )
        ).Hash.ToLowerInvariant()
        'response=' + (
            Get-FileHash -Algorithm SHA256 -LiteralPath (
                Join-Path $fixtureDirectory 'article-mutation-v1.response.json'
            )
        ).Hash.ToLowerInvariant()
        'firstLogin=' + (
            Get-FileHash -Algorithm SHA256 -LiteralPath (
                Join-Path $fixtureDirectory (
                    'first-login-offline-authorization-v1.response.json')
            )
        ).Hash.ToLowerInvariant()
    ) | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory 'contract-digests.txt'
    ) -Encoding UTF8

    $dotnetPath = 'C:\Dev\dotnet10\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_canonical_sdk_missing' `
            -Passed $false
    }
    $buildEvidence = Join-Path $evidenceDirectory 'exact-main-build.txt'
    & $dotnetPath build (
        Join-Path $repoRoot 'Win7POS.slnx'
    ) -c Release --no-restore *> $buildEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_exact_main_solution_build_failed' `
            -Passed $false
    }
    & $dotnetPath build (
        Join-Path $repoRoot (
            'tests\Win7POS.Wpf.UiSmokeHarness\' +
            'Win7POS.Wpf.UiSmokeHarness.csproj')
    ) -c Release -p:Platform=x86 -p:PlatformTarget=x86 `
        --no-restore *>> $buildEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_exact_main_harness_build_failed' `
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
            '--run-id', $runId,
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
        $acceptanceFailureCode = Get-Win7PosAcceptanceResultCode `
            -Code ([string]$acceptanceResult.code)
        $requiredEvidence = @(
            'preflight.txt',
            'exact-main-build.txt',
            'contract-digests.txt',
            'first-login-result.json',
            'catalog-exactness.json',
            'article-mutation-results.json',
            'local-outbox-state.json',
            'price-history-counts.txt',
            'stock-movement-counts.txt',
            'no-echo-result.txt',
            'redaction-scan.txt',
            'article-mutation-product-editor-1024x768.png',
            'article-mutation-sync-center-conflict-1024x768.png',
            'staging-acceptance-products-readonly.png',
            'CLEANUP-MANIFEST.json',
            'NEXT-CODEX-MAC-FINAL-CLEANUP.md'
        )
        $evidenceComplete = $true
        foreach ($requiredName in $requiredEvidence) {
            if (-not (Test-Path -LiteralPath (
                Join-Path $evidenceDirectory $requiredName
            ) -PathType Leaf)) {
                $evidenceComplete = $false
                break
            }
        }
        $completePass = $processResult.ExitCode -eq 0 -and
            $acceptanceResult.passed -eq $true -and
            [int]$acceptanceResult.logicalRuns -eq 1 -and
            $acceptanceResult.offlineAuthorizationValid -eq $true -and
            $acceptanceResult.exactnessVerified -eq $true -and
            $acceptanceResult.saleSafe -eq $true -and
            $acceptanceResult.posUnlocked -eq $true -and
            $acceptanceResult.articleMutationsPassed -eq $true -and
            [int]$acceptanceResult.articleWaitingDependency -eq 0 -and
            [int]$acceptanceResult.articlePending -eq 0 -and
            [int]$acceptanceResult.articleInProgress -eq 0 -and
            [int]$acceptanceResult.articleRetryWait -eq 0 -and
            [int]$acceptanceResult.articleBlockedConflicts -eq 1 -and
            [int]$acceptanceResult.hardwareActions -eq 0 -and
            $acceptanceResult.zeroEcho -eq $true -and
            $acceptanceResult.logRedactionPassed -eq $true -and
            $acceptanceResult.evidenceRedactionPassed -eq $true -and
            $evidenceComplete -and
            [string]::Equals(
                [string]$acceptanceResult.runId,
                $runId,
                [System.StringComparison]::Ordinal)
    }
    catch {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ResultMissingOrInvalid `
            -Code 'acceptance_result_invalid' `
            -Passed $false `
            -ProcessResult $processResult
    }

    if (-not $completePass) {
        $completeFailureCode = if ($acceptanceResult.passed -ne $true) {
            $acceptanceFailureCode
        }
        elseif (-not $evidenceComplete) {
            'acceptance_evidence_incomplete'
        }
        else {
            'acceptance_complete_failure'
        }
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.CompleteFailure `
            -Code $completeFailureCode `
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
