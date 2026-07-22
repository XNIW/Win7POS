[CmdletBinding()]
param(
    [string]$ToolDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$CommitSha,
    [string]$DotNetExe = "dotnet",
    [string]$SolutionPath = (Join-Path $PSScriptRoot "..\Win7POS.slnx"),
    [string]$ToolsConfigPath = (Join-Path $PSScriptRoot "..\eng\supply-chain\tools.json"),
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Read-JsonFile([string]$path) {
    try { return [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $path).Path) | ConvertFrom-Json }
    catch { throw "Invalid or missing JSON '$path': $($_.Exception.Message)" }
}

function Get-LockedPackages {
    $packages = @{}
    $locks = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src"),(Join-Path $repoRoot "tests") -Filter packages.lock.json -Recurse)
    foreach ($lock in $locks) {
        $json = Read-JsonFile $lock.FullName
        foreach ($framework in $json.dependencies.PSObject.Properties) {
            foreach ($package in $framework.Value.PSObject.Properties) {
                if ([string]$package.Value.type -ceq "Project") { continue }
                $id = [string]$package.Name
                $resolved = [string]$package.Value.resolved
                $packages[($id + "@" + $resolved).ToLowerInvariant()] = $true
            }
        }
    }
    return $packages
}

function Get-MetadataProperty($bom, [string]$name) {
    $match = @($bom.metadata.properties | Where-Object { [string]$_.name -ceq $name })
    if ($match.Count -ne 1) { throw "SBOM metadata property '$name' is missing or duplicated." }
    return [string]$match[0].value
}

function Get-DeterministicSerialNumber([string]$version, [string]$commitSha) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $inputBytes = [Text.Encoding]::UTF8.GetBytes("Win7POS|$version|$commitSha")
        $hex = ([BitConverter]::ToString($sha256.ComputeHash($inputBytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
    $uuid = $hex.Substring(0, 8) + "-" +
        $hex.Substring(8, 4) + "-5" +
        $hex.Substring(13, 3) + "-8" +
        $hex.Substring(17, 3) + "-" +
        $hex.Substring(20, 12)
    return "urn:uuid:$uuid"
}

function Assert-Sbom([string]$path, $config) {
    $bom = Read-JsonFile $path
    if ([string]$bom.bomFormat -cne "CycloneDX" -or [string]$bom.specVersion -cne "1.6") {
        throw "SBOM must be CycloneDX JSON specification 1.6."
    }
    $documentVersionProperty = $bom.PSObject.Properties["version"]
    if ($null -eq $documentVersionProperty -or
        ($documentVersionProperty.Value -isnot [int] -and
            $documentVersionProperty.Value -isnot [long]) -or
        [long]$documentVersionProperty.Value -lt 1) {
        throw "SBOM document version must be a positive integer."
    }
    $expectedSerialNumber = Get-DeterministicSerialNumber -version $Version -commitSha $CommitSha.ToLowerInvariant()
    if ([string]$bom.serialNumber -cne $expectedSerialNumber -or
        [string]$bom.serialNumber -cnotmatch '^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-8[0-9a-f]{3}-[0-9a-f]{12}$') {
        throw "SBOM must contain the exact deterministic CycloneDX serial number."
    }
    if ([string]$bom.metadata.component.name -cne "Win7POS" -or
        [string]$bom.metadata.component.version -cne $Version) {
        throw "SBOM root component name/version mismatch."
    }
    $rootBomRef = [string]$bom.metadata.component.'bom-ref'
    if ([string]::IsNullOrWhiteSpace($rootBomRef)) {
        throw "SBOM root component must contain a non-empty bom-ref."
    }
    $toolVersions = @($bom.metadata.tools.components | Where-Object { [string]$_.name -ceq "CycloneDX module for .NET" } | ForEach-Object { [string]$_.version })
    if ($toolVersions.Count -ne 1 -or $toolVersions[0] -notlike "6.2.0*") {
        throw "SBOM does not record CycloneDX tool version 6.2.0."
    }
    if ((Get-MetadataProperty $bom "win7pos:source-commit") -cne $CommitSha.ToLowerInvariant()) {
        throw "SBOM source commit metadata mismatch."
    }
    if ((Get-MetadataProperty $bom "win7pos:cyclonedx-package-sha256") -cne [string]$config.cycloneDx.sha256) {
        throw "SBOM pinned CycloneDX package hash metadata mismatch."
    }

    $expected = Get-LockedPackages
    $actual = @{}
    $knownBomRefs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (-not $knownBomRefs.Add($rootBomRef)) {
        throw "SBOM root component bom-ref is duplicated."
    }
    foreach ($component in @($bom.components)) {
        if ([string]$component.type -cne "library" -or [string]$component.purl -notlike "pkg:nuget/*") {
            throw "SBOM contains a non-NuGet package component in the dependency inventory."
        }
        $componentBomRef = [string]$component.'bom-ref'
        if ([string]::IsNullOrWhiteSpace($componentBomRef) -or
            -not $knownBomRefs.Add($componentBomRef)) {
            throw "SBOM contains a missing or duplicate component bom-ref."
        }
        $key = (([string]$component.name) + "@" + ([string]$component.version)).ToLowerInvariant()
        if ($actual.ContainsKey($key)) { throw "SBOM contains duplicate package component '$key'." }
        $actual[$key] = $true
    }
    $missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object)
    $extra = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "SBOM/lock coverage mismatch. Missing=$($missing -join ','); Extra=$($extra -join ',')."
    }

    $dependenciesProperty = $bom.PSObject.Properties["dependencies"]
    if ($null -eq $dependenciesProperty -or $dependenciesProperty.Value -isnot [System.Array]) {
        throw "SBOM must contain an explicit dependency graph array."
    }
    $dependencyRefIndex = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($dependency in @($dependenciesProperty.Value)) {
        $dependencyRef = [string]$dependency.ref
        if ([string]::IsNullOrWhiteSpace($dependencyRef) -or
            -not $knownBomRefs.Contains($dependencyRef) -or
            -not $dependencyRefIndex.Add($dependencyRef)) {
            throw "SBOM dependency graph contains an unknown or duplicate node ref."
        }
        $dependsOnProperty = $dependency.PSObject.Properties["dependsOn"]
        if ($null -ne $dependsOnProperty) {
            if ($dependsOnProperty.Value -isnot [System.Array]) {
                throw "SBOM dependency graph dependsOn must be an array when present."
            }
            foreach ($targetRef in @($dependsOnProperty.Value)) {
                if ([string]::IsNullOrWhiteSpace([string]$targetRef) -or
                    -not $knownBomRefs.Contains([string]$targetRef)) {
                    throw "SBOM dependency graph contains an unknown dependsOn ref."
                }
            }
        }
    }
    if ($dependencyRefIndex.Count -ne $knownBomRefs.Count -or
        @($knownBomRefs | Where-Object { -not $dependencyRefIndex.Contains($_) }).Count -ne 0) {
        throw "SBOM dependency graph does not contain exactly the root and locked component refs."
    }
    return $expected.Count
}

