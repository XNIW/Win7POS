# Validates the exact unsigned release payload in fresh net48/x86 child processes.
# Keeps the positive payload immutable; negative fixtures stay outside evidence.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][string]$HarnessExe,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedCommitSha
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Hash-Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
function Same-Path([string]$A, [string]$B) {
    return [string]::Equals([IO.Path]::GetFullPath($A).TrimEnd('\','/'), [IO.Path]::GetFullPath($B).TrimEnd('\','/'), [StringComparison]::OrdinalIgnoreCase)
}
function Within([string]$Path, [string]$Root) {
    return (Same-Path $Path $Root) -or [IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
function Local-Path([string]$Path) {
    Require ([IO.Path]::IsPathFullyQualified($Path) -and -not $Path.StartsWith('\\')) 'A fully qualified local, non-device path is required.'
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    Require (([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))).DriveType -ne [IO.DriveType]::Network) 'Network drives are forbidden.'
    return $full
}
function No-Reparse([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        Require (Test-Path -LiteralPath $current) "A required local path is missing: $current"
        Require (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse points are forbidden: $current"
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function No-ReparseExistingAncestors([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            Require (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Reparse-point ancestors are forbidden: $current"
        }
        $current=[IO.Path]::GetDirectoryName($current)
    }
}
function Write-NewJson([string]$Path, [object]$Value) {
    $text = ($Value | ConvertTo-Json -Depth 35) + "`n"
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($text)
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function File-Map([string]$Root) {
    No-Reparse $Root
    $map = [ordered]@{}
    foreach ($item in @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName)) {
        Require (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Payload trees cannot contain reparse points.'
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\','/')
        $map[$relative] = [ordered]@{ sha256 = Hash-File $item.FullName; size = [long]$item.Length }
    }
    return ,$map
}
function Map-Json([object]$Map) { return ($Map | ConvertTo-Json -Depth 6 -Compress) }
function Assert-PositiveImmutable {
    Require ((Map-Json (File-Map $script:payload)) -ceq $script:positiveMapJson) 'The original positive payload changed; no further case is allowed.'
    foreach ($entry in $script:sourceMap.GetEnumerator()) {
        Require ((Hash-File (Join-Path $script:repoRoot $entry.Key)) -ceq $entry.Value) "Protected source/checker changed: $($entry.Key)"
    }
}
function Zip-Member([IO.Compression.ZipArchive]$Zip, [string]$Name) {
    $matches = @($Zip.Entries | Where-Object { $_.FullName -ceq $Name })
    Require ($matches.Count -eq 1 -and $matches[0].Length -gt 0 -and $matches[0].Length -le 131072) "One bounded package ZIP member is required: $Name"
    $inputStream = $matches[0].Open(); $memory = [IO.MemoryStream]::new()
    try { $inputStream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $inputStream.Dispose(); $memory.Dispose() }
}
function New-NegativeCopy([string]$Name, [string]$ChangedPath, [string]$Kind) {
    Assert-PositiveImmutable
    $destination = Join-Path $script:fixtureRoot $Name
    Require (-not (Test-Path -LiteralPath $destination)) 'Each owned negative fixture directory must be new.'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $script:payload -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -ErrorAction Stop
    }
    Require ((Map-Json (File-Map $destination)) -ceq $script:positiveMapJson) 'A negative fixture must begin as an exact positive copy.'
    $changed = Join-Path $destination $ChangedPath
    switch ($Kind) {
        'remove' { [IO.File]::Delete($changed) }
        'implementation' { [IO.File]::WriteAllBytes($changed, $script:implementationBytes) }
        'redirect' {
            $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($changed, $settings)
            $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null
            try { $xml.Load($reader) } finally { $reader.Dispose() }
            $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('a','urn:schemas-microsoft-com:asm.v1')
            $nodes = @($xml.SelectNodes('/configuration/runtime/a:assemblyBinding/a:dependentAssembly[a:assemblyIdentity/@name="System.ValueTuple"]', $ns))
            Require ($nodes.Count -eq 1) 'The owned redirect negative must remove exactly one ValueTuple dependentAssembly.'
            [void]$nodes[0].ParentNode.RemoveChild($nodes[0])
            $writerSettings = [Xml.XmlWriterSettings]::new(); $writerSettings.Encoding = [Text.UTF8Encoding]::new($false); $writerSettings.Indent = $true
            $writer = [Xml.XmlWriter]::Create($changed, $writerSettings)
            try { $xml.Save($writer) } finally { $writer.Dispose() }
        }
        'unexpected' { [IO.File]::Copy((Join-Path $destination 'Dapper.dll'), $changed, $false) }
        default { throw 'Unknown owned mutation.' }
    }
    $after = File-Map $destination
    $keys = @(@($script:positiveMap.Keys) + @($after.Keys) | Sort-Object -Unique)
    $deltas = @($keys | Where-Object {
        -not $script:positiveMap.Contains($_) -or -not $after.Contains($_) -or
        (Map-Json $script:positiveMap[$_]) -cne (Map-Json $after[$_])
    })
    Require ($deltas.Count -eq 1 -and $deltas[0] -ceq $ChangedPath) 'A negative fixture must change exactly its declared one file.'
    foreach ($manifest in @('APP-FILES.txt','SHA256SUMS.txt')) {
        Require ($after[$manifest].sha256 -ceq $script:positiveMap[$manifest].sha256) 'Baseline payload manifests must never be regenerated or changed in negatives.'
    }
    Write-NewJson (Join-Path $script:evidence "$Name-source-map.json") ([ordered]@{
        schema='WIN7POS_PACK_VALUE_TUPLE_FIXTURE_V1'; case=$Name; original_payload=$script:payload; fixture=$destination
        positive_map_sha256=Hash-Bytes ([Text.Encoding]::UTF8.GetBytes($script:positiveMapJson)); mutation=$Kind
        changed_paths=$deltas; expected_changed_path=$ChangedPath; manifests_regenerated=$false; files=$after
    })
    return $destination
}
function Stop-ExactOwnedProcess([Diagnostics.Process]$Process, [object]$Run, [string]$Name) {
    if ($null -eq $Run.pid -or $Process.HasExited) { return }
    $persistenceError=$null
    # Persist intent for exceptional exits too. A write failure invalidates the gate,
    # but cannot bypass bounded cleanup of a positively identified owned process.
    try { Write-NewJson (Join-Path $script:evidence "$Name.cleanup-before-kill.json") $Run }
    catch {
        $persistenceError=$_; $Run.cleanup_evidence_error=$_.Exception.Message
        [Console]::Error.WriteLine('FAIL cleanup intent persistence; exact owned cleanup still required; no subsequent case.')
    }
    try {
        if (-not $Process.HasExited) {
            Require ($Process.Id -eq $Run.pid -and $Process.StartTime.ToUniversalTime().Ticks -eq $Run.process_start_ticks -and (Same-Path $Process.MainModule.FileName $Run.executable)) 'Exact owned process identity cannot be confirmed.'
            $Process.Kill($false)
        }
        Require ($Process.WaitForExit(5000)) 'The exact owned process did not exit within the cleanup bound.'
        $Run.cleanup='EXACT_OWNED_PROCESS_EXIT_CONFIRMED'; $Run.exited=$true; $Run.exit_code=$Process.ExitCode
    }
    catch { $script:cleanupAmbiguous=$true; $Run.cleanup='AMBIGUOUS_STOP_FUTURE_CASES'; throw }
    if ($null -ne $persistenceError) { throw $persistenceError }
}
function Invoke-OwnedProcess([string]$Name, [string]$Exe, [string[]]$Arguments, [string]$WorkingDirectory, [int]$TimeoutMilliseconds=60000) {
    Require ($TimeoutMilliseconds -eq 60000 -or ($Name -ceq '10-guard-receiptInsidePayload' -and $TimeoutMilliseconds -eq 30000)) 'Only the receipt-destination guard may use the shorter 30000 ms bound.'
    Require (-not $script:cleanupAmbiguous) 'A previous ambiguous cleanup forbids every later case.'
    Assert-PositiveImmutable
    $argumentJson = ConvertTo-Json -InputObject @($Arguments) -Compress
    $run = [ordered]@{
        schema='WIN7POS_PACK_VALUE_TUPLE_PROCESS_V1'; case=$Name; executable=$Exe; executable_sha256=Hash-File $Exe
        argv=@($Arguments); argv_sha256=Hash-Bytes ([Text.Encoding]::UTF8.GetBytes($argumentJson)); working_directory=$WorkingDirectory
        timeout_ms=$TimeoutMilliseconds; cleanup_wait_ms=5000; started_utc=[DateTime]::UtcNow.ToString('o'); pid=$null
        process_start_ticks=$null; exited=$false; exit_code=$null; timed_out=$false; cleanup='NOT_NEEDED'; elapsed_ms=$null
        stdout_file="$Name.stdout.log"; stderr_file="$Name.stderr.log"; stdout_sha256=$null; stderr_sha256=$null
        cleanup_evidence_error=$null; error=$null
    }
    $process = [Diagnostics.Process]::new(); $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $start = [Diagnostics.ProcessStartInfo]::new(); $start.FileName=$Exe; $start.WorkingDirectory=$WorkingDirectory
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
        $process.StartInfo=$start
        Require ($process.Start()) 'The exact child process did not start.'
        $run.pid=$process.Id; $run.process_start_ticks=$process.StartTime.ToUniversalTime().Ticks
        $stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $run.timed_out=$true; $run.elapsed_ms=$watch.ElapsedMilliseconds
            # Durable timeout evidence precedes cleanup. Even an I/O exception must
            # enter the exact-owned cleanup path and fail the whole gate.
            try { Write-NewJson (Join-Path $script:evidence "$Name.timeout-before-cleanup.json") $run }
            finally { Stop-ExactOwnedProcess $process $run $Name }
        }
        $run.exited=$process.HasExited; if ($run.exited) { $run.exit_code=$process.ExitCode }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask,$stderrTask),5000)) {
            $script:cleanupAmbiguous=$true
            throw 'Output pipes did not close within 5000 ms; no subsequent case is allowed.'
        }
        $stdout=$stdoutTask.GetAwaiter().GetResult(); $stderr=$stderrTask.GetAwaiter().GetResult()
        Require ($stdout.Length -le 131072 -and $stderr.Length -le 131072) 'Child logs exceeded the small evidence bound.'
        [IO.File]::WriteAllText((Join-Path $script:evidence $run.stdout_file),$stdout,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $script:evidence $run.stderr_file),$stderr,[Text.UTF8Encoding]::new($false))
        $run.stdout_sha256=Hash-File (Join-Path $script:evidence $run.stdout_file)
        $run.stderr_sha256=Hash-File (Join-Path $script:evidence $run.stderr_file)
        Require (-not $run.timed_out) "Owned case timed out: $Name"
        return [pscustomobject]@{ process=$run; stdout=$stdout; stderr=$stderr }
    }
    catch { $run.error=$_.Exception.Message; throw }
    finally {
        $cleanupFailure=$null
        try { Stop-ExactOwnedProcess $process $run $Name }
        catch { $cleanupFailure=$_; $run.error=$_.Exception.Message }
        $run.elapsed_ms=$watch.ElapsedMilliseconds
        try { Write-NewJson (Join-Path $script:evidence "$Name-process.json") $run }
        finally { $process.Dispose() }
        if ($null -ne $cleanupFailure) { throw $cleanupFailure }
    }
}
function Invoke-ClosureCase([string]$Name, [string]$CasePayload, [string]$ExpectedStage, [bool]$ExpectedUnloaded, [bool]$Positive) {
    $receiptPath=Join-Path $script:evidence "$Name-receipt.json"
    $caseMapBefore=Map-Json (File-Map $CasePayload)
    $result=Invoke-OwnedProcess $Name $script:harness @('--valuetuple-release-closure-smoke','--payload-dir',$CasePayload,'--receipt',$receiptPath) $CasePayload
    Require (Test-Path -LiteralPath $receiptPath -PathType Leaf) "Child receipt missing: $Name"
    Require ((Get-Item -LiteralPath $receiptPath).Length -le 131072) 'The child receipt exceeded the small evidence bound.'
    $receipt=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    Require ($receipt.schema -ceq 'WIN7POS_PACK_VALUE_TUPLE_CLOSURE_V1') 'Unknown child receipt schema.'
    Require ((Same-Path $receipt.payload_directory $CasePayload) -and $receipt.isolated_domain_unloaded -eq $ExpectedUnloaded) 'Child payload/unload evidence does not match the exact case.'
    Require ($receipt.config_sha256 -ceq (Hash-File (Join-Path $CasePayload 'Win7POS.Wpf.exe.config'))) 'The child did not observe the exact case configuration.'
    Require ($receipt.controller_runtime.framework -ceq '.NETFramework' -and $receipt.controller_runtime.process_bits -eq 32 -and $receipt.controller_runtime.installed_release -ge 528040) 'The child was not an x86 .NET Framework 4.8+ process.'
    if ($Positive) {
        Require ($result.process.exit_code -eq 0 -and $receipt.status -ceq 'PASS' -and $receipt.stage -ceq 'complete' -and $null -eq $receipt.failStage -and $receipt.probe.status -ceq 'PASS') 'The positive packed closure must pass fully.'
        foreach ($dll in $script:trackedDlls) {
            $assembly=@($receipt.assemblies | Where-Object { $_.name -ceq [IO.Path]::GetFileNameWithoutExtension($dll) })
            Require ($assembly.Count -eq 1 -and $assembly[0].sha256 -ceq $script:positiveMap[$dll].sha256 -and (Same-Path $assembly[0].location (Join-Path $CasePayload $dll)) -and $assembly[0].gac -eq $false) "Positive assembly hash/location mismatch: $dll"
            if ($dll -cne 'System.ValueTuple.dll') {
                $expectedReference=if ($dll -ceq 'System.Text.Encoding.CodePages.dll') { '4.0.5.0' } else { '4.0.3.0' }
                $references=@($assembly[0].valuetuple_references)
                Require ($references.Count -eq 1 -and $references[0].version -ceq $expectedReference -and $references[0].public_key_token -ceq 'cc7b13ffcd2ddd51' -and $references[0].full_name -ceq "System.ValueTuple, Version=$expectedReference, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51") "Consumer must expose its actual reviewed ValueTuple AssemblyRef: $dll"
            }
        }
        Require (@($receipt.forwarded_types).Count -eq 11 -and @($receipt.requested_facades).Count -eq 2) 'The positive child did not prove all 11 forwarders and both requested facade identities.'
        foreach ($consumer in @('System.Text.Encoding.CodePages','ExcelDataReader','ExcelDataReader.DataSet')) {
            Require ($receipt.consumers[$consumer].status -ceq 'PASS') "The positive consumer was not exercised: $consumer"
        }
    }
    else {
        Require ($result.process.exit_code -ne 0 -and $receipt.status -ceq 'FAIL' -and $receipt.stage -ceq $ExpectedStage -and $receipt.failStage -ceq $ExpectedStage) "Negative must fail at exactly its declared stage: $Name"
        if (-not $ExpectedUnloaded) {
            Require ($ExpectedStage -ceq 'controller_config' -and $null -eq $receipt.probe -and @($receipt.assemblies).Count -eq 0 -and @($receipt.forwarded_types).Count -eq 0 -and @($receipt.requested_facades).Count -eq 0) 'Only missingredirect may stop before creating the isolated AppDomain.'
        }
    }
    Require ((Map-Json (File-Map $CasePayload)) -ceq $caseMapBefore) 'The child must leave its complete input payload copy unchanged.'
    Assert-PositiveImmutable
    $script:results.Add([ordered]@{case=$Name; result='PASS'; child_exit=$result.process.exit_code; child_status=$receipt.status; expected_fail_stage=$ExpectedStage; isolated_domain_unloaded=$receipt.isolated_domain_unloaded; receipt_sha256=Hash-File $receiptPath})
}

