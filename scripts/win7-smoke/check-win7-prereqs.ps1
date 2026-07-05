param(
    [string]$AppDir = "",
    [string]$DataDir = "",
    [string]$AdminWebBaseUrl = ""
)

$ErrorActionPreference = "Stop"
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Warn([string]$message) {
    Write-Host "WARN: $message" -ForegroundColor Yellow
}

function Get-RegistryDword([string[]]$paths, [string]$name) {
    foreach ($path in $paths) {
        try {
            $item = Get-ItemProperty -Path $path -Name $name -ErrorAction Stop
            if ($null -ne $item.$name) {
                return [int]$item.$name
            }
        }
        catch {
        }
    }

    return $null
}

function Get-PeMachine([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 66) {
        throw "PE file too small: $path"
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or ($peOffset + 6) -gt $bytes.Length) {
        throw "PE header missing: $path"
    }

    return [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

function Test-AppDir([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) {
        Warn "AppDir not provided; runtime pack file checks skipped"
        return
    }

    if (-not (Test-Path $root -PathType Container)) {
        Fail "AppDir missing: $root"
        return
    }

    $required = @(
        "Win7POS.Wpf.exe",
        "Win7POS.Core.dll",
        "Win7POS.Data.dll",
        "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
        "e_sqlite3.dll",
        "README_RUN.txt",
        "VERSION.txt"
    )

    foreach ($name in $required) {
        $path = Join-Path $root $name
        if (-not (Test-Path $path -PathType Leaf)) {
            Fail "AppDir missing runtime file: $name"
        }
        else {
            Pass "AppDir contains $name"
        }
    }

    $exe = Join-Path $root "Win7POS.Wpf.exe"
    if (Test-Path $exe -PathType Leaf) {
        $machine = Get-PeMachine $exe
        if ($machine -ne 0x014c) {
            Fail ("Win7POS.Wpf.exe must be x86 PE machine 0x014c, found 0x{0:x4}" -f $machine)
        }
        else {
            Pass "Win7POS.Wpf.exe PE machine is x86"
        }
    }

    $forbidden = @(
        "Win7POS.Cli.exe",
        "Win7POS.Cli.dll",
        "Win7POS.Cli.deps.json",
        "Win7POS.Cli.runtimeconfig.json",
        "Win7POS.Cli.pdb"
    )
    foreach ($name in $forbidden) {
        $path = Join-Path $root $name
        if (Test-Path $path -PathType Leaf) {
            Fail "AppDir contains forbidden CLI diagnostic file: $name"
        }
        else {
            Pass "AppDir does not contain $name"
        }
    }
}

function Resolve-DataRoot([string]$requested) {
    if (-not [string]::IsNullOrWhiteSpace($requested)) {
        return [System.IO.Path]::GetFullPath($requested)
    }

    $override = [Environment]::GetEnvironmentVariable("WIN7POS_DATA_DIR")
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        return [System.IO.Path]::GetFullPath($override.Trim())
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramData)) {
        return [System.IO.Path]::GetFullPath((Join-Path $env:ProgramData "Win7POS"))
    }

    return [System.IO.Path]::GetFullPath((Join-Path $env:TEMP "Win7POS"))
}

function Test-DataRoot([string]$root) {
    try {
        if (-not (Test-Path $root -PathType Container)) {
            New-Item -ItemType Directory -Force -Path $root | Out-Null
        }

        $logs = Join-Path $root "logs"
        $backups = Join-Path $root "backups"
        $exports = Join-Path $root "exports"
        foreach ($dir in @($logs, $backups, $exports)) {
            if (-not (Test-Path $dir -PathType Container)) {
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
            }
        }

        $probe = Join-Path $root "win7pos-prereq-write.tmp"
        [System.IO.File]::WriteAllText($probe, "write-ok", [System.Text.Encoding]::UTF8)
        Remove-Item -Path $probe -Force
        Pass "Data root writable: $root"
    }
    catch {
        Fail ("Data root is not writable: " + $_.Exception.Message)
    }
}

function Test-AdminWebConfig([string]$root, [string]$explicitUrl) {
    $envUrl = [Environment]::GetEnvironmentVariable("WIN7POS_ADMIN_WEB_BASE_URL")
    $configPath = Join-Path $root "pos-admin-web.config"
    $configUrl = ""

    if (Test-Path $configPath -PathType Leaf) {
        $lines = [System.IO.File]::ReadAllLines($configPath)
        foreach ($line in $lines) {
            $trimmed = $line.Trim()
            if ($trimmed.Length -eq 0 -or $trimmed.StartsWith("#")) {
                continue
            }

            if ($trimmed -match "^AdminWebBaseUrl\s*=\s*(.+)$") {
                $configUrl = $matches[1].Trim()
            }
            else {
                Fail "pos-admin-web.config contains unsupported line: $trimmed"
            }
        }
    }

    $source = "none"
    $value = ""
    if (-not [string]::IsNullOrWhiteSpace($envUrl)) {
        $source = "WIN7POS_ADMIN_WEB_BASE_URL"
        $value = $envUrl.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($configUrl)) {
        $source = "pos-admin-web.config"
        $value = $configUrl.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($explicitUrl)) {
        $source = "parameter"
        $value = $explicitUrl.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        Warn "Admin Web URL not configured; online smoke will need setup before linking"
        return
    }

    $uri = $null
    if (-not [System.Uri]::TryCreate($value, [System.UriKind]::Absolute, [ref]$uri)) {
        Fail "Admin Web URL is not absolute from $source"
        return
    }

    if (-not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        Fail "Admin Web URL must not include query string or fragment"
    }

    if ($uri.AbsolutePath -ne "/") {
        Fail "Admin Web URL must be a base URL, not an endpoint path"
    }

    $allowInsecure = [Environment]::GetEnvironmentVariable("WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB")
    if ($uri.Scheme -ne "https" -and $allowInsecure -ne "1") {
        Fail "Admin Web URL must use HTTPS unless WIN7POS_ALLOW_INSECURE_LAN_ADMIN_WEB=1 is explicitly set"
    }
    else {
        Pass "Admin Web URL source is valid: $source"
    }
}

$os = [Environment]::OSVersion.Version
if ($os.Major -lt 6 -or ($os.Major -eq 6 -and $os.Minor -lt 1)) {
    Fail ("Windows version must be Windows 7 SP1 or later; found " + $os.ToString())
}
elseif ($os.Major -eq 6 -and $os.Minor -eq 1 -and $os.Build -lt 7601) {
    Fail ("Windows 7 must be SP1 build 7601 or later; found " + $os.ToString())
}
else {
    Pass ("Windows version is supported: " + $os.ToString())
}

$dotNetRelease = Get-RegistryDword @(
    "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full"
) "Release"
if ($null -eq $dotNetRelease -or $dotNetRelease -lt 528040) {
    Fail ".NET Framework 4.8 or later not detected"
}
else {
    Pass (".NET Framework 4.8+ detected, Release=" + $dotNetRelease)
}

$vcInstalled = Get-RegistryDword @(
    "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
) "Installed"
if ($vcInstalled -ne 1) {
    Fail "Microsoft Visual C++ Runtime x86 not detected"
}
else {
    Pass "Microsoft Visual C++ Runtime x86 detected"
}

$dataRoot = Resolve-DataRoot $DataDir
Pass ("Data root resolved to: " + $dataRoot)
Test-DataRoot $dataRoot
Test-AdminWebConfig $dataRoot $AdminWebBaseUrl
Test-AppDir $AppDir

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
