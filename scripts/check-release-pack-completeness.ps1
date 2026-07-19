param(
    [string]$ReleasePackSource = "",
    [switch]$WriteManifests,
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

function Resolve-Source([string]$source) {
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    if ([System.IO.Path]::IsPathRooted($source)) {
        return $source
    }

    return (Join-Path $repoRoot $source)
}

function Get-RelativePath([string]$root, [string]$path) {
    return $path.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-ManifestPayloadFiles([string]$root) {
    $appManifest = Join-Path $root "APP-FILES.txt"
    $hashManifest = Join-Path $root "SHA256SUMS.txt"
    return @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        Where-Object {
            -not [string]::Equals($_.FullName, $appManifest, [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::Equals($_.FullName, $hashManifest, [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object { Get-RelativePath $root $_.FullName })
}

function Read-StrictUtf8NoBomLines([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasUtf8Bom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $hasUtf16Bom = $bytes.Length -ge 2 -and
        (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or
         ($bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF))
    $hasUtf32BeBom = $bytes.Length -ge 4 -and
        $bytes[0] -eq 0x00 -and $bytes[1] -eq 0x00 -and
        $bytes[2] -eq 0xFE -and $bytes[3] -eq 0xFF
    if ($hasUtf8Bom -or $hasUtf16Bom -or $hasUtf32BeBom) {
        Fail "ReleasePack manifest must be portable UTF-8 without any Unicode BOM: $([System.IO.Path]::GetFileName($path))"
        return $null
    }

    try {
        $strictUtf8 = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false, $true
        $text = $strictUtf8.GetString($bytes)
        return @($text -split "`r?`n")
    }
    catch {
        Fail "ReleasePack manifest is not valid strict UTF-8: $([System.IO.Path]::GetFileName($path))"
        return $null
    }
}

function Test-VersionMetadata([string]$root, [string]$expectedCommitSha) {
    $versionPath = Join-Path $root "VERSION.txt"
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { return }
    $lines = [System.IO.File]::ReadAllLines($versionPath)
    $commitLines = @($lines | Where-Object { $_ -match '^CommitSHA=([0-9a-fA-F]{40})$' })
    $configurationLines = @($lines | Where-Object { $_ -ceq 'Configuration=Release' })
    $platformLines = @($lines | Where-Object { $_ -ceq 'Platform=x86' })
    $sdkLines = @($lines | Where-Object { $_ -match '^SdkVersion=10\.0\.3\d{2}$' })
    $treeLines = @($lines | Where-Object { $_ -match '^TreeState=(clean|dirty)$' })
    if ($commitLines.Count -ne 1 -or
        $configurationLines.Count -ne 1 -or
        $platformLines.Count -ne 1 -or
        $sdkLines.Count -ne 1 -or
        $treeLines.Count -ne 1) {
        Fail "VERSION.txt must contain one valid CommitSHA, Configuration=Release, Platform=x86, 10.0.3xx SDK and clean/dirty TreeState"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($expectedCommitSha)) {
        if ($expectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
            Fail "ExpectedCommitSha must be a full 40-character commit SHA"
            return
        }
        $actualCommit = $commitLines[0].Substring('CommitSHA='.Length)
        if (-not [string]::Equals($actualCommit, $expectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "VERSION.txt CommitSHA does not match the expected build commit"
            return
        }
    }
    Pass "VERSION.txt contains valid x86 Release provenance"
}

function Write-ReleaseManifests([string]$root) {
    $files = Get-ManifestPayloadFiles $root
    $appFiles = @($files | ForEach-Object { Get-RelativePath $root $_.FullName })
    $utf8NoBom = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllLines(
        (Join-Path $root "APP-FILES.txt"),
        $appFiles,
        $utf8NoBom)

    $hashLines = foreach ($file in $files) {
        $relative = Get-RelativePath $root $file.FullName
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName
        "$($hash.Hash.ToLowerInvariant())  $relative"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $root "SHA256SUMS.txt"),
        $hashLines,
        $utf8NoBom)
    Pass "ReleasePack manifests written: APP-FILES.txt, SHA256SUMS.txt"
}

function Test-ReleaseManifests([string]$root) {
    $appPath = Join-Path $root "APP-FILES.txt"
    $hashPath = Join-Path $root "SHA256SUMS.txt"
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hashPath -PathType Leaf)) {
        Fail "ReleasePack root manifests APP-FILES.txt and SHA256SUMS.txt are required"
        return
    }

    $appLines = Read-StrictUtf8NoBomLines $appPath
    $hashLines = Read-StrictUtf8NoBomLines $hashPath
    if ($null -eq $appLines -or $null -eq $hashLines) { return }

    $files = Get-ManifestPayloadFiles $root
    $expectedPaths = @($files | ForEach-Object { Get-RelativePath $root $_.FullName })
    $listedPaths = @($appLines |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 })
    $invalidListedPath = @($listedPaths | Where-Object {
        [System.IO.Path]::IsPathRooted($_) -or
        $_ -match '(^|/)\.\.(/|$)' -or
        $_ -match '\\'
    })
    $listedUnique = @($listedPaths | Sort-Object -Unique)
    $expectedSorted = @($expectedPaths | Sort-Object)
    $listedSorted = @($listedPaths | Sort-Object)
    if ($invalidListedPath.Count -gt 0 -or
        $listedUnique.Count -ne $listedPaths.Count -or
        (Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $listedSorted)) {
        Fail "APP-FILES.txt must exactly enumerate every payload file once using safe relative paths"
        return
    }

    $hashes = @{}
    $malformedHashLine = $false
    foreach ($line in $hashLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})  (.+)$')
        if (-not $match.Success) {
            $malformedHashLine = $true
            continue
        }
        $relative = $match.Groups[2].Value
        if ([System.IO.Path]::IsPathRooted($relative) -or
            $relative -match '(^|/)\.\.(/|$)' -or
            $relative -match '\\' -or
            $hashes.ContainsKey($relative)) {
            $malformedHashLine = $true
            continue
        }
        $hashes[$relative] = $match.Groups[1].Value.ToLowerInvariant()
    }

    if ($malformedHashLine -or
        $hashes.Count -ne $expectedPaths.Count -or
        @($expectedPaths | Where-Object { -not $hashes.ContainsKey($_) }).Count -gt 0) {
        Fail "SHA256SUMS.txt must contain one well-formed hash for every payload file"
        return
    }

    foreach ($file in $files) {
        $relative = Get-RelativePath $root $file.FullName
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actual, $hashes[$relative], [StringComparison]::OrdinalIgnoreCase)) {
            Fail "SHA256SUMS.txt hash mismatch: $relative"
            return
        }
    }
    Pass "ReleasePack manifests exactly match payload inventory and SHA-256 content"
}

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
    "System.Buffers.dll",
    "System.Drawing.Common.dll",
    "System.IO.Packaging.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encoding.CodePages.dll",
    "System.Threading.Tasks.Extensions.dll",
    "System.ValueTuple.dll",
    "e_sqlite3.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "zxing.dll",
    "zxing.presentation.dll",
    "ZXing.Windows.Compatibility.dll",
    "XLParser.dll",
    "Assets/sii_qrcode.png",
    "VERSION.txt",
    "README_RUN.txt",
    "RELEASE_CHECKLIST.txt",
    "check-win7-prereqs.ps1",
    "set-admin-web-staging-url.bat",
    "APP-FILES.txt",
    "SHA256SUMS.txt"
)

