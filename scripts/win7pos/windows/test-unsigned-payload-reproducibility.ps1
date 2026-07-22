[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceDirectory,

    [string]$ExpectedCommitSha = "",
    [string]$Ref = "",
    [string]$DotnetExe = "",
    [switch]$KeepBuildRoots
)

$ErrorActionPreference = "Stop"
$RequiredSdkVersion = "10.0.301"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$ProjectRelativePath = "src\Win7POS.Wpf\Win7POS.Wpf.csproj"
$OutputRelativePath = "src\Win7POS.Wpf\bin\x86\Release\net48"
$WriterRelativePath = "scripts\win7pos\windows\write-release-support-files.ps1"
$temporaryRoot = $null
$worktrees = New-Object System.Collections.Generic.List[string]

function Get-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = & git -C $RepoRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "git command failed: git -C <repo> $($Arguments -join ' ')"
    }
    return (($result | Select-Object -First 1) -as [string]).Trim()
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $([System.IO.Path]::GetFileName($FilePath)) $($Arguments[0])"
        }
    }
    finally {
        Pop-Location
    }
}

function Resolve-ExactDotnet {
    param([string]$RequestedPath)

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) { $candidates.Add($RequestedPath) | Out-Null }
    if ($env:WIN7POS_DOTNET_EXE) { $candidates.Add($env:WIN7POS_DOTNET_EXE) | Out-Null }
    $fromPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($fromPath) { $candidates.Add($fromPath.Source) | Out-Null }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $resolved = (Resolve-Path -LiteralPath $candidate).Path
        try {
            $version = (& $resolved --version 2>$null | Select-Object -First 1) -as [string]
            if ($LASTEXITCODE -eq 0 -and [string]::Equals($version.Trim(), $RequiredSdkVersion, [StringComparison]::Ordinal)) {
                return $resolved
            }
        }
        catch { }
    }

    throw "The reproducibility gate requires the exact .NET SDK $RequiredSdkVersion. Pass -DotnetExe or set WIN7POS_DOTNET_EXE."
}

function Assert-SafeTemporaryRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($fullPath).StartsWith("Win7POS-Repro-", [StringComparison]::Ordinal)) {
        throw "Refusing to manage an unsafe reproducibility temporary path: $fullPath"
    }
    return $fullPath
}

function Remove-IsolatedWorktrees {
    param([switch]$FailOnError)

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($worktree in @($worktrees.ToArray()) | Sort-Object -Descending) {
        & git -C $RepoRoot worktree remove --force $worktree 2>$null
        if ($LASTEXITCODE -ne 0) {
            $failures.Add($worktree) | Out-Null
            continue
        }
        $worktrees.Remove($worktree) | Out-Null
    }
    & git -C $RepoRoot worktree prune 2>$null
    $pruneFailed = $LASTEXITCODE -ne 0
    if ($FailOnError -and ($failures.Count -gt 0 -or $pruneFailed)) {
        throw "Could not clean all isolated reproducibility worktrees and prune their registrations."
    }
    foreach ($failure in $failures) {
        Write-Warning "Could not remove temporary worktree: $failure"
    }
    if ($pruneFailed) {
        Write-Warning "Could not prune temporary worktree registrations."
    }
    return ($failures.Count -eq 0 -and -not $pruneFailed)
}

