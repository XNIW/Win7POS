$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Read-Required([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing required file: $relativePath")
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) {
        $failures.Add($message)
    }
}

function Reject-Match([string]$text, [string]$pattern, [string]$message) {
    if ($text -match $pattern) {
        $failures.Add($message)
    }
}

$core = Read-Required "src/Win7POS.Core/Images/ProductImageContract.cs"
$binary = Read-Required "src/Win7POS.Core/Images/ProductImageBinaryPolicy.cs"
$cache = Read-Required "src/Win7POS.Data/Images/ProductImageDiskCache.cs"
$decoder = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImageDecodeService.cs"
$preprocess = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImagePreprocessService.cs"
$flags = Read-Required "src/Win7POS.Wpf/Products/Images/ProductImageFeatureFlags.cs"
$editor = Read-Required "src/Win7POS.Wpf/Products/ProductEditDialog.xaml"
$wpfProject = Read-Required "src/Win7POS.Wpf/Win7POS.Wpf.csproj"

Require ($flags -match 'const\s+bool\s+IsPhaseAEnabled\s*=\s*false') `
    "Phase A product image feature flag must remain compile-time false."
Require ($editor -match 'DataContext\.ProductImagesPhaseAEnabled') `
    "The editor preview must bind visibility to the parent feature flag."
Require ($core -match 'interface\s+IProductImageStreamProvider') `
    "The offline stream-provider boundary is missing."
Require ($cache -match 'SpecialFolder\.LocalApplicationData') `
    "The cache default must use LocalApplicationData."
Require ($cache -match 'DefaultMaximumBytes\s*=\s*32L\s*\*\s*1024L\s*\*\s*1024L') `
    "The conservative 32 MiB default cache budget changed."
Require ($cache -match 'DefaultMaximumEntries\s*=\s*256') `
    "The conservative 256-entry default cache budget changed."
Require ($cache -match 'MinimumMaximumBytes\s*=\s*3L\s*\*\s*1024L\s*\*\s*1024L') `
    "The replacement-safe 3 MiB cache floor changed."
Require ($cache -match 'MinimumMaximumEntries\s*=\s*2') `
    "The replacement-safe two-entry cache floor changed."
Require ($cache -match 'DefaultMaximumConcurrentProducers\s*=\s*2') `
    "The cache producer concurrency default must remain bounded at two."
Require ($cache -match 'FileAttributes\.ReparsePoint') `
    "The cache must reject reparse roots and entries."
Require ($cache -match 'FileFlagOpenReparsePoint') `
    "The root lock must be opened without following reparse points."
Require ($cache -match 'RootLockFileName') `
    "The cache root singleton/process lock is missing."
Require ($cache -match 'StageSequence' -and $cache -match 'IsPromoted') `
    "The cache must persist staged/promoted variant state."
Require ($cache -match 'image_cache_directory_overflow') `
    "Bounded directory scans must fail closed on overflow."
Require ($cache -notmatch 'CommitEntry[\s\S]{0,1800}RemoveOtherVersions') `
    "A staged cache commit must not purge the prior valid version."
Require ($decoder -match 'BitmapCacheOption\.OnLoad') `
    "The WPF decoder must use BitmapCacheOption.OnLoad."
Require ($decoder -match '\.Freeze\(\)') `
    "The WPF decoder must freeze images before cross-thread handoff."
Require ($decoder -match 'DecodePixel(Width|Height)') `
    "The WPF decoder must use bounded DecodePixelWidth/Height."
Require ($preprocess -match 'Math\.Min\(\s*1\.0') `
    "Preprocessing must not upscale the source image."
Reject-Match $preprocess 'BitmapCreateOptions\.IgnoreColorProfile' `
    "Preprocessing must keep WIC color management enabled before canonical encoding."
Require ($wpfProject -match '<TargetFramework>net48</TargetFramework>') `
    "WPF must remain net48."
Require ($wpfProject -match '<PlatformTarget>x86</PlatformTarget>') `
    "WPF must remain x86."

$coreImages = (Get-ChildItem `
    -LiteralPath (Join-Path $repoRoot "src/Win7POS.Core/Images") `
    -File `
    -Filter "*.cs" |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
$dataImages = (Get-ChildItem `
    -LiteralPath (Join-Path $repoRoot "src/Win7POS.Data/Images") `
    -File `
    -Filter "*.cs" |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"
$wpfImages = (Get-ChildItem `
    -LiteralPath (Join-Path $repoRoot "src/Win7POS.Wpf/Products/Images") `
    -File |
    Where-Object { $_.Extension -in @(".cs", ".xaml") } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"

Reject-Match $coreImages 'System\.Windows|PresentationCore|Bitmap(Image|Source)' `
    "Core Images must not reference WPF types."
Reject-Match ($coreImages + $dataImages + $wpfImages) `
    'HttpClient|Supabase|Cloudflare|asus-staging|signed[_A-Za-z]*url|upload[_A-Za-z]*url' `
    "Phase A image code must not contain network, staging, or signed/upload URL surfaces."

$changed = @(
    & git -C $repoRoot diff --name-only origin/main --
    & git -C $repoRoot ls-files --others --exclude-standard
) | Sort-Object -Unique
$forbiddenChanges = @($changed | Where-Object {
    $_ -match '^supabase/' -or
    $_ -match '^src/Win7POS\.Data/Migrations/' -or
    $_ -match '^src/Win7POS\.(Data|Wpf)/.+Outbox' -or
    $_ -eq 'src/Win7POS.Wpf/Products/ProductsWorkflowService.cs' -or
    $_ -match '^src/Win7POS\.Data/Repositories/Product'
})
if ($forbiddenChanges.Count -gt 0) {
    $failures.Add(
        "Phase A changed schema, product persistence/workflow, or outbox files: " +
        ($forbiddenChanges -join ", "))
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Product image Phase A static gate passed."
Write-Host ("Checked {0} changed paths; feature flag remains disabled." -f $changed.Count)
