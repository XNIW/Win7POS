[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OwnershipPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$PhaseFile,
    [Parameter(Mandatory = $true)][string]$StopFile,
    [string[]]$AdditionalPhaseFiles = @(),
    [ValidateRange(100, 60000)][int]$IntervalMs = 500,
    [switch]$Diagnostic,
    [ValidateRange(0, 100)][int]$DiagnosticMaxSamples = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$writer = $null
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$script:tracked = @{}
$script:unregisteredDepartedCandidates = @{}
$script:previousCpu = @{}
$script:events = New-Object System.Collections.Generic.List[object]
$script:phase = $null
$script:phaseLineCount = 0
$script:additionalPhaseStates = @()
$script:sampleCount = 0
$script:metricErrorCount = 0
$script:identityErrorCount = 0
$script:ownedRoot = $null
$script:nonce = $null
$script:ownershipHash = $null
$script:lastMissingImageCandidate = $null
$script:ownerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Path-Absolute([string]$Path) {
    if (-not [System.IO.Path]::IsPathRooted($Path)) { throw "Absolute path required: $Path" }
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}
function Same-Path([string]$Left, [string]$Right) {
    return [string]::Equals((Path-Absolute $Left), (Path-Absolute $Right), [System.StringComparison]::OrdinalIgnoreCase)
}
function Assert-OwnedPath([string]$Path) {
    $full = Path-Absolute $Path
    if (-not $full.StartsWith($script:ownedRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be strictly inside owned runRoot: $full"
    }
    $cursor = $full
    while ($cursor -and $cursor.StartsWith($script:ownedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path refused: $cursor" }
        }
        if (Same-Path $cursor $script:ownedRoot) { break }
        $cursor = [System.IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
function Write-Record($Record) {
    $writer.WriteLine(($Record | ConvertTo-Json -Depth 24 -Compress))
}
function Parse-Time($Value) {
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    return [DateTimeOffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}
function Cim-Time($Value) { return ([datetime]$Value).ToUniversalTime() }
function Process-Key($Cim) { return "$($Cim.ProcessId):$((Cim-Time $Cim.CreationDate).Ticks)" }
function Assert-RootIdentity($Cim, $Expected, [string]$Role) {
    if ($null -eq $Cim) { throw "$Role root process is absent" }
    $expectedId = if ($Role -eq 'postgres') { [int]$Expected.rootPid } else { [int]$Expected.pid }
    if ([int]$Cim.ProcessId -ne $expectedId) { throw "$Role PID mismatch" }
    if (-not (Same-Path ([string]$Cim.ExecutablePath) ([string]$Expected.executablePath))) { throw "$Role executable mismatch" }
    $difference = [Math]::Abs(((Cim-Time $Cim.CreationDate) - (Parse-Time $Expected.startedAtUtc)).TotalMilliseconds)
    if ($difference -gt 1) { throw "$Role process creation time mismatch (PID reuse guard)" }
    $sid = (Invoke-CimMethod -InputObject $Cim -MethodName GetOwnerSid).Sid
    if ($sid -ne $script:ownerSid) { throw "$Role process belongs to another Windows SID" }
    return $sid
}
function Read-Phases($State) {
    if (-not (Test-Path -LiteralPath $State.file)) { return }
    $phaseStream = [System.IO.File]::Open($State.file, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
    $phaseReader = New-Object System.IO.StreamReader($phaseStream, $utf8)
    try { $phaseText = $phaseReader.ReadToEnd() } finally { $phaseReader.Dispose() }
    # A concurrent append may be visible only as a partial final record. Rescan
    # from the beginning next time and advance lineCount only for complete lines.
    $lastNewline = $phaseText.LastIndexOf([char]10)
    $lines = @()
    if ($lastNewline -ge 0) { $lines = @($phaseText.Substring(0, $lastNewline).Split([char]10)) }
    if ($lines.Length -lt $State.lineCount) { throw 'Phase file was truncated' }
    for ($index = $State.lineCount; $index -lt $lines.Length; $index++) {
        if ([string]::IsNullOrWhiteSpace($lines[$index])) { throw 'Empty phase record refused' }
        $entry = $lines[$index] | ConvertFrom-Json
        if ($entry.nonce -ne $script:nonce) { throw 'Phase nonce mismatch' }
        if ($entry.phase -notmatch '^[A-Za-z0-9_.-]{1,80}$') { throw 'Phase name must be 1..80 plain identifier characters' }
        if ([long]$entry.sequence -ne ($State.lineCount + 1)) { throw 'Phase sequence must begin at 1 and increase exactly once' }
        $started = Parse-Time $entry.startedAtUtc
        if ($started -gt [DateTime]::UtcNow.AddSeconds(2)) { throw 'Phase timestamp is in the future' }
        if ($null -ne $State.phase -and $started -lt (Parse-Time $State.phase.startedAtUtc)) { throw 'Phase timestamps must be monotone' }
        $State.phase = $entry
        $State.lineCount++
        Write-Record ([ordered]@{ type = 'phase'; sourceFile = $State.file; canonical = $State.canonical; observedAtUtc = [DateTime]::UtcNow.ToString('o'); elapsedMs = $watch.Elapsed.TotalMilliseconds; event = $entry })
    }
}
function Record-UnregisteredDeparture($Cim, [string]$Role, [string]$Basis, $Failure, [string]$Operation, $VerifiedSid) {
    # Tolerance is available only to an admitted descendant. A failed query
    # is never absence; a live or reused PID remains fatal.
    $key = Process-Key $Cim
    $fresh = @(Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$Cim.ProcessId)" -ErrorAction Stop)
    if ($fresh.Count -ne 0) {
        if ($fresh.Count -ne 1) { throw 'Departure recheck returned duplicate PID identities' }
        if ((Process-Key $fresh[0]) -ne $key) { throw 'Discovered descendant PID was reused during registration' }
        if (-not (Same-Path ([string]$fresh[0].ExecutablePath) ([string]$Cim.ExecutablePath)) -or [int]$fresh[0].ParentProcessId -ne [int]$Cim.ParentProcessId) {
            throw 'Discovered live descendant identity changed during registration'
        }
        throw $Failure
    }
    if (-not $script:unregisteredDepartedCandidates.ContainsKey($key)) {
        $script:unregisteredDepartedCandidates[$key] = $true
        Write-Record ([ordered]@{
            type = 'process_exit_observed'; status = 'unregistered_departed_candidate'
            key = $key; pid = [int]$Cim.ProcessId; parentPid = [int]$Cim.ParentProcessId; role = $Role
            startedAtUtc = (Cim-Time $Cim.CreationDate).ToString('o'); executablePath = [string]$Cim.ExecutablePath
            observedAtUtc = [DateTime]::UtcNow.ToString('o'); elapsedMs = $watch.Elapsed.TotalMilliseconds
            ownerSid = $VerifiedSid; ownerSidObserved = ($null -ne $VerifiedSid)
            identityFullyVerified = $false; registrationCompleted = $false; acceptedForDescendantLineage = $false
            initialSnapshotBasis = $Basis; absentFromFreshCimPidQuery = $true
            departureOperation = $Operation; originalErrorType = $Failure.Exception.GetType().FullName
            originalErrorMessage = $Failure.Exception.Message; originalErrorStack = $Failure.ScriptStackTrace
            metricStatus = 'UNKNOWN_NOT_OBSERVED'; privateBytes = $null; workingSetBytes = $null
            privateWorkingSetBytes = $null; handleCount = $null; cpuTotalMs = $null
            osLifetimePeakWorkingSetBytes = $null; osLifetimePeakPrivateCommitBytes = $null
            allLifetimeMetricsOrPidsProvenCaptured = $false
            limit = 'CIM candidate departed before registration completed; no SID or metrics inferred, no zero substitute or all-lifetime child coverage claim.'
        })
    }
}
function Record-UnregisteredMissingImageDeparture($Cim, [datetime]$AcceptedParentStartedAtUtc) {
    # This candidate has parent PID/creation lineage only. Its image and SID
    # were never verified; fresh absence cannot promote it to owned lineage.
    if ($null -eq $Cim -or [int]$Cim.ProcessId -le 0 -or [int]$Cim.ParentProcessId -le 0 -or
        [string]$Cim.Name -ine 'postgres.exe' -or $null -eq $Cim.CreationDate -or
        -not [string]::IsNullOrWhiteSpace([string]$Cim.ExecutablePath)) {
        throw 'Missing-image departure requires a real incomplete postgres candidate'
    }
    $created = Cim-Time $Cim.CreationDate
    if ($created -lt $AcceptedParentStartedAtUtc) { throw 'Missing-image candidate predates admitted parent' }
    $key = Process-Key $Cim
    $script:lastMissingImageCandidate = [ordered]@{
        key = $key; pid = [int]$Cim.ProcessId; parentPid = [int]$Cim.ParentProcessId
        name = [string]$Cim.Name; startedAtUtc = $created.ToString('o'); executablePath = [string]$Cim.ExecutablePath
        acceptedParentStartedAtUtc = $AcceptedParentStartedAtUtc.ToString('o')
        observedAtUtc = [DateTime]::UtcNow.ToString('o'); imageObserved = $false
        freshQuery = "ProcessId = $([int]$Cim.ProcessId)"; freshQuerySucceeded = $false; freshCount = $null; freshSnapshot = $null
    }
    $fresh = @(Get-CimInstance Win32_Process -Filter $script:lastMissingImageCandidate.freshQuery -ErrorAction Stop)
    $script:lastMissingImageCandidate.freshQuerySucceeded = $true
    $script:lastMissingImageCandidate.freshCount = $fresh.Count
    $script:lastMissingImageCandidate.freshObservedAtUtc = [DateTime]::UtcNow.ToString('o')
    if ($fresh.Count -ne 0) {
        if ($fresh.Count -ne 1) { throw 'Missing-image departure recheck returned duplicate PID identities' }
        $script:lastMissingImageCandidate.freshSnapshot = [ordered]@{
            pid = [int]$fresh[0].ProcessId; parentPid = [int]$fresh[0].ParentProcessId
            startedAtUtc = (Cim-Time $fresh[0].CreationDate).ToString('o'); executablePath = [string]$fresh[0].ExecutablePath
        }
        if ((Process-Key $fresh[0]) -ne $key) { throw 'Missing-image descendant PID was reused during discovery' }
        if ([int]$fresh[0].ParentProcessId -ne [int]$Cim.ParentProcessId) { throw 'Missing-image live descendant parent identity changed' }
        throw 'Live descendant image was not verified at discovery; owner identity remains unverified'
    }
    if (-not $script:unregisteredDepartedCandidates.ContainsKey($key)) {
        $script:unregisteredDepartedCandidates[$key] = $true
        Write-Record ([ordered]@{
            type = 'process_exit_observed'; status = 'unregistered_departed_candidate'
            key = $key; pid = [int]$Cim.ProcessId; parentPid = [int]$Cim.ParentProcessId; role = 'postgres'
            startedAtUtc = $created.ToString('o'); executablePath = [string]$Cim.ExecutablePath
            observedAtUtc = [DateTime]::UtcNow.ToString('o'); elapsedMs = $watch.Elapsed.TotalMilliseconds
            ownerSid = $null; ownerSidObserved = $false; initialImageObserved = $false
            identityFullyVerified = $false; registrationCompleted = $false; acceptedForDescendantLineage = $false
            initialSnapshotBasis = 'Admitted parent PID/creation lineage only; image and owner SID not observed'
            absentFromFreshCimPidQuery = $true; departureOperation = 'ExecutablePathMissing.FreshPidAbsent'
            missingImageSnapshot = $script:lastMissingImageCandidate
            originalErrorType = $null; originalErrorMessage = $null; originalErrorStack = $null
            metricStatus = 'UNKNOWN_NOT_OBSERVED'; privateBytes = $null; workingSetBytes = $null
            privateWorkingSetBytes = $null; handleCount = $null; cpuTotalMs = $null
            osLifetimePeakWorkingSetBytes = $null; osLifetimePeakPrivateCommitBytes = $null
            allLifetimeMetricsOrPidsProvenCaptured = $false
            limit = 'Image and SID never verified. Successful exact-PID absence only; no registration, metrics, owned lineage or full child coverage inferred.'
        })
    }
}
function Register-Process($Cim, [string]$Role, [string]$Basis, [switch]$AllowDepartedDescendant) {
    if ($AllowDepartedDescendant -and ($Role -cne 'postgres' -or $Basis -cne 'Live descendant PID/creation lineage of owned postmaster; exact executable+owner SID')) {
        throw 'Departure tolerance is restricted to admitted PostgreSQL descendant snapshots'
    }
    $key = Process-Key $Cim
    if ($script:tracked.ContainsKey($key)) {
        if ($AllowDepartedDescendant) { return $true }
        return
    }
    try {
        $sid = (Invoke-CimMethod -InputObject $Cim -MethodName GetOwnerSid -ErrorAction Stop).Sid
    } catch [Microsoft.Management.Infrastructure.CimException] {
        $nativeException = $_.Exception
        while ($null -ne $nativeException -and $nativeException -isnot [Microsoft.Management.Infrastructure.CimException]) {
            $nativeException = $nativeException.InnerException
        }
        if (-not $AllowDepartedDescendant -or $null -eq $nativeException -or $nativeException.NativeErrorCode -ne [Microsoft.Management.Infrastructure.NativeErrorCode]::NotFound) { throw }
        Record-UnregisteredDeparture $Cim $Role $Basis $_ 'GetOwnerSid.NativeErrorCode.NotFound' $null
        return $false
    }
    if ($sid -ne $script:ownerSid) { throw "Discovered $Role process belongs to another SID" }
    try {
        $process = [System.Diagnostics.Process]::GetProcessById([int]$Cim.ProcessId)
    } catch [System.ArgumentException] {
        if (-not $AllowDepartedDescendant) { throw }
        Record-UnregisteredDeparture $Cim $Role $Basis $_ 'GetProcessById.ArgumentException' $sid
        return $false
    }
    $entry = [ordered]@{
        key = $key; pid = [int]$Cim.ProcessId; parentPid = [int]$Cim.ParentProcessId; role = $Role
        startedAtUtc = (Cim-Time $Cim.CreationDate).ToString('o'); executablePath = [string]$Cim.ExecutablePath
        ownerSid = $sid; ownershipBasis = $Basis; firstObservedAtUtc = [DateTime]::UtcNow.ToString('o')
        process = $process
        seenExit = $false
    }
    $script:tracked[$key] = $entry
    Write-Record ([ordered]@{ type = 'process_discovered'; elapsedMs = $watch.Elapsed.TotalMilliseconds; process = [ordered]@{
        key = $key; pid = $entry.pid; parentPid = $entry.parentPid; role = $Role; startedAtUtc = $entry.startedAtUtc
        executablePath = $entry.executablePath; ownerSid = $sid; ownershipBasis = $Basis; firstObservedAtUtc = $entry.firstObservedAtUtc
    }})
    if ($AllowDepartedDescendant) { return $true }
}
function Read-Metrics($Entry, [double]$ElapsedMs) {
    $errors = New-Object System.Collections.Generic.List[string]
    $process = $Entry.process
    $result = [ordered]@{
        key = $Entry.key; pid = $Entry.pid; parentPid = $Entry.parentPid; role = $Entry.role; status = 'live'
        privateBytes = $null; privateWorkingSetBytes = $null; privateWorkingSetUnavailableReason = $null
        workingSetBytes = $null; handleCount = $null; threadCount = $null
        cpuTotalMs = $null; cpuDeltaMs = $null; cpuPercentOneCore = $null; cpuPercentHost = $null
        osLifetimePeakWorkingSetBytes = $null; osLifetimePeakPrivateCommitBytes = $null
        nativePageFaultCount = $null; measuredAtUtc = [DateTime]::UtcNow.ToString('o'); errors = @()
    }
    try {
        $process.Refresh()
        if ($process.HasExited) { $result.status = 'exited' }
        if (-not $process.HasExited) {
            $result.privateBytes = [long]$process.PrivateMemorySize64
            $result.workingSetBytes = [long]$process.WorkingSet64
            $result.handleCount = [int]$process.HandleCount
            $result.threadCount = [int]$process.Threads.Count
            $result.osLifetimePeakWorkingSetBytes = [long]$process.PeakWorkingSet64
            $memory = [MaximumQualification.Native]::ReadMemory($process.Handle)
            $result.osLifetimePeakPrivateCommitBytes = [long]$memory.PeakPagefileUsage.ToUInt64()
            $result.nativePageFaultCount = [long]$memory.PageFaultCount
            $privateWorkingSet = [MaximumQualification.Native]::ReadPrivateWorkingSet($process.Handle)
            if ($privateWorkingSet.Available) { $result.privateWorkingSetBytes = [long]$privateWorkingSet.Bytes } else {
                $result.privateWorkingSetUnavailableReason = "GetProcessMemoryInfo(EX2) unavailable, Win32=$($privateWorkingSet.Win32Error); API requires Windows 10/11 22H2 September 2023 update or later"
            }
        }
        $result.cpuTotalMs = $process.TotalProcessorTime.TotalMilliseconds
        if ($script:previousCpu.ContainsKey($Entry.key)) {
            $previous = $script:previousCpu[$Entry.key]
            $duration = $ElapsedMs - $previous.elapsedMs
            $delta = $result.cpuTotalMs - $previous.cpuTotalMs
            if ($duration -gt 0 -and $delta -ge 0) {
                $result.cpuDeltaMs = $delta
                $result.cpuPercentOneCore = 100.0 * $delta / $duration
                $result.cpuPercentHost = $result.cpuPercentOneCore / [Environment]::ProcessorCount
            }
        }
        $script:previousCpu[$Entry.key] = @{ elapsedMs = $ElapsedMs; cpuTotalMs = $result.cpuTotalMs }
    } catch {
        $errors.Add($_.Exception.Message)
        $script:metricErrorCount++
        try { if ($process.HasExited) { $result.status = 'exited' } } catch { $result.status = 'unavailable' }
    }
    $result.errors = $errors.ToArray()
    if ($result.status -eq 'exited' -and -not $Entry.seenExit) {
        $Entry.seenExit = $true
        Write-Record ([ordered]@{ type = 'process_exit_observed'; key = $Entry.key; pid = $Entry.pid; role = $Entry.role; observedAtUtc = [DateTime]::UtcNow.ToString('o'); elapsedMs = $ElapsedMs })
    }
    return $result
}
function Sum-Nullable($Rows, [string]$Property) {
    if ($Rows.Count -eq 0) { return $null }
    $sum = 0.0
    foreach ($row in $Rows) { if ($null -eq $row[$Property]) { return $null }; $sum += $row[$Property] }
    return $sum
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'This collector is Windows-only' }
    if ($DiagnosticMaxSamples -gt 0 -and -not $Diagnostic) { throw 'DiagnosticMaxSamples requires -Diagnostic' }
    $ownership = Get-Content -LiteralPath $OwnershipPath -Raw | ConvertFrom-Json
    if ($ownership.schemaVersion -ne 'maximum-uniform-owned-processes-v1') { throw 'Unsupported ownership manifest schema' }
    $script:nonce = [string]$ownership.nonce
    if ($script:nonce -notmatch '^[A-Za-z0-9_-]{16,128}$') { throw 'Ownership nonce must be 16..128 plain characters' }
    $script:ownedRoot = Path-Absolute $ownership.runRoot
    if (-not (Test-Path -LiteralPath $script:ownedRoot -PathType Container)) { throw 'Owned runRoot is absent' }
    $OwnershipPath = Assert-OwnedPath $OwnershipPath
    $OutputPath = Assert-OwnedPath $OutputPath
    $PhaseFile = Assert-OwnedPath $PhaseFile
    $StopFile = Assert-OwnedPath $StopFile
    $readyFile = Assert-OwnedPath ($OutputPath + '.ready.json')
    $canonicalPhaseState = @{ file = $PhaseFile; lineCount = 0; phase = $null; canonical = $true }
    foreach ($additionalPhaseFile in $AdditionalPhaseFiles) {
        $additionalFullPath = Assert-OwnedPath $additionalPhaseFile
        if (Same-Path $additionalFullPath $PhaseFile) { throw 'Additional phase file duplicates canonical PhaseFile' }
        $script:additionalPhaseStates += @{ file = $additionalFullPath; lineCount = 0; phase = $null; canonical = $false }
    }
    foreach ($path in @($OutputPath, $readyFile, $StopFile)) { if (Test-Path -LiteralPath $path) { throw "Refusing pre-existing output/control file: $path" } }
    if (-not (Test-Path -LiteralPath ([System.IO.Path]::GetDirectoryName($OutputPath)))) { throw 'Output parent directory is absent' }
    $script:ownershipHash = (Get-FileHash -LiteralPath $OwnershipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $workerScript = Assert-OwnedPath ([string]$ownership.worker.scriptPath)
    if (-not (Test-Path -LiteralPath $workerScript -PathType Leaf)) { throw 'Worker script is absent' }
    if (-not $Diagnostic -and [System.IO.Path]::GetFileName($ownership.worker.executablePath) -ine 'node.exe') { throw 'Qualification worker must be a Node process' }
    $sourceBinding = $null
    if ($null -ne $ownership.source) {
        $sourceRoot = Path-Absolute $ownership.source.root
        $sourceSql = Path-Absolute $ownership.source.sqlPath
        if (-not $sourceSql.StartsWith($sourceRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) { throw 'SQL source path must be inside source root' }
        $sourceHash = (Get-FileHash -LiteralPath $sourceSql -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sourceHash -ne $ownership.source.sqlSha256) { throw 'Source SQL byte SHA256 mismatch' }
        $sourceCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $ownership.source.commit) { throw 'Source commit mismatch' }
        $sourceTree = (& git -C $sourceRoot rev-parse 'HEAD^{tree}').Trim()
        if ($LASTEXITCODE -ne 0 -or $sourceTree -ne $ownership.source.tree) { throw 'Source tree mismatch' }
        $sourceBinding = [ordered]@{ root = $sourceRoot; commit = $sourceCommit; tree = $sourceTree; sqlPath = $sourceSql; sqlSha256 = $sourceHash }
    } elseif (-not $Diagnostic) { throw 'Qualification requires source commit/tree/SQL binding' }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace MaximumQualification {
  [StructLayout(LayoutKind.Sequential)]
  public struct MemoryCounters {
    public uint cb, PageFaultCount;
    public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
    public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct MemoryCountersEx2 {
    public uint cb, PageFaultCount;
    public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
    public UIntPtr QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    public UIntPtr PrivateWorkingSetSize;
    public ulong SharedCommitUsage;
  }
  public class PrivateWorkingSetReading {
    public bool Available;
    public ulong Bytes;
    public int Win32Error;
  }
  public static class Native {
    [DllImport("psapi.dll", SetLastError=true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters memory, uint size);
    [DllImport("psapi.dll", EntryPoint="GetProcessMemoryInfo", SetLastError=true)]
    private static extern bool GetProcessMemoryInfoEx2(IntPtr process, ref MemoryCountersEx2 memory, uint size);
    [DllImport("shell32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    public static MemoryCounters ReadMemory(IntPtr process) {
      MemoryCounters value = new MemoryCounters();
      uint size = (uint)Marshal.SizeOf(typeof(MemoryCounters));
      value.cb = size;
      if (!GetProcessMemoryInfo(process, ref value, size)) throw new Win32Exception(Marshal.GetLastWin32Error());
      return value;
    }
    public static PrivateWorkingSetReading ReadPrivateWorkingSet(IntPtr process) {
      MemoryCountersEx2 memory = new MemoryCountersEx2();
      uint size = (uint)Marshal.SizeOf(typeof(MemoryCountersEx2));
      memory.cb = size;
      bool available = GetProcessMemoryInfoEx2(process, ref memory, size);
      return new PrivateWorkingSetReading { Available=available, Bytes=available ? memory.PrivateWorkingSetSize.ToUInt64() : 0, Win32Error=available ? 0 : Marshal.GetLastWin32Error() };
    }
    public static string[] Arguments(string commandLine) {
      int count; IntPtr pointer = CommandLineToArgvW(commandLine, out count);
      if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
      try {
        var values = new string[count];
        for (int index=0; index<count; index++) values[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index*IntPtr.Size));
        return values;
      } finally { LocalFree(pointer); }
    }
  }
}
'@

    $workerCim = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$ownership.worker.pid)"
    $null = Assert-RootIdentity $workerCim $ownership.worker 'worker'
    $workerArguments = [MaximumQualification.Native]::Arguments([string]$workerCim.CommandLine)
    $workerScriptFound = $false
    foreach ($argument in $workerArguments) { if ([System.IO.Path]::IsPathRooted($argument) -and (Same-Path $argument $workerScript)) { $workerScriptFound = $true } }
    if (-not $workerScriptFound -or -not ($workerArguments -contains "--qualification-nonce=$script:nonce")) { throw 'Worker command must bind exact script path and --qualification-nonce=<nonce>' }
    $clients = @()
    $clientCims = @{}
    if ($ownership.PSObject.Properties.Name -contains 'clients') {
        $clients = @($ownership.clients)
        foreach ($client in $clients) {
            if ([int]$client.pid -eq [int]$ownership.worker.pid -or $clientCims.ContainsKey([int]$client.pid) -or ($null -ne $ownership.postgres -and [int]$client.pid -eq [int]$ownership.postgres.rootPid)) { throw 'Client PID duplicates a tracked root/client' }
            if ($client.role -notmatch '^[a-z][a-z0-9-]{1,40}$' -or $client.role -in @('worker','postgres','observer')) { throw 'Invalid client role' }
            $entryPath = Assert-OwnedPath ([string]$client.entryPath)
            if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) { throw 'Client entryPath is absent' }
            $clientCim = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$client.pid)"
            $null = Assert-RootIdentity $clientCim $client $client.role
            $clientArguments = [MaximumQualification.Native]::Arguments([string]$clientCim.CommandLine)
            $entryFound = $false
            foreach ($argument in $clientArguments) { if ([System.IO.Path]::IsPathRooted($argument) -and (Same-Path $argument $entryPath)) { $entryFound = $true } }
            if (-not $entryFound -or -not ($clientArguments -contains "--qualification-nonce=$script:nonce")) { throw 'Client command must bind exact entryPath and --qualification-nonce=<nonce>' }
            $clientCims[[int]$client.pid] = $clientCim
        }
    }
    if ($null -eq $ownership.postgres -and -not $Diagnostic) { throw 'Qualification requires owned PostgreSQL' }
    $pgRootKey = $null
    if ($null -ne $ownership.postgres) {
        $pgData = Assert-OwnedPath ([string]$ownership.postgres.dataDirectory)
        $pgExecutable = Assert-OwnedPath ([string]$ownership.postgres.executablePath)
        if ([System.IO.Path]::GetFileName($pgExecutable) -ine 'postgres.exe') { throw 'Expected postgres.exe' }
        $marker = Assert-OwnedPath (Join-Path $pgData 'ownership-nonce.txt')
        if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne $script:nonce) { throw 'PostgreSQL data-dir ownership nonce mismatch' }
        $postmaster = [System.IO.File]::ReadAllLines((Join-Path $pgData 'postmaster.pid'))
        if ($postmaster.Length -lt 4 -or [int]$postmaster[0] -ne [int]$ownership.postgres.rootPid -or -not (Same-Path $postmaster[1] $pgData) -or [int]$postmaster[3] -ne [int]$ownership.postgres.port) { throw 'Postmaster PID/data-dir/port identity mismatch' }
        if ($ownership.postgres.database -notmatch '^max_uniform_[a-f0-9]+$') { throw 'Owned qualification database must use max_uniform_<hex> identity' }
        $pgRootCim = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$ownership.postgres.rootPid)"
        $null = Assert-RootIdentity $pgRootCim $ownership.postgres 'postgres'
        $pgArgs = [MaximumQualification.Native]::Arguments([string]$pgRootCim.CommandLine)
        $dataArgFound = $false
        for ($argIndex = 0; $argIndex -lt ($pgArgs.Length - 1); $argIndex++) {
            if ($pgArgs[$argIndex] -eq '-D' -and (Same-Path $pgArgs[$argIndex + 1] $pgData)) { $dataArgFound = $true }
        }
        if (-not $dataArgFound) { throw 'Postgres root command must bind -D <owned data directory>' }
        $pgRootKey = Process-Key $pgRootCim
    }
    $stream = New-Object System.IO.FileStream($OutputPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
    $writer = New-Object System.IO.StreamWriter($stream, $utf8)
    $writer.AutoFlush = $true
    $startUtc = [DateTime]::UtcNow
    Write-Record ([ordered]@{
        type = 'header'; schemaVersion = 'maximum-uniform-resources-v1'; nonce = $script:nonce
        startedAtUtc = $startUtc.ToString('o'); collectorPid = $PID; diagnostic = [bool]$Diagnostic
        intervalMs = $IntervalMs; logicalCpuCount = [Environment]::ProcessorCount; windowsVersion = [Environment]::OSVersion.VersionString
        ownershipPath = $OwnershipPath; ownershipSha256 = $script:ownershipHash; source = $sourceBinding
        phaseFiles = @($PhaseFile) + @($script:additionalPhaseStates | ForEach-Object { $_.file })
        methods = [ordered]@{
            identity = 'Win32_Process PID+CreationDate+ExecutablePath+owner SID; root command argv, immutable ownership SHA and data-dir nonce/postmaster.pid; postgres descendants share executable+owner SID'
            sampledMemory = 'PrivateMemorySize64 is private committed bytes, not resident RAM; WorkingSet64 is physical resident shared+private bytes; PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize is physical private resident bytes where supported; no GC or process modification'
            osLifetimePeakWorkingSet = 'System.Diagnostics.Process.PeakWorkingSet64; includes shared pages; since process start'
            osLifetimePeakPrivateCommit = 'GetProcessMemoryInfo PROCESS_MEMORY_COUNTERS_EX.PeakPagefileUsage; private committed bytes peak since process start'
            cpu = 'TotalProcessorTime; deltas divided by monotone elapsed time; one-core percentage may exceed 100; host percentage divides logical CPU count'
            postgresAggregate = 'Sum of process samples taken sequentially during each sampling interval; working-set sums double-count shared pages'
        }
        limits = @(
            'No exact simultaneous aggregate OS-lifetime memory peak exists here; aggregate peak is null; individual lifetime peaks are not summed as an aggregate peak.'
            'Polling can miss short-lived PostgreSQL children between discovery snapshots; no all-lifetime PID completeness claim.'
            'A candidate absent from a successful fresh CIM PID query before registration completed remains an explicit unregistered departed record with unknown metrics; never promoted to owned lineage.'
            'Final OS peaks are the greatest OS counter values observed while each process was readable; later exit-time changes can be missed.'
            'First CPU sample includes startup CPU; subsequent observed CPU deltas omit intervals before first observation of new children.'
            'Worker process is a local Node harness running actual handlers, not deployed Cloudflare isolate memory or CPU.'
            'Collector sampling and WMI have overhead; observer metrics are recorded separately; no fixed resource budget is applied.'
            'Private resident RAM requires GetProcessMemoryInfo(EX2), supported on Windows 10/11 22H2 September 2023 update or later; unsupported/error values remain null with Win32 reason.'
        )
    })
    Register-Process $workerCim 'worker' 'PID+creation+executable+owner+script argv+nonce argv'
    if ($null -ne $ownership.postgres) { Register-Process $pgRootCim 'postgres' 'postmaster.pid+data directory nonce+-D argv+PID+creation+executable+owner' }
    foreach ($client in $clients) { Register-Process $clientCims[[int]$client.pid] $client.role 'PID+creation+executable+owner+entry argv+nonce argv' }
    $self = [System.Diagnostics.Process]::GetCurrentProcess()
    $observer = @{ key = "observer:$PID"; pid = $PID; parentPid = $null; role = 'observer'; process = $self; seenExit = $false }
    $lastElapsed = $null
    $stopReason = $null
    while ($true) {
        if ((Get-FileHash -LiteralPath $OwnershipPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $script:ownershipHash) { throw 'Ownership manifest changed during collection' }
        Read-Phases $canonicalPhaseState
        foreach ($additionalState in $script:additionalPhaseStates) { Read-Phases $additionalState }
        $script:phase = $canonicalPhaseState.phase
        $stopping = Test-Path -LiteralPath $StopFile
        if ($stopping) {
            $stop = Get-Content -LiteralPath $StopFile -Raw | ConvertFrom-Json
            if ($stop.nonce -ne $script:nonce) { throw 'Stop-file nonce mismatch' }
            $stopReason = [string]$stop.reason
        }
        $samplingStarted = [DateTime]::UtcNow
        $elapsed = $watch.Elapsed.TotalMilliseconds
        $filter = "ProcessId = $([int]$ownership.worker.pid)"
        if ($null -ne $ownership.postgres) { $filter += " OR Name = 'postgres.exe'" }
        foreach ($client in $clients) { $filter += " OR ProcessId = $([int]$client.pid)" }
        $snapshot = @(Get-CimInstance Win32_Process -Filter $filter)
        $workerCurrent = $snapshot | Where-Object { [int]$_.ProcessId -eq [int]$ownership.worker.pid } | Select-Object -First 1
        $null = Assert-RootIdentity $workerCurrent $ownership.worker 'worker'
        foreach ($client in $clients) {
            $currentClient = $snapshot | Where-Object { [int]$_.ProcessId -eq [int]$client.pid } | Select-Object -First 1
            if ($null -ne $currentClient) { $null = Assert-RootIdentity $currentClient $client $client.role }
        }
        if ($null -ne $ownership.postgres) {
            $rootCurrent = $snapshot | Where-Object { [int]$_.ProcessId -eq [int]$ownership.postgres.rootPid } | Select-Object -First 1
            $null = Assert-RootIdentity $rootCurrent $ownership.postgres 'postgres'
            if ((Process-Key $rootCurrent) -ne $pgRootKey) { throw 'Postgres root identity changed' }
            $acceptedIds = @{}
            foreach ($trackedEntry in $script:tracked.Values) {
                if ($trackedEntry.role -ne 'postgres') { continue }
                $live = $snapshot | Where-Object { [int]$_.ProcessId -eq $trackedEntry.pid -and (Process-Key $_) -eq $trackedEntry.key } | Select-Object -First 1
                if ($null -ne $live) { $acceptedIds[$trackedEntry.pid] = Cim-Time $live.CreationDate }
            }
            $progress = $true
            while ($progress) {
                $progress = $false
                foreach ($candidate in $snapshot) {
                    $candidateId = [int]$candidate.ProcessId
                    $parentId = [int]$candidate.ParentProcessId
                    if ($acceptedIds.ContainsKey($candidateId) -or -not $acceptedIds.ContainsKey($parentId)) { continue }
                    if ([string]::IsNullOrWhiteSpace([string]$candidate.ExecutablePath)) {
                        if ($null -eq $candidate.CreationDate) { throw 'Missing-image candidate lacks creation time' }
                        if ((Cim-Time $candidate.CreationDate) -lt $acceptedIds[$parentId]) { continue }
                        Record-UnregisteredMissingImageDeparture $candidate $acceptedIds[$parentId]
                        continue
                    }
                    if (-not (Same-Path ([string]$candidate.ExecutablePath) $pgExecutable)) { continue }
                    if ((Cim-Time $candidate.CreationDate) -lt $acceptedIds[$parentId]) { continue }
                    $registered = Register-Process $candidate 'postgres' 'Live descendant PID/creation lineage of owned postmaster; exact executable+owner SID' -AllowDepartedDescendant
                    if ($registered) {
                        $acceptedIds[$candidateId] = Cim-Time $candidate.CreationDate
                        $progress = $true
                    }
                }
            }
        }
        $rows = @($script:tracked.Values | Sort-Object role,pid | ForEach-Object { Read-Metrics $_ $elapsed })
        $observerMetrics = Read-Metrics $observer $elapsed
        $postgresRows = @($rows | Where-Object { $_.role -eq 'postgres' -and $_.status -eq 'live' })
        $phaseName = if ($null -eq $script:phase) { 'collector-baseline' } else { [string]$script:phase.phase }
        $phaseStarted = if ($null -eq $script:phase) { $startUtc } else { Parse-Time $script:phase.startedAtUtc }
        Write-Record ([ordered]@{
            type = 'sample'; sequence = $script:sampleCount + 1; elapsedMs = $elapsed
            samplingStartedAtUtc = $samplingStarted.ToString('o'); samplingCompletedAtUtc = [DateTime]::UtcNow.ToString('o')
            sampleDurationMs = $watch.Elapsed.TotalMilliseconds - $elapsed
            gapSincePreviousSampleMs = $(if ($null -eq $lastElapsed) { $null } else { $elapsed - $lastElapsed })
            phase = $phaseName; phaseSequence = $(if ($null -eq $script:phase) { 0 } else { [long]$script:phase.sequence })
            phaseStartedAtUtc = $phaseStarted.ToString('o'); phaseElapsedMs = ($samplingStarted - $phaseStarted).TotalMilliseconds
            processes = $rows; observer = $observerMetrics
            postgres = [ordered]@{
                liveObservedProcessCount = $postgresRows.Count; privateBytesSampledSum = Sum-Nullable $postgresRows 'privateBytes'
                privateWorkingSetBytesSampledSum = Sum-Nullable $postgresRows 'privateWorkingSetBytes'
                workingSetBytesSampledSum = Sum-Nullable $postgresRows 'workingSetBytes'; handleCountSampledSum = Sum-Nullable $postgresRows 'handleCount'
                cpuDeltaMsObservedSum = Sum-Nullable $postgresRows 'cpuDeltaMs'
                cpuPercentHostSampledSum = Sum-Nullable $postgresRows 'cpuPercentHost'
                osLifetimeSimultaneousAggregatePeakWorkingSetBytes = $null
                osLifetimeSimultaneousAggregatePeakPrivateCommitBytes = $null
            }
        })
        $script:sampleCount++
        $lastElapsed = $elapsed
        if ($script:sampleCount -eq 1) {
            [System.IO.File]::WriteAllText($readyFile, ([ordered]@{ nonce = $script:nonce; collectorPid = $PID; firstSampleAtUtc = $samplingStarted.ToString('o'); outputPath = $OutputPath; ownershipSha256 = $script:ownershipHash } | ConvertTo-Json -Compress), $utf8)
        }
        if ($stopping) { break }
        if ($DiagnosticMaxSamples -gt 0 -and $script:sampleCount -ge $DiagnosticMaxSamples) { $stopReason = 'diagnostic_max_samples'; break }
        $remaining = $IntervalMs - ($watch.Elapsed.TotalMilliseconds - $elapsed)
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]$remaining) }
    }
    Write-Record ([ordered]@{ type = 'footer'; stoppedAtUtc = [DateTime]::UtcNow.ToString('o'); durationMs = $watch.Elapsed.TotalMilliseconds; sampleCount = $script:sampleCount; stopReason = $stopReason; collectorStatus = 'COMPLETED_OBSERVATION'; unregisteredDepartedCandidateCount = $script:unregisteredDepartedCandidates.Count; metricErrorCount = $script:metricErrorCount; identityErrorCount = $script:identityErrorCount; assertsAnyResourceBudget = $false })
} catch {
    $script:identityErrorCount++
    $failure = [ordered]@{ type = 'failure'; failedAtUtc = [DateTime]::UtcNow.ToString('o'); nonce = $script:nonce; elapsedMs = $watch.Elapsed.TotalMilliseconds; sampleCount = $script:sampleCount; message = $_.Exception.Message; stack = $_.ScriptStackTrace; collectorStatus = 'FAILED'; lastMissingImageCandidate = $script:lastMissingImageCandidate; assertsAnyResourceBudget = $false }
    if ($null -ne $writer) { Write-Record $failure }
    $failureRoot = if ($script:ownedRoot -and (Test-Path -LiteralPath $script:ownedRoot)) { $script:ownedRoot } else { [System.IO.Path]::GetDirectoryName((Path-Absolute $OwnershipPath)) }
    $failurePath = Join-Path $failureRoot ('collector-first-failure-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
    [System.IO.File]::WriteAllText($failurePath, ($failure | ConvertTo-Json -Depth 8), $utf8)
    throw
} finally {
    if ($null -ne $writer) { $writer.Dispose() }
    foreach ($entry in $script:tracked.Values) { $entry.process.Dispose() }
}
