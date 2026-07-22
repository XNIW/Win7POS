[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$failed = $false

function Fail([string]$Message) {
    Write-Host "FAIL: $Message" -ForegroundColor Red
    $script:failed = $true
}

function Pass([string]$Message) {
    Write-Host "PASS: $Message" -ForegroundColor Green
}

function Get-CheckoutStepBlock {
    param([string[]]$Lines, [int]$UsesIndex)

    $start = $UsesIndex
    while ($start -ge 0 -and $Lines[$start] -notmatch '^(\s*)-\s') {
        $start--
    }
    if ($start -lt 0) { return $Lines[$UsesIndex] }

    $null = $Lines[$start] -match '^(\s*)-\s'
    $indent = $Matches[1].Length
    $end = $Lines.Count
    for ($index = $start + 1; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -match ("^\s{" + $indent + "}-\s")) {
            $end = $index
            break
        }
    }
    return ($Lines[$start..($end - 1)] -join [Environment]::NewLine)
}

function Get-WorkflowViolations {
    param([string]$Name, [string]$Text)

    $violations = New-Object System.Collections.Generic.List[string]
    $allowedWritePermissions = @{
        "security-supply-chain.yml|codeql-csharp|security-events" = $true
        "protected-release.yml|attest-signed-release|id-token" = $true
        "protected-release.yml|attest-signed-release|attestations" = $true
        "protected-release.yml|attest-signed-release|artifact-metadata" = $true
    }
    if ($Text -notmatch '(?m)^permissions:\s*\r?$') {
        $violations.Add("$Name has no explicit top-level permissions block")
    }
    if ($Text -notmatch '(?m)^permissions:\s*\r?\n\s{2}contents:\s*read\s*\r?$') {
        $violations.Add("$Name does not declare least-privilege contents: read")
    }
    if ($Text -match '(?im)^\s*permissions:\s*write-all\s*$') {
        $violations.Add("$Name grants write-all permission")
    }
    $lines = @($Text -split '\r?\n')
    $inJobs = $false
    $currentJob = ""
    foreach ($line in $lines) {
        if ($line -match '^jobs:\s*$') {
            $inJobs = $true
            $currentJob = ""
            continue
        }
        if ($inJobs -and $line -match '^  ([A-Za-z0-9_-]+):\s*$') {
            $currentJob = $Matches[1]
            continue
        }
        if ($line -match '^\s*([a-z-]+):\s*write\s*$') {
            $permissionName = $Matches[1].ToLowerInvariant()
            $permissionKey = "$Name|$currentJob|$permissionName"
            if (-not $allowedWritePermissions.ContainsKey($permissionKey)) {
                $scope = if ([string]::IsNullOrWhiteSpace($currentJob)) { "top level" } else { "job '$currentJob'" }
                $violations.Add("$Name grants unapproved write permission '$permissionName' in $scope")
            }
        }
    }

    $usesCount = 0
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line -notmatch 'uses:\s*([^\s@]+)@([^\s#]+)(?:\s+#\s*(.+))?$') { continue }

        $usesCount++
        $action = $Matches[1]
        $ref = $Matches[2]
        $comment = $Matches[3]
        if ($ref -notmatch '^[0-9a-f]{40}$') {
            $violations.Add("$Name uses unpinned action $action@$ref")
        }
        if ([string]::IsNullOrWhiteSpace($comment) -or $comment -notmatch '^v\d+\.\d+\.\d+(?:\s|$)') {
            $violations.Add("$Name action $action@$ref lacks a trailing semantic release tag comment")
        }
        if ($action -eq 'actions/checkout') {
            $block = Get-CheckoutStepBlock -Lines $lines -UsesIndex $index
            if ($block -notmatch '(?m)^\s+persist-credentials:\s*false\s*$') {
                $violations.Add("$Name checkout step does not set persist-credentials: false")
            }
            if ($block -notmatch '(?m)^\s+ref:\s*\$\{\{\s*[^}]+\s*\}\}\s*$') {
                $violations.Add("$Name checkout step does not select an explicit workflow SHA")
            }
        }
        if ($action -eq 'actions/setup-dotnet') {
            $block = Get-CheckoutStepBlock -Lines $lines -UsesIndex $index
            if ($block -notmatch '(?m)^\s+global-json-file:\s*global\.json\s*$') {
                $violations.Add("$Name setup-dotnet step does not consume global.json")
            }
        }
    }
    if ($usesCount -eq 0) {
        $violations.Add("$Name contains no pinned action uses")
    }

    $continuationPattern = [regex]::Escape([string][char]96) + '\r?\n\s*'
    $normalized = $Text -replace $continuationPattern, ' '
    foreach ($line in ($normalized -split '\r?\n')) {
        if ($line -match '\bdotnet\s+restore\b' -and $line -notmatch '(?:^|\s)--locked-mode(?:\s|$)') {
            $violations.Add("$Name contains restore without --locked-mode")
        }
        if ($line -match '\bdotnet\s+(?:build|test|run)\b' -and
            $line -notmatch '(?:^|\s)--no-restore(?:\s|$)') {
            $violations.Add("$Name contains build/test/run without --no-restore")
        }
    }
    return @($violations)
}

