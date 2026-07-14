param(
    [string]$Configuration = "Release",
    [string]$Platform = "x86",
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$BuildInstaller,
    [switch]$AllowDirty,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$Warnings = New-Object System.Collections.Generic.List[string]
$Errors = New-Object System.Collections.Generic.List[string]
$InstallerGenerated = $false
$MsBuildPath = $null
$MsBuildVersion = $null
$DotnetPath = $null
$DotnetVersion = $null
$UseDotnetCli = $false

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$ProjectPath = Join-Path $RepoRoot "src\Win7POS.Wpf\Win7POS.Wpf.csproj"
$OutputDir = Join-Path $RepoRoot ("src\Win7POS.Wpf\bin\{0}\{1}\net48" -f $Platform, $Configuration)
$LegacyOutputDir = Join-Path $RepoRoot ("src\Win7POS.Wpf\bin\{0}\net48" -f $Configuration)
$DistRoot = Join-Path $RepoRoot "dist"
$DistDir = Join-Path $DistRoot "Win7POS"
$ReportPath = Join-Path $DistRoot "Win7POS-build-report.md"
$InstallerScript = Join-Path $RepoRoot "installer\Win7POS.iss"
$InstallerOutput = Join-Path $RepoRoot "installer\output\Win7POS-Setup.exe"

function Add-WarningMessage {
    param([string]$Message)
    $Warnings.Add($Message) | Out-Null
    Write-Warning $Message
}

function Add-ErrorMessage {
    param([string]$Message)
    $Errors.Add($Message) | Out-Null
    Write-Host "ERROR: $Message" -ForegroundColor Red
}

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Get-CommandPath {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Find-MSBuild {
    if ($env:MSBUILD_EXE -and (Test-Path $env:MSBUILD_EXE)) {
        return (Resolve-Path $env:MSBUILD_EXE).Path
    }

    $vswhereCandidates = @()
    $fromPath = Get-CommandPath "vswhere.exe"
    if ($fromPath) { $vswhereCandidates += $fromPath }
    if (${env:ProgramFiles(x86)}) {
        $vswhereCandidates += (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe")
    }
    if ($env:ProgramFiles) {
        $vswhereCandidates += (Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe")
    }

    foreach ($vswhere in ($vswhereCandidates | Select-Object -Unique)) {
        if (-not (Test-Path $vswhere)) { continue }
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null |
            Where-Object { $_ -and (Test-Path $_) } |
            Select-Object -First 1
        if ($found) { return $found }
    }

    $where = Get-CommandPath "where.exe"
    if ($where) {
        $found = & $where msbuild 2>$null | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if ($found) { return $found }
    }

    return $null
}

function Find-Dotnet {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:WIN7POS_DOTNET_EXE -and (Test-Path -LiteralPath $env:WIN7POS_DOTNET_EXE -PathType Leaf)) {
        $candidates.Add((Resolve-Path -LiteralPath $env:WIN7POS_DOTNET_EXE).Path) | Out-Null
    }

    if ($env:DOTNET_ROOT) {
        $candidate = Join-Path $env:DOTNET_ROOT "dotnet.exe"
        if (Test-Path $candidate) { $candidates.Add((Resolve-Path $candidate).Path) | Out-Null }
    }

    $fromPath = Get-CommandPath "dotnet.exe"
    if ($fromPath) { $candidates.Add($fromPath) | Out-Null }

    $buildMachineFallback = "C:\Dev\dotnet10\dotnet.exe"
    if (Test-Path -LiteralPath $buildMachineFallback -PathType Leaf) {
        $candidates.Add((Resolve-Path -LiteralPath $buildMachineFallback).Path) | Out-Null
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        $sdk = Get-DotnetSdkVersion $candidate
        if ($sdk -match '^10\.0\.3\d{2}$') {
            return $candidate
        }
        Write-Warning "Skipping incompatible dotnet candidate: $candidate (SDK '$sdk')"
    }

    return $null
}

function Get-DotnetSdkVersion {
    param([string]$DotnetExe)

    if (-not $DotnetExe -or -not (Test-Path $DotnetExe)) { return "" }

    try {
        $version = & $DotnetExe --version 2>$null
        if ($LASTEXITCODE -eq 0) { return (($version | Select-Object -First 1) -as [string]) }
    }
    catch { }
    return ""
}

function Test-NeedsDotnetCli {
    if ([string]::IsNullOrWhiteSpace($DotnetVersion) -or
        [string]::IsNullOrWhiteSpace($MsBuildVersion)) {
        return $false
    }

    $dotnetMajor = 0
    $msbuildMajor = 0
    [int]::TryParse(($DotnetVersion -split "\.")[0], [ref]$dotnetMajor) | Out-Null
    [int]::TryParse(($MsBuildVersion -split "\.")[0], [ref]$msbuildMajor) | Out-Null

    return ($dotnetMajor -ge 10 -and $msbuildMajor -lt 18)
}

function Find-ISCC {
    if ($env:ISCC_EXE -and (Test-Path $env:ISCC_EXE)) {
        return (Resolve-Path $env:ISCC_EXE).Path
    }

    $fromPath = Get-CommandPath "iscc.exe"
    if ($fromPath) { return $fromPath }

    $candidates = @()
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    }
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

function Invoke-LoggedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    $display = '"' + $FilePath + '" ' + ($Arguments -join " ")
    if ($DryRun) {
        Write-Host "[DRY-RUN] $display"
        return
    }

    Write-Host $display
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $display"
    }
}

function Get-GitValue {
    param([string[]]$Arguments)

    if (-not (Get-CommandPath "git.exe")) { return "" }

    try {
        $value = & git @Arguments 2>$null
        if ($LASTEXITCODE -eq 0) { return (($value | Select-Object -First 1) -as [string]) }
    }
    catch { }
    return ""
}

function Get-RelativeOrMissing {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return "missing" }
    return "present"
}