$forbiddenFiles = @(
    "Win7POS.Cli.exe",
    "Win7POS.Cli.dll",
    "Win7POS.Cli.deps.json",
    "Win7POS.Cli.runtimeconfig.json",
    "Win7POS.Cli.pdb",
    "Win7POS.Wpf.UiSmokeHarness.exe",
    "Win7POS.Wpf.UiSmokeHarness.exe.config",
    "Win7POS.Wpf.UiSmokeHarness.dll",
    "Win7POS.Wpf.UiSmokeHarness.pdb",
    "PdfSharp-gdi.dll"
)

$secretPatterns = @(
    '(?i)SUPABASE_SERVICE_ROLE_KEY|\bservice_role\b',
    '(?i)mcpos_(device|session)_[A-Za-z0-9_-]{8,}',
    '(?i)Authorization\s*:\s*Bearer\s+[A-Za-z0-9._~+/-]{8,}',
    '(?i)"?(?:session[_-]?token|device[_-]?token|trusted[_-]?device[_-]?token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|apikey)"?\s*[:=]\s*(?:"[^"\r\n]{8,}"|[^\s,;]{8,})',
    '(?i)"?(?:password|credential|pin|pwd|db_password|database\s+password)"?\s*[:=]\s*(?:"[^"\r\n]+"|[^\s,;]+)',
    '(?i)\b(?:sk[-_]|sb_secret_)[A-Za-z0-9_-]{12,}\b',
    '\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b',
    '(?i)-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----'
)