$goodVector = @"
permissions:
  contents: read
jobs:
  test:
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v1.2.3
        with:
          persist-credentials: false
          ref: `${{ github.sha }}
      - uses: actions/setup-dotnet@2222222222222222222222222222222222222222 # v1.2.3
        with:
          global-json-file: global.json
      - run: dotnet restore Demo.sln --locked-mode
      - run: dotnet build Demo.sln --no-restore
"@
if ((Get-WorkflowViolations -Name "positive-vector" -Text $goodVector).Count -ne 0) {
    Fail "Workflow policy positive self-test failed"
}
else {
    $mobileVector = $goodVector.Replace(
        'actions/checkout@1111111111111111111111111111111111111111',
        'actions/checkout@v5')
    $credentialVector = $goodVector.Replace('persist-credentials: false', 'persist-credentials: true')
    $restoreVector = $goodVector.Replace('dotnet restore Demo.sln --locked-mode', 'dotnet restore Demo.sln')
    $writeVector = $goodVector.Replace('contents: read', 'contents: write')
    $wrongJobWriteVector = @"
permissions:
  contents: read
jobs:
  dependency-sbom-secret-reproducibility:
    permissions:
      contents: read
      security-events: write
    steps:
      - uses: actions/checkout@1111111111111111111111111111111111111111 # v1.2.3
        with:
          persist-credentials: false
          ref: `${{ github.sha }}
"@
    if ((Get-WorkflowViolations -Name "mobile-vector" -Text $mobileVector).Count -eq 0 -or
        (Get-WorkflowViolations -Name "credential-vector" -Text $credentialVector).Count -eq 0 -or
        (Get-WorkflowViolations -Name "restore-vector" -Text $restoreVector).Count -eq 0 -or
        (Get-WorkflowViolations -Name "write-vector" -Text $writeVector).Count -eq 0 -or
        (Get-WorkflowViolations -Name "security-supply-chain.yml" -Text $wrongJobWriteVector).Count -eq 0) {
        Fail "Workflow policy negative self-tests did not fail closed"
    }
    else {
        Pass "Workflow pin, checkout credential and locked-restore negative vectors fail closed"
    }
}

$workflowFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot ".github\workflows") -File |
    Where-Object { $_.Extension -in @(".yml", ".yaml") } |
    Sort-Object Name)
if ($workflowFiles.Count -eq 0) { Fail "No workflow files found" }
foreach ($workflow in $workflowFiles) {
    $text = [System.IO.File]::ReadAllText($workflow.FullName)
    $violations = @(Get-WorkflowViolations -Name $workflow.Name -Text $text)
    if ($violations.Count -gt 0) {
        foreach ($violation in $violations) { Fail $violation }
    }
    else {
        Pass "$($workflow.Name) uses least privilege, full-SHA Actions and locked restore"
    }
}

$globalJsonPath = Join-Path $repoRoot "global.json"
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    Fail "global.json is required"
}
else {
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    if ($globalJson.sdk.version -notmatch '^\d+\.\d+\.\d+$' -or
        $globalJson.sdk.rollForward -ne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false) {
        Fail "global.json must pin one exact stable SDK with rollForward=disable"
    }
    else {
        Pass "global.json pins exact stable SDK $($globalJson.sdk.version)"
    }
}

$projectFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src") -Recurse -File -Filter "*.csproj") +
    @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests") -Recurse -File -Filter "*.csproj")
foreach ($project in $projectFiles) {
    $lockPath = Join-Path $project.Directory.FullName "packages.lock.json"
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        Fail "NuGet lock file missing for $($project.FullName.Substring($repoRoot.Length + 1))"
        continue
    }
    try {
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
        if ($lock.version -ne 1 -or $null -eq $lock.dependencies) { throw "unsupported lock schema" }
        Pass "NuGet lock present for $($project.BaseName)"
    }
    catch {
        Fail "NuGet lock file is invalid: $($lockPath.Substring($repoRoot.Length + 1))"
    }
}