function Find-FilesByPattern {
    param(
        [string]$Root,
        [string[]]$Patterns,
        [int]$Depth = 4
    )

    if (-not (Test-Path $Root)) { return @() }
    $all = Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue
    $matches = New-Object System.Collections.Generic.List[string]

    foreach ($file in $all) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        if (($relative -split '[\\/]').Count -gt $Depth) { continue }

        foreach ($pattern in $Patterns) {
            if ($file.Name -like $pattern) {
                $matches.Add($relative) | Out-Null
                break
            }
        }
    }

    return $matches | Sort-Object
}

function Write-BuildReport {
    if ($DryRun) {
        Write-Host "[DRY-RUN] Would write report: $ReportPath"
        return
    }

    New-Item -ItemType Directory -Force $DistRoot | Out-Null

    $branch = Get-GitValue @("rev-parse", "--abbrev-ref", "HEAD")
    $commit = Get-GitValue @("rev-parse", "HEAD")
    $exePath = Join-Path $DistDir "Win7POS.Wpf.exe"
    $exePresent = Test-Path $exePath
    $configPath = Join-Path $DistDir "Win7POS.Wpf.exe.config"
    $projectDlls = @("Win7POS.Core.dll", "Win7POS.Data.dll") |
        ForEach-Object { "- ${_}: " + (Get-RelativeOrMissing (Join-Path $DistDir $_)) }
    $dependencyFiles = Find-FilesByPattern $DistDir @(
        "Dapper*.dll",
        "Microsoft.Data.Sqlite*.dll",
        "SQLitePCLRaw*.dll",
        "PDFsharp*.dll",
        "ZXing*.dll",
        "ClosedXML*.dll",
        "ExcelDataReader*.dll"
    ) 4
    $nativeFiles = Find-FilesByPattern $DistDir @("e_sqlite3.dll", "SQLite.Interop.dll") 6

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Win7POS build report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Date/time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')") | Out-Null
    $lines.Add("- Branch: $branch") | Out-Null
    $lines.Add("- Commit: $commit") | Out-Null
    $lines.Add("- OS: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)") | Out-Null
    $lines.Add("- MSBuild path: $MsBuildPath") | Out-Null
    $lines.Add("- MSBuild version: $MsBuildVersion") | Out-Null
    $lines.Add("- dotnet path: $DotnetPath") | Out-Null
    $lines.Add("- dotnet SDK version: $DotnetVersion") | Out-Null
    $lines.Add("- Build tool: $(if ($UseDotnetCli) { "dotnet CLI" } else { "MSBuild" })") | Out-Null
    $lines.Add("- Configuration: $Configuration") | Out-Null
    $lines.Add("- Platform: $Platform") | Out-Null
    $lines.Add("- Output path: $OutputDir") | Out-Null
    $lines.Add("- Legacy output fallback: $LegacyOutputDir") | Out-Null
    $lines.Add("- Dist path: $DistDir") | Out-Null
    $lines.Add("- Exe present: $exePresent") | Out-Null
    $lines.Add("- Config present: $(Test-Path $configPath)") | Out-Null
    $lines.Add("- Installer requested: $([bool]$BuildInstaller)") | Out-Null
    $lines.Add("- Installer generated: $InstallerGenerated") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Project DLLs") | Out-Null
    $projectDlls | ForEach-Object { $lines.Add($_) | Out-Null }
    $lines.Add("") | Out-Null
    $lines.Add("## Known dependency DLLs") | Out-Null
    if ($dependencyFiles.Count -gt 0) {
        $dependencyFiles | ForEach-Object { $lines.Add("- $_") | Out-Null }
    }
    else {
        $lines.Add("- none found in expected scan depth") | Out-Null
    }
    $lines.Add("") | Out-Null
    $lines.Add("## Native SQLite candidates") | Out-Null
    if ($nativeFiles.Count -gt 0) {
        $nativeFiles | ForEach-Object { $lines.Add("- $_") | Out-Null }
    }
    else {
        $lines.Add("- none found; verify if Win7 smoke fails at DB startup") | Out-Null
    }
    $lines.Add("") | Out-Null
    $lines.Add("## Warnings") | Out-Null
    if ($Warnings.Count -gt 0) {
        $Warnings | ForEach-Object { $lines.Add("- $_") | Out-Null }
    }
    else {
        $lines.Add("- none recorded") | Out-Null
    }
    $lines.Add("") | Out-Null
    $lines.Add("## Errors") | Out-Null
    if ($Errors.Count -gt 0) {
        $Errors | ForEach-Object { $lines.Add("- $_") | Out-Null }
    }
    else {
        $lines.Add("- none recorded") | Out-Null
    }
    $lines.Add("") | Out-Null
    $lines.Add("## Next Mac command") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add('```bash') | Out-Null
    $lines.Add('scripts/win7pos/validate-drop.sh --source <exported-drop-on-mac>') | Out-Null
    $lines.Add('```') | Out-Null

    Set-Content -Path $ReportPath -Value $lines -Encoding UTF8
    Write-Host "Build report written: $ReportPath"
}

