param(
    [string]$ReleasePackSource = "",
    [string]$ExpectedCommitSha = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Text([string]$relativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Require-Text([string]$name, [string]$text, [string]$pattern) {
    if ($text -notmatch $pattern) {
        Fail $name
    }
    else {
        Pass $name
    }
}

function Resolve-Source([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($source)) {
        return $source
    }

    return (Join-Path $repoRoot $source)
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

function Test-ReleasePackSource([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        Pass "Runtime release validator loaded; pass -ReleasePackSource to inspect a folder or zip"
        return
    }

    $resolved = Resolve-Source $source
    if (-not (Test-Path $resolved)) {
        Fail "ReleasePack source missing: $source"
        return
    }
    $resolved = (Resolve-Path -LiteralPath $resolved).Path

    $packChecker = Join-Path $repoRoot "scripts\check-release-pack-completeness.ps1"
    $pwsh = (Get-Command pwsh -ErrorAction SilentlyContinue).Source
    if (-not $pwsh) { $pwsh = (Get-Command powershell -ErrorAction Stop).Source }
    & $pwsh -NoProfile -File $packChecker -ReleasePackSource $resolved -ExpectedCommitSha $ExpectedCommitSha
    if ($LASTEXITCODE -ne 0) {
        Fail "Canonical release completeness/manifest validation failed"
        return
    }
    Pass "Canonical release completeness and manifest validation passed"

    $root = $resolved
    $tempDir = $null
    if (-not (Test-Path $resolved -PathType Container)) {
        if ([System.IO.Path]::GetExtension($resolved) -notmatch "\.zip") {
            Fail "ReleasePack source must be a folder or zip: $source"
            return
        }

        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-runtime-pack-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Expand-Archive -Path $resolved -DestinationPath $tempDir -Force
        $root = $tempDir
    }

    try {
        $requiredFiles = @(
            "Win7POS.Wpf.exe",
            "Win7POS.Wpf.exe.config",
            "Win7POS.Core.dll",
            "Win7POS.Data.dll",
            "ClosedXML.dll",
            "Dapper.dll",
            "DocumentFormat.OpenXml.dll",
            "ExcelDataReader.dll",
            "ExcelDataReader.DataSet.dll",
            "ExcelNumberFormat.dll",
            "Irony.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "Microsoft.Data.Sqlite.dll",
            "SixLabors.Fonts.dll",
            "SQLitePCLRaw.core.dll",
            "SQLitePCLRaw.batteries_v2.dll",
            "SQLitePCLRaw.provider.e_sqlite3.dll",
            "System.Buffers.dll",
            "System.Drawing.Common.dll",
            "System.IO.Packaging.dll",
            "System.Memory.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Text.Encoding.CodePages.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.ValueTuple.dll",
            "XLParser.dll",
            "zxing.dll",
            "zxing.presentation.dll",
            "ZXing.Windows.Compatibility.dll",
            "e_sqlite3.dll",
            "Assets/sii_qrcode.png",
            "README_RUN.txt",
            "RELEASE_CHECKLIST.txt",
            "VERSION.txt",
            "check-win7-prereqs.ps1",
            "APP-FILES.txt",
            "SHA256SUMS.txt"
        )

        foreach ($name in $requiredFiles) {
            $requiredPath = Join-Path $root $name
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                Fail "ReleasePack missing required runtime file: $name"
            }
            else {
                Pass "ReleasePack contains $name"
            }
        }

        $exePath = Join-Path $root "Win7POS.Wpf.exe"
        if (Test-Path -LiteralPath $exePath -PathType Leaf) {
            $machine = Get-PeMachine $exePath
            if ($machine -ne 0x014c) {
                Fail ("Win7POS.Wpf.exe must be PE32 x86 machine 0x014c, found 0x{0:x4}" -f $machine)
            }
            else {
                Pass "Win7POS.Wpf.exe PE machine is x86"
            }
        }

        $nativeSqlitePath = Join-Path $root "e_sqlite3.dll"
        if (Test-Path -LiteralPath $nativeSqlitePath -PathType Leaf) {
            $nativeMachine = Get-PeMachine $nativeSqlitePath
            if ($nativeMachine -ne 0x014c) {
                Fail ("e_sqlite3.dll must be PE32 x86 machine 0x014c, found 0x{0:x4}" -f $nativeMachine)
            }
            else {
                Pass "e_sqlite3.dll PE machine is x86"
            }
        }

        $configPath = Join-Path $root "Win7POS.Wpf.exe.config"
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            try {
                [xml]$configXml = [System.IO.File]::ReadAllText($configPath)
                $supportedRuntime = $configXml.configuration.startup.supportedRuntime
                if ($null -eq $supportedRuntime -or
                    $supportedRuntime.version -ne "v4.0" -or
                    $supportedRuntime.sku -ne ".NETFramework,Version=v4.8") {
                    Fail "Win7POS.Wpf.exe.config must target CLR v4.0 / .NET Framework 4.8"
                }
                else {
                    Pass "Win7POS.Wpf.exe.config targets CLR v4.0 / .NET Framework 4.8"
                }
            }
            catch {
                Fail "Win7POS.Wpf.exe.config is not valid XML: $($_.Exception.Message)"
            }
        }

        $forbiddenFiles = @(
            "Win7POS.Cli.exe",
            "Win7POS.Cli.dll",
            "Win7POS.Cli.deps.json",
            "Win7POS.Cli.runtimeconfig.json",
            "Win7POS.Cli.pdb"
        )
        foreach ($name in $forbiddenFiles) {
            $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -ne $found) {
                Fail "ReleasePack contains forbidden CLI diagnostic file: $name"
            }
            else {
                Pass "ReleasePack does not contain $name"
            }
        }

        $forbiddenThermalOnlyPayload = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
            Where-Object {
                $_.Extension -ieq ".pdf" -or
                $_.Name -match '(?i)^PdfSharp.*\.dll$'
            })
        if ($forbiddenThermalOnlyPayload.Count -gt 0) {
            Fail "Thermal-only runtime contains PDF payload/runtime: $($forbiddenThermalOnlyPayload.Name -join ', ')"
        }
        else {
            Pass "Thermal-only runtime contains no PDF payload or PdfSharp DLL"
        }

        $readmePath = Join-Path $root "README_RUN.txt"
        if (Test-Path -LiteralPath $readmePath -PathType Leaf) {
            $readmeText = [System.IO.File]::ReadAllText($readmePath)
            Require-Text "README_RUN mentions Windows 7 SP1" $readmeText "Windows 7 SP1"
            Require-Text "README_RUN mentions .NET Framework 4.8" $readmeText "\.NET Framework 4\.8"
            Require-Text "README_RUN mentions Visual C++ Runtime x86" $readmeText "Visual C\+\+ Runtime x86"
        }
    }
    finally {
        if ($tempDir -and (Test-Path $tempDir)) {
            Remove-Item -Path $tempDir -Recurse -Force
        }
    }
}

