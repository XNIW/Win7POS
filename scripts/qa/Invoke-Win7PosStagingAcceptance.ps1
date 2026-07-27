[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,63}$')][string]$Profile,
    [string]$DataDirectory = 'C:\POSData\Win7POSAutomatedStagingAcceptance',
    [string]$HarnessPath = (Join-Path $PSScriptRoot '..\..\tests\Win7POS.Wpf.UiSmokeHarness\bin\x86\Release\net48\Win7POS.Wpf.UiSmokeHarness.exe')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Win7PosQaCredentialVault.psm1') -Force

$profileState = Test-Win7PosQaCredentialProfile -Profile $Profile
if (-not $profileState.Exists) {
    Write-Output ('QA_CREDENTIAL_PROFILE_MISSING. Setup: pwsh -NoProfile -File scripts\qa\Set-Win7PosStagingCredential.ps1 -Profile ' + $Profile)
    exit 2
}
if (-not $profileState.Valid) {
    throw 'QA credential profile is corrupt, expired, has an invalid staging hostname, has an invalid stable device identity, or has unsafe ACLs.'
}

$fullDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
$expectedDataDirectory = [System.IO.Path]::GetFullPath('C:\POSData\Win7POSAutomatedStagingAcceptance')
if (-not [string]::Equals($fullDataDirectory, $expectedDataDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Automated staging acceptance only permits its fixed isolated data directory.'
}
if (Test-Path -LiteralPath $fullDataDirectory) {
    $backup = $fullDataDirectory + '.backup-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    Move-Item -LiteralPath $fullDataDirectory -Destination $backup -ErrorAction Stop
}
New-Item -ItemType Directory -Path $fullDataDirectory -Force | Out-Null

$fullHarnessPath = [System.IO.Path]::GetFullPath($HarnessPath)
if (-not (Test-Path -LiteralPath $fullHarnessPath -PathType Leaf)) {
    throw 'Staging acceptance harness is not built. Build tests\Win7POS.Wpf.UiSmokeHarness in Release x86 first.'
}
$evidenceDirectory = Join-Path 'C:\Dev\_codex-evidence' ('win7pos-staging-acceptance-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null

& $fullHarnessPath --data-dir $fullDataDirectory --staging-acceptance --profile $Profile --acceptance-output $evidenceDirectory
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { throw ('Automated staging acceptance failed with exit code ' + $exitCode + '. See redacted evidence: ' + $evidenceDirectory) }
Write-Output ('WIN7POS_AUTOMATED_STAGING_ACCEPTANCE=PASS evidence=' + $evidenceDirectory)