function Write-ReleaseSupportFiles {
    if ($DryRun) {
        Write-Host "[DRY-RUN] Would write release support files."
        return
    }

    $writer = Join-Path $RepoRoot "scripts\win7pos\windows\write-release-support-files.ps1"
    Invoke-LoggedCommand (Get-CommandPath "pwsh.exe") @(
        "-NoProfile",
        "-File",
        $writer,
        "-OutputDirectory",
        $DistDir,
        "-Configuration",
        $Configuration,
        "-Platform",
        $Platform,
        "-SdkVersion",
        $DotnetVersion
    )
}

function Show-DropSummary {
    Write-Host ""
    Write-Host "Drop summary: $DistDir"
    $files = @(
        "Win7POS.Wpf.exe",
        "Win7POS.Wpf.exe.config",
        "Win7POS.Core.dll",
        "Win7POS.Data.dll",
        "Assets\sii_qrcode.png"
    )

    foreach ($file in $files) {
        $path = Join-Path $DistDir $file
        if (Test-Path $path) {
            Write-Host "  OK: $file"
        }
        else {
            Write-Host "  INFO: not found: $file"
        }
    }

    $dependencies = Find-FilesByPattern $DistDir @(
        "Dapper*.dll",
        "Microsoft.Data.Sqlite*.dll",
        "SQLitePCLRaw*.dll",
        "PDFsharp*.dll",
        "ZXing*.dll",
        "ClosedXML*.dll",
        "ExcelDataReader*.dll"
    ) 4
    if ($dependencies.Count -gt 0) {
        Write-Host ""
        Write-Host "Known dependency DLLs:"
        $dependencies | ForEach-Object { Write-Host "  $_" }
    }

    $native = Find-FilesByPattern $DistDir @("e_sqlite3.dll", "SQLite.Interop.dll") 6
    if ($native.Count -gt 0) {
        Write-Host ""
        Write-Host "Native SQLite candidates:"
        $native | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Add-WarningMessage "No e_sqlite3.dll or SQLite.Interop.dll found in drop scan; verify native SQLite assets if Win7 smoke fails at DB startup."
    }
}

