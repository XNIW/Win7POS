param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Configuration = "Release",
    [string]$Platform = "x86",
    [string]$SdkVersion = "unknown",
    [string]$CommitSha = "",
    [string]$Ref = "",
    [string]$GitHubRunNumber = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
}
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path

function Get-GitText([string[]]$Arguments) {
    try {
        $value = & git -C $repoRoot @Arguments 2>$null
        if ($LASTEXITCODE -eq 0) { return (($value | Select-Object -First 1) -as [string]).Trim() }
    }
    catch { }
    return ""
}

if ([string]::IsNullOrWhiteSpace($CommitSha)) {
    $CommitSha = Get-GitText @("rev-parse", "HEAD")
}
if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Cannot write VERSION.txt: a full 40-character commit SHA is required."
}

if ([string]::IsNullOrWhiteSpace($Ref)) {
    $Ref = Get-GitText @("rev-parse", "--abbrev-ref", "HEAD")
}
if ([string]::IsNullOrWhiteSpace($Ref)) { $Ref = "unknown" }

$shortSha = $CommitSha.Substring(0, 12)
$treeState = "unknown"
try {
    $porcelain = & git -C $repoRoot status --porcelain 2>$null
    if ($LASTEXITCODE -eq 0) {
        $treeState = if ($porcelain) { "dirty" } else { "clean" }
    }
}
catch { }

$version = @(
    "Win7POS Windows x86 release pack",
    "CommitSHA=$CommitSha",
    "ShortSHA=$shortSha",
    "Ref=$Ref",
    "BuildTimestampUtc=$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))",
    "Configuration=$Configuration",
    "Platform=$Platform",
    "SdkVersion=$SdkVersion",
    "TreeState=$treeState"
)
if (-not [string]::IsNullOrWhiteSpace($GitHubRunNumber)) {
    $version += "GitHubRunNumber=$GitHubRunNumber"
}
[System.IO.File]::WriteAllLines((Join-Path $output "VERSION.txt"), $version, [System.Text.Encoding]::UTF8)

$readme = @(
    "Win7POS Windows x86 release pack",
    "",
    "Requirements:",
    "- Windows 7 SP1 or later.",
    "- .NET Framework 4.8.",
    "- Microsoft Visual C++ Runtime x86 (2015-2022).",
    "",
    "Run:",
    "1. Keep this folder intact and run check-win7-prereqs.ps1.",
    "2. Start Win7POS.Wpf.exe using a test-only data directory.",
    "3. The unified access screen asks for shop code, staff code and PIN/password.",
    "4. If Admin Web is temporarily unavailable on a truly empty DB, retry online or explicitly choose local recovery.",
    "5. Local recovery keeps sales disabled until an imported/restored catalog is verified sale-safe.",
    "",
    "Admin Web staging:",
    "- Run set-admin-web-staging-url.bat to write %ProgramData%\Win7POS\pos-admin-web.config.",
    "- Staging URL: https://merchandise-control-admin-web-staging.merchandise-control-admin-web.workers.dev",
    "- Never store production DBs, tokens, PINs, passwords or production config in this pack."
)
[System.IO.File]::WriteAllLines((Join-Path $output "README_RUN.txt"), $readme, [System.Text.Encoding]::UTF8)

$checklist = @(
    "Win7POS release checklist",
    "",
    "[ ] Windows 7 SP1, .NET Framework 4.8 and Visual C++ Runtime x86 are present.",
    "[ ] Win7POS.Wpf.exe starts and SQLite x86 loads.",
    "[ ] No PDB, source, DB, token, PIN/password, secret or production config is present.",
    "[ ] set-admin-web-staging-url.bat writes %ProgramData%\Win7POS\pos-admin-web.config.",
    "[ ] Unified online login works with test-only shop code, staff code and PIN/password.",
    "[ ] Offline fresh install offers explicit local recovery only after a transient server/network failure.",
    "[ ] Authentication denial offers no offline fallback or local admin recovery.",
    "[ ] Recovery mode exposes Products/Import and safe maintenance but keeps sales disabled.",
    "[ ] Sale, payment, refund, void and cash drawer work only after sale-safe verification.",
    "[ ] Backup/restore is tested only with test data.",
    "[ ] Hardware checks are recorded as PASS or PENDING HARDWARE."
)
[System.IO.File]::WriteAllLines((Join-Path $output "RELEASE_CHECKLIST.txt"), $checklist, [System.Text.Encoding]::UTF8)

foreach ($supportFile in @(
    @{ Source = "scripts\set-admin-web-staging-url.bat"; Destination = "set-admin-web-staging-url.bat" },
    @{ Source = "scripts\win7-smoke\check-win7-prereqs.ps1"; Destination = "check-win7-prereqs.ps1" }
)) {
    $source = Join-Path $repoRoot $supportFile.Source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release support file missing: $($supportFile.Source)"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $supportFile.Destination) -Force
}

Write-Host "Release support files written to: $output"