if ($Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Version must be a deterministic semantic build version."
}
if ($CommitSha -cnotmatch '^[0-9a-fA-F]{40}$') {
    throw "CommitSha must be a full 40-character Git SHA."
}
$CommitSha = $CommitSha.ToLowerInvariant()
$config = Read-JsonFile $ToolsConfigPath
$outputFull = [System.IO.Path]::GetFullPath($OutputPath)

if (-not $ValidateOnly) {
    if ([string]::IsNullOrWhiteSpace($ToolDirectory)) { throw "ToolDirectory is required for SBOM generation." }
    $toolRoot = [System.IO.Path]::GetFullPath($ToolDirectory)
    $markerPath = Join-Path $toolRoot "supply-chain-toolchain.json"
    $marker = Read-JsonFile $markerPath
    $configHash = (Get-FileHash -LiteralPath (Resolve-Path -LiteralPath $ToolsConfigPath).Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$marker.configSha256 -cne $configHash -or [string]$marker.cycloneDx.version -cne "6.2.0" -or
        [string]$marker.cycloneDx.packageSha256 -cne [string]$config.cycloneDx.sha256) {
        throw "Pinned CycloneDX marker verification failed."
    }
    $cycloneDll = Join-Path $toolRoot ([string]$marker.cycloneDx.commandDll)
    if (-not (Test-Path -LiteralPath $cycloneDll -PathType Leaf)) { throw "Pinned CycloneDX command assembly is missing." }
    $actualToolVersion = (& $DotNetExe $cycloneDll --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualToolVersion -notlike "6.2.0+*") { throw "Pinned CycloneDX command version mismatch." }

    $parent = Split-Path -Parent $outputFull
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $fileName = Split-Path -Leaf $outputFull
    & $DotNetExe $cycloneDll (Resolve-Path -LiteralPath $SolutionPath).Path --output $parent --filename $fileName --output-format Json --spec-version 1.6 --disable-package-restore --no-serial-number --set-name Win7POS --set-version $Version
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputFull -PathType Leaf)) {
        throw "CycloneDX SBOM generation failed with exit code $LASTEXITCODE."
    }
    $bom = Read-JsonFile $outputFull
    $properties = @(
        [ordered]@{ name = "win7pos:source-commit"; value = $CommitSha },
        [ordered]@{ name = "win7pos:cyclonedx-package-version"; value = "6.2.0" },
        [ordered]@{ name = "win7pos:cyclonedx-package-sha256"; value = [string]$config.cycloneDx.sha256 }
    )
    $bom | Add-Member -NotePropertyName serialNumber -NotePropertyValue (Get-DeterministicSerialNumber -version $Version -commitSha $CommitSha) -Force
    $bom.metadata | Add-Member -NotePropertyName properties -NotePropertyValue $properties -Force
    [System.IO.File]::WriteAllText($outputFull, ($bom | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}

$count = Assert-Sbom $outputFull $config
$hash = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "PASS: CycloneDX 1.6 SBOM covers all $count exact locked package/version pairs"
Write-Host "SBOM SHA-256: $hash"
Write-Host "SBOM: $outputFull"