function Test-SecretScannerVectors {
    $secretTestVectors = @(
        'password=P@ssw0rd!',
        'credential:temporary-value',
        'pin=1234',
        'db_password=correct-horse',
        '{"client_secret":"client-secret-value"}',
        'Authorization: Bearer bearer-secret-value',
        'sk-abcdefghijklmnopqrstuvwxyz',
        'eyJheader12345.payload12345.signature12345',
        '-----BEGIN PRIVATE KEY-----'
    )
    $missedSecretVectors = @($secretTestVectors | Where-Object {
        $value = $_
        -not ($secretPatterns | Where-Object { $value -match $_ } | Select-Object -First 1)
    })
    if ($missedSecretVectors.Count -gt 0) {
        Fail "ReleasePack secret scanner missed built-in vectors: $($missedSecretVectors -join ', ')"
    }
    else {
        Pass "ReleasePack secret scanner rejects credential, token, JWT and private-key vectors"
    }
    $safeTextVectors = @(
        'Never store production DBs, tokens, PINs, passwords or production config in this pack.',
        'VISUAL_CONFIRMATION=REQUIRED',
        'No secret is included.'
    )
    $unsafeFalsePositives = @($safeTextVectors | Where-Object {
        $value = $_
        $secretPatterns | Where-Object { $value -match $_ } | Select-Object -First 1
    })
    if ($unsafeFalsePositives.Count -gt 0) {
        Fail "ReleasePack secret scanner rejected safe documentation vectors"
    }
    else {
        Pass "ReleasePack secret scanner accepts safe documentation vectors"
    }
}

Test-SecretScannerVectors

if ([string]::IsNullOrWhiteSpace($ReleasePackSource)) {
    if ($fail) {
        Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
        exit 1
    }
    Pass "ReleasePack completeness checker loaded"
    Pass "Required files: $($requiredFiles -join ', ')"
    Pass "Forbidden runtime files: $($forbiddenFiles -join ', ')"
    Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
    exit 0
}

$source = Resolve-Source $ReleasePackSource
if (-not (Test-Path $source)) {
    Fail "ReleasePack source missing: $ReleasePackSource"
}
elseif (-not $fail) {
    $source = (Resolve-Path -LiteralPath $source).Path
}

$tempDir = $null
$root = $source
if (-not $fail -and -not (Test-Path $source -PathType Container)) {
    if ([System.IO.Path]::GetExtension($source) -notmatch "\.zip") {
        Fail "ReleasePack source must be a folder or zip: $ReleasePackSource"
    }
    else {
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-pack-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        Expand-Archive -Path $source -DestinationPath $tempDir -Force
        $root = $tempDir
    }
}

