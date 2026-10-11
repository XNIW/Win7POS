Set-StrictMode -Version Latest

function Invoke-Win7PosCatalogImportRecoveryPhases {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$HarnessPath,
        [Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$EvidenceDirectory,
        [Parameter(Mandatory)][DateTimeOffset]$StartedAt,
        [Parameter(Mandatory)][ValidateRange(15, 60)][int]$TimeoutMinutes,
        [Parameter(Mandatory)][scriptblock]$ConfirmPayload,
        [Parameter(Mandatory)][scriptblock]$ConfirmReadiness
    )
    $result = @{ Passed = $false; Code = 'u2_result_missing_or_invalid'; ProcessResult = $null }
    $previousToken = [Environment]::GetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', $RunId, 'Process')
        foreach ($phase in @('prepare', 'resume', 'continue')) {
            & $ConfirmPayload
            & $ConfirmReadiness
            if (Test-Win7PosAcceptanceProcessActive) { $result.Code = 'u2_acceptance_process_active'; return [pscustomobject]$result }
            $remaining = ($TimeoutMinutes * 60 * 1000) - [int][Math]::Ceiling(([DateTimeOffset]::UtcNow - $StartedAt).TotalMilliseconds)
            if ($remaining -lt 100) { $result.Code = 'u2_runner_budget_exhausted'; return [pscustomobject]$result }
            $process = Invoke-Win7PosWaitedProcess -FilePath $HarnessPath -ArgumentList @(
                '--data-dir', $DataDirectory, '--staging-catalog-import-recovery',
                '--profile', $Profile, '--run-id', $RunId, '--acceptance-output', $EvidenceDirectory,
                '--acceptance-phase', $phase
            ) -TimeoutMilliseconds $remaining -EvidenceDirectory $EvidenceDirectory
            $result.ProcessResult = $process
            & $ConfirmPayload
            if (-not $process.Started -or $process.TimedOut -or $process.OrphanRemaining) {
                $result.Code = 'u2_phase_launch_or_timeout_failure'; return [pscustomobject]$result
            }
            $path = Join-Path $EvidenceDirectory ('catalog-import-recovery-live-' + $phase + '-result.json')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return [pscustomobject]$result }
            $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($report.runId -cne $RunId -or $report.Profile -cne $Profile -or $report.Phase -cne $phase -or
                $report.Scope -cne 'qa-articles-zero-sales' -or $report.SalesAllowed -ne $false) { return [pscustomobject]$result }
            $expectedCode = switch ($phase) { prepare { 'u2_draft_restart_required' } resume { 'u2_response_loss_restart_required' } continue { 'success' } }
            $expectedExit = if ($phase -ceq 'continue') { 0 } else { 75 }
            if ($process.ExitCode -ne $expectedExit -or $report.code -cne $expectedCode) {
                $result.Code = Get-Win7PosAcceptanceResultCode -Code ([string]$report.code); return [pscustomobject]$result
            }
        }
        foreach ($field in @('OrdinaryPassed', 'DraftPersistRestartDiscardPassed', 'LocalCommitRevalidationPassed',
            'RetirementPassed', 'BackupVerified', 'Recovery5000Passed', 'LostResponseRetryRestartPassed',
            'LateOriginalFencePassed', 'CloudCanonicalReadbackPassed', 'LocalSqliteIntegrityPassed',
            'AcceptedPlanPartSuccessionPassed', 'IndependentAcceptedContributorPassed', 'SuccessionEconomicOncePassed',
            'SuccessionLateFencePassed', 'SuccessionCoverageOnlyPassed', 'SuccessionBackupVerified')) {
            if ($report.$field -ne $true) { return [pscustomobject]$result }
        }
        if ($report.passed -ne $true -or $report.SeededLegacyUncertainty -ne $true -or
            $report.LegacyOriginalHttpDispatchObserved -ne $false -or @($report.Wire).Count -eq 0) { return [pscustomobject]$result }
        if ($report.SuccessionLegacyOriginalHttpDispatchObserved -ne $false -or
            $report.SuccessionAcceptedPlanPartRows -ne 1000 -or $report.SuccessionAcceptedContributorRows -ne 1 -or
            @($report.SuccessionLocalOnlyLegacyHistoryIds).Count -ne 2 -or
            @($report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck).Count -ne 2 -or
            @($report.SuccessionLocalOnlyLegacyHistoryIdsAfterAck).Count -ne 2 -or
            (@($report.SuccessionLocalOnlyLegacyHistoryIds) -join ',') -cne (@($report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck) -join ',') -or
            (@($report.SuccessionLocalOnlyLegacyHistoryIdsBeforeAck) -join ',') -cne (@($report.SuccessionLocalOnlyLegacyHistoryIdsAfterAck) -join ',') -or
            $report.SuccessionMembershipSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $report.SuccessionPlanCanonicalHash -cnotmatch '^sha256:[0-9a-f]{64}$') { return [pscustomobject]$result }
        foreach ($request in $report.Wire) {
            if ($request.BodyUtf8Bytes -le 0 -or $request.BodyUtf8Bytes -gt 524288 -or $request.Items -gt 1000 -or
                $request.BodySha256 -cnotmatch '^[0-9a-f]{64}$') { return [pscustomobject]$result }
        }
        if (@($report.Wire | Where-Object { $_.ServerResponseObserved -eq $true }).Count -eq 0 -or
            @($report.Wire | Where-Object { $_.AcceptedResponseDropped -eq $true }).Count -ne 1) { return [pscustomobject]$result }
        if (-not (Test-Path -LiteralPath (Join-Path $EvidenceDirectory 'U2-CLEANUP-MANIFEST.json') -PathType Leaf)) { return [pscustomobject]$result }
        $result.Passed = $true; $result.Code = 'success'
        return [pscustomobject]$result
    }
    catch { return [pscustomobject]$result }
    finally {
        if ($null -eq $previousToken) {
            [Environment]::SetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', [System.Management.Automation.Language.NullString]::Value, 'Process')
        } else {
            [Environment]::SetEnvironmentVariable('WIN7POS_U2_ACCEPTANCE_RUNNER_TOKEN', $previousToken, 'Process')
        }
    }
}

Export-ModuleMember -Function Invoke-Win7PosCatalogImportRecoveryPhases