try {
    if (-not (Test-IsWindows)) {
        throw "This script must be run on Windows 10/11 Builder. Current OS: $([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
    }

    Set-Location $RepoRoot

    if (-not $DryRun) {
        $gitStatus = & git -C $RepoRoot status --porcelain 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Git status is required to create a provenance-safe release pack."
        }
        if ($gitStatus -and -not $AllowDirty) {
            throw "Working tree is dirty. Commit the intended changes or use -AllowDirty for a non-official local pack."
        }
        if ($gitStatus -and $AllowDirty) {
            Add-WarningMessage "Building an explicitly allowed dirty local pack."
        }
    }

    if (-not (Test-Path $ProjectPath)) {
        throw "Project file not found: $ProjectPath"
    }

    $MsBuildPath = Find-MSBuild
    if (-not $MsBuildPath) {
        throw "MSBuild not found. Open Developer Command Prompt for VS or install Visual Studio Build Tools with .NET Framework 4.8 targeting pack."
    }

    try {
        $MsBuildVersion = ((& $MsBuildPath /version /nologo 2>$null) | Select-Object -Last 1)
    }
    catch {
        Add-WarningMessage "Could not read MSBuild version: $($_.Exception.Message)"
    }

    $DotnetPath = Find-Dotnet
    $DotnetVersion = Get-DotnetSdkVersion $DotnetPath
    if (-not $DotnetPath) {
        throw "Compatible .NET SDK not found. Set WIN7POS_DOTNET_EXE, DOTNET_ROOT, PATH, or install 10.0.301 under C:\Dev\dotnet10."
    }
    if ($DotnetVersion -notmatch '^10\.0\.3\d{2}$') {
        throw "Selected dotnet SDK '$DotnetVersion' is incompatible. Win7POS requires the .NET 10.0.3xx feature band from global.json."
    }
    $UseDotnetCli = Test-NeedsDotnetCli
    if ($UseDotnetCli) {
        if (-not $DotnetPath) {
            throw "dotnet CLI not found, but MSBuild $MsBuildVersion cannot build with SDK $DotnetVersion."
        }
        Add-WarningMessage "Using dotnet CLI SDK $DotnetVersion because MSBuild $MsBuildVersion cannot resolve SDK 10 projects."
    }

    Write-Host "Repo root: $RepoRoot"
    Write-Host "Project: $ProjectPath"
    Write-Host "MSBuild: $MsBuildPath"
    Write-Host "Selected dotnet executable: $DotnetPath"
    Write-Host "Selected dotnet SDK: $DotnetVersion"
    Write-Host "Configuration: $Configuration"
    Write-Host "Platform: $Platform"

    if (-not $SkipRestore) {
        if ($UseDotnetCli) {
            Invoke-LoggedCommand $DotnetPath @(
                "restore",
                $ProjectPath,
                "-p:Configuration=$Configuration",
                "-p:Platform=$Platform"
            )
        }
        else {
            Invoke-LoggedCommand $MsBuildPath @(
                $ProjectPath,
                "/t:Restore",
                "/p:Configuration=$Configuration",
                "/p:Platform=$Platform"
            )
        }
    }
    else {
        Add-WarningMessage "Restore skipped by -SkipRestore."
    }

    if (-not $SkipBuild) {
        if ($UseDotnetCli) {
            $buildArgs = @(
                "build",
                $ProjectPath,
                "-c",
                $Configuration,
                "-p:Platform=$Platform",
                "-p:PlatformTarget=$Platform",
                "--no-restore"
            )
            Invoke-LoggedCommand $DotnetPath $buildArgs
        }
        else {
            Invoke-LoggedCommand $MsBuildPath @(
                $ProjectPath,
                "/t:Build",
                "/p:Configuration=$Configuration",
                "/p:Platform=$Platform",
                "/p:PlatformTarget=$Platform"
            )
        }
    }
    else {
        Add-WarningMessage "Build skipped by -SkipBuild."
    }

    if ($DryRun) {
        Write-Host "[DRY-RUN] Would create dist folder: $DistDir"
        Write-Host "[DRY-RUN] Would copy output from: $OutputDir"
    }
    else {
        $resolvedOutputDir = $OutputDir
        if (-not (Test-Path $resolvedOutputDir)) {
            if (Test-Path $LegacyOutputDir) {
                $resolvedOutputDir = $LegacyOutputDir
                Add-WarningMessage "Platform output folder not found; using legacy output folder: $LegacyOutputDir"
            }
            else {
                throw "Build output folder not found: $OutputDir"
            }
        }

        if (Test-Path $DistDir) {
            Remove-Item -Path $DistDir -Recurse -Force
        }
        New-Item -ItemType Directory -Force $DistDir | Out-Null
        Copy-Item -Path (Join-Path $resolvedOutputDir "*") -Destination $DistDir -Recurse -Force
        Get-ChildItem -Path $DistDir -Recurse -File -Filter "*.pdb" |
            Remove-Item -Force
        Write-ReleaseSupportFiles
        Write-Host "Copied build output to: $DistDir"

        $exe = Join-Path $DistDir "Win7POS.Wpf.exe"
        if (-not (Test-Path $exe)) {
            throw "Expected executable not found after copy: $exe"
        }

        $currentCommit = Get-GitValue @("rev-parse", "HEAD")
        $versionText = [System.IO.File]::ReadAllText((Join-Path $DistDir "VERSION.txt"))
        if (-not $currentCommit -or $versionText -notmatch ("(?m)^CommitSHA=" + [regex]::Escape($currentCommit) + "\s*$")) {
            throw "VERSION.txt does not match the build repository HEAD."
        }

        $forbiddenPayload = Get-ChildItem -LiteralPath $DistDir -Recurse -File | Where-Object {
            $_.Extension -in @(".pdb", ".cs", ".xaml", ".csproj", ".db", ".sqlite", ".sln", ".slnx") -or
            $_.Name -match '(?i)(token|secret|production).*(json|config|txt)$'
        }
        if ($forbiddenPayload) {
            throw "Release pack contains forbidden source, database, debug or secret-like files: $($forbiddenPayload.Name -join ', ')"
        }
    }

    if (-not $DryRun) {
        Show-DropSummary
    }

    if ($BuildInstaller) {
        $iscc = Find-ISCC
        if (-not $iscc) {
            Add-WarningMessage "Inno Setup iscc.exe not found. Installer skipped."
        }
        elseif ($DryRun) {
            Write-Host "[DRY-RUN] Would run installer compiler: $iscc $InstallerScript"
        }
        else {
            Invoke-LoggedCommand $iscc @($InstallerScript)
            $InstallerGenerated = Test-Path $InstallerOutput
            if (-not $InstallerGenerated) {
                Add-WarningMessage "Installer command finished but expected output was not found: $InstallerOutput"
            }
        }
    }

    Write-BuildReport

    if (-not $DryRun) {
        Write-Host ""
        Write-Host "Next Mac command:"
        Write-Host "  scripts/win7pos/validate-drop.sh --source <exported-drop-on-mac>"
    }
}
catch {
    Add-ErrorMessage $_.Exception.Message
    try {
        if ((Test-IsWindows) -and (-not $DryRun)) {
            Write-BuildReport
        }
    }
    catch {
        Write-Warning "Could not write build report after failure: $($_.Exception.Message)"
    }
    exit 1
}
