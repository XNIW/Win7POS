[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $repoRoot (
    'scripts\qa\Win7PosAcceptanceProcessRunner.psm1')
$acceptanceWrapper = Join-Path $repoRoot (
    'scripts\qa\Invoke-Win7PosStagingAcceptance.ps1')
$syntheticHarness = Join-Path $PSScriptRoot (
    'fixtures\Invoke-SyntheticAcceptanceHarness.ps1')
$mutexHolder = Join-Path $PSScriptRoot (
    'fixtures\Hold-Win7PosAcceptanceMutex.ps1')
Import-Module $modulePath -Force

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'win7pos-acceptance-runner-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function Assert-Runner {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

try {
    $wrapperTokens = $null
    $wrapperParseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $acceptanceWrapper,
        [ref]$wrapperTokens,
        [ref]$wrapperParseErrors) | Out-Null
    Assert-Runner (
        $wrapperParseErrors.Count -eq 0
    ) ('Acceptance wrapper has parser errors: ' +
        (($wrapperParseErrors | ForEach-Object {
            $_.Message
        }) -join ' | '))
    $wrapperText = [System.IO.File]::ReadAllText($acceptanceWrapper)
    Assert-Runner (
        $wrapperText -match
        "-c Release -p:PlatformTarget=x86[\s\S]{0,750}" +
        "bin\\Release\\net48\\Win7POS\.Wpf\.UiSmokeHarness\.exe"
    ) 'Acceptance wrapper did not run its x86 Release harness output.'
    Assert-Runner (
        $wrapperText -notmatch
        "bin\\x86\\Release\\net48\\Win7POS\.Wpf\.UiSmokeHarness\.exe"
    ) 'Acceptance wrapper selected the parallel untrusted x86 output path.'

    Assert-Runner (
        (Get-Win7PosAcceptanceResultCode `
            -Code ' Bootstrap_Catalog_Pull_HTTP_5XX ') -eq
        'bootstrap_catalog_pull_http_5xx'
    ) 'Typed harness result code was not preserved.'
    Assert-Runner (
        (Get-Win7PosAcceptanceResultCode `
            -Code '../raw request body') -eq
        'acceptance_result_invalid_code'
    ) 'Unsafe harness result code was not rejected.'
    Assert-Runner (
        (Get-Win7PosAcceptanceResultCode -Code '') -eq
        'acceptance_result_invalid_code'
    ) 'Empty harness result code was not rejected.'

    $pwshPath = (Get-Process -Id $PID).Path
    $waitEvidence = Join-Path $testRoot 'wait-evidence'
    $waitMarker = Join-Path $testRoot 'wait-marker.txt'
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $waited = Invoke-Win7PosWaitedProcess `
        -FilePath $pwshPath `
        -ArgumentList @(
            '-NoProfile',
            '-File', $syntheticHarness,
            '-AcceptanceOutput', $waitEvidence,
            '-DelayMilliseconds', '800',
            '-SyntheticExitCode', '7',
            '-MarkerPath', $waitMarker
        ) `
        -TimeoutMilliseconds 10000 `
        -EvidenceDirectory $waitEvidence
    $timer.Stop()

    Assert-Runner ($waited.Started) 'Synthetic harness did not start.'
    Assert-Runner (-not $waited.TimedOut) 'Synthetic harness timed out unexpectedly.'
    Assert-Runner ($waited.ExitCode -eq 7) 'Harness exit code was not propagated.'
    Assert-Runner ($timer.ElapsedMilliseconds -ge 700) 'Runner returned before harness exit.'
    Assert-Runner (-not $waited.OrphanRemaining) 'Completed harness remained orphaned.'
    Assert-Runner (
        $waited.EvidenceDirectory -eq
        [System.IO.Path]::GetFullPath($waitEvidence)
    ) 'Evidence path was not returned.'
    Assert-Runner (
        (Get-Content -LiteralPath $waitMarker).Count -eq 1
    ) 'Synthetic harness launched more than once.'

    $launchFailureEvidence = Join-Path $testRoot 'launch-failure-evidence'
    $launchFailure = Invoke-Win7PosWaitedProcess `
        -FilePath (Join-Path $testRoot 'missing-harness.exe') `
        -ArgumentList @('--synthetic-unused') `
        -TimeoutMilliseconds 10000 `
        -EvidenceDirectory $launchFailureEvidence
    Assert-Runner (-not $launchFailure.Started) 'Missing harness was reported as started.'
    Assert-Runner (
        -not [string]::IsNullOrWhiteSpace($launchFailure.LaunchError)
    ) 'Harness launch failure did not retain a typed exception name.'
    Assert-Runner (
        -not $launchFailure.OrphanRemaining
    ) 'Failed harness launch reported an orphan process.'

    $timeoutEvidence = Join-Path $testRoot 'timeout-evidence'
    $timeoutMarker = Join-Path $testRoot 'timeout-marker.txt'
    $timeoutRunId = 'ASUSART_POST_PR63_RUNNER_TIMEOUT'
    $runConsumedMarker = Join-Path $timeoutEvidence (
        'run-consumed-redacted.json')
    # Keep the timeout below the synthetic 10-second delay while allowing a
    # new pwsh process to start and atomically publish its durable marker.
    $timedOut = Invoke-Win7PosWaitedProcess `
        -FilePath $pwshPath `
        -ArgumentList @(
            '-NoProfile',
            '-File', $syntheticHarness,
            '-AcceptanceOutput', $timeoutEvidence,
            '-DelayMilliseconds', '10000',
            '-SyntheticExitCode', '0',
            '-MarkerPath', $timeoutMarker,
            '-RunConsumedMarkerPath', $runConsumedMarker,
            '-RunId', $timeoutRunId
        ) `
        -TimeoutMilliseconds 5000 `
        -EvidenceDirectory $timeoutEvidence

    Assert-Runner ($timedOut.TimedOut) 'Timeout was not reported.'
    Assert-Runner (-not $timedOut.OrphanRemaining) 'Timed-out harness remained orphaned.'
    Assert-Runner (
        $null -eq (Get-Process -Id $timedOut.ProcessId -ErrorAction SilentlyContinue)
    ) 'Timed-out process still exists.'
    Assert-Runner (
        Test-Path -LiteralPath $runConsumedMarker -PathType Leaf
    ) 'Timed-out harness did not publish the durable consumed marker.'
    Assert-Runner (
        (Get-Win7PosAcceptanceLogicalRunCount `
            -EvidenceDirectory $timeoutEvidence `
            -RunId $timeoutRunId) -eq 1
    ) 'Timeout after the consumed marker incorrectly restored run budget.'

    $temporaryOnlyEvidence = Join-Path $testRoot (
        'temporary-marker-evidence')
    New-Item -ItemType Directory -Path $temporaryOnlyEvidence -Force |
        Out-Null
    Set-Content `
        -LiteralPath (Join-Path $temporaryOnlyEvidence (
            'run-consumed-redacted.json.' +
            [Guid]::NewGuid().ToString('N') +
            '.tmp')) `
        -Value '{"requestReachedServer":true}' `
        -Encoding UTF8
    Assert-Runner (
        (Get-Win7PosAcceptanceLogicalRunCount `
            -EvidenceDirectory $temporaryOnlyEvidence `
            -RunId $timeoutRunId) -eq 1
    ) 'Flushed pre-rename marker incorrectly restored run budget.'

    $mutexName = 'Local\Win7POS.RunnerTest.' +
        [Guid]::NewGuid().ToString('N')
    $mutexMarker = Join-Path $testRoot 'mutex-marker.txt'
    $holder = Start-Process `
        -FilePath $pwshPath `
        -ArgumentList @(
            '-NoProfile',
            '-File', $mutexHolder,
            '-ModulePath', $modulePath,
            '-MutexName', $mutexName,
            '-MarkerPath', $mutexMarker,
            '-HoldMilliseconds', '1500'
        ) `
        -PassThru `
        -WindowStyle Hidden
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $mutexMarker) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    Assert-Runner (
        (Test-Path -LiteralPath $mutexMarker)
    ) 'Mutex holder did not acquire the lock.'

    $blockedLease = Enter-Win7PosAcceptanceLock -Name $mutexName
    try {
        Assert-Runner (
            -not $blockedLease.Acquired
        ) 'Second acceptance acquired the single-instance mutex.'
    }
    finally {
        Exit-Win7PosAcceptanceLock -Lease $blockedLease
    }
    $holder.WaitForExit(10000) | Out-Null
    Assert-Runner ($holder.HasExited) 'Mutex holder did not exit.'
    $holder.Dispose()

    $releasedLease = Enter-Win7PosAcceptanceLock -Name $mutexName
    try {
        Assert-Runner (
            $releasedLease.Acquired
        ) 'Released single-instance mutex was not reusable.'
    }
    finally {
        Exit-Win7PosAcceptanceLock -Lease $releasedLease
    }

    Write-Output 'WIN7POS_STAGING_ACCEPTANCE_RUNNER_TEST=PASS'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
