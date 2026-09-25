Set-StrictMode -Version Latest

function Assert-Win7PosArticleReadiness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Json,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][string]$ClientCommitSha,
        [Parameter(Mandatory)][string]$StagingHost,
        [Parameter(Mandatory)][string]$ProfileBindingSha256,
        [Parameter(Mandatory)][hashtable]$ContractDigests,
        [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow
    )
    function Require-Readiness([bool]$Condition, [string]$Code) {
        if (-not $Condition) { throw ('acceptance_admin_readiness_' + $Code) }
    }
    try {
        # Reject duplicate keys before PowerShell's JSON conversion can overwrite them.
        $document = [System.Text.Json.JsonDocument]::Parse($Json)
        $timeStrings = @{}
        try {
            $allowed = @('schemaVersion','state','runId','clientCommitSha','stagingHost','environment',
                'profileBindingSha256','scope','scopeManifestSha256','adminRuntimeCommitSha','workerDeploymentId',
                'workerVersionId','issuedAtUtc','expiresAtUtc','deploymentVerifiedAtUtc','http503','exceededCpu',
                'exceededMemory','activeQaRuns','qaScopeClean','salesAllowed','contractDigests')
            $keys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $document.RootElement.EnumerateObject()) {
                Require-Readiness ($keys.Add($property.Name)) 'duplicate_key'
                Require-Readiness ($allowed -ccontains $property.Name) 'unknown_field'
            }
            Require-Readiness ($keys.Count -eq $allowed.Count) 'missing_field'
            $digestKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $document.RootElement.GetProperty('contractDigests').EnumerateObject()) {
                Require-Readiness ($digestKeys.Add($property.Name)) 'duplicate_key'
                Require-Readiness (@('request','response','firstLogin') -ccontains $property.Name) 'contract_mismatch'
            }
            Require-Readiness ($digestKeys.Count -eq 3) 'contract_mismatch'
            foreach ($field in @('issuedAtUtc','expiresAtUtc','deploymentVerifiedAtUtc')) {
                $timeStrings[$field] = $document.RootElement.GetProperty($field).GetString()
            }
        } finally { $document.Dispose() }
        $ready = ConvertFrom-Json -InputObject $Json -AsHashtable
        # PowerShell versions differ in automatic ISO date conversion.
        foreach ($field in $timeStrings.Keys) { $ready[$field] = $timeStrings[$field] }
        Require-Readiness ($ready.schemaVersion -ceq 'win7pos-article-readiness-v1' -and $ready.state -ceq 'READY') 'not_ready'
        Require-Readiness ($RunId -cmatch '^ASUSART_POST_PR68_[0-9]{8}T[0-9]{9}Z_[A-F0-9]{8}$' -and $ready.runId -ceq $RunId) 'run_mismatch'
        Require-Readiness ($ClientCommitSha -cmatch '^[0-9a-f]{40}$' -and $ready.clientCommitSha -ceq $ClientCommitSha) 'client_mismatch'
        Require-Readiness ($ready.stagingHost -ceq $StagingHost -and $ready.environment -ceq 'staging') 'environment_mismatch'
        Require-Readiness ($ProfileBindingSha256 -cmatch '^[0-9a-f]{64}$' -and $ready.profileBindingSha256 -ceq $ProfileBindingSha256) 'profile_mismatch'
        Require-Readiness ($ready.scope -ceq 'qa-articles-zero-sales' -and $ready.scopeManifestSha256 -cmatch '^[0-9a-f]{64}$') 'scope_invalid'
        Require-Readiness ($ready.adminRuntimeCommitSha -cmatch '^[0-9a-f]{40}$') 'runtime_invalid'
        foreach ($field in @('workerDeploymentId','workerVersionId')) {
            $guid = [Guid]::Empty
            Require-Readiness ([Guid]::TryParseExact([string]$ready[$field], 'D', [ref]$guid) -and $guid -ne [Guid]::Empty) 'deployment_invalid'
        }
        $times = @{}
        foreach ($field in @('issuedAtUtc','expiresAtUtc','deploymentVerifiedAtUtc')) {
            $time = [DateTimeOffset]::MinValue
            Require-Readiness ($ready[$field] -cmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$' -and
                [DateTimeOffset]::TryParse([string]$ready[$field], [cultureinfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$time)) 'timestamp_invalid'
            $times[$field] = $time
        }
        Require-Readiness ($times.issuedAtUtc -le $Now -and $times.expiresAtUtc -gt $Now -and
            $times.expiresAtUtc -gt $times.issuedAtUtc -and $times.expiresAtUtc -le $times.issuedAtUtc.AddHours(2)) 'expired_or_future'
        Require-Readiness ($times.deploymentVerifiedAtUtc -le $times.issuedAtUtc -and
            $times.deploymentVerifiedAtUtc -ge $times.issuedAtUtc.AddMinutes(-15)) 'deployment_stale'
        $runTime = [DateTimeOffset]::ParseExact($RunId.Split('_')[3], "yyyyMMdd'T'HHmmssfff'Z'", [cultureinfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal)
        Require-Readiness ($runTime -ge $times.issuedAtUtc.AddMinutes(-15) -and $runTime -le $Now) 'run_stale'
        foreach ($field in @('http503','exceededCpu','exceededMemory','activeQaRuns')) {
            Require-Readiness (($ready[$field] -is [int] -or $ready[$field] -is [long]) -and $ready[$field] -eq 0) 'preflight_failed'
        }
        Require-Readiness ($ready.qaScopeClean -is [bool] -and $ready.qaScopeClean -eq $true -and
            $ready.salesAllowed -is [bool] -and $ready.salesAllowed -eq $false) 'preflight_failed'
        foreach ($field in @('request','response','firstLogin')) {
            Require-Readiness ($ContractDigests[$field] -cmatch '^[0-9a-f]{64}$' -and
                $ready.contractDigests[$field] -ceq $ContractDigests[$field]) 'contract_mismatch'
        }
        return $ready
    } catch {
        if ($_.Exception.Message -cmatch '^acceptance_admin_readiness_[a-z_]+$') { throw }
        throw 'acceptance_admin_readiness_invalid'
    }
}

Export-ModuleMember -Function Assert-Win7PosArticleReadiness
