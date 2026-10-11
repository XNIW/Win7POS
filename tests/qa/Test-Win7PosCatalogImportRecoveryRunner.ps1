[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
foreach ($relative in @('scripts/qa/Invoke-Win7PosCatalogImportRecoveryAcceptance.ps1',
    'scripts/qa/Win7PosCatalogImportRecoveryRunner.psm1', 'scripts/qa/Invoke-Win7PosStagingAcceptance.ps1')) {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot $relative), [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw ('U2 runner parser failure: ' + $relative) }
}
Import-Module (Join-Path $repoRoot 'scripts/qa/Win7PosCatalogImportRecoveryRunner.psm1') -Force
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('win7pos-u2-runner-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$script:phases = [Collections.Generic.List[string]]::new()
$script:scenario = 'pass'
$script:callbackCount = 0
$script:runId = 'ASUSART_POST_PR68_20261011T180000000Z_1234ABCD'

# In-memory stubs: no executable, GUI, credentials, network or live dataset.
function global:Test-Win7PosAcceptanceProcessActive { return $false }
function global:Get-Win7PosAcceptanceResultCode { param($Code) return $Code }
function global:Invoke-Win7PosWaitedProcess {
    param($FilePath, $ArgumentList, $TimeoutMilliseconds, $EvidenceDirectory)
    $phase = $ArgumentList[-1]; $script:phases.Add($phase)
    $report = [ordered]@{
        passed = ($phase -eq 'continue'); code = $(switch ($phase) { prepare {'u2_draft_restart_required'} resume {'u2_response_loss_restart_required'} continue {'success'} })
        runId = $script:runId; Profile = 'asus-staging'; Phase = $phase; Scope = 'qa-articles-zero-sales'; SalesAllowed = $false
        SeededLegacyUncertainty = $true; LegacyOriginalHttpDispatchObserved = $false
        SuccessionLegacyOriginalHttpDispatchObserved = $false
        SuccessionAcceptedPlanPartRows = 1000; SuccessionAcceptedContributorRows = 1
        SuccessionLocalOnlyLegacyHistoryIds = @(1001,1002); SuccessionMembershipSha256 = ('b'*64); SuccessionPlanCanonicalHash = ('sha256:' + ('c'*64))
        SuccessionLocalOnlyLegacyHistoryIdsBeforeAck = @(1001,1002); SuccessionLocalOnlyLegacyHistoryIdsAfterAck = @(1001,1002)
        Wire = @(@{BodyUtf8Bytes=319;Items=0;BodySha256=('a'*64);ServerResponseObserved=$true;AcceptedResponseDropped=$true})
    }
    foreach ($field in @('OrdinaryPassed','DraftPersistRestartDiscardPassed','LocalCommitRevalidationPassed','RetirementPassed',
        'BackupVerified','Recovery5000Passed','LostResponseRetryRestartPassed','LateOriginalFencePassed','CloudCanonicalReadbackPassed','LocalSqliteIntegrityPassed',
        'AcceptedPlanPartSuccessionPassed','IndependentAcceptedContributorPassed','SuccessionEconomicOncePassed','SuccessionLateFencePassed','SuccessionCoverageOnlyPassed','SuccessionBackupVerified','SqlNormalizedItemBudgetsPassed')) { $report[$field] = $true }
    if ($script:scenario -eq 'scope') { $report.Scope = 'wrong-scope' }
    if ($script:scenario -eq 'oversize') { $report.Wire[0].BodyUtf8Bytes = 524289 }
    if ($script:scenario -eq 'incomplete') { $report.CloudCanonicalReadbackPassed = $false }
    if ($script:scenario -eq 'contributor_incomplete') { $report.IndependentAcceptedContributorPassed = $false }
    if ($script:scenario -eq 'wrong_membership') { $report.SuccessionAcceptedContributorRows = 2 }
    if ($script:scenario -eq 'local_history_identity_changed') { $report.SuccessionLocalOnlyLegacyHistoryIdsAfterAck = @(1001,1003) }
    if ($script:scenario -eq 'sql_item_budget_unverified') { $report.SqlNormalizedItemBudgetsPassed = $false }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory ('catalog-import-recovery-live-' + $phase + '-result.json'))
    '{}' | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'U2-CLEANUP-MANIFEST.json')
    return [pscustomobject]@{Started=$true;TimedOut=$false;OrphanRemaining=$false;ExitCode=$(if ($phase -eq 'continue') {0} else {75})}
}
try {
    $previous = [Environment]::GetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', 'Process')
    foreach ($case in @('pass', 'scope', 'oversize', 'incomplete', 'contributor_incomplete', 'wrong_membership', 'local_history_identity_changed', 'sql_item_budget_unverified')) {
        $script:scenario = $case; $script:phases.Clear(); $script:callbackCount = 0
        $result = Invoke-Win7PosCatalogImportRecoveryPhases -HarnessPath 'unused.exe' -Profile asus-staging -RunId $script:runId `
            -DataDirectory 'unused' -EvidenceDirectory $testRoot -StartedAt ([DateTimeOffset]::UtcNow) -TimeoutMinutes 15 `
            -ConfirmPayload {$script:callbackCount++} -ConfirmReadiness {$script:callbackCount++}
        if ($result.Passed -ne ($case -eq 'pass')) { throw ('Unexpected U2 runner outcome: ' + $case) }
        if ($case -eq 'scope' -and $script:phases.Count -ne 1) { throw 'Wrong scope continued into a dependent phase.' }
        if ($case -eq 'pass' -and ($script:phases -join ',') -ne 'prepare,resume,continue') { throw 'Restart phases changed order.' }
        if ([Environment]::GetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', 'Process') -cne $previous) { throw 'Runner handshake was not restored.' }
    }
    'PASS U2 runner synthetic phase/identity/scope/bytes/completeness/handshake checks'
}
finally {
    foreach ($name in @('Test-Win7PosAcceptanceProcessActive','Get-Win7PosAcceptanceResultCode','Invoke-Win7PosWaitedProcess')) { Remove-Item -LiteralPath ('Function:\global:' + $name) -ErrorAction SilentlyContinue }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
