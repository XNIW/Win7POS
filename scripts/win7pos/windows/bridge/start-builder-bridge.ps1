param(
    [string]$BridgeRoot = "C:\Win7POSBridge",
    [string]$RepoRoot,
    [switch]$Once,
    [switch]$Watch,
    [int]$PollSeconds = 5
)

$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
}

$Inbox = Join-Path $BridgeRoot "inbox"
$Outbox = Join-Path $BridgeRoot "outbox"
$Done = Join-Path $Outbox "done"
$Failed = Join-Path $Outbox "failed"
$Logs = Join-Path $BridgeRoot "logs"
$Screenshots = Join-Path $BridgeRoot "screenshots"
$BuildScript = Join-Path $RepoRoot "scripts\win7pos\windows\build-release-x86.ps1"
$ScreenshotScript = Join-Path $RepoRoot "scripts\win7pos\windows\bridge\capture-screenshot.ps1"
$DistDir = Join-Path $RepoRoot "dist\Win7POS"
$DropZip = Join-Path $RepoRoot "dist\Win7POS-drop.zip"
$DropChecksum = Join-Path $RepoRoot "dist\Win7POS-drop.sha256.txt"

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Initialize-Bridge {
    foreach ($dir in @($BridgeRoot, $Inbox, $Outbox, $Done, $Failed, $Logs, $Screenshots)) {
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
    }
}

function Get-CommandSourceOrMissing {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return "missing"
}

function Invoke-Captured {
    param(
        [scriptblock]$Action,
        [string]$LogPath
    )

    $global:LASTEXITCODE = 0
    $output = & $Action 2>&1
    $exitCode = $global:LASTEXITCODE

    if ($null -ne $output) {
        $output | ForEach-Object { $_.ToString() } | Add-Content -Path $LogPath -Encoding UTF8
    }

    if ($exitCode -ne 0) {
        throw "Command exited with code $exitCode"
    }
}

function Write-EnvReport {
    param([string]$LogPath)

    Add-Content -Path $LogPath -Encoding UTF8 -Value @(
        "Job: env-report",
        "Date/time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
        "Bridge root: $BridgeRoot",
        "Repo root: $RepoRoot",
        "OS: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)",
        "PowerShell: $($PSVersionTable.PSVersion)",
        "powershell.exe: $(Get-CommandSourceOrMissing 'powershell.exe')",
        "git.exe: $(Get-CommandSourceOrMissing 'git.exe')",
        "msbuild.exe: $(Get-CommandSourceOrMissing 'msbuild.exe')",
        "Build script: $BuildScript",
        "Build script present: $(Test-Path $BuildScript)",
        "Dist dir: $DistDir",
        "Dist exe present: $(Test-Path (Join-Path $DistDir 'Win7POS.Wpf.exe'))"
    )
}

function Invoke-BuildScript {
    param(
        [string]$LogPath,
        [switch]$DryRun
    )

    if (-not (Test-Path $BuildScript)) {
        throw "Build script not found: $BuildScript"
    }

    $powershellExe = Get-CommandSourceOrMissing "powershell.exe"
    if ($powershellExe -eq "missing") {
        throw "powershell.exe not found."
    }

    $commandArgs = @("-ExecutionPolicy", "Bypass", "-File", $BuildScript)
    if ($DryRun) { $commandArgs += "-DryRun" }

    Add-Content -Path $LogPath -Encoding UTF8 -Value "Command: $powershellExe $($commandArgs -join ' ')"
    Invoke-Captured -LogPath $LogPath -Action {
        & $powershellExe @commandArgs
    }
}

function Invoke-PackageDrop {
    param([string]$LogPath)

    $exe = Join-Path $DistDir "Win7POS.Wpf.exe"
    if (-not (Test-Path $exe)) {
        throw "Cannot package drop because executable is missing: $exe"
    }

    $distRoot = Join-Path $RepoRoot "dist"
    if (-not (Test-Path $distRoot)) {
        New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
    }

    Add-Content -Path $LogPath -Encoding UTF8 -Value "Zip path: $DropZip"
    Compress-Archive -Path (Join-Path $DistDir "*") -DestinationPath $DropZip -Force
    $hash = Get-FileHash $DropZip -Algorithm SHA256
    $hash | Format-List | Out-String | Set-Content -Path $DropChecksum -Encoding UTF8
    Copy-Item -Path $DropZip -Destination (Join-Path $Outbox "Win7POS-drop.zip") -Force
    Copy-Item -Path $DropChecksum -Destination (Join-Path $Outbox "Win7POS-drop.sha256.txt") -Force

    $buildReport = Join-Path $RepoRoot "dist\Win7POS-build-report.md"
    if (Test-Path $buildReport) {
        Copy-Item -Path $buildReport -Destination (Join-Path $Outbox "Win7POS-build-report.md") -Force
    }

    Add-Content -Path $LogPath -Encoding UTF8 -Value @(
        "Checksum path: $DropChecksum",
        "Outbox zip: $(Join-Path $Outbox 'Win7POS-drop.zip')",
        "Outbox checksum: $(Join-Path $Outbox 'Win7POS-drop.sha256.txt')",
        "SHA256: $($hash.Hash)"
    )
}