function New-IsolatedPayloadBuild {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$BuildRoot,
        [Parameter(Mandatory = $true)][string]$CommitSha,
        [Parameter(Mandatory = $true)][object]$VersionInfo,
        [Parameter(Mandatory = $true)][string]$ResolvedDotnet,
        [Parameter(Mandatory = $true)][string]$IsolationKey
    )

    $sourceRoot = Join-Path $BuildRoot "source"
    $payloadRoot = Join-Path $BuildRoot "payload"
    New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null

    & git -C $RepoRoot worktree add --detach $sourceRoot $CommitSha
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create isolated $Label worktree at exact commit $CommitSha."
    }
    $worktrees.Add($sourceRoot) | Out-Null

    $worktreeHead = (& git -C $sourceRoot rev-parse HEAD 2>$null | Select-Object -First 1) -as [string]
    if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($worktreeHead.Trim(), $CommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label worktree is not at exact commit $CommitSha."
    }
    $initialStatus = & git -C $sourceRoot status --porcelain --untracked-files=all 2>$null
    if ($LASTEXITCODE -ne 0 -or $initialStatus) {
        throw "$Label worktree is not clean before restore/build."
    }

    $projectPath = Join-Path $sourceRoot $ProjectRelativePath
    $commonProperties = @(
        "-p:Configuration=Release",
        "-p:Platform=x86",
        "-p:PlatformTarget=x86",
        "-p:Win7PosVersion=$($VersionInfo.ProductVersion)",
        "-p:Win7PosCommitSha=$CommitSha",
        "-p:Win7PosBuildVersion=$($VersionInfo.BuildVersion)",
        "-p:Win7PosInformationalVersion=$($VersionInfo.InformationalVersion)",
        "-p:AssemblyVersion=$($VersionInfo.AssemblyVersion)",
        "-p:FileVersion=$($VersionInfo.FileVersion)",
        "-p:InformationalVersion=$($VersionInfo.InformationalVersion)",
        "-p:ContinuousIntegrationBuild=true"
    )
    Invoke-Checked -FilePath $ResolvedDotnet -WorkingDirectory $sourceRoot -Arguments (@(
        "restore",
        $projectPath,
        "--locked-mode",
        "--nologo"
    ) + $commonProperties)
    Invoke-Checked -FilePath $ResolvedDotnet -WorkingDirectory $sourceRoot -Arguments (@(
        "build",
        $projectPath,
        "-c", "Release",
        "--no-restore",
        "--nologo",
        "--verbosity", "minimal"
    ) + $commonProperties)

    $outputRoot = Join-Path $sourceRoot $OutputRelativePath
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "$Label did not produce the expected net48/x86 output: $outputRoot"
    }
    New-Item -ItemType Directory -Path $payloadRoot | Out-Null
    Copy-Item -Path (Join-Path $outputRoot "*") -Destination $payloadRoot -Recurse -Force

    $pdbFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "*.pdb")
    foreach ($pdbFile in $pdbFiles) {
        Remove-Item -LiteralPath $pdbFile.FullName -Force
    }
    if (Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter "*.pdb" | Select-Object -First 1) {
        throw "$Label still contains a PDB after the explicit debug-file removal step."
    }

    $supportWriter = Join-Path $sourceRoot $WriterRelativePath
    & $supportWriter `
        -OutputDirectory $payloadRoot `
        -Configuration "Release" `
        -Platform "x86" `
        -SdkVersion $RequiredSdkVersion `
        -CommitSha $CommitSha `
        -Ref $VersionInfo.Ref `
        -BuildVersion $VersionInfo.BuildVersion `
        -AssemblyVersion $VersionInfo.AssemblyVersion `
        -FileVersion $VersionInfo.FileVersion `
        -InformationalVersion $VersionInfo.InformationalVersion `
        -InstallerBaseFilename $VersionInfo.InstallerBaseFilename

    foreach ($requiredPayloadPath in @("Win7POS.Wpf.exe", "Win7POS.Wpf.exe.config", "Win7POS.Core.dll", "Win7POS.Data.dll", "VERSION.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $payloadRoot $requiredPayloadPath) -PathType Leaf)) {
            throw "$Label payload is missing required file: $requiredPayloadPath"
        }
    }

    return [pscustomobject][ordered]@{
        Label = $Label
        PayloadRoot = $payloadRoot
        RemovedPdbCount = $pdbFiles.Count
        CommitSha = $CommitSha
        CleanBeforeRestore = $true
        IsolationKey = $IsolationKey
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw "The net48/x86 reproducibility build gate must run on a Windows build machine."
}

try {
    $headSha = Get-GitText -Arguments @("rev-parse", "HEAD")
    if ([string]::IsNullOrWhiteSpace($ExpectedCommitSha)) { $ExpectedCommitSha = $headSha }
    if ($ExpectedCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "ExpectedCommitSha must be a full 40-character commit SHA."
    }
    $ExpectedCommitSha = $ExpectedCommitSha.ToLowerInvariant()
    if (-not [string]::Equals($headSha, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Checked-out HEAD '$headSha' does not match exact requested commit '$ExpectedCommitSha'."
    }
    $repoStatus = & git -C $RepoRoot status --porcelain --untracked-files=all 2>$null
    if ($LASTEXITCODE -ne 0 -or $repoStatus) {
        throw "The reproducibility gate requires a clean exact-commit working tree."
    }

    if ([string]::IsNullOrWhiteSpace($Ref)) {
        $Ref = if ($env:GITHUB_REF) {
            $env:GITHUB_REF
        }
        else {
            $branch = Get-GitText -Arguments @("rev-parse", "--abbrev-ref", "HEAD")
            if ([string]::Equals($branch, "HEAD", [StringComparison]::Ordinal)) { "refs/heads/detached" } else { "refs/heads/$branch" }
        }
    }

    $resolvedDotnet = Resolve-ExactDotnet -RequestedPath $DotnetExe
    $resolver = Join-Path $PSScriptRoot "resolve-release-version.ps1"
    $versionInfo = (& $resolver -RepoRoot $RepoRoot -CommitSha $ExpectedCommitSha -Ref $Ref -AsJson) | ConvertFrom-Json

    $evidenceFullPath = [System.IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\', '/')
    if ([string]::Equals($evidenceFullPath, $RepoRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "EvidenceDirectory must not be the repository root."
    }
    if (Test-Path -LiteralPath $evidenceFullPath) {
        if (-not (Test-Path -LiteralPath $evidenceFullPath -PathType Container)) {
            throw "EvidenceDirectory is not a directory: $evidenceFullPath"
        }
        if (Get-ChildItem -LiteralPath $evidenceFullPath -Force | Select-Object -First 1) {
            throw "EvidenceDirectory must be new or empty: $evidenceFullPath"
        }
    }
    else {
        New-Item -ItemType Directory -Force -Path $evidenceFullPath | Out-Null
    }

    $temporaryRoot = Assert-SafeTemporaryRoot -Path (Join-Path ([System.IO.Path]::GetTempPath()) ("Win7POS-Repro-" + [Guid]::NewGuid().ToString("N")))
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    Write-Host "Building unsigned net48/x86 payload twice at exact commit $ExpectedCommitSha with SDK $RequiredSdkVersion."
    $buildA = New-IsolatedPayloadBuild `
        -Label "Build A" `
        -BuildRoot (Join-Path $temporaryRoot "build-a") `
        -CommitSha $ExpectedCommitSha `
        -VersionInfo $versionInfo `
        -ResolvedDotnet $resolvedDotnet `
        -IsolationKey "build-a"
    $buildB = New-IsolatedPayloadBuild `
        -Label "Build B" `
        -BuildRoot (Join-Path $temporaryRoot "build-b") `
        -CommitSha $ExpectedCommitSha `
        -VersionInfo $versionInfo `
        -ResolvedDotnet $resolvedDotnet `
        -IsolationKey "build-b"

    $manifestWriter = Join-Path $PSScriptRoot "write-reproducibility-payload-manifest.ps1"
    $manifestAPath = Join-Path $evidenceFullPath "build-a.normalized-manifest.json"
    $manifestBPath = Join-Path $evidenceFullPath "build-b.normalized-manifest.json"
    & $manifestWriter -PayloadRoot $buildA.PayloadRoot -ManifestPath $manifestAPath -ExpectedCommitSha $ExpectedCommitSha -BuildVersion $versionInfo.BuildVersion
    & $manifestWriter -PayloadRoot $buildB.PayloadRoot -ManifestPath $manifestBPath -ExpectedCommitSha $ExpectedCommitSha -BuildVersion $versionInfo.BuildVersion

    $comparisonPath = Join-Path $evidenceFullPath "comparison.json"
    $comparator = Join-Path $PSScriptRoot "compare-reproducibility-payload-manifests.ps1"
    & $comparator -ManifestA $manifestAPath -ManifestB $manifestBPath -ComparisonEvidencePath $comparisonPath -ExpectedCommitSha $ExpectedCommitSha
    $canonicalManifestPath = Join-Path $evidenceFullPath "unsigned-payload.normalized-manifest.json"
    Copy-Item -LiteralPath $manifestAPath -Destination $canonicalManifestPath

    $worktreesCleaned = Remove-IsolatedWorktrees -FailOnError
    if (-not $worktreesCleaned) {
        throw "Isolated reproducibility worktree cleanup did not complete."
    }
    $temporaryBuildRootRemoved = $false
    if (-not $KeepBuildRoots) {
        $safeRoot = Assert-SafeTemporaryRoot -Path $temporaryRoot
        Remove-Item -LiteralPath $safeRoot -Recurse -Force
        if (Test-Path -LiteralPath $safeRoot) {
            throw "Temporary reproducibility build root cleanup did not complete: $safeRoot"
        }
        $temporaryBuildRootRemoved = $true
        $temporaryRoot = $null
    }

    $runEvidence = [pscustomobject][ordered]@{
        format = "win7pos-unsigned-payload-reproducibility-run-v1"
        passed = $true
        commitSha = $ExpectedCommitSha
        ref = $versionInfo.Ref
        productVersion = $versionInfo.ProductVersion
        buildVersion = $versionInfo.BuildVersion
        sdkVersion = $RequiredSdkVersion
        configuration = "Release"
        platform = "x86"
        targetFramework = "net48"
        isolatedCleanBuildCount = 2
        builds = @(
            [pscustomobject][ordered]@{
                label = $buildA.Label
                sourceKind = "detached-git-worktree"
                commitSha = $buildA.CommitSha
                cleanBeforeRestore = $buildA.CleanBeforeRestore
                outputCopiedToIsolatedPayload = $true
                payloadIsolationKey = $buildA.IsolationKey
                configuration = "Release"
                platform = "x86"
                targetFramework = "net48"
            },
            [pscustomobject][ordered]@{
                label = $buildB.Label
                sourceKind = "detached-git-worktree"
                commitSha = $buildB.CommitSha
                cleanBeforeRestore = $buildB.CleanBeforeRestore
                outputCopiedToIsolatedPayload = $true
                payloadIsolationKey = $buildB.IsolationKey
                configuration = "Release"
                platform = "x86"
                targetFramework = "net48"
            }
        )
        cleanup = [pscustomobject][ordered]@{
            worktreesRemoved = $true
            gitWorktreePruned = $true
            temporaryRootPolicy = "system-temp/Win7POS-Repro-*"
            temporaryBuildRootRemoved = $temporaryBuildRootRemoved
            retainedByExplicitRequest = [bool]$KeepBuildRoots
        }
        removedFiles = [pscustomobject][ordered]@{
            policy = "PDB debug files only"
            buildA = $buildA.RemovedPdbCount
            buildB = $buildB.RemovedPdbCount
        }
        normalizedFields = @("VERSION.txt:BuildTimestampUtc")
        excludedDerivedOrOuterContainers = @(
            "APP-FILES.txt",
            "SHA256SUMS.txt",
            "Win7POS-$($versionInfo.BuildVersion)-x86.zip",
            "Win7POS-$($versionInfo.BuildVersion)-Setup.exe"
        )
        manifests = @(
            [pscustomobject][ordered]@{ fileName = "build-a.normalized-manifest.json"; sha256 = (Get-FileHash -LiteralPath $manifestAPath -Algorithm SHA256).Hash.ToLowerInvariant() },
            [pscustomobject][ordered]@{ fileName = "build-b.normalized-manifest.json"; sha256 = (Get-FileHash -LiteralPath $manifestBPath -Algorithm SHA256).Hash.ToLowerInvariant() },
            [pscustomobject][ordered]@{ fileName = "unsigned-payload.normalized-manifest.json"; sha256 = (Get-FileHash -LiteralPath $canonicalManifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); role = "canonical unsigned payload integrity input" }
        )
        comparison = [pscustomobject][ordered]@{
            fileName = "comparison.json"
            sha256 = (Get-FileHash -LiteralPath $comparisonPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $runPath = Join-Path $evidenceFullPath "run.json"
    [System.IO.File]::WriteAllText(
        $runPath,
        ($runEvidence | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))

    $evidenceValidator = Join-Path $PSScriptRoot "test-reproducibility-evidence.ps1"
    & $evidenceValidator -EvidenceDirectory $evidenceFullPath -ExpectedCommitSha $ExpectedCommitSha

    Write-Host "Unsigned payload reproducibility PASS. Evidence: $evidenceFullPath"
}
finally {
    Remove-IsolatedWorktrees | Out-Null
    if ($temporaryRoot -and -not $KeepBuildRoots -and (Test-Path -LiteralPath $temporaryRoot -PathType Container)) {
        $safeRoot = Assert-SafeTemporaryRoot -Path $temporaryRoot
        Remove-Item -LiteralPath $safeRoot -Recurse -Force
    }
    elseif ($temporaryRoot -and $KeepBuildRoots) {
        Write-Warning "Retained reproducibility build roots for diagnostics: $temporaryRoot"
    }
}
