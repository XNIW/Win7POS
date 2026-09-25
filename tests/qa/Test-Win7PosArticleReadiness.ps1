[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../scripts/qa/Win7PosArticleReadiness.psm1') -Force
$now = [DateTimeOffset]'2026-09-25T20:00:00Z'
$run = 'ASUSART_POST_PR68_20260925T195900000Z_ABCD1234'
$digests = @{ request=('a'*64); response=('b'*64); firstLogin=('c'*64) }
$valid = @{
    schemaVersion='win7pos-article-readiness-v1'; state='READY'; runId=$run; clientCommitSha=('1'*40)
    adminRuntimeCommitSha=('2'*40); stagingHost='synthetic-staging.workers.dev'; environment='staging'
    workerDeploymentId='11111111-1111-4111-8111-111111111111'; workerVersionId='22222222-2222-4222-8222-222222222222'
    profileBindingSha256=('d'*64); scope='qa-articles-zero-sales'; scopeManifestSha256=('e'*64)
    issuedAtUtc='2026-09-25T19:59:00Z'; expiresAtUtc='2026-09-25T21:00:00Z'; deploymentVerifiedAtUtc='2026-09-25T19:58:00Z'
    http503=0; exceededCpu=0; exceededMemory=0; activeQaRuns=0; qaScopeClean=$true; salesAllowed=$false; contractDigests=$digests
}
function Validate([string]$Json) {
    Assert-Win7PosArticleReadiness -Json $Json -RunId $run -ClientCommitSha ('1'*40) `
        -StagingHost 'synthetic-staging.workers.dev' -ProfileBindingSha256 ('d'*64) -ContractDigests $digests -Now $now
}
$json = $valid | ConvertTo-Json -Depth 5
$accepted = Validate $json
if ($accepted.adminRuntimeCommitSha -cne ('2'*40)) { throw 'Fresh nonhistorical runtime not accepted.' }
$negative = 0
$mutations = @(
    { param($v) $v.state='SUPERSEDED' }, { param($v) $v.state='CLOSED' },
    { param($v) $v.schemaVersion='unknown' }, { param($v) $v.runId='ASUSART_POST_PR68_20260925T195800000Z_ABCD1234' },
    { param($v) $v.clientCommitSha='3'*40 }, { param($v) $v.environment='production' },
    { param($v) $v.stagingHost='different.workers.dev' }, { param($v) $v.profileBindingSha256='f'*64 },
    { param($v) $v.scope='sales' }, { param($v) $v.scopeManifestSha256='missing' },
    { param($v) $v.adminRuntimeCommitSha='not-a-sha' }, { param($v) $v.workerDeploymentId='old' },
    { param($v) $v.workerVersionId='00000000-0000-0000-0000-000000000000' },
    { param($v) $v.issuedAtUtc='2026-09-25T20:01:00Z' }, { param($v) $v.expiresAtUtc='2026-09-25T19:59:59Z' },
    { param($v) $v.expiresAtUtc='2026-09-26T20:00:00Z' }, { param($v) $v.issuedAtUtc='yesterday' },
    { param($v) $v.deploymentVerifiedAtUtc='2026-09-25T18:00:00Z' },
    { param($v) $v.deploymentVerifiedAtUtc='2026-09-25T20:00:00Z' },
    { param($v) $v.http503=1 }, { param($v) $v.exceededCpu=1 }, { param($v) $v.exceededMemory=1 },
    { param($v) $v.activeQaRuns=1 }, { param($v) $v.qaScopeClean=$false }, { param($v) $v.salesAllowed=$true },
    { param($v) $v.qaScopeClean='true' }, { param($v) $v.salesAllowed='false' }, { param($v) $v.http503='0' },
    { param($v) $v.contractDigests.request='f'*64 }, { param($v) $v.contractDigests.Remove('response') },
    { param($v) $v.Remove('expiresAtUtc') }, { param($v) $v.credential='must-never-be-a-contract-field' }
)
foreach ($mutation in $mutations) {
    $changed = ConvertFrom-Json -InputObject $json -AsHashtable
    & $mutation $changed | Out-Null
    $rejected = $false
    try { $null = Validate ($changed | ConvertTo-Json -Depth 5) } catch { $rejected = $true }
    if (-not $rejected) { throw "Readiness negative vector $negative accepted." }
    $negative++
}
foreach ($bad in @('not-json', '[]', ($json -replace '"state": "READY"','"state": "CLOSED", "state": "READY"'),
    ($json -replace '"request":', '"request":"ignored", "request":'))) {
    $rejected = $false
    try { $null = Validate $bad } catch { $rejected = $true }
    if (-not $rejected) { throw 'Malformed/duplicate readiness accepted.' }
    $negative++
}
Write-Output "ARTICLE_READINESS_CONTRACT=PASS positive=1 negative=$negative no_network=true"