function Invoke-Screenshot {
    param([string]$LogPath)

    if (-not (Test-Path $ScreenshotScript)) {
        throw "Screenshot script not found: $ScreenshotScript"
    }

    $shot = Join-Path $Screenshots ("builder-{0}.png" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    $powershellExe = Get-CommandSourceOrMissing "powershell.exe"
    if ($powershellExe -eq "missing") {
        throw "powershell.exe not found."
    }

    Add-Content -Path $LogPath -Encoding UTF8 -Value "Screenshot path: $shot"
    Invoke-Captured -LogPath $LogPath -Action {
        & $powershellExe -ExecutionPolicy Bypass -File $ScreenshotScript -OutputPath $shot
    }
}

function Get-AllowedJobName {
    param([string]$FileName)

    $name = [System.IO.Path]::GetFileNameWithoutExtension($FileName)
    switch ($name) {
        "env-report" { return $name }
        "build-dry-run" { return $name }
        "build-release" { return $name }
        "package-drop" { return $name }
        "screenshot" { return $name }
        default { return "" }
    }
}

function Process-OneJob {
    Initialize-Bridge

    $jobFile = Get-ChildItem -Path $Inbox -Filter "*.job" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $jobFile) {
        Write-Host "No job files in: $Inbox"
        return $false
    }

    $job = Get-AllowedJobName $jobFile.Name
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $safeName = [System.IO.Path]::GetFileNameWithoutExtension($jobFile.Name)
    $logPath = Join-Path $Outbox ("{0}-{1}.log" -f $timestamp, $safeName)

    Set-Content -Path $logPath -Encoding UTF8 -Value @(
        "Win7POS Builder Bridge",
        "Date/time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
        "Job file: $($jobFile.FullName)",
        "Allowed job: $job",
        ""
    )

    try {
        if (-not $job) {
            throw "Unsupported job file name: $($jobFile.Name)"
        }

        switch ($job) {
            "env-report" { Write-EnvReport -LogPath $logPath }
            "build-dry-run" { Invoke-BuildScript -LogPath $logPath -DryRun }
            "build-release" { Invoke-BuildScript -LogPath $logPath }
            "package-drop" { Invoke-PackageDrop -LogPath $logPath }
            "screenshot" { Invoke-Screenshot -LogPath $logPath }
        }

        Move-Item -Path $jobFile.FullName -Destination (Join-Path $Done ("{0}-{1}" -f $timestamp, $jobFile.Name))
        Add-Content -Path $logPath -Encoding UTF8 -Value "Result: PASS"
        Write-Host "Job completed: $($jobFile.Name)"
    }
    catch {
        Add-Content -Path $logPath -Encoding UTF8 -Value "ERROR: $($_.Exception.Message)"
        Move-Item -Path $jobFile.FullName -Destination (Join-Path $Failed ("{0}-{1}" -f $timestamp, $jobFile.Name))
        Write-Host "Job failed: $($jobFile.Name)" -ForegroundColor Red
        Write-Host "Log: $logPath"
        return $false
    }

    Write-Host "Log: $logPath"
    return $true
}

if ($Once -and $Watch) {
    throw "Use either -Once or -Watch, not both."
}

if ($PollSeconds -lt 1) {
    throw "-PollSeconds must be >= 1."
}

if (-not (Test-IsWindows)) {
    throw "This bridge must be run on Windows 10/11 Builder."
}

Initialize-Bridge
Write-Host "Win7POS Builder Bridge"
Write-Host "Bridge root: $BridgeRoot"
Write-Host "Repo root: $RepoRoot"
$mode = if ($Watch) { "Watch" } else { "Once" }
Write-Host "Mode: $mode"
Write-Host "Allowed jobs: env-report, build-dry-run, build-release, package-drop, screenshot"

if ($Watch) {
    Write-Host "Press Ctrl+C to stop."
    while ($true) {
        [void](Process-OneJob)
        Start-Sleep -Seconds $PollSeconds
    }
}
else {
    [void](Process-OneJob)
}
