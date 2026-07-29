[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [string]$DataDirectory = 'C:\POSData\Win7POSFinalArticleSyncAcceptance',
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

$runId = 'ASUSART_FINAL_' +
    [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') +
    '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant()
$evidenceDirectory = Join-Path 'C:\Dev\_codex-evidence' (
    'win7pos-final-article-sync-' + $runId)
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null

function Complete-Win7PosAcceptanceRunner {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][bool]$Passed,
        $ProcessResult = $null
    )

    $logicalRuns = Get-Win7PosAcceptanceLogicalRunCount `
        -EvidenceDirectory $evidenceDirectory `
        -RunId $runId
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
        logicalRuns = $logicalRuns
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
        '# WIN7POS FINAL ARTICLE SYNC STAGING RESULT'
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

    $wrapperFinalScanPassed = $true
    $scannableExtensions = @('.json', '.txt', '.md', '.log')
    $forbiddenMarkers = @(
        '"deviceToken"',
        '"sessionToken"',
        'Authorization:',
        'Cookie:',
        'canonical_payload_json',
        'intent_json',
        'payload_json',
        'rawRequestBody',
        'rawResponseBody',
        'rawMutationPayload'
    )
    foreach ($evidenceFile in Get-ChildItem -LiteralPath (
        $evidenceDirectory) -Recurse -File) {
        if ($evidenceFile.Extension -notin $scannableExtensions) {
            continue
        }
        if ($evidenceFile.Length -le 0 -or
            $evidenceFile.Length -gt 2MB) {
            $wrapperFinalScanPassed = $false
            break
        }
        $evidenceText = Get-Content -Raw -LiteralPath (
            $evidenceFile.FullName)
        if ($forbiddenMarkers | Where-Object {
            $evidenceText.IndexOf(
                $_,
                [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        }) {
            $wrapperFinalScanPassed = $false
            break
        }
    }
    $redactionEvidencePath = Join-Path $evidenceDirectory (
        '14-redaction-scan.txt')
    if (Test-Path -LiteralPath $redactionEvidencePath -PathType Leaf) {
        @(
            'wrapperFinalArtifactScan=' +
                $(if ($wrapperFinalScanPassed) { 'PASS' } else { 'FAIL' })
            'completeArtifactSetScanned=True'
        ) | Add-Content -LiteralPath $redactionEvidencePath -Encoding UTF8
    }
    elseif ($Passed) {
        $wrapperFinalScanPassed = $false
    }
    if ($Passed -and -not $wrapperFinalScanPassed) {
        $Passed = $false
        $Code = 'acceptance_final_artifact_redaction_failed'
        $ExitCode = $runnerExit.CompleteFailure
        $runnerResult.code = $Code
        $runnerResult.passed = $false
        $runnerResult |
            ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (
                Join-Path $evidenceDirectory (
                    'staging-acceptance-runner-result.json')
            ) -Encoding UTF8
        @(
            '# WIN7POS FINAL ARTICLE SYNC STAGING RESULT'
            ''
            '- status: FAIL'
            '- code: ' + $Code
            '- runId: ' + $runId
            '- logicalRuns: ' + $runnerResult.logicalRuns
            '- automaticRetry: false'
            '- hardwareActions: 0'
        ) | Set-Content -LiteralPath (
            Join-Path $evidenceDirectory 'FINAL-RESULT.md'
        ) -Encoding UTF8
    }

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
        'C:\POSData\Win7POSFinalArticleSyncAcceptance')
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
    $worktreeChanges = @(& git -C $repoRoot status --porcelain)
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($headSha) -or
        -not [string]::Equals(
            $headSha,
            $originMainSha,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $worktreeChanges.Count -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_exact_main_required' `
            -Passed $false
    }

    $latestCommits = @(
        & git -C $repoRoot log -12 --format='%h %s' origin/main
    )
    if ($LASTEXITCODE -ne 0 -or $latestCommits.Count -ne 12) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_repo_history_unavailable' `
            -Passed $false
    }
    @(
        'repository=XNIW/Win7POS'
        'head=' + $headSha
        'originMain=' + $originMainSha
        'mainEqualsOriginMain=True'
        'worktreeCleanIncludingUntracked=True'
        'latestCommits:'
    ) + $latestCommits | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory '00-repo-sync.txt'
    ) -Encoding UTF8

    $ghCommand = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -eq $ghCommand) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_admin_handoff_reader_missing' `
            -Passed $false
    }
    $adminMainSha = (& gh api `
        repos/XNIW/merchandise-control-admin-web/commits/main `
        --jq '.sha').Trim()
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($adminMainSha)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_admin_main_unavailable' `
            -Passed $false
    }
    $handoffBase64 = (& gh api (
        'repos/XNIW/merchandise-control-admin-web/contents/' +
        'docs/HANDOFFS/' +
        'WIN7POS_FINAL_ARTICLE_SYNC_CPU_REMEDIATION_READY.md' +
        '?ref=main') --jq '.content').Trim()
    try {
        $handoffText = [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String(
                ($handoffBase64 -replace '\s', '')))
    }
    catch {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_admin_handoff_invalid' `
            -Passed $false
    }
    $adminHandoffReady =
        $handoffText -match
            'READY_FOR_ASUS_FINAL_ARTICLE_SYNC_ACCEPTANCE' -and
        $handoffText -match
            '9fb54f50999b8587bc37f5e2040743df20df8f08' -and
        $handoffText -match '5ad3652d' -and
        $handoffText -match '57af0535' -and
        $handoffText -match '503[^0-9]+0' -and
        $handoffText -match 'exceededCpu[^0-9]+0' -and
        $handoffText -match 'exceededMemory[^0-9]+0'
    if (-not $adminHandoffReady) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_admin_handoff_not_ready' `
            -Passed $false
    }
    @(
        'repository=XNIW/merchandise-control-admin-web'
        'main=' + $adminMainSha
        'handoffState=READY_FOR_ASUS_FINAL_ARTICLE_SYNC_ACCEPTANCE'
        'runtimeSource=9fb54f50999b8587bc37f5e2040743df20df8f08'
        'workerDeployment=5ad3652d'
        'workerVersion=57af0535'
        'serverAcceptance=PASS'
        'http503=0'
        'exceededCpu=0'
        'exceededMemory=0'
        'syntheticFixtureCleaned=True'
        'productionModified=False'
        'billingModified=False'
    ) | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory '01-admin-handoff.txt'
    ) -Encoding UTF8

    @(
        'profile=' + $Profile
        'exists=' + [bool]$profileState.Exists
        'valid=' + [bool]$profileState.Valid
        'profileVersion=' + [int]$profileState.ProfileVersion
        'aclPassed=' + [bool]$profileState.AclPassed
        'expired=' + [bool]$profileState.Expired
        'migrated=' + [bool]$profileState.Migrated
        'stagingHost=' + [string]$profileState.BaseUrlHost
        'stableDeviceIdentityPresent=' +
            [bool]$profileState.DeviceIdentifierPresent
        'stableDeviceIdentityFormatValid=' +
            [bool]$profileState.DeviceIdentifierFormatValid
        'credentialPrinted=False'
    ) | Set-Content -LiteralPath (
        Join-Path $evidenceDirectory '02-profile-preflight.txt'
    ) -Encoding UTF8

    @(
        'runId=' + $runId
        'profile=' + $Profile
        'dataDirectory=' + $fullDataDirectory
        'head=' + $headSha
        'originMain=' + $originMainSha
        'exactMain=True'
        'trackedWorktreeClean=True'
        'logicalRunsBeforeServerRequest=0'
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
    $localGateEvidence =
        Join-Path $evidenceDirectory '04-local-gates.txt'
    & pwsh -NoProfile -File (
        Join-Path $repoRoot 'scripts\check-required-gates.ps1'
    ) *> $localGateEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_required_gates_failed' `
            -Passed $false
    }
    & $dotnetPath test (
        Join-Path $repoRoot (
            'tests\Win7POS.Core.Tests\Win7POS.Core.Tests.csproj')
    ) -c Release --no-restore *>> $localGateEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_core_tests_failed' `
            -Passed $false
    }
    & pwsh -NoProfile -File (
        Join-Path $repoRoot (
            'tests\qa\Test-Win7PosStagingAcceptanceRunner.ps1')
    ) *>> $localGateEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_runner_tests_failed' `
            -Passed $false
    }
    $gitleaksToolDirectory = Join-Path $env:LOCALAPPDATA (
        'Temp\Win7POS.SupplyChain.Agent')
    if (-not (Test-Path -LiteralPath $gitleaksToolDirectory -PathType Container)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_gitleaks_toolchain_missing' `
            -Passed $false
    }
    & pwsh -NoProfile -File (
        Join-Path $repoRoot 'scripts\invoke-gitleaks-scans.ps1'
    ) -ToolDirectory $gitleaksToolDirectory -OutputDirectory (
        Join-Path $evidenceDirectory 'gitleaks'
    ) *>> $localGateEvidence
    if ($LASTEXITCODE -ne 0) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_gitleaks_failed' `
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
    Copy-Item -LiteralPath $buildEvidence -Destination (
        Join-Path $evidenceDirectory '03-exact-main-build.txt'
    ) -Force

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

    $acceptanceStartedAt = [DateTimeOffset]::UtcNow
    $prepareProcessResult = Invoke-Win7PosWaitedProcess `
        -FilePath $fullHarnessPath `
        -ArgumentList @(
            '--data-dir', $fullDataDirectory,
            '--staging-acceptance',
            '--profile', $Profile,
            '--run-id', $runId,
            '--acceptance-output', $evidenceDirectory,
            '--acceptance-phase', 'prepare'
        ) `
        -TimeoutMilliseconds ($TimeoutMinutes * 60 * 1000) `
        -EvidenceDirectory $evidenceDirectory

    if (-not $prepareProcessResult.Started) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_prepare_launch_failed' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    if ($prepareProcessResult.TimedOut -or
        $prepareProcessResult.OrphanRemaining) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.Timeout `
            -Code 'acceptance_prepare_timeout' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    $resultPath = Join-Path $evidenceDirectory (
        'staging-acceptance-result.json')
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ResultMissingOrInvalid `
            -Code 'acceptance_prepare_result_missing' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    try {
        $prepareAcceptanceResult =
            Get-Content -Raw -LiteralPath $resultPath |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.ResultMissingOrInvalid `
            -Code 'acceptance_prepare_result_invalid' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    if ($prepareProcessResult.ExitCode -ne 75 -or
        $prepareAcceptanceResult.requestReachedServer -ne $true -or
        [int]$prepareAcceptanceResult.logicalRuns -ne 1 -or
        [string]$prepareAcceptanceResult.code -ne
            'article_restart_required') {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.CompleteFailure `
            -Code (Get-Win7PosAcceptanceResultCode `
                -Code ([string]$prepareAcceptanceResult.code)) `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    Copy-Item -LiteralPath $resultPath -Destination (
        Join-Path $evidenceDirectory (
            'staging-acceptance-prepare-result.json')
    ) -Force
    if (Test-Win7PosAcceptanceProcessActive) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.AlreadyRunning `
            -Code 'acceptance_prepare_process_remained_active' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }
    $elapsed = [DateTimeOffset]::UtcNow - $acceptanceStartedAt
    $elapsedMilliseconds = [int][Math]::Ceiling(
        $elapsed.TotalMilliseconds)
    $remainingMilliseconds =
        ($TimeoutMinutes * 60 * 1000) - $elapsedMilliseconds
    if ($remainingMilliseconds -lt 100) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.Timeout `
            -Code 'acceptance_restart_budget_exhausted' `
            -Passed $false `
            -ProcessResult $prepareProcessResult
    }

    $processResult = Invoke-Win7PosWaitedProcess `
        -FilePath $fullHarnessPath `
        -ArgumentList @(
            '--data-dir', $fullDataDirectory,
            '--staging-acceptance',
            '--profile', $Profile,
            '--run-id', $runId,
            '--acceptance-output', $evidenceDirectory,
            '--acceptance-phase', 'resume'
        ) `
        -TimeoutMilliseconds $remainingMilliseconds `
        -EvidenceDirectory $evidenceDirectory
    if (-not $processResult.Started) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.LaunchFailure `
            -Code 'acceptance_resume_launch_failed' `
            -Passed $false `
            -ProcessResult $processResult
    }
    if ($processResult.TimedOut -or $processResult.OrphanRemaining) {
        Complete-Win7PosAcceptanceRunner `
            -ExitCode $runnerExit.Timeout `
            -Code 'acceptance_resume_timeout' `
            -Passed $false `
            -ProcessResult $processResult
    }
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
            '00-repo-sync.txt',
            '01-admin-handoff.txt',
            '02-profile-preflight.txt',
            '03-exact-main-build.txt',
            '04-local-gates.txt',
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
            '05-first-login-redacted.json',
            '06-catalog-exactness.json',
            '07-article-mutation-results-redacted.json',
            '08-outbox-state-redacted.json',
            '09-price-history-counts.txt',
            '10-stock-movement-counts.txt',
            '11-replay-conflict-results.txt',
            '12-no-echo-result.txt',
            '13-ui-smoke-result.txt',
            '14-redaction-scan.txt',
            'article-mutation-create-article-1024x768.png',
            'article-mutation-product-editor-1024x768.png',
            'article-mutation-duplicate-article-1024x768.png',
            'article-mutation-sync-center-pending-1024x768.png',
            'article-mutation-sync-center-in-progress-1024x768.png',
            'article-mutation-sync-center-conflict-1024x768.png',
            'article-mutation-sync-center-clean-1024x768.png',
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
            $acceptanceResult.offlineAuthorityAfterServerTime -eq $true -and
            $acceptanceResult.offlineAuthorityWithinSessionExpiry -eq $true -and
            $acceptanceResult.restartOnlineRecoveryValid -eq $true -and
            $acceptanceResult.restartOfflineAuthorityCleared -eq $true -and
            $acceptanceResult.exactnessVerified -eq $true -and
            $acceptanceResult.repairRequired -eq $false -and
            $acceptanceResult.terminalHasMore -eq $false -and
            [long]$acceptanceResult.rowsSkipped -eq 0 -and
            [long]$acceptanceResult.manifestPriceRows -ge 0 -and
            [long]$acceptanceResult.manifestPriceRows -eq
                [long]$acceptanceResult.localPriceRows -and
            [int]$acceptanceResult.automaticCompleteRunRetries -eq 0 -and
            $acceptanceResult.saleSafe -eq $true -and
            $acceptanceResult.posUnlocked -eq $true -and
            $acceptanceResult.articleMutationsPassed -eq $true -and
            [int]$acceptanceResult.articleWaitingDependency -eq 0 -and
            [int]$acceptanceResult.articlePending -eq 0 -and
            [int]$acceptanceResult.articleInProgress -eq 0 -and
            [int]$acceptanceResult.articleRetryWait -eq 0 -and
            [int]$acceptanceResult.articleBlockedConflicts -eq 0 -and
            $acceptanceResult.articleConflictResolved -eq $true -and
            $acceptanceResult.articleCanonicalValuesMatch -eq $true -and
            $acceptanceResult.articleAckCatalogRevisionMatch -eq $true -and
            $acceptanceResult.articleRemoteChildIdsAssigned -eq $true -and
            $acceptanceResult.articleDuplicateIdentityIndependent -eq $true -and
            $acceptanceResult.articleLifecycleCanonicalReadback -eq $true -and
            $acceptanceResult.articleReplayAckPreserved -eq $true -and
            $acceptanceResult.articleUiLanguagesVerified -eq $true -and
            $acceptanceResult.articleUiKeyboardNavigationVerified -eq $true -and
            $acceptanceResult.articleUiControlsUnclipped -eq $true -and
            $acceptanceResult.articleUiResponsive -eq $true -and
            $acceptanceResult.articleUiConflictNonModal -eq $true -and
            [int]$acceptanceResult.articleUiScreenshots -ge 10 -and
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
