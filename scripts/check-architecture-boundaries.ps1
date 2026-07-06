$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function RelPath([string]$path) {
    return $path.Substring($repoRoot.Length).TrimStart('\', '/')
}

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Get-SourceFiles([string]$relativeRoot, [string[]]$include) {
    $root = Join-Path $repoRoot $relativeRoot
    if (-not (Test-Path $root)) {
        Fail "Missing path: $relativeRoot"
        return @()
    }

    return Get-ChildItem -Path $root -Recurse -File -Include $include |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }
}

function Test-NoPattern([string]$label, [string]$relativeRoot, [string]$pattern, [string[]]$include = @("*.cs", "*.csproj")) {
    $matches = @()
    foreach ($file in Get-SourceFiles $relativeRoot $include) {
        $found = Select-String -Path $file.FullName -Pattern $pattern -AllMatches
        foreach ($match in $found) {
            $matches += [pscustomobject]@{
                Path = RelPath $file.FullName
                Line = $match.LineNumber
            }
        }
    }

    if ($matches.Count -eq 0) {
        Pass $label
        return
    }

    foreach ($match in $matches) {
        Fail "${label}: $($match.Path):$($match.Line)"
    }
}

function Read-Text([string]$relativePath) {
    return [System.IO.File]::ReadAllText((Join-Path $repoRoot $relativePath))
}

function Test-ProjectReferenceShape {
    $expected = @{
        "src/Win7POS.Core/Win7POS.Core.csproj" = @()
        "src/Win7POS.Data/Win7POS.Data.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj")
        "src/Win7POS.Wpf/Win7POS.Wpf.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj", "..\Win7POS.Data\Win7POS.Data.csproj")
        "src/Win7POS.Cli/Win7POS.Cli.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj", "..\Win7POS.Data\Win7POS.Data.csproj")
        "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj" = @("..\..\src\Win7POS.Core\Win7POS.Core.csproj", "..\..\src\Win7POS.Data\Win7POS.Data.csproj")
    }

    foreach ($project in $expected.Keys) {
        $fullPath = Join-Path $repoRoot $project
        if (-not (Test-Path $fullPath)) {
            Fail "Project missing: $project"
            continue
        }

        [xml]$xml = Get-Content -LiteralPath $fullPath
        $actual = @($xml.Project.ItemGroup.ProjectReference | ForEach-Object { $_.Include }) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object
        $wanted = @($expected[$project]) | Sort-Object

        $actualText = ($actual -join "|")
        $wantedText = ($wanted -join "|")
        if ($actualText -ne $wantedText) {
            Fail "Project references differ: $project expected=[$wantedText] actual=[$actualText]"
        }
    }
}

function Test-PayloadBuilderRedaction {
    $badPayloadPattern = "(?i)(deviceToken|sessionToken|trustedDeviceToken|pin|password|credential)"

    $catalogBuilder = "src/Win7POS.Data/Online/CatalogImportOutboxPayloadBuilder.cs"
    $salesBuilder = "src/Win7POS.Data/Online/PosSalesSyncRequestBuilder.cs"

    foreach ($path in @($catalogBuilder, $salesBuilder)) {
        if (-not (Test-Path (Join-Path $repoRoot $path))) {
            Fail "Payload builder missing: $path"
        }
    }

    $catalogText = Read-Text $catalogBuilder
    if ($catalogText -match $badPayloadPattern) {
        Fail "Catalog persisted payload builder contains auth/credential marker: $catalogBuilder"
    }
    else {
        Pass "Catalog persisted payload builder excludes auth/credential markers"
    }

    $salesText = Read-Text $salesBuilder
    if ($salesText -match "(?i)(trustedDeviceToken|pin|password|credential)") {
        Fail "Sales sync builder contains forbidden credential marker: $salesBuilder"
    }
    elseif ($salesText -match "SerializeRedacted" -and $salesText -match "DeviceToken\s*=\s*null" -and $salesText -match "SessionToken\s*=\s*null") {
        Pass "Sales sync persisted payload serializer redacts auth tokens"
    }
    else {
        Fail "Sales sync persisted payload serializer must redact auth tokens: $salesBuilder"
    }

    if ($catalogText -match "Path\.GetFileName\(") {
        Pass "Catalog import payload keeps only workbook file name"
    }
    else {
        Fail "Catalog import payload must redact full workbook paths: $catalogBuilder"
    }
}

Test-NoPattern `
    "Core has no WPF/UI namespaces" `
    "src/Win7POS.Core" `
    "\bSystem\.Windows\b|\bWindows\.Forms\b|\bMicrosoft\.Win32\b|\bPrintDialog\b|\bPrintQueue\b|\bLocalPrintServer\b"

Test-NoPattern `
    "Core has no SQLite/Dapper/Data implementation references" `
    "src/Win7POS.Core" `
    "\bWin7POS\.Data\b|\bMicrosoft\.Data\.Sqlite\b|\bSqliteConnection\b|\bSqliteTransaction\b|\bDapper\b"

Test-NoPattern `
    "Core has no concrete HTTP transport" `
    "src/Win7POS.Core" `
    "\bSystem\.Net\.Http\b|\bHttpClient\b|\bWebRequest\b|\bHttpWebRequest\b|\bServicePointManager\b"

Test-NoPattern `
    "Core has no concrete workbook reader packages" `
    "src/Win7POS.Core" `
    "\bClosedXML\b|\bExcelDataReader\b|\bXLWorkbook\b|\bExcelReaderFactory\b"

Test-NoPattern `
    "Data has no WPF/UI references" `
    "src/Win7POS.Data" `
    "\bSystem\.Windows\b|\bWindows\.Forms\b|\bMicrosoft\.Win32\b|\bPrintDialog\b|\bPrintQueue\b|\bLocalPrintServer\b|\bPresentationFramework\b"

Test-NoPattern `
    "WPF has no direct SQLite/Dapper usage" `
    "src/Win7POS.Wpf" `
    "\bMicrosoft\.Data\.Sqlite\b|\busing\s+Dapper\s*;|\bDapper\b|\bSqliteConnection\b|\bSqliteTransaction\b"

Test-NoPattern `
    "Source has no direct Supabase client or secret markers" `
    "src" `
    "(?i)(SUPABASE_SERVICE_ROLE_KEY|NEXT_PUBLIC_SUPABASE|createClient\s*\(|supabase\.co|supabaseUrl|supabaseKey|\bservice_role\b|anon key)"

Test-PayloadBuilderRedaction
Test-ProjectReferenceShape

if (-not $fail) {
    Pass "Project references remain acyclic and layered"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
