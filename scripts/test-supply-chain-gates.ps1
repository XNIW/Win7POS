[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ToolDirectory,
    [string]$DotNetExe = "dotnet"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-supply-chain-negative-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$passed = 0

function Write-Json([string]$path, $value) {
    [System.IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}

function Invoke-ExpectedFailure([string]$label, [string]$script, [string[]]$arguments) {
    $log = Join-Path $testRoot (($label -replace '[^A-Za-z0-9.-]', '-') + ".log")
    & $pwsh -NoProfile -File $script @arguments *> $log
    if ($LASTEXITCODE -eq 0) { throw "Negative vector unexpectedly passed: $label. Log: $log" }
    $script:passed++
    Write-Host "PASS: negative vector rejected - $label"
}

$nugetGateScript = Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1"
$nugetReportCommands = @(Get-Content -LiteralPath $nugetGateScript | Where-Object { $_ -match 'Invoke-DotNetReport @\(' })
if ($nugetReportCommands.Count -ne 2 -or @($nugetReportCommands | Where-Object { $_ -notmatch '"--no-restore"' }).Count -ne 0) {
    throw "Both NuGet list package report commands must use --no-restore."
}
Write-Host "PASS: both NuGet list package report commands use --no-restore"

$toolsConfig = Join-Path $repoRoot "eng\supply-chain\tools.json"
$badTools = Get-Content -Raw -LiteralPath $toolsConfig | ConvertFrom-Json
$badTools.gitleaks.sha256 = "123"
$badToolsPath = Join-Path $testRoot "bad-tools.json"
Write-Json $badToolsPath $badTools
Invoke-ExpectedFailure "bad tool hash length" (Join-Path $PSScriptRoot "install-pinned-supply-chain-tools.ps1") @(
    "-ToolDirectory", (Join-Path $testRoot "unused-tools"), "-DotNetExe", $DotNetExe,
    "-ToolsConfigPath", $badToolsPath, "-ValidateConfigOnly")

$traversalTools = Get-Content -Raw -LiteralPath $toolsConfig | ConvertFrom-Json
$traversalTools.gitleaks.executable = "..\..\rogue.exe"
$traversalToolsPath = Join-Path $testRoot "traversal-tools.json"
Write-Json $traversalToolsPath $traversalTools
Invoke-ExpectedFailure "Gitleaks executable path traversal" (Join-Path $PSScriptRoot "install-pinned-supply-chain-tools.ps1") @(
    "-ToolDirectory", (Join-Path $testRoot "unused-traversal-tools"), "-DotNetExe", $DotNetExe,
    "-ToolsConfigPath", $traversalToolsPath, "-ValidateConfigOnly")

$projectPaths = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src"),(Join-Path $repoRoot "tests") -Filter packages.lock.json -Recurse |
    ForEach-Object { @(Get-ChildItem -LiteralPath $_.DirectoryName -Filter *.csproj -File)[0].FullName.Replace('\','/') } |
    Sort-Object -Unique)
function New-Report([string]$kind) {
    return [ordered]@{
        version = 1
        parameters = "--$kind --include-transitive"
        sources = @("https://api.nuget.org/v3/index.json")
        projects = @($projectPaths | ForEach-Object { [pscustomobject][ordered]@{ path = $_ } })
    }
}
$cleanVulnerable = New-Report "vulnerable"
$cleanDeprecated = New-Report "deprecated"
$cleanVulnerablePath = Join-Path $testRoot "clean-vulnerable.json"
$cleanDeprecatedPath = Join-Path $testRoot "clean-deprecated.json"
Write-Json $cleanVulnerablePath $cleanVulnerable
Write-Json $cleanDeprecatedPath $cleanDeprecated

$vulnerableProblems = New-Report "vulnerable"
$vulnerableProblems["problems"] = @([ordered]@{ level = "warning"; message = "Synthetic incomplete vulnerability audit." })
$vulnerableProblemsPath = Join-Path $testRoot "vulnerable-problems.json"
Write-Json $vulnerableProblemsPath $vulnerableProblems
Invoke-ExpectedFailure "vulnerability report top-level problems" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-vulnerability-problems"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $vulnerableProblemsPath, "-DeprecationReportPath", $cleanDeprecatedPath)

$deprecatedProblems = New-Report "deprecated"
$deprecatedProblems["problems"] = @([ordered]@{ level = "warning"; message = "Synthetic incomplete deprecation audit." })
$deprecatedProblemsPath = Join-Path $testRoot "deprecated-problems.json"
Write-Json $deprecatedProblemsPath $deprecatedProblems
Invoke-ExpectedFailure "deprecation report top-level problems" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-deprecation-problems"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $cleanVulnerablePath, "-DeprecationReportPath", $deprecatedProblemsPath)

$emptySources = New-Report "vulnerable"
$emptySources["sources"] = @()
$emptySourcesPath = Join-Path $testRoot "empty-sources.json"
Write-Json $emptySourcesPath $emptySources
Invoke-ExpectedFailure "empty NuGet sources" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-empty-sources"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $emptySourcesPath, "-DeprecationReportPath", $cleanDeprecatedPath)

$insecureSources = New-Report "vulnerable"
$insecureSources["sources"] = @("http://api.nuget.org/v3/index.json")
$insecureSourcesPath = Join-Path $testRoot "insecure-sources.json"
Write-Json $insecureSourcesPath $insecureSources
Invoke-ExpectedFailure "non-HTTPS NuGet source" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-insecure-sources"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $insecureSourcesPath, "-DeprecationReportPath", $cleanDeprecatedPath)

$missingNuGetOrg = New-Report "vulnerable"
$missingNuGetOrg["sources"] = @("https://packages.invalid.example/v3/index.json")
$missingNuGetOrgPath = Join-Path $testRoot "missing-nuget-org.json"
Write-Json $missingNuGetOrgPath $missingNuGetOrg
Invoke-ExpectedFailure "missing api.nuget.org source" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-missing-nuget-org"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $missingNuGetOrgPath, "-DeprecationReportPath", $cleanDeprecatedPath)

$badVulnerable = New-Report "vulnerable"
$badVulnerable.projects[0] | Add-Member -NotePropertyName frameworks -NotePropertyValue @([ordered]@{
    framework = "net10.0"
    transitivePackages = @([ordered]@{ id = "Synthetic.Vulnerable"; resolvedVersion = "1.0.0"; severity = "High" })
})
$badVulnerablePath = Join-Path $testRoot "bad-vulnerable.json"
Write-Json $badVulnerablePath $badVulnerable
Invoke-ExpectedFailure "known vulnerable package" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-vulnerable"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $badVulnerablePath, "-DeprecationReportPath", $cleanDeprecatedPath)

$badDeprecated = New-Report "deprecated"
$badDeprecated.projects[0] | Add-Member -NotePropertyName frameworks -NotePropertyValue @([ordered]@{
    framework = "net10.0"
    topLevelPackages = @([ordered]@{ id = "Synthetic.Deprecated"; resolvedVersion = "1.0.0"; deprecationReasons = @("Legacy") })
})
$badDeprecatedPath = Join-Path $testRoot "bad-deprecated.json"
Write-Json $badDeprecatedPath $badDeprecated
Invoke-ExpectedFailure "deprecated package" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-deprecated"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $cleanVulnerablePath, "-DeprecationReportPath", $badDeprecatedPath)

$badPolicy = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "eng\supply-chain\license-policy.json") | ConvertFrom-Json
$badPolicy.licenseGroups[0].packages = @($badPolicy.licenseGroups[0].packages | Select-Object -Skip 1)
$badPolicyPath = Join-Path $testRoot "bad-license-policy.json"
Write-Json $badPolicyPath $badPolicy
Invoke-ExpectedFailure "missing exact license mapping" (Join-Path $PSScriptRoot "invoke-nuget-supply-chain-gates.ps1") @(
    "-OutputDirectory", (Join-Path $testRoot "out-license"), "-DotNetExe", $DotNetExe,
    "-VulnerabilityReportPath", $cleanVulnerablePath, "-DeprecationReportPath", $cleanDeprecatedPath,
    "-LicensePolicyPath", $badPolicyPath)

$locked = @{}
foreach ($lock in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src"),(Join-Path $repoRoot "tests") -Filter packages.lock.json -Recurse)) {
    $json = Get-Content -Raw -LiteralPath $lock.FullName | ConvertFrom-Json
    foreach ($framework in $json.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ([string]$package.Value.type -ceq "Project") { continue }
            $key = (([string]$package.Name) + "@" + ([string]$package.Value.resolved)).ToLowerInvariant()
            $locked[$key] = [ordered]@{
                type = "library"; name = [string]$package.Name; version = [string]$package.Value.resolved
                purl = "pkg:nuget/$([Uri]::EscapeDataString([string]$package.Name))@$([Uri]::EscapeDataString([string]$package.Value.resolved))"
                "bom-ref" = "pkg:nuget/$([Uri]::EscapeDataString([string]$package.Name))@$([Uri]::EscapeDataString([string]$package.Value.resolved))"
            }
        }
    }
}
$config = Get-Content -Raw -LiteralPath $toolsConfig | ConvertFrom-Json
$sha = "0123456789abcdef0123456789abcdef01234567"
$serialHasher = [Security.Cryptography.SHA256]::Create()
try {
    $serialHex = ([BitConverter]::ToString($serialHasher.ComputeHash(
        [Text.Encoding]::UTF8.GetBytes("Win7POS|1.0.0-dev.test|$sha"))) -replace '-', '').ToLowerInvariant()
}
finally { $serialHasher.Dispose() }
$serial = "urn:uuid:" + $serialHex.Substring(0, 8) + "-" +
    $serialHex.Substring(8, 4) + "-5" + $serialHex.Substring(13, 3) +
    "-8" + $serialHex.Substring(17, 3) + "-" + $serialHex.Substring(20, 12)
$bom = [ordered]@{
    bomFormat = "CycloneDX"; specVersion = "1.6"; serialNumber = $serial; version = 1
    metadata = [ordered]@{
        tools = [ordered]@{ components = @([ordered]@{ type = "application"; name = "CycloneDX module for .NET"; version = "6.2.0.0" }) }
        component = [ordered]@{
            type = "application"; name = "Win7POS"; version = "1.0.0-dev.test"
            "bom-ref" = "Win7POS@1.0.0-dev.test"
        }
        properties = @(
            [ordered]@{ name = "win7pos:source-commit"; value = $sha },
            [ordered]@{ name = "win7pos:cyclonedx-package-sha256"; value = [string]$config.cycloneDx.sha256 }
        )
    }
    components = @($locked.Keys | Sort-Object | ForEach-Object { $locked[$_] })
    dependencies = @(
        [ordered]@{
            ref = "Win7POS@1.0.0-dev.test"
            dependsOn = @($locked.Keys | Sort-Object | ForEach-Object { $locked[$_]["bom-ref"] })
        }
        @($locked.Keys | Sort-Object | ForEach-Object {
            [ordered]@{ ref = $locked[$_]["bom-ref"]; dependsOn = @() }
        })
    )
}
$badBom = $bom | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$badBom.components = @($badBom.components | Select-Object -Skip 1)
$badBomPath = Join-Path $testRoot "bad-bom.json"
Write-Json $badBomPath $badBom
Invoke-ExpectedFailure "SBOM missing locked package" (Join-Path $PSScriptRoot "new-cyclonedx-sbom.ps1") @(
    "-OutputPath", $badBomPath, "-Version", "1.0.0-dev.test", "-CommitSha", $sha, "-ValidateOnly")

$badSerialBom = $bom | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$badSerialBom.serialNumber = "urn:uuid:00000000-0000-5000-8000-000000000000"
$badSerialBomPath = Join-Path $testRoot "bad-serial-bom.json"
Write-Json $badSerialBomPath $badSerialBom
Invoke-ExpectedFailure "SBOM mismatched deterministic serial" (Join-Path $PSScriptRoot "new-cyclonedx-sbom.ps1") @(
    "-OutputPath", $badSerialBomPath, "-Version", "1.0.0-dev.test", "-CommitSha", $sha, "-ValidateOnly")

$zeroVersionBom = $bom | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$zeroVersionBom.version = 0
$zeroVersionBomPath = Join-Path $testRoot "zero-version-bom.json"
Write-Json $zeroVersionBomPath $zeroVersionBom
Invoke-ExpectedFailure "SBOM zero document version" (Join-Path $PSScriptRoot "new-cyclonedx-sbom.ps1") @(
    "-OutputPath", $zeroVersionBomPath, "-Version", "1.0.0-dev.test", "-CommitSha", $sha, "-ValidateOnly")

$missingDependencyBom = $bom | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$missingDependencyBom.dependencies = @($missingDependencyBom.dependencies | Select-Object -Skip 1)
$missingDependencyBomPath = Join-Path $testRoot "missing-dependency-bom.json"
Write-Json $missingDependencyBomPath $missingDependencyBom
Invoke-ExpectedFailure "SBOM missing dependency graph node" (Join-Path $PSScriptRoot "new-cyclonedx-sbom.ps1") @(
    "-OutputPath", $missingDependencyBomPath, "-Version", "1.0.0-dev.test", "-CommitSha", $sha, "-ValidateOnly")

$unknownDependencyBom = $bom | ConvertTo-Json -Depth 100 | ConvertFrom-Json
$unknownDependencyBom.dependencies[0].dependsOn[0] = "pkg:nuget/Unknown.Package@9.9.9"
$unknownDependencyBomPath = Join-Path $testRoot "unknown-dependency-bom.json"
Write-Json $unknownDependencyBomPath $unknownDependencyBom
Invoke-ExpectedFailure "SBOM unknown dependency graph target" (Join-Path $PSScriptRoot "new-cyclonedx-sbom.ps1") @(
    "-OutputPath", $unknownDependencyBomPath, "-Version", "1.0.0-dev.test", "-CommitSha", $sha, "-ValidateOnly")

$marker = Get-Content -Raw -LiteralPath (Join-Path ([System.IO.Path]::GetFullPath($ToolDirectory)) "supply-chain-toolchain.json") | ConvertFrom-Json
$gitleaksExe = Join-Path ([System.IO.Path]::GetFullPath($ToolDirectory)) ([string]$marker.gitleaks.executable)
$syntheticSecret = "github_token=ghp_" + [Guid]::NewGuid().ToString("N") + [Guid]::NewGuid().ToString("N").Substring(0, 4)
$secretDir = Join-Path $testRoot "secret-working-tree"
New-Item -ItemType Directory -Path $secretDir | Out-Null
[System.IO.File]::WriteAllText((Join-Path $secretDir "fixture.txt"), $syntheticSecret, [Text.UTF8Encoding]::new($false))
$workingReport = Join-Path $testRoot "negative-gitleaks-working.json"
& $gitleaksExe dir $secretDir --config (Join-Path $repoRoot "eng\supply-chain\gitleaks.toml") --report-format json --report-path $workingReport --redact=100 --no-banner --no-color
if ($LASTEXITCODE -ne 1 -or @(Get-Content -Raw -LiteralPath $workingReport | ConvertFrom-Json).Count -lt 1) {
    throw "Gitleaks working-tree negative vector was not detected."
}
$passed++
Write-Host "PASS: negative vector rejected - working-tree secret"

$historyDir = Join-Path $testRoot "secret-history"
New-Item -ItemType Directory -Path $historyDir | Out-Null
git -C $historyDir init --quiet
git -C $historyDir config user.name "Win7POS Supply Chain Test"
git -C $historyDir config user.email "supply-chain-test@invalid.example"
[System.IO.File]::WriteAllText((Join-Path $historyDir "fixture.txt"), $syntheticSecret, [Text.UTF8Encoding]::new($false))
git -C $historyDir add fixture.txt
git -C $historyDir commit --quiet -m "synthetic secret fixture"
[System.IO.File]::WriteAllText((Join-Path $historyDir "fixture.txt"), "safe", [Text.UTF8Encoding]::new($false))
git -C $historyDir add fixture.txt
git -C $historyDir commit --quiet -m "remove synthetic secret fixture"
$historyReport = Join-Path $testRoot "negative-gitleaks-history.json"
& $gitleaksExe git $historyDir --config (Join-Path $repoRoot "eng\supply-chain\gitleaks.toml") --report-format json --report-path $historyReport --redact=100 --no-banner --no-color
if ($LASTEXITCODE -ne 1 -or @(Get-Content -Raw -LiteralPath $historyReport | ConvertFrom-Json).Count -lt 1) {
    throw "Gitleaks full-history negative vector was not detected."
}
$passed++
Write-Host "PASS: negative vector rejected - historical secret"

Write-Host "PASS: $passed focused fail-closed supply-chain negative vectors"
Write-Host "Retained self-test artifacts: $testRoot"
