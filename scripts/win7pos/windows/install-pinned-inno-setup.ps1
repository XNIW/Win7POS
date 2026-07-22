[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolDirectory,
    [string]$DownloadDirectory = "",
    [string]$ToolchainConfigPath = ""
)

$ErrorActionPreference = "Stop"

if (-not $IsWindows) {
    throw "Pinned Inno Setup installation requires a Windows build machine."
}
if ([string]::IsNullOrWhiteSpace($ToolchainConfigPath)) {
    $ToolchainConfigPath = Join-Path $PSScriptRoot "inno-setup-toolchain.json"
}
$validator = Join-Path $PSScriptRoot "test-inno-setup-toolchain.ps1"
$toolchain = & $validator -ToolchainConfigPath $ToolchainConfigPath

$ToolDirectory = [System.IO.Path]::GetFullPath($ToolDirectory)
if ([string]::IsNullOrWhiteSpace($DownloadDirectory)) {
    $DownloadDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "win7pos-inno-download"
}
$DownloadDirectory = [System.IO.Path]::GetFullPath($DownloadDirectory)
New-Item -ItemType Directory -Force -Path $DownloadDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $ToolDirectory | Out-Null

$installerPath = Join-Path $DownloadDirectory ("innosetup-{0}.exe" -f $toolchain.Version)
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    Invoke-WebRequest -Uri $toolchain.DownloadUrl -OutFile $installerPath
}

# Hash and publisher verification happen before any downloaded byte is executed.
& $validator -ToolchainConfigPath $ToolchainConfigPath -InstallerPath $installerPath | Out-Null

$arguments = @(
    "/VERYSILENT",
    "/SUPPRESSMSGBOXES",
    "/NORESTART",
    "/SP-",
    "/CURRENTUSER",
    "/DIR=`"$ToolDirectory`""
)
$installProcess = Start-Process `
    -FilePath $installerPath `
    -ArgumentList $arguments `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($installProcess.ExitCode -ne 0) {
    throw "Pinned Inno Setup installer failed with exit code $($installProcess.ExitCode)."
}

$isccPath = Join-Path $ToolDirectory "ISCC.exe"
if (-not (Test-Path -LiteralPath $isccPath -PathType Leaf)) {
    throw "Pinned Inno Setup installation completed without ISCC.exe: $isccPath"
}

$marker = [ordered]@{
    version = $toolchain.Version
    downloadUrl = $toolchain.DownloadUrl
    installerSha256 = $toolchain.InstallerSha256
    installerSize = $toolchain.InstallerSize
    compilerSha256 = (Get-FileHash -LiteralPath $isccPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$markerJson = $marker | ConvertTo-Json
[System.IO.File]::WriteAllText(
    (Join-Path $ToolDirectory "win7pos-inno-toolchain.json"),
    $markerJson + [Environment]::NewLine,
    (New-Object System.Text.UTF8Encoding($false)))

& $validator -ToolchainConfigPath $ToolchainConfigPath -IsccPath $isccPath | Out-Null
Write-Output $isccPath