$requiredRepoFiles = @(
    "src/Win7POS.Wpf/Win7POS.Wpf.csproj",
    "src/Win7POS.Core/AppPaths.cs",
    "src/Win7POS.Core/PosPaths.cs",
    ".github/workflows/release-pack.yml",
    "installer/Win7POS.iss",
    "scripts/check-release-pack-completeness.ps1",
    "scripts/check-required-gates.ps1",
    "scripts/win7-smoke/check-win7-prereqs.ps1",
    "docs/WIN7_PRODUCTION_SMOKE_CHECKLIST.md"
)

foreach ($path in $requiredRepoFiles) {
    if (-not (Test-Path (Join-Path $repoRoot $path))) {
        Fail "$path missing"
    }
}

if (-not $fail) {
    $wpf = Read-Text "src/Win7POS.Wpf/Win7POS.Wpf.csproj"
    $paths = Read-Text "src/Win7POS.Core/PosPaths.cs"
    $appPaths = Read-Text "src/Win7POS.Core/AppPaths.cs"
    $workflow = Read-Text ".github/workflows/release-pack.yml"
    $installer = Read-Text "installer/Win7POS.iss"
    $packCheck = Read-Text "scripts/check-release-pack-completeness.ps1"
    $requiredGates = Read-Text "scripts/check-required-gates.ps1"
    $smoke = Read-Text "docs/WIN7_PRODUCTION_SMOKE_CHECKLIST.md"

    Require-Text "WPF targets net48" $wpf "<TargetFramework>net48</TargetFramework>"
    Require-Text "WPF PlatformTarget is x86" $wpf "<PlatformTarget>x86</PlatformTarget>"
    Require-Text "WPF platform list is x86" $wpf "<Platforms>x86</Platforms>"
    Require-Text "WPF prefers 32-bit runtime" $wpf "<Prefer32Bit>true</Prefer32Bit>"

    Require-Text "Data root defaults to ProgramData Win7POS" $paths "CommonApplicationData[\s\S]*Win7POS"
    Require-Text "Data root supports WIN7POS_DATA_DIR override" $paths "WIN7POS_DATA_DIR"
    Require-Text "AppPaths creates logs/backups/exports" $appPaths "LogsDirectory[\s\S]*BackupsDirectory[\s\S]*ExportsDirectory"

    Require-Text "Release workflow uses deterministic .NET 10 SDK" $workflow 'dotnet-version:\s*"10\.0\.301"'
    Require-Text "Release workflow builds WPF Release x86" $workflow 'dotnet build src/Win7POS\.Wpf/Win7POS\.Wpf\.csproj[\s\S]*-c Release[\s\S]*-p:Platform=x86[\s\S]*-p:PlatformTarget=x86'
    Require-Text "Release workflow copies WPF net48 x86 output" $workflow 'src/Win7POS\.Wpf/bin/x86/Release/net48'
    Require-Text "Release workflow validates pack folder" $workflow 'check-release-pack-completeness\.ps1[\s\S]{0,500}"-ReleasePackSource",\s*"dist/Win7POS"'
    Require-Text "Release workflow validates Win7 runtime folder through canonical gates" ($workflow + $requiredGates) 'check-required-gates\.ps1[\s\S]*check-win7-runtime-release-validation\.ps1'
    Require-Text "Release workflow validates Win7 runtime zip" $workflow 'check-win7-runtime-release-validation\.ps1[\s\S]{0,500}"-ReleasePackSource",\s*\$zip'
    Require-Text "Release workflow fails on every child-process exit" $workflow 'function\s+Invoke-CheckedProcess[\s\S]*LASTEXITCODE\s+-ne\s+0[\s\S]*throw'
    Require-Text "Release workflow installs Inno Setup through Chocolatey" $workflow 'choco install innosetup --yes --no-progress'
    Require-Text "Release workflow resolves and exports ISCC" $workflow 'ISCC_EXE=\$iscc[\s\S]*GITHUB_ENV[\s\S]*\$env:ISCC_EXE'
    Require-Text "Release workflow validates ISCC without a nonzero help command" $workflow 'VersionInfo\.FileVersion[\s\S]*ISCC ready'
    Require-Text "Release workflow requires a fresh exact installer artifact" $workflow 'installer/output/Win7POS-Setup\.exe[\s\S]*Remove-Item[\s\S]*ISCC failed[\s\S]*Fresh installer output missing'
    Require-Text "Release workflow fails when expected artifacts are absent" $workflow 'if-no-files-found:\s*error'

    if ($workflow -match 'jrsoftware\.org/download\.php|Invoke-WebRequest[\s\S]{0,200}innosetup') {
        Fail "Release workflow must not execute an unverified dynamic Inno Setup download"
    }
    else {
        Pass "Release workflow avoids unverified dynamic Inno Setup downloads"
    }

    if ($workflow -match 'Copy-Item[\s\S]{0,160}Win7POS\.Cli') {
        Fail "Release workflow must not copy CLI diagnostics into runtime pack"
    }
    else {
        Pass "Release workflow does not copy CLI diagnostics into runtime pack"
    }

    Require-Text "Pack check requires Microsoft.Data.Sqlite" $packCheck "Microsoft\.Data\.Sqlite\.dll"
    Require-Text "Pack check requires Dapper/transitive runtime closure and fiscal QR asset" $packCheck 'Dapper\.dll[\s\S]*ExcelNumberFormat\.dll[\s\S]*System\.Memory\.dll[\s\S]*Assets/sii_qrcode\.png'
    Require-Text "Pack check validates exact commit provenance" $packCheck 'ExpectedCommitSha[\s\S]*VERSION\.txt CommitSHA does not match'
    Require-Text "Pack check forbids CLI deps/runtimeconfig/pdb" $packCheck "Win7POS\.Cli\.deps\.json[\s\S]*Win7POS\.Cli\.runtimeconfig\.json[\s\S]*Win7POS\.Cli\.pdb"

    Require-Text "Installer requires Windows 7 SP1" $installer "(?m)^MinVersion=6\.1sp1\r?$"
    Require-Text "Installer checks .NET 4.8 release key" $installer "IsDotNet48OrLaterInstalled[\s\S]*ReleaseValue\s*>=\s*528040"
    Require-Text "Installer checks VC++ x86 runtime" $installer "VisualStudio\\14\.0\\VC\\Runtimes\\x86"
    Require-Text "Installer requires admin" $installer "(?m)^PrivilegesRequired=admin\r?$"
    Require-Text "Installer removes obsolete app-local PDF and diagnostic binaries on upgrade" $installer '\[InstallDelete\][\s\S]*\{app\}\\PdfSharp\*\.dll[\s\S]*\{app\}\\Win7POS\.Cli\.\*[\s\S]*\{app\}\\Win7POS\.Wpf\.UiSmokeHarness\.\*'
    if ($installer -match '(?im)^Type:\s*[^\r\n]*ProgramData') {
        Fail "Installer upgrade cleanup must never target ProgramData"
    }
    else {
        Pass "Installer upgrade cleanup does not target ProgramData"
    }

    Require-Text "Smoke checklist includes prereq script" $smoke "scripts[\\/]+win7-smoke[\\/]+check-win7-prereqs\.ps1"
    Require-Text "Smoke checklist covers Visual C++ Runtime x86" $smoke "Visual C\+\+ Runtime x86"
}

Test-ReleasePackSource $ReleasePackSource

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