if (-not $fail) {
    if ($WriteManifests) {
        if ($tempDir) {
            Fail "-WriteManifests requires a folder source, not a zip"
        }
        else {
            Write-ReleaseManifests $root
        }
    }

    foreach ($name in $requiredFiles) {
        $requiredPath = Join-Path $root $name
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            Fail "ReleasePack missing required file: $name"
        }
        else {
            Pass "ReleasePack contains $name"
        }
    }

    $actualInventory = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        ForEach-Object { Get-RelativePath $root $_.FullName } |
        Sort-Object)
    $requiredInventory = @($requiredFiles |
        ForEach-Object { $_.Replace('\', '/') } |
        Sort-Object)
    if (Compare-Object -ReferenceObject $requiredInventory -DifferenceObject $actualInventory) {
        Fail "ReleasePack inventory must exactly match the reviewed x86 runtime closure"
    }
    else {
        Pass "ReleasePack inventory exactly matches the reviewed x86 runtime closure"
    }

    Test-VersionMetadata $root $ExpectedCommitSha

    Test-ReleaseManifests $root

    $cliFolder = Join-Path $root "cli"
    if (Test-Path $cliFolder) {
        Fail "ReleasePack must not bundle CLI diagnostics under runtime folder: cli"
    }
    else {
        Pass "ReleasePack does not bundle CLI diagnostics folder"
    }

    $receiptArchiveFolders = Get-ChildItem -Path $root -Recurse -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '(?i)^(receipts?|receipt[-_ ]archives?|scontrini)$' }
    if ($receiptArchiveFolders) {
        Fail "ReleasePack contains automatic receipt archive folders: $($receiptArchiveFolders.FullName -join ', ')"
    }
    else {
        Pass "ReleasePack contains no automatic receipt archive folder"
    }

    $receiptArchiveFiles = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        Where-Object {
            $_.Extension.ToLowerInvariant() -in @('.png', '.jpg', '.jpeg', '.bmp', '.txt') -and
            $_.BaseName -match '(?i)^(receipt|scontrino|boleta|daily[-_ ]summary|daily[-_ ]close)[-_ ]?(sale|refund|void|test|qa|\d)'
        }
    if ($receiptArchiveFiles) {
        Fail "ReleasePack contains receipt archive/sample output: $($receiptArchiveFiles.Name -join ', ')"
    }
    else {
        Pass "ReleasePack contains no receipt archive/sample output"
    }

    foreach ($name in $forbiddenFiles) {
        $found = Get-ChildItem -Path $root -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $found) {
            Fail "ReleasePack contains forbidden non-runtime file: $name"
        }
        else {
            Pass "ReleasePack does not contain $name"
        }
    }

    $forbiddenPdfSharp = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Name -match '(?i)^PdfSharp.*\.dll$' })
    if ($forbiddenPdfSharp.Count -gt 0) {
        Fail "ReleasePack contains forbidden PDF runtime DLL(s): $($forbiddenPdfSharp.Name -join ', ')"
    }
    else {
        Pass "ReleasePack contains no PdfSharp runtime DLL"
    }

    $forbiddenRuntimeDataFolders = @(Get-ChildItem -LiteralPath $root -Recurse -Directory -ErrorAction Stop |
        Where-Object { $_.Name -match '(?i)^(backups?|logs?|exports?)$' })
    if ($forbiddenRuntimeDataFolders.Count -gt 0) {
        Fail "ReleasePack contains runtime data folder(s): $($forbiddenRuntimeDataFolders.FullName -join ', ')"
    }
    else {
        Pass "ReleasePack contains no runtime backup, log or export folders"
    }

    $forbiddenExtensions = @(".pdb", ".cs", ".xaml", ".csproj", ".sln", ".slnx", ".db", ".sqlite", ".sqlite3", ".pdf", ".pem", ".key", ".pfx", ".p12", ".bak", ".dump", ".sql", ".log", ".trace", ".dmp")
    $forbiddenPayload = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        Where-Object {
            $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() -or
            $_.Name -match '(?i)^\.env(?:\.|$)' -or
            $_.Name -match '(?i)\.(?:db|sqlite|sqlite3)(?:-(?:wal|shm|journal))?$' -or
            $_.Name -match '(?i)(UiSmokeHarness|UI_SURFACE_INVENTORY|UI_RUNTIME_MATRIX|lifecycle-result|shell-window-state|qa[-_ ]fixture|screenshots?|customer[-_ ]display.*(?:result|matrix|screenshot)|monitor.*(?:result|fixture|test))' -or
            $_.Name -match '(?i)^(pos-admin-web\.config|.*production.*\.(json|config|txt)|.*(?:token|secret|credential|password|api[-_]?key|private[-_]?key).*(?:json|config|txt|xml|yml|yaml|ini|log))$'
        }
    if ($forbiddenPayload) {
        Fail "ReleasePack contains source, debug, DB, production config or secret-like files: $($forbiddenPayload.Name -join ', ')"
    }
    else {
        Pass "ReleasePack excludes source, debug, DB, production config and secret-like files"
    }

    $textPayload = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Extension.ToLowerInvariant() -in @(".txt", ".json", ".config", ".xml", ".bat", ".cmd", ".ps1", ".yml", ".yaml", ".ini", ".log") }
    $secretLeak = @($textPayload | Select-String -Pattern $secretPatterns)
    if ($secretLeak) {
        Fail "ReleasePack contains a token/service-role marker"
    }
    else {
        Pass "ReleasePack text contains no token/service-role marker"
    }

}

if ($tempDir -and (Test-Path $tempDir)) {
    Remove-Item -Path $tempDir -Recurse -Force
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
