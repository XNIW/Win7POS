[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("x86")]
    [string]$Platform = "x86"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$candidatePaths = @(
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Platform\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe"),
    (Join-Path $repoRoot "tests\Win7POS.Wpf.UiSmokeHarness\bin\$Configuration\net48\Win7POS.Wpf.UiSmokeHarness.exe")
)
$harnessExe = $candidatePaths |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($harnessExe)) {
    throw "Authorization lease WPF smoke harness is missing; build the x86 net48 harness first."
}

function Invoke-AuthorizationHarness {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DataDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Mode,
        [Parameter(Mandatory = $true)]
        [string]$ArtifactName
    )

    New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
    $quotedDataDirectory = '"' + $DataDirectory.Replace('"', '\"') + '"'
    $process = Start-Process `
        -FilePath $harnessExe `
        -ArgumentList @("--data-dir", $quotedDataDirectory, $Mode) `
        -PassThru `
        -WindowStyle Hidden
    $artifact = Join-Path $DataDirectory $ArtifactName
    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "Authorization lease smoke mode $Mode exceeded its 180-second timeout. Artifact: $artifact"
    }
    if ($process.ExitCode -ne 0) {
        throw "Authorization lease smoke mode $Mode failed with exit code $($process.ExitCode). Artifact: $artifact"
    }
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Authorization lease smoke mode $Mode did not produce its result artifact: $artifact"
    }

    $result = [System.IO.File]::ReadAllText($artifact).Trim()
    if (-not $result.StartsWith("PASS", [StringComparison]::Ordinal)) {
        throw "Authorization lease smoke mode $Mode did not report PASS. Artifact: $artifact"
    }

    return [pscustomobject]@{
        Artifact = $artifact
        ProcessId = $process.Id
        Result = $result
    }
}

function Read-ResultValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $pattern = "(?m)^" + [regex]::Escape($Name) + "=([^\r\n]+)\r?$"
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) {
        throw "Authorization lease smoke result is missing $Name."
    }
    return $match.Groups[1].Value.Trim()
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$qaRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "Win7POS-QA"))
if (-not $qaRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create authorization lease smoke data outside the system temp directory."
}

$dataDir = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLease." + [Guid]::NewGuid().ToString("N"))))
$restartDataDir = [System.IO.Path]::GetFullPath(
    (Join-Path $qaRoot ("AuthorizationLeaseRestart." + [Guid]::NewGuid().ToString("N"))))

$smoke = Invoke-AuthorizationHarness `
    -DataDirectory $dataDir `
    -Mode "--authorization-lease-smoke" `
    -ArtifactName "authorization-lease-smoke.txt"
$prepare = Invoke-AuthorizationHarness `
    -DataDirectory $restartDataDir `
    -Mode "--authorization-lease-restart-prepare" `
    -ArtifactName "authorization-lease-restart-prepare.txt"
$verify = Invoke-AuthorizationHarness `
    -DataDirectory $restartDataDir `
    -Mode "--authorization-lease-restart-verify" `
    -ArtifactName "authorization-lease-restart-verify.txt"

$prepareInstance = Read-ResultValue `
    -Text $prepare.Result `
    -Name "processInstance"
$verifyInstance = Read-ResultValue `
    -Text $verify.Result `
    -Name "processInstance"
if ([string]::Equals(
        $prepareInstance,
        $verifyInstance,
        [StringComparison]::Ordinal)) {
    throw "Authorization lease restart regression did not use two distinct process instances."
}
if ((Read-ResultValue -Text $verify.Result -Name "offlineAttestationAfterRestart") -ne "False" -or
    (Read-ResultValue -Text $verify.Result -Name "offlineDenial") -ne "offline_attestation_required" -or
    (Read-ResultValue -Text $verify.Result -Name "unauthorizedSaleSinkRows") -ne "0" -or
    (Read-ResultValue -Text $verify.Result -Name "unauthorizedPublicationOutboxRows") -ne "0" -or
    (Read-ResultValue -Text $verify.Result -Name "freshOnlineRecovery") -ne "True") {
    throw "Authorization lease restart regression did not prove fail-closed authorization and online recovery."
}

Write-Host $smoke.Result
Write-Host $prepare.Result
Write-Host $verify.Result
Write-Host "AUTHORIZATION_LEASE_ARTIFACT=$($smoke.Artifact)"
Write-Host "AUTHORIZATION_LEASE_RESTART_PREPARE_ARTIFACT=$($prepare.Artifact)"
Write-Host "AUTHORIZATION_LEASE_RESTART_VERIFY_ARTIFACT=$($verify.Artifact)"
