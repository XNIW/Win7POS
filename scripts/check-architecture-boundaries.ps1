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

function Normalize-ProjectPath([string]$path) {
    if ($null -eq $path) { return "" }
    $normalized = $path.Trim().Replace('\', '/')
    while ($normalized.StartsWith("./", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    return $normalized
}

function Test-SolutionProjectInventory {
    $classifications = @{
        "src/Win7POS.Core/Win7POS.Core.csproj" = "Core"
        "src/Win7POS.Data/Win7POS.Data.csproj" = "Data"
        "src/Win7POS.Wpf/Win7POS.Wpf.csproj" = "WPF runtime"
        "src/Win7POS.Cli/Win7POS.Cli.csproj" = "CLI diagnostics"
        "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj" = "Automated tests"
        "tests/Win7POS.CatalogPerformance/Win7POS.CatalogPerformance.csproj" = "Performance test"
        "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" = "QA-only WPF harness"
    }

    $solutionPath = Join-Path $repoRoot "Win7POS.slnx"
    if (-not (Test-Path $solutionPath)) {
        Fail "Solution missing: Win7POS.slnx"
        return
    }

    [xml]$solution = Get-Content -LiteralPath $solutionPath
    $solutionProjects = @($solution.SelectNodes("//Project") | ForEach-Object {
        Normalize-ProjectPath ([string]$_.Path)
    }) | Sort-Object -Unique
    $repoProjects = @(Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") `
            -Recurse -File -Filter "*.csproj" |
        Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" } |
        ForEach-Object { Normalize-ProjectPath (RelPath $_.FullName) }) | Sort-Object -Unique

    $unknown = @($repoProjects | Where-Object { -not $classifications.ContainsKey($_) })
    $classifiedMissing = @($classifications.Keys | Where-Object { $_ -notin $repoProjects })
    $missingFromSolution = @($repoProjects | Where-Object { $_ -notin $solutionProjects })
    $unknownInSolution = @($solutionProjects | Where-Object { $_ -notin $repoProjects })

    if ($unknown.Count -gt 0) {
        Fail "Unknown/unclassified projects: $($unknown -join ', ')"
    }
    else {
        Pass "All repository projects are classified ($($repoProjects.Count))"
    }

    if ($classifiedMissing.Count -gt 0) {
        Fail "Classified projects missing from repository: $($classifiedMissing -join ', ')"
    }
    elseif ($missingFromSolution.Count -gt 0 -or $unknownInSolution.Count -gt 0) {
        Fail "Solution/project inventory mismatch missing=[$($missingFromSolution -join ', ')] extra=[$($unknownInSolution -join ', ')]"
    }
    else {
        Pass "Win7POS.slnx exactly matches the classified project inventory ($($solutionProjects.Count))"
    }
}

function Test-ProjectReferenceShape {
    $expected = @{
        "src/Win7POS.Core/Win7POS.Core.csproj" = @()
        "src/Win7POS.Data/Win7POS.Data.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj")
        "src/Win7POS.Wpf/Win7POS.Wpf.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj", "..\Win7POS.Data\Win7POS.Data.csproj")
        "src/Win7POS.Cli/Win7POS.Cli.csproj" = @("..\Win7POS.Core\Win7POS.Core.csproj", "..\Win7POS.Data\Win7POS.Data.csproj")
        "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj" = @("..\..\src\Win7POS.Core\Win7POS.Core.csproj", "..\..\src\Win7POS.Data\Win7POS.Data.csproj")
        "tests/Win7POS.CatalogPerformance/Win7POS.CatalogPerformance.csproj" = @("..\..\src\Win7POS.Core\Win7POS.Core.csproj", "..\..\src\Win7POS.Data\Win7POS.Data.csproj")
        "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" = @("..\..\src\Win7POS.Core\Win7POS.Core.csproj", "..\..\src\Win7POS.Data\Win7POS.Data.csproj", "..\..\src\Win7POS.Wpf\Win7POS.Wpf.csproj")
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
        else {
            Pass "Project references classified: $project"
        }
    }
}

function Get-ProjectProperty([string]$relativePath, [string]$propertyName) {
    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path $fullPath)) {
        Fail "Project missing: $relativePath"
        return ""
    }

    [xml]$xml = Get-Content -LiteralPath $fullPath
    $nodes = $xml.SelectNodes("//PropertyGroup/$propertyName")
    if ($null -eq $nodes -or $nodes.Count -eq 0) {
        return ""
    }

    for ($i = $nodes.Count - 1; $i -ge 0; $i--) {
        $value = [string]$nodes.Item($i).InnerText
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
    }

    return ""
}

