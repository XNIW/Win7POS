[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$DotNetExe = "dotnet",
    [string]$SolutionPath = (Join-Path $PSScriptRoot "..\Win7POS.slnx"),
    [string]$LicensePolicyPath = (Join-Path $PSScriptRoot "..\eng\supply-chain\license-policy.json"),
    [string]$VulnerabilityReportPath,
    [string]$DeprecationReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$NuGetAuditSource = "https://api.nuget.org/v3/index.json"

function Read-JsonFile([string]$path) {
    try {
        return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $path).Path) | ConvertFrom-Json
    }
    catch {
        throw "Invalid or missing JSON '$path': $($_.Exception.Message)"
    }
}

function Write-Utf8([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

function Invoke-DotNetReport([string[]]$arguments, [string]$reportPath) {
    $errorPath = "$reportPath.stderr.log"
    $stdout = @(& $DotNetExe @arguments 2> $errorPath)
    $exitCode = $LASTEXITCODE
    Write-Utf8 $reportPath ($stdout -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        $firstError = if (Test-Path -LiteralPath $errorPath) {
            @(Get-Content -LiteralPath $errorPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
        } else { @() }
        $message = if ($firstError.Count) { [string]$firstError[0] } else { "no stderr" }
        throw "dotnet package gate failed with exit code ${exitCode}: $message"
    }
}

function Copy-Report([string]$source, [string]$destination) {
    $sourceFull = (Resolve-Path -LiteralPath $source).Path
    $destinationFull = [System.IO.Path]::GetFullPath($destination)
    if ($sourceFull -cne $destinationFull) {
        Copy-Item -LiteralPath $sourceFull -Destination $destinationFull -Force
    }
}

function Get-Findings($report, [string]$kind) {
    if ([int]$report.version -ne 1 -or [string]$report.parameters -notmatch "--$kind" -or
        [string]$report.parameters -notmatch "--include-transitive") {
        throw "$kind report does not prove an include-transitive JSON scan."
    }

    $problemsProperty = $report.PSObject.Properties["problems"]
    if ($null -ne $problemsProperty) {
        $problems = @($problemsProperty.Value | Where-Object { $null -ne $_ })
        if ($problems.Count -gt 0) {
            throw "$kind report contains top-level NuGet problems and cannot prove a complete audit."
        }
    }

    $sourcesProperty = $report.PSObject.Properties["sources"]
    $sources = @()
    if ($null -ne $sourcesProperty) {
        $sources = @($sourcesProperty.Value | Where-Object { $null -ne $_ })
    }
    if ($sources.Count -ne 1 -or
        $sources[0] -isnot [string] -or
        [string]$sources[0] -cne $NuGetAuditSource) {
        throw "$kind report must contain only the approved NuGet source '$NuGetAuditSource'."
    }
    foreach ($sourceValue in $sources) {
        if ($sourceValue -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$sourceValue)) {
            throw "$kind report contains an invalid NuGet source."
        }
        $sourceUri = $null
        if (-not [Uri]::TryCreate([string]$sourceValue, [UriKind]::Absolute, [ref]$sourceUri) -or
            $sourceUri.Scheme -cne [Uri]::UriSchemeHttps) {
            throw "$kind report contains a non-HTTPS NuGet source."
        }
    }
    $findings = [Collections.Generic.List[object]]::new()
    foreach ($project in @($report.projects)) {
        $frameworksProperty = $project.PSObject.Properties["frameworks"]
        if ($null -eq $frameworksProperty) { continue }
        foreach ($framework in @($frameworksProperty.Value)) {
            foreach ($propertyName in @("topLevelPackages", "transitivePackages")) {
                $property = $framework.PSObject.Properties[$propertyName]
                if ($null -ne $property) {
                    foreach ($package in @($property.Value)) {
                        $findings.Add([pscustomobject]@{
                            project = [string]$project.path
                            framework = [string]$framework.framework
                            package = [string]$package.id
                            version = [string]$package.resolvedVersion
                        })
                    }
                }
            }
        }
    }
    return @($findings)
}

$out = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $out | Out-Null
$vulnerabilityOut = Join-Path $out "nuget-vulnerable.json"
$deprecationOut = Join-Path $out "nuget-deprecated.json"

if ([string]::IsNullOrWhiteSpace($VulnerabilityReportPath)) {
    Invoke-DotNetReport @("list", (Resolve-Path -LiteralPath $SolutionPath).Path, "package", "--vulnerable", "--include-transitive", "--format", "json", "--no-restore", "--source", $NuGetAuditSource) $vulnerabilityOut
} else {
    Copy-Report $VulnerabilityReportPath $vulnerabilityOut
}
if ([string]::IsNullOrWhiteSpace($DeprecationReportPath)) {
    Invoke-DotNetReport @("list", (Resolve-Path -LiteralPath $SolutionPath).Path, "package", "--deprecated", "--include-transitive", "--format", "json", "--no-restore", "--source", $NuGetAuditSource) $deprecationOut
} else {
    Copy-Report $DeprecationReportPath $deprecationOut
}

$vulnerabilityReport = Read-JsonFile $vulnerabilityOut
$deprecationReport = Read-JsonFile $deprecationOut
$vulnerabilities = @(Get-Findings $vulnerabilityReport "vulnerable")
$deprecated = @(Get-Findings $deprecationReport "deprecated")
if ($vulnerabilities.Count -gt 0) {
    throw "Known vulnerable NuGet packages found: $($vulnerabilities.Count). See $vulnerabilityOut"
}
if ($deprecated.Count -gt 0) {
    throw "Deprecated NuGet packages found: $($deprecated.Count). See $deprecationOut"
}

$lockFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src"),(Join-Path $repoRoot "tests") -Filter packages.lock.json -Recurse | Sort-Object FullName)
if ($lockFiles.Count -eq 0) {
    throw "No packages.lock.json files were found."
}
$locked = @{}
$expectedProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($lock in $lockFiles) {
    $projects = @(Get-ChildItem -LiteralPath $lock.DirectoryName -Filter *.csproj -File)
    if ($projects.Count -ne 1) {
        throw "Lock file directory must contain exactly one project: $($lock.FullName)"
    }
    [void]$expectedProjects.Add($projects[0].FullName.Replace('\','/'))
    $json = Read-JsonFile $lock.FullName
    if ([int]$json.version -ne 1) {
        throw "Unsupported lock file version in $($lock.FullName)."
    }
    foreach ($framework in $json.dependencies.PSObject.Properties) {
        foreach ($package in $framework.Value.PSObject.Properties) {
            if ([string]$package.Value.type -ceq "Project") { continue }
            $id = [string]$package.Name
            $version = [string]$package.Value.resolved
            if ([string]::IsNullOrWhiteSpace($version)) {
                throw "Lock entry has no resolved version: $id in $($lock.FullName)."
            }
            $key = ($id + "@" + $version).ToLowerInvariant()
            $locked[$key] = [ordered]@{ id = $id; version = $version }
        }
    }
}

foreach ($report in @($vulnerabilityReport, $deprecationReport)) {
    $reported = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in @($report.projects)) { [void]$reported.Add(([string]$project.path).Replace('\','/')) }
    foreach ($project in $expectedProjects) {
        if (-not $reported.Contains($project)) {
            throw "NuGet scan report is missing solution project: $project"
        }
    }
}

$policy = Read-JsonFile $LicensePolicyPath
if ([int]$policy.schemaVersion -ne 1 -or @($policy.exceptions).Count -ne 0) {
    throw "License policy schema is invalid or contains an unapproved exception."
}
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($expression in @($policy.allowedExpressions)) { [void]$allowed.Add([string]$expression) }
$mapped = @{}
foreach ($group in @($policy.licenseGroups)) {
    $expression = [string]$group.expression
    if (-not $allowed.Contains($expression) -or [string]::IsNullOrWhiteSpace([string]$group.evidence)) {
        throw "License group is not approved or lacks evidence: '$expression'."
    }
    foreach ($package in @($group.packages)) {
        if ([string]$package -cnotmatch '^.+@[^@]+$') { throw "Invalid exact license mapping: '$package'." }
        $key = ([string]$package).ToLowerInvariant()
        if ($mapped.ContainsKey($key)) { throw "Duplicate license mapping: '$package'." }
        $mapped[$key] = $expression
    }
}

$unknown = @($locked.Keys | Where-Object { -not $mapped.ContainsKey($_) } | Sort-Object)
$stale = @($mapped.Keys | Where-Object { -not $locked.ContainsKey($_) } | Sort-Object)
if ($unknown.Count -gt 0) { throw "NuGet packages without approved exact license mappings: $($unknown -join ', ')." }
if ($stale.Count -gt 0) { throw "License policy contains mappings not present in lock files: $($stale -join ', ')." }

$inventory = [ordered]@{
    schemaVersion = 1
    lockFileCount = $lockFiles.Count
    packageVersionCount = $locked.Count
    packages = @($locked.Keys | Sort-Object | ForEach-Object {
        [ordered]@{ id = $locked[$_].id; version = $locked[$_].version; license = $mapped[$_] }
    })
}
Write-Utf8 (Join-Path $out "nuget-license-inventory.json") ($inventory | ConvertTo-Json -Depth 6)

Write-Host "PASS: NuGet vulnerability findings 0; deprecated findings 0"
Write-Host "PASS: $($locked.Count) exact package/version license mappings approved across $($lockFiles.Count) lock files"
Write-Host "Supply-chain reports: $out"