$dependabotPath = Join-Path $repoRoot ".github\dependabot.yml"
if (-not (Test-Path -LiteralPath $dependabotPath -PathType Leaf)) {
    Fail "Controlled Dependabot configuration is required"
}
else {
    $dependabot = [System.IO.File]::ReadAllText($dependabotPath)
    if ($dependabot -notmatch 'package-ecosystem:\s*github-actions' -or
        $dependabot -notmatch 'package-ecosystem:\s*nuget' -or
        $dependabot -notmatch 'open-pull-requests-limit:\s*[1-9]') {
        Fail "Dependabot must cover GitHub Actions and NuGet with a bounded PR limit"
    }
    else {
        Pass "Dependabot updates Actions and NuGet on a bounded schedule"
    }
}

$versionResolver = Join-Path $repoRoot "scripts\win7pos\windows\resolve-release-version.ps1"
$supportWriter = Join-Path $repoRoot "scripts\win7pos\windows\write-release-support-files.ps1"
$innoValidator = Join-Path $repoRoot "scripts\win7pos\windows\test-inno-setup-toolchain.ps1"
$headSha = ((& git -C $repoRoot rev-parse HEAD) | Select-Object -First 1) -as [string]
if ($LASTEXITCODE -ne 0 -or $headSha -notmatch '^[0-9a-f]{40}$') {
    Fail "Cannot resolve the exact repository HEAD for release-tool negative vectors"
}
else {
    $resolved = $null
    try {
        $resolved = & $versionResolver -RepoRoot $repoRoot -CommitSha $headSha -Ref "refs/heads/release-foundation-test"
        if ($resolved.BuildVersion -notmatch '^\d+\.\d+\.\d+-dev\.[0-9a-f]{12}$') {
            throw "unexpected development version '$($resolved.BuildVersion)'"
        }
        Pass "Semantic version resolver accepts an exact-SHA development build"

        $releaseRef = "refs/tags/v$($resolved.ProductVersion)"
        $releaseResolved = & $versionResolver -RepoRoot $repoRoot -CommitSha $headSha -Ref $releaseRef
        if (-not $releaseResolved.IsRelease -or
            $releaseResolved.BuildVersion -ne $resolved.ProductVersion -or
            $releaseResolved.InstallerBaseFilename -ne "Win7POS-$($resolved.ProductVersion)-Setup") {
            throw "exact release tag did not produce consistent release metadata"
        }
        Pass "Exact authoritative release tag resolves consistent release metadata"
    }
    catch {
        Fail "Semantic version resolver positive vector failed: $($_.Exception.Message)"
    }

    if ($null -ne $resolved) {
        $versionParts = @($resolved.ProductVersion.Split('.') | ForEach-Object { [int]$_ })
        $mismatchedTag = "refs/tags/v$($versionParts[0]).$($versionParts[1]).$($versionParts[2] + 1)"
        foreach ($badRef in @($mismatchedTag, "refs/tags/release-$($resolved.ProductVersion)")) {
            $rejected = $false
            try {
                $null = & $versionResolver -RepoRoot $repoRoot -CommitSha $headSha -Ref $badRef
            }
            catch { $rejected = $true }
            if (-not $rejected) { Fail "Semantic version resolver accepted invalid release ref '$badRef'" }
        }
        if (-not $failed) { Pass "Mismatched and malformed release tags fail closed" }
    }

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("win7pos-release-foundation-" + [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $testRoot | Out-Null
        $writerRejected = $false
        try {
            $null = & $supportWriter -OutputDirectory (Join-Path $testRoot "support") -CommitSha $headSha -Ref "refs/heads/release-foundation-test" -BuildVersion "9.9.9"
        }
        catch { $writerRejected = $true }
        if (-not $writerRejected) {
            Fail "Release support writer accepted a mismatched supplied version"
        }
        else {
            Pass "Release support writer rejects mismatched version metadata"
        }

        $badInstaller = Join-Path $testRoot "untrusted-inno-installer.exe"
        [System.IO.File]::WriteAllBytes($badInstaller, [byte[]](0x00, 0x01, 0x02, 0x03))
        $hashRejected = $false
        try {
            $null = & $innoValidator -InstallerPath $badInstaller -SkipAuthenticode
        }
        catch { $hashRejected = $true }
        if (-not $hashRejected) {
            Fail "Pinned Inno validator accepted an installer with the wrong SHA-256"
        }
        else {
            Pass "Pinned Inno validator rejects an installer with the wrong SHA-256"
        }

        $compilerRejected = $false
        try {
            $null = & $innoValidator -IsccPath $badInstaller
        }
        catch { $compilerRejected = $true }
        if (-not $compilerRejected) {
            Fail "Pinned Inno validator accepted a compiler without trusted provenance"
        }
        else {
            Pass "Pinned Inno validator rejects an untrusted compiler"
        }
    }
    finally {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        if ($resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($resolvedTestRoot).StartsWith("win7pos-release-foundation-", [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "=== RESULT: FAIL ===" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== RESULT: ALL PASS ===" -ForegroundColor Green
exit 0