function Test-ProjectProperty([string]$label, [string]$relativePath, [string]$propertyName, [string]$expectedValue) {
    $actualValue = Get-ProjectProperty $relativePath $propertyName
    if ([string]::Equals($actualValue, $expectedValue, [StringComparison]::OrdinalIgnoreCase)) {
        Pass $label
    }
    else {
        Fail "${label}: $relativePath $propertyName expected '$expectedValue' actual '$actualValue'"
    }
}

function Test-ProjectTargetShape {
    Test-ProjectProperty "Core targets netstandard2.0" "src/Win7POS.Core/Win7POS.Core.csproj" "TargetFramework" "netstandard2.0"
    Test-ProjectProperty "Data targets netstandard2.0" "src/Win7POS.Data/Win7POS.Data.csproj" "TargetFramework" "netstandard2.0"
    Test-ProjectProperty "Data language version remains C# 8" "src/Win7POS.Data/Win7POS.Data.csproj" "LangVersion" "8.0"
    Test-ProjectProperty "CLI targets net10.0" "src/Win7POS.Cli/Win7POS.Cli.csproj" "TargetFramework" "net10.0"
    Test-ProjectProperty "Core tests target net10.0" "tests/Win7POS.Core.Tests/Win7POS.Core.Tests.csproj" "TargetFramework" "net10.0"
    Test-ProjectProperty "Catalog performance targets net10.0 and net48" "tests/Win7POS.CatalogPerformance/Win7POS.CatalogPerformance.csproj" "TargetFrameworks" "net10.0;net48"
    Test-ProjectProperty "Catalog performance net48 target is x86" "tests/Win7POS.CatalogPerformance/Win7POS.CatalogPerformance.csproj" "PlatformTarget" "x86"
    Test-ProjectProperty "Catalog performance net48 prefers 32-bit" "tests/Win7POS.CatalogPerformance/Win7POS.CatalogPerformance.csproj" "Prefer32Bit" "true"
    Test-ProjectProperty "WPF targets net48" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "TargetFramework" "net48"
    Test-ProjectProperty "WPF UseWPF enabled" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "UseWPF" "true"
    Test-ProjectProperty "WPF declares x86 platform" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "Platforms" "x86"
    Test-ProjectProperty "WPF PlatformTarget is x86" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "PlatformTarget" "x86"
    Test-ProjectProperty "WPF Prefer32Bit enabled" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "Prefer32Bit" "true"
    Test-ProjectProperty "WPF language version remains C# 8" "src/Win7POS.Wpf/Win7POS.Wpf.csproj" "LangVersion" "8.0"
    Test-ProjectProperty "UI smoke harness targets net48" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "TargetFramework" "net48"
    Test-ProjectProperty "UI smoke harness UseWPF enabled" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "UseWPF" "true"
    Test-ProjectProperty "UI smoke harness declares x86 platform" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "Platforms" "x86"
    Test-ProjectProperty "UI smoke harness PlatformTarget is x86" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "PlatformTarget" "x86"
    Test-ProjectProperty "UI smoke harness Prefer32Bit enabled" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "Prefer32Bit" "true"
    Test-ProjectProperty "UI smoke harness language version remains C# 8" "tests/Win7POS.Wpf.UiSmokeHarness/Win7POS.Wpf.UiSmokeHarness.csproj" "LangVersion" "8.0"
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

function Test-NamedLayerOwnership {
    $policyPath = "src/Win7POS.Core/Online/CatalogSyncPolicy.cs"
    $coordinatorPath = "src/Win7POS.Data/Online/CatalogSyncCoordinator.cs"
    $syncCenterPaths = @(
        "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterDialog.xaml.cs",
        "src/Win7POS.Wpf/Pos/Dialogs/SyncCenterViewModel.cs"
    )

    if ((Test-Path (Join-Path $repoRoot $policyPath)) -and
        (Read-Text $policyPath) -match "\b(static\s+)?class\s+CatalogSyncPolicy\b") {
        Pass "CatalogSyncPolicy remains in Core"
    }
    else {
        Fail "CatalogSyncPolicy missing from Core"
    }

    if ((Test-Path (Join-Path $repoRoot $coordinatorPath)) -and
        (Read-Text $coordinatorPath) -match "\bclass\s+CatalogSyncCoordinator\b") {
        $coordinatorText = Read-Text $coordinatorPath
        if ($coordinatorText -match "\bSystem\.Windows\b|\bDispatcher\b|\bWindow\b") {
            Fail "CatalogSyncCoordinator contains WPF/Dispatcher ownership"
        }
        else {
            Pass "CatalogSyncCoordinator remains UI-agnostic in Data"
        }
    }
    else {
        Fail "CatalogSyncCoordinator missing from Data"
    }

    $syncCenterText = ""
    foreach ($path in $syncCenterPaths) {
        if (-not (Test-Path (Join-Path $repoRoot $path))) {
            Fail "Sync Center source missing: $path"
            continue
        }
        $syncCenterText += Read-Text $path
    }
    if ($syncCenterText -match "\bDapper\b|\bSqliteConnection\b|\bSqliteTransaction\b|\bHttpClient\b|\bHttpRequestMessage\b") {
        Fail "Sync Center contains direct SQL or HTTP transport"
    }
    else {
        Pass "Sync Center delegates persistence and HTTP transport"
    }
}

function Test-NoRuntimeBlockingWaits {
    $matches = @()
    $roots = @("src/Win7POS.Wpf", "src/Win7POS.Data/Online")
    foreach ($relativeRoot in $roots) {
        foreach ($file in Get-SourceFiles $relativeRoot @("*.cs")) {
            if ($file.Name -eq "SupplierExcelWpfViewModelSmoke.cs") {
                continue
            }
            $found = Select-String -Path $file.FullName `
                -Pattern "\.Wait\s*\(|GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(" `
                -AllMatches
            foreach ($match in $found) {
                $matches += "$(RelPath $file.FullName):$($match.LineNumber)"
            }
        }
    }

    if ($matches.Count -gt 0) {
        Fail "Runtime WPF/sync paths contain blocking waits: $($matches -join ', ')"
    }
    else {
        Pass "Runtime WPF/sync paths contain no Wait()/GetAwaiter().GetResult() (QA dispatcher-pump smoke excluded)"
    }
}

function Test-HarnessReleaseExclusion {
    $releaseChecker = Read-Text "scripts/check-release-pack-completeness.ps1"
    if ($releaseChecker -notmatch "Win7POS\.Wpf\.UiSmokeHarness" -or
        $releaseChecker -notmatch "UI_RUNTIME_MATRIX" -or
        $releaseChecker -notmatch "shell-window-state") {
        Fail "Release completeness checker does not explicitly exclude the QA harness/artifacts"
        return
    }

    $releaseRoot = Join-Path $repoRoot "dist/Win7POS"
    if (Test-Path $releaseRoot) {
        $leaks = @(Get-ChildItem -Path $releaseRoot -Recurse -File -ErrorAction Stop |
            Where-Object { $_.Name -match "(?i)(UiSmokeHarness|UI_SURFACE_INVENTORY|UI_RUNTIME_MATRIX|lifecycle-result|shell-window-state|qa[-_ ]fixture|screenshots?)" })
        if ($leaks.Count -gt 0) {
            Fail "Current release pack contains QA harness/artifacts: $($leaks.Name -join ', ')"
            return
        }
    }

    Pass "QA harness and runtime-matrix artifacts are excluded by the release gate"
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
Test-SolutionProjectInventory
Test-ProjectReferenceShape
Test-ProjectTargetShape
Test-NamedLayerOwnership
Test-NoRuntimeBlockingWaits
Test-HarnessReleaseExclusion

if (-not $fail) {
    Pass "Project references remain acyclic and layered"
}

if ($fail) {
    Write-Host "`n=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
