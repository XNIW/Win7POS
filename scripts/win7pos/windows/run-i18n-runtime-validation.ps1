param(
    [string]$RepoRoot = "",
    [string]$Configuration = "Release",
    [string]$Platform = "x86",
    [string]$DataRoot = "C:\Win7POSTest\data",
    [string]$EvidenceRoot = "C:\Win7POSTest\evidence",
    [int]$LaunchSeconds = 20,
    [string]$ExpectedLanguage = "",
    [switch]$SkipBuild,
    [switch]$NoManualWait,
    [switch]$StopAfterCollection,
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
}

function Test-IsWindowsHost {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Add-Line {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Value
    )
    $Lines.Add($Value) | Out-Null
}

function Get-DotNetFrameworkRelease {
    try {
        $item = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" -Name Release -ErrorAction Stop
        return [string]$item.Release
    }
    catch {
        return "UNKNOWN"
    }
}

function Get-OsSummary {
    try {
        $os = Get-WmiObject -Class Win32_OperatingSystem -ErrorAction Stop
        return "$($os.Caption) $($os.Version) $($os.OSArchitecture)"
    }
    catch {
        return [System.Environment]::OSVersion.VersionString
    }
}

function Resolve-Win7PosExe {
    param([string]$Root, [string]$Config, [string]$Plat)

    $candidates = @(
        (Join-Path $Root "dist\Win7POS\Win7POS.Wpf.exe"),
        (Join-Path $Root ("src\Win7POS.Wpf\bin\{0}\{1}\net48\Win7POS.Wpf.exe" -f $Plat, $Config)),
        (Join-Path $Root ("src\Win7POS.Wpf\bin\{0}\net48\Win7POS.Wpf.exe" -f $Config))
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "Win7POS.Wpf.exe not found. Build first or pass -SkipBuild only after a successful build."
}

function Copy-IfPresent {
    param([string]$Source, [string]$DestinationDirectory)

    if (Test-Path $Source) {
        Copy-Item -Path $Source -Destination $DestinationDirectory -Force
        return "copied"
    }

    return "missing"
}

function Invoke-Build {
    $buildScript = Join-Path $RepoRoot "scripts\win7pos\windows\build-release-x86.ps1"

    if (Test-Path $buildScript) {
        & $buildScript -Configuration $Configuration -Platform $Platform
        if ($LASTEXITCODE -ne 0) {
            throw "build-release-x86.ps1 failed with exit code $LASTEXITCODE"
        }
        return
    }

    & dotnet build (Join-Path $RepoRoot "src\Win7POS.Wpf\Win7POS.Wpf.csproj") -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

if ($PlanOnly) {
    Write-Host "Win7POS i18n runtime validation plan"
    Write-Host "RepoRoot: $RepoRoot"
    Write-Host "Build: $Configuration $Platform"
    Write-Host "DataRoot: $DataRoot"
    Write-Host "EvidenceRoot: $EvidenceRoot"
    Write-Host "This mode does not build, launch, write app data, or touch cloud configuration."
    Write-Host "Runtime command on Windows:"
    Write-Host "  pwsh -NoLogo -NoProfile -File scripts\win7pos\windows\run-i18n-runtime-validation.ps1"
    exit 0
}

if (-not (Test-IsWindowsHost)) {
    throw "This runtime validation must run on Windows/VM. Use -PlanOnly outside Windows."
}

if ($Platform -ne "x86") {
    throw "Runtime validation requires x86. Received Platform=$Platform"
}

New-Item -ItemType Directory -Force $DataRoot | Out-Null
New-Item -ItemType Directory -Force $EvidenceRoot | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$evidenceDir = Join-Path $EvidenceRoot $stamp
New-Item -ItemType Directory -Force $evidenceDir | Out-Null

$cloudConfig = Join-Path $DataRoot "pos-admin-web.config"
if (Test-Path $cloudConfig) {
    throw "Refusing runtime validation: isolated data dir contains pos-admin-web.config. Remove it or use a clean DataRoot."
}

if (-not $SkipBuild) {
    Invoke-Build
}

$exePath = Resolve-Win7PosExe -Root $RepoRoot -Config $Configuration -Plat $Platform
$exeDir = Split-Path -Parent $exePath

$oldDataDir = $env:WIN7POS_DATA_DIR
$oldSafeStart = $env:WIN7POS_SAFE_START
$oldAdminUrl = $env:WIN7POS_ADMIN_WEB_BASE_URL
$oldAllowInsecure = $env:WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB

$process = $null
$processAlive = $false
$processExitCode = "not-started"
$settingsLanguage = "NOT_CHECKED"
$sqliteStatus = "NOT_RUN"

try {
    $env:WIN7POS_DATA_DIR = $DataRoot
    $env:WIN7POS_SAFE_START = "1"
    Remove-Item Env:\WIN7POS_ADMIN_WEB_BASE_URL -ErrorAction SilentlyContinue
    Remove-Item Env:\WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB -ErrorAction SilentlyContinue

    $process = Start-Process -FilePath $exePath -ArgumentList "--safe-start" -WorkingDirectory $exeDir -PassThru
    Start-Sleep -Seconds $LaunchSeconds
    $process.Refresh()
    $processAlive = -not $process.HasExited
    if ($process.HasExited) {
        $processExitCode = [string]$process.ExitCode
    }
    else {
        $processExitCode = "running"
    }

    if (-not $NoManualWait) {
        Write-Host ""
        Write-Host "Complete the manual i18n checks now:"
        Write-Host "  en, it, es, zh-CN language switch and restart persistence"
        Write-Host "  MainWindow, POS/cart, payment, import, products, users, settings, support/about, daily report"
        Write-Host "  zh-CN no tofu/layout break"
        Write-Host "  printer settings fallback and DailyReport export cancel"
        Read-Host "Press Enter after manual checks are complete"
    }

    $sqlite = Get-Command "sqlite3.exe" -ErrorAction SilentlyContinue
    $dbPath = Join-Path $DataRoot "pos.db"
    if ($sqlite -and (Test-Path $dbPath)) {
        $settingsLanguage = (& $sqlite.Source $dbPath "select value from app_settings where key='ui.language';" 2>$null | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($settingsLanguage)) {
            $settingsLanguage = "EMPTY"
        }
        $sqliteStatus = "RUN"
    }
    elseif (Test-Path $dbPath) {
        $sqliteStatus = "SQLITE_CLI_MISSING"
    }
    else {
        $sqliteStatus = "DB_MISSING_OR_FIRST_RUN_NOT_COMPLETED"
    }
}
finally {
    if ($StopAfterCollection -and $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }

    $env:WIN7POS_DATA_DIR = $oldDataDir
    $env:WIN7POS_SAFE_START = $oldSafeStart
    $env:WIN7POS_ADMIN_WEB_BASE_URL = $oldAdminUrl
    $env:WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB = $oldAllowInsecure
}

$logDir = Join-Path $DataRoot "logs"
$appLog = Join-Path $logDir "app.log"
$startupTrace = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "Win7POS\logs\startup-trace.log"
$appLogCopy = Copy-IfPresent -Source $appLog -DestinationDirectory $evidenceDir
$startupTraceCopy = Copy-IfPresent -Source $startupTrace -DestinationDirectory $evidenceDir

$taskListPath = Join-Path $evidenceDir "tasklist.txt"
try {
    tasklist /FI "IMAGENAME eq Win7POS.Wpf.exe" | Out-File -FilePath $taskListPath -Encoding UTF8
}
catch {
    "tasklist failed: $($_.Exception.Message)" | Out-File -FilePath $taskListPath -Encoding UTF8
}

$reportPath = Join-Path $evidenceDir "i18n-runtime-validation-report.md"
$lines = New-Object System.Collections.Generic.List[string]
Add-Line $lines "# Win7POS i18n runtime validation report"
Add-Line $lines ""
Add-Line $lines "- Date/time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
Add-Line $lines "- OS: $(Get-OsSummary)"
Add-Line $lines "- .NET Framework release: $(Get-DotNetFrameworkRelease)"
Add-Line $lines "- Process architecture requested: $Platform"
Add-Line $lines "- Repo root: $RepoRoot"
Add-Line $lines "- Exe: $exePath"
Add-Line $lines "- DataRoot: $DataRoot"
Add-Line $lines "- EvidenceDir: $evidenceDir"
Add-Line $lines "- Safe start: WIN7POS_SAFE_START=1 and --safe-start"
Add-Line $lines "- Admin Web env cleared: yes"
Add-Line $lines "- Cloud config in isolated data dir: absent"
Add-Line $lines "- Physical printer: NOT_CHECKED_BY_SCRIPT"
Add-Line $lines ""
Add-Line $lines "## Runtime"
Add-Line $lines ""
Add-Line $lines "- Process alive after ${LaunchSeconds}s: $processAlive"
Add-Line $lines "- Process exit code: $processExitCode"
Add-Line $lines "- App log copied: $appLogCopy"
Add-Line $lines "- Startup trace copied: $startupTraceCopy"
Add-Line $lines "- SQLite ui.language check: $sqliteStatus"
Add-Line $lines "- ui.language value: $settingsLanguage"
if (-not [string]::IsNullOrWhiteSpace($ExpectedLanguage)) {
    Add-Line $lines "- Expected language: $ExpectedLanguage"
    Add-Line $lines "- Expected language matched: $([string]::Equals($settingsLanguage, $ExpectedLanguage, [System.StringComparison]::OrdinalIgnoreCase))"
}
Add-Line $lines ""
Add-Line $lines "## Manual checklist result"
Add-Line $lines ""
Add-Line $lines "- en: PENDING_OPERATOR"
Add-Line $lines "- it: PENDING_OPERATOR"
Add-Line $lines "- es: PENDING_OPERATOR"
Add-Line $lines "- zh-CN: PENDING_OPERATOR"
Add-Line $lines "- Restart persistence: PENDING_OPERATOR"
Add-Line $lines "- Unsupported language fallback to en: PENDING_OPERATOR"
Add-Line $lines "- zh-CN no tofu/layout break: PENDING_OPERATOR"
Add-Line $lines "- Printer fallback: PENDING_OPERATOR"
Add-Line $lines "- DailyReport export cancel/filter: PENDING_OPERATOR"
Add-Line $lines "- Physical printer: PHYSICAL_PRINTER_EXTERNAL_GATE unless tested manually"
Add-Line $lines ""
Add-Line $lines "## PASS criteria"
Add-Line $lines ""
Add-Line $lines "Mark PASS only when the WPF window opens without crash, logs have no WPF/binding/localization exceptions, all four languages switch and persist, zh-CN renders legibly, and printer/export fallbacks do not crash."

Set-Content -Path $reportPath -Value $lines -Encoding UTF8

Write-Host "Runtime evidence written:"
Write-Host "  $reportPath"
if (-not $processAlive) {
    throw "Win7POS process was not alive after launch window. See evidence report."
}
