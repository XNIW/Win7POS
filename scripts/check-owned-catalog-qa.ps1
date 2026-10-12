[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$node = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$version = (& $node --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^v(?<major>\d+)\.' -or [int]$Matches.major -lt 18) {
    throw 'Owned catalog QA requires Node18+ for the synthetic node:test checks; missing/old runtime is a failure, never a skip.'
}
Write-Host ('QA_NODE_VERSION=' + $version)
$pwshCommand = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$pwsh = if ($null -ne $pwshCommand) { $pwshCommand.Source } else {
    (Get-Command powershell -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}

# Toy files and in-memory identity stubs only. Never launch the collector,
# worker server, PostgreSQL, WPF, a build or a deployed provider from this gate.
& $node --test (Join-Path $repoRoot 'scripts/qa/catalog-source-index-fixture.test.mjs') (Join-Path $repoRoot 'tests/qa/catalog-import-replay-protocol.test.mjs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $pwsh -NoProfile -File (Join-Path $repoRoot 'tests/qa/Test-Win7PosOwnedProcessIdentity.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'OWNED_CATALOG_QA_GATE=PASS'
