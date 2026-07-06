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
    return [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Test-NoMatch([string]$rootRelativePath, [string]$pattern, [string]$message) {
    $root = Join-Path $repoRoot $rootRelativePath
    if (-not (Test-Path $root)) {
        Fail "Missing path: $rootRelativePath"
        return
    }

    $matches = Get-ChildItem -Path $root -Recurse -File -Include *.cs |
        Where-Object {
            $text = [System.IO.File]::ReadAllText($_.FullName)
            [regex]::IsMatch($text, $pattern)
        }

    if ($matches) {
        foreach ($match in $matches) {
            Fail "${message}: $($match.FullName.Substring($repoRoot.Length).TrimStart('\', '/'))"
        }
    }
    else {
        Pass $message
    }
}

Test-NoMatch "src/Win7POS.Core" "\bWin7POS\.Wpf\b|\bWin7POS\.Data\b" "Core has no WPF/Data dependency markers"
Test-NoMatch "src/Win7POS.Data" "\bWin7POS\.Wpf\b" "Data has no WPF dependency markers"

$coreOnline = @(
    "src/Win7POS.Core/Online/PosAdminWebClient.cs",
    "src/Win7POS.Core/Online/PosAdminWebOptions.cs",
    "src/Win7POS.Core/Online/PosTrustedDeviceSession.cs",
    "src/Win7POS.Core/Online/PosOnlineContract.cs",
    "src/Win7POS.Core/Online/PosCatalogImportContract.cs"
)

foreach ($path in $coreOnline) {
    $fullPath = Join-Path $repoRoot $path
    if (-not (Test-Path $fullPath)) {
        Fail "Core online file missing: $path"
        continue
    }

    $text = Read-Text $path
    if ($text -notmatch "\bnamespace\s+Win7POS\.Core\.Online\b") {
        Fail "Core online namespace is not Win7POS.Core.Online: $path"
    }
}

if (-not $fail) {
    Pass "Core online contracts use Win7POS.Core.Online namespace"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