$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$results=[Collections.Generic.List[object]]::new(); $cleanupAmbiguous=$false; $sourceMap=[ordered]@{}
$summary=[ordered]@{schema='WIN7POS_PACK_VALUE_TUPLE_RUNNER_V1'; status='FAIL'; commit=$ExpectedCommitSha.ToLowerInvariant(); runtime_cases_expected=7; inventory_negatives_expected=2; destination_guards_expected=1; processes_expected=10; cases=$results; error=$null; fixtures_outside_evidence=$true; immutable_positive_verified=$false; cleanup_ambiguous=$false; fixtures_root=$null; immutable_error=$null}
$evidence=$null; $payload=$null; $fixtureRoot=$null; $evidenceValidated=$false; $positiveCaptured=$false
try {
    Require ($IsWindows -and $PSVersionTable.PSEdition -ceq 'Core' -and $PSVersionTable.PSVersion.Major -eq 7) 'Windows PowerShell 7 is required.'
    $payload=Local-Path $PayloadDirectory; $harness=Local-Path $HarnessExe; $evidence=Local-Path $EvidenceDirectory
    Require (Same-Path $payload (Join-Path $repoRoot 'dist/Win7POS')) 'The positive must be the actual dist/Win7POS payload.'
    Require (Same-Path $harness (Join-Path $repoRoot 'tests/Win7POS.Wpf.UiSmokeHarness/bin/x86/Release/net48/Win7POS.Wpf.UiSmokeHarness.exe')) 'The exact built net48/x86 harness is required; no fallback path is permitted.'
    Require (-not (Within $evidence $payload) -and -not (Within $payload $evidence) -and -not (Within $harness $payload)) 'Evidence, positive payload and harness must be separated.'
    No-Reparse $payload; No-Reparse $harness
    No-ReparseExistingAncestors $evidence
    if (Test-Path -LiteralPath $evidence) {
        Require (Test-Path -LiteralPath $evidence -PathType Container) 'EvidenceDirectory must be a directory.'
        Require (@(Get-ChildItem -LiteralPath $evidence -Force).Count -eq 0) 'EvidenceDirectory must be fresh or empty; previous evidence is preserved.'
    }
    else { [IO.Directory]::CreateDirectory($evidence) | Out-Null }
    No-Reparse $evidence; $evidenceValidated=$true
    $actualHead=(& git -C $repoRoot rev-parse HEAD).Trim(); Require ($LASTEXITCODE -eq 0 -and $actualHead -ceq $ExpectedCommitSha.ToLowerInvariant()) 'ExpectedCommitSha must be the exact checked-out head.'
    $tree=@(& git -C $repoRoot status --porcelain=v1); Require ($LASTEXITCODE -eq 0 -and $tree.Count -eq 0) 'The source tree must be clean.'
    foreach ($file in @('scripts/run-valuetuple-release-closure-smoke.ps1','global.json','.github/workflows/release-pack.yml','scripts/check-release-pack-completeness.ps1','scripts/check-win7-runtime-release-validation.ps1','scripts/win7-smoke/check-win7-prereqs.ps1','tests/Win7POS.Wpf.UiSmokeHarness/ValueTupleReleaseClosureSmoke.cs','tests/Win7POS.Wpf.UiSmokeHarness/Program.cs','src/Win7POS.Wpf/Win7POS.Wpf.csproj','src/Win7POS.Wpf/App.config','src/Win7POS.Data/packages.lock.json','src/Win7POS.Wpf/packages.lock.json','tests/Win7POS.Wpf.UiSmokeHarness/packages.lock.json')) {
        No-Reparse (Join-Path $repoRoot $file); $sourceMap[$file]=Hash-File (Join-Path $repoRoot $file)
    }
    $checker=Join-Path $repoRoot 'scripts/check-release-pack-completeness.ps1'
    $pwsh=(Get-Process -Id $PID).Path; No-Reparse $pwsh
    $positiveMap=File-Map $payload; $positiveMapJson=Map-Json $positiveMap; $positiveCaptured=$true
    $trackedDlls=@('System.ValueTuple.dll','System.Text.Encoding.CodePages.dll','ExcelDataReader.dll','ExcelDataReader.DataSet.dll')
    foreach ($file in @($trackedDlls)+@('Win7POS.Wpf.exe.config','APP-FILES.txt','SHA256SUMS.txt')) { Require ($positiveMap.Contains($file)) "Positive payload file missing: $file" }
    $version=[IO.File]::ReadAllLines((Join-Path $payload 'VERSION.txt'))
    Require (@($version | Where-Object { $_ -ceq "CommitSHA=$($ExpectedCommitSha.ToLowerInvariant())" }).Count -eq 1) 'Positive payload VERSION.txt must identify the exact expected commit.'

    # WPF is the payload consumer that resolves ValueTuple; Data's netstandard2.0 lock does not contain it.
    $lockPath=Join-Path $repoRoot 'src/Win7POS.Wpf/packages.lock.json'
    $lock=Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
    $locked=@($lock.dependencies.GetEnumerator() | ForEach-Object { if ($_.Value.Contains('System.ValueTuple')) { $_.Value['System.ValueTuple'] } })
    Require ($locked.Count -gt 0 -and @($locked | Where-Object { $_.resolved -cne '4.6.2' }).Count -eq 0) 'The WPF payload lock must resolve exactly System.ValueTuple 4.6.2.'
    $contentHashes=@($locked | ForEach-Object { $_.contentHash } | Sort-Object -Unique); Require ($contentHashes.Count -eq 1) 'ValueTuple lock contentHash must be unique.'
    $assetsPath=Join-Path $repoRoot 'src/Win7POS.Wpf/obj/project.assets.json'; No-Reparse $assetsPath
    $assets=Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    Require ($assets.libraries['System.ValueTuple/4.6.2'].sha512 -ceq $contentHashes[0]) 'Restored assets must join the locked package contentHash.'
    $archives=@($assets.packageFolders.Keys | ForEach-Object { Join-Path $_ 'system.valuetuple/4.6.2/system.valuetuple.4.6.2.nupkg' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    Require ($archives.Count -eq 1) 'Exactly one existing restored package archive is required; no restore, download or cache fallback is allowed.'
    $archive=Local-Path $archives[0]; No-Reparse $archive
    # Reviewed NuGet 4.6.2 pins: logical content and signed archive bytes are distinct contracts.
    $reviewedContentHash='yQgmjfFximrNm9LIV3mL6T5MzjeC+epeE5rl4hXxAlYmxby7RM1dPSkIKXk9HNkl6G54h2JHOmLD46+Pey+IRg=='
    $reviewedArchiveSha512='sMfnKIAepEOav44iCUQAECVXLA1/TVNC37g+mxo690/BFiHzbuHPFvdyJ6MdED4CL7iq6N7kllbMUVi5/nWJiA=='
    $reviewedFacadeSha256='7cd7ac314a72774b5e2255fd798f876926c63e19f9b8304cae25df012beabbcb'
    $packageDirectory=[IO.Path]::GetDirectoryName($archive)
    $metadataPath=Join-Path $packageDirectory '.nupkg.metadata'
    $cacheArchiveHashPath=$archive+'.sha512'
    $cacheFacadePath=Join-Path $packageDirectory 'lib/net47/System.ValueTuple.dll'
    foreach ($path in @($metadataPath,$cacheArchiveHashPath,$cacheFacadePath)) { No-Reparse $path }
    $packageMetadata=Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -AsHashtable
    $cacheArchiveHash=[IO.File]::ReadAllText($cacheArchiveHashPath).Trim()
    Require ($contentHashes[0] -ceq $reviewedContentHash -and $packageMetadata.contentHash -ceq $contentHashes[0]) 'The reviewed logical contentHash must match WPF lock, assets library.sha512 and cache metadata; it is not the signed archive byte hash.'
    $archiveStream=[IO.File]::OpenRead($archive); $sha512=[Security.Cryptography.SHA512]::Create()
    try { $archiveHash=[Convert]::ToBase64String($sha512.ComputeHash($archiveStream)) } finally { $archiveStream.Dispose(); $sha512.Dispose() }
    Require ($archiveHash -ceq $cacheArchiveHash -and $archiveHash -ceq $reviewedArchiveSha512) 'The signed package archive byte SHA512 must match both cache .nupkg.sha512 and the separately reviewed archive pin.'
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try { $facadeBytes=Zip-Member $zip 'lib/net47/System.ValueTuple.dll'; $implementationBytes=Zip-Member $zip 'lib/net462/System.ValueTuple.dll' } finally { $zip.Dispose() }
    $facadeHash=Hash-Bytes $facadeBytes; $implementationHash=Hash-Bytes $implementationBytes
    $cacheFacadeHash=Hash-File $cacheFacadePath
    Require ($facadeHash -ceq $reviewedFacadeSha256 -and $cacheFacadeHash -ceq $facadeHash -and $positiveMap['System.ValueTuple.dll'].sha256 -ceq $facadeHash -and $facadeHash -cne $implementationHash) 'The ZIP net47 member, restored cache asset and immutable payload facade must match the reviewed member SHA256, not net462 implementation.'
    $fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('Win7POS.ValueTupleClosure.'+[Guid]::NewGuid().ToString('N'))
    $fixtureRoot=Local-Path $fixtureRoot
    Require (-not (Within $fixtureRoot $payload) -and -not (Within $payload $fixtureRoot) -and -not (Within $fixtureRoot $evidence) -and -not (Within $evidence $fixtureRoot)) 'Owned negative fixtures must be outside both payload and evidence.'
    No-ReparseExistingAncestors $fixtureRoot
    Require (-not (Test-Path -LiteralPath $fixtureRoot)) 'The owned fixture root must be new.'
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null; No-Reparse $fixtureRoot
    Write-NewJson (Join-Path $evidence 'positive-source-map.json') ([ordered]@{schema='WIN7POS_PACK_VALUE_TUPLE_SOURCE_MAP_V1'; commit=$ExpectedCommitSha.ToLowerInvariant(); payload=$payload; harness=$harness; harness_sha256=Hash-File $harness; source_sha256=$sourceMap; files=$positiveMap; package_archive=$archive; package_archive_sha512_base64=$archiveHash; cache_archive_sha512_path=$cacheArchiveHashPath; cache_archive_sha512_file_sha256=Hash-File $cacheArchiveHashPath; cache_archive_sha512_base64=$cacheArchiveHash; reviewed_archive_sha512_base64=$reviewedArchiveSha512; lock_path=$lockPath; lock_sha256=Hash-File $lockPath; lock_content_hash=$contentHashes[0]; assets_library_sha512=$assets.libraries['System.ValueTuple/4.6.2'].sha512; cache_metadata_path=$metadataPath; cache_metadata_sha256=Hash-File $metadataPath; cache_metadata_content_hash=$packageMetadata.contentHash; reviewed_nuget_content_hash=$reviewedContentHash; locked_member='lib/net47/System.ValueTuple.dll'; locked_member_sha256=$facadeHash; cache_facade_path=$cacheFacadePath; cache_facade_sha256=$cacheFacadeHash; reviewed_facade_sha256=$reviewedFacadeSha256; positive_facade_sha256=$positiveMap['System.ValueTuple.dll'].sha256; implementation_member='lib/net462/System.ValueTuple.dll'; implementation_member_sha256=$implementationHash; assets_sha256=Hash-File $assetsPath; fixtures_root=$fixtureRoot})

    # Each case starts the exact harness executable directly, in a fresh process.
    Invoke-ClosureCase '01-positive' $payload '' $true $true
    foreach ($case in @(
        @{name='02-missingfacade'; file='System.ValueTuple.dll'; kind='remove'; stage='facade_binding'; unloaded=$true},
        @{name='03-implementationnet462'; file='System.ValueTuple.dll'; kind='implementation'; stage='forwarded_type_providers'; unloaded=$true},
        @{name='04-missingredirect'; file='Win7POS.Wpf.exe.config'; kind='redirect'; stage='controller_config'; unloaded=$false},
        @{name='05-missingCodePages'; file='System.Text.Encoding.CodePages.dll'; kind='remove'; stage='consumer_load_codepages'; unloaded=$true},
        @{name='06-missingExcel'; file='ExcelDataReader.dll'; kind='remove'; stage='consumer_load_excel'; unloaded=$true},
        @{name='07-missingDataSet'; file='ExcelDataReader.DataSet.dll'; kind='remove'; stage='consumer_load_dataset'; unloaded=$true}
    )) {
        $fixture=New-NegativeCopy $case.name $case.file $case.kind
        Invoke-ClosureCase $case.name $fixture $case.stage $case.unloaded $false
    }
    # These two independent inventory checks run only after the direct runtime cases.
    foreach ($case in @(
        @{name='08-inventory-missingDapper'; file='Dapper.dll'; kind='remove'; marker='FAIL: ReleasePack missing required file: Dapper.dll'},
        @{name='09-inventory-unexpectedRuntime'; file='UnexpectedRuntime.dll'; kind='unexpected'; marker='FAIL: ReleasePack inventory must exactly match the reviewed x86 runtime closure'}
    )) {
        $fixture=New-NegativeCopy $case.name $case.file $case.kind
        $checkerMapBefore=Map-Json (File-Map $fixture)
        $checked=Invoke-OwnedProcess $case.name $pwsh @('-NoProfile','-NonInteractive','-File',$checker,'-ReleasePackSource',$fixture,'-ExpectedCommitSha',$ExpectedCommitSha.ToLowerInvariant()) $repoRoot
        Require ($checked.process.exit_code -eq 1 -and $checked.stdout.Contains($case.marker,[StringComparison]::Ordinal)) "The unchanged inventory checker must reject the exact declared negative: $($case.name)"
        Require ((Map-Json (File-Map $fixture)) -ceq $checkerMapBefore) 'The unchanged checker must not mutate the negative copy or its baseline manifests.'
        Assert-PositiveImmutable
        $results.Add([ordered]@{case=$case.name;result='PASS';child_exit=$checked.process.exit_code;expected_marker=$case.marker;manifests_regenerated=$false})
    }
    # Regression for a rejected receipt destination: no helper receipt may exist.
    # This copy has NO intentional mutation and its manifests are never rewritten.
    Assert-PositiveImmutable
    $guardName='10-guard-receiptInsidePayload'
    $guardPayload=Join-Path $fixtureRoot $guardName
    Require (-not (Test-Path -LiteralPath $guardPayload)) 'The owned destination-guard fixture must be new.'
    [IO.Directory]::CreateDirectory($guardPayload) | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $payload -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $guardPayload -Recurse -ErrorAction Stop
    }
    $guardBefore=File-Map $guardPayload
    Require ((Map-Json $guardBefore) -ceq $positiveMapJson) 'The destination guard must start as an exact positive copy.'
    Write-NewJson (Join-Path $evidence "$guardName-source-map.json") ([ordered]@{
        schema='WIN7POS_PACK_VALUE_TUPLE_DESTINATION_GUARD_V1'; case=$guardName
        original_payload=$payload; fixture=$guardPayload; mutation='NONE'; manifests_regenerated=$false; files=$guardBefore
    })
    $deniedReceipt=Join-Path $guardPayload 'denied.json'
    Require (-not (Test-Path -LiteralPath $deniedReceipt)) 'The rejected receipt path must be new.'
    $guard=Invoke-OwnedProcess $guardName $harness @('--valuetuple-release-closure-smoke','--payload-dir',$guardPayload,'--receipt',$deniedReceipt) $guardPayload 30000
    $guardMarker='VALUETUPLE_RELEASE_CLOSURE FAIL: ValueTupleReceiptDestinationException'
    $guardMarkerLines=@($guard.stderr -split '\r?\n' | Where-Object { $_ -ceq $guardMarker })
    Require ($guard.process.exit_code -eq 1 -and $guardMarkerLines.Count -eq 1 -and -not (Test-Path -LiteralPath $deniedReceipt)) 'The receipt-destination guard must exit 1 with its dedicated stderr line and create no file.'
    Require ((Map-Json (File-Map $guardPayload)) -ceq (Map-Json $guardBefore)) 'Rejecting the receipt destination must leave the complete owned payload copy unchanged.'
    Assert-PositiveImmutable
    $results.Add([ordered]@{case=$guardName;result='PASS';child_exit=$guard.process.exit_code;expected_stderr_line=$guardMarker;timeout_ms=30000;rejected_path=$deniedReceipt;child_receipt_written=$false;helper_receipt_or_fail_stage_claimed=$false;owned_copy_unchanged=$true;manifests_regenerated=$false})
    Require ($results.Count -eq 10) 'Exactly seven runtime cases, two inventory negatives and one destination guard must complete.'
    Assert-PositiveImmutable; $summary.immutable_positive_verified=$true; $summary.status='PASS'
}
catch { $summary.error=[ordered]@{type=$_.Exception.GetType().FullName; message=$_.Exception.Message}; Write-Error -Message $_ -ErrorAction Continue }
finally {
    $summary.cleanup_ambiguous=$cleanupAmbiguous; $summary.fixtures_root=$fixtureRoot
    if ($positiveCaptured) {
        try { Assert-PositiveImmutable; $summary.immutable_positive_verified=$true } catch { $summary.status='FAIL'; $summary.immutable_positive_verified=$false; $summary.immutable_error=$_.Exception.Message }
    }
    if ($evidenceValidated) { Write-NewJson (Join-Path $evidence 'summary.json') $summary }
}
if ($summary.status -cne 'PASS') { exit 1 }
Write-Output 'PASS packed_valuetuple_release_closure=true runtime_cases=7 inventory_negatives=2 destination_guards=1 processes=10 immutable_positive=true'
exit 0
