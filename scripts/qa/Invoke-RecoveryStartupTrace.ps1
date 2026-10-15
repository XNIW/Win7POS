[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Qualify','Recovery')][string]$Mode,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This diagnostic collector requires Windows.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Evidence directory must be new.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$raw = Join-Path $env:RUNNER_TEMP ('W7TraceRaw.' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $raw) { throw 'Trace directory collision.' }
$null = New-Item -ItemType Directory -Path $raw
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$logman = Join-Path $env:WINDIR 'System32/logman.exe'
$tracerpt = Join-Path $env:WINDIR 'System32/tracerpt.exe'
$tools = @($logman,$tracerpt | ForEach-Object {
    $file = Get-Item -LiteralPath $_
    $signature = Get-AuthenticodeSignature -LiteralPath $_
    if ($signature.Status -ne 'Valid') { throw "Unverified built-in collector: $($file.Name)" }
    [ordered]@{path=$file.FullName;version=$file.VersionInfo.FileVersion;sha256=(Get-FileHash -LiteralPath $_).Hash;signature=[string]$signature.Status}
})
$tools | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'collector-provenance.json')
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
[ordered]@{image=$env:ImageOS;imageVersion=$env:ImageVersion;os=(Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture);cpu=(Get-CimInstance Win32_ComputerSystem | Select-Object NumberOfLogicalProcessors,TotalPhysicalMemory);frameworkX86=@('clr.dll','mscordacwks.dll','sos.dll' | ForEach-Object { $path=Join-Path $framework $_; [ordered]@{name=$_;version=(Get-Item -LiteralPath $path).VersionInfo.FileVersion;sha256=(Get-FileHash -LiteralPath $path).Hash} })} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'host.json')
# Only query/stop the random session successfully created by this invocation.
# ControlTrace STOP returns the final loss counters after buffers are flushed.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RecoveryEtwSession {
  [StructLayout(LayoutKind.Sequential)] public struct Wnode {
    public uint BufferSize, ProviderId; public ulong HistoricalContext; public long Timestamp;
    public Guid Guid; public uint ClientContext, Flags;
  }
  [StructLayout(LayoutKind.Sequential)] public struct Properties {
    public Wnode Wnode; public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize,
      LogFileMode, FlushTimer, EnableFlags, AgeLimit, NumberOfBuffers, FreeBuffers, EventsLost,
      BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
    public IntPtr LoggerThreadId; public uint LogFileNameOffset, LoggerNameOffset;
  }
  [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
  static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);
  public static Properties Stop(string name) {
    int size = Marshal.SizeOf(typeof(Properties));
    IntPtr buffer = Marshal.AllocHGlobal(size + 4096);
    try {
      var bytes = new byte[size + 4096]; Marshal.Copy(bytes, 0, buffer, bytes.Length);
      var p = new Properties(); p.Wnode.BufferSize = (uint)bytes.Length;
      p.LoggerNameOffset = (uint)size; p.LogFileNameOffset = (uint)(size + 2048);
      Marshal.StructureToPtr(p, buffer, false);
      uint error = ControlTrace(0, name, buffer, 1);
      if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
      return (Properties)Marshal.PtrToStructure(buffer, typeof(Properties));
    } finally { Marshal.FreeHGlobal(buffer); }
  }
}
'@
function Invoke-BoundedNative([string]$Executable, [string[]]$Arguments, [string]$Label, [int]$Seconds = 45) {
    $start = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $raw "$Label.stdout.txt") -RedirectStandardError (Join-Path $raw "$Label.stderr.txt")
    $failure = $null
    $record = [ordered]@{identity=$null;elapsedMs=0;exitCode=$null;cleanupConfirmed=$false;cleanupError=$null;firstError=$null}
    try {
        $null = $p.Handle
        $record.identity = [ordered]@{pid=$p.Id;creationUtc=$p.StartTime.ToUniversalTime().ToString('O');executable=$Executable;sha256=(Get-FileHash -LiteralPath $Executable).Hash}
        if (-not $p.WaitForExit($Seconds * 1000)) { throw "$Label exceeded its finite diagnostic budget." }
        $p.WaitForExit()
        $record.exitCode = $p.ExitCode
        if ($p.ExitCode -ne 0) { throw "$Label exit $($p.ExitCode): $([IO.File]::ReadAllText((Join-Path $raw "$Label.stderr.txt")))" }
    } catch { $failure = $_; $record.firstError = $_.Exception.Message }
    finally {
        try {
            if (-not $p.HasExited) { $p.Kill(); if (-not $p.WaitForExit(5000)) { throw 'Owned diagnostic child did not stop within cleanup budget.' } }
            $record.cleanupConfirmed = $p.HasExited
            if (-not $record.cleanupConfirmed) { throw 'Owned diagnostic child cleanup is unconfirmed.' }
        } catch { $record.cleanupError=$_.Exception.Message }
        finally { $p.Dispose() }
        $record.elapsedMs = $start.Elapsed.TotalMilliseconds
        try { $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Label-process.json") }
        catch { if ($null -eq $failure) { $failure=$_ } }
    }
    if ($null -ne $failure) { throw $failure }
    if (-not $record.cleanupConfirmed) { throw $record.cleanupError }
    return $record
}
function Start-OwnedTrace([string]$Label) {
    $session = 'W7Recovery.' + [Guid]::NewGuid().ToString('N')
    $etl = Join-Path $raw "$Label.etl"
    & $logman create trace $session -o $etl -p '{E13C0D23-CCBC-4E12-931B-D9CC2EEE27E4}' 0x18 5 -nb 16 64 -bs 64 -f bin -max 64 -ets *> (Join-Path $raw "$Label.start.txt")
    if ($LASTEXITCODE -ne 0) { throw "ETW start failed; no elevation or alternate collector: $LASTEXITCODE" }
    [ordered]@{name=$session;etl=$etl;label=$Label;startedUtc=[DateTime]::UtcNow.ToString('O');stopped=$false}
}
function Stop-OwnedTrace($Trace) {
    if ($Trace.stopped) { return }
    $stats = [RecoveryEtwSession]::Stop($Trace.name)
    $Trace.stopped = $true
    [ordered]@{session=$Trace.name;eventsLost=$stats.EventsLost;logBuffersLost=$stats.LogBuffersLost;realTimeBuffersLost=$stats.RealTimeBuffersLost;buffersWritten=$stats.BuffersWritten;stopUtc=[DateTime]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory ($Trace.label + '-loss.json'))
    if ($stats.EventsLost -ne 0 -or $stats.LogBuffersLost -ne 0 -or $stats.RealTimeBuffersLost -ne 0) { throw 'ETW events were lost; trace is not qualified.' }
}
function Export-TargetEvents($Trace, [int[]]$TargetProcessIds) {
    $xmlPath = Join-Path $raw ($Trace.label + '.xml')
    $summaryPath = Join-Path $OutputDirectory ($Trace.label + '-decoder-summary.txt')
    $null = Invoke-BoundedNative $tracerpt @(('"'+$Trace.etl+'"'),'-o',('"'+$xmlPath+'"'),'-of','XML','-summary',('"'+$summaryPath+'"'),'-y') ($Trace.label + '-decode') 60
    if ((Get-Item -LiteralPath $xmlPath).Length -gt 256MB) { throw 'Decoded trace exceeds bounded parse size.' }
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($xmlPath,$settings)
    $out = [IO.StreamWriter]::new((Join-Path $OutputDirectory ($Trace.label + '-target-events.jsonl')), $false, [Text.UTF8Encoding]::new($false))
    $total = 0; $starts = 0; $loads = 0; $fixtureMethods = 0
    $schemaSamples = [Collections.Generic.List[string]]::new()
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -ne [Xml.XmlNodeType]::Element -or $reader.LocalName -ne 'Event') { continue }
            $sub = $reader.ReadSubtree()
            try { $doc = [Xml.XmlDocument]::new(); $doc.XmlResolver=$null; $doc.Load($sub) } finally { $sub.Dispose() }
            $system = $doc.SelectSingleNode('/*[local-name()="Event"]/*[local-name()="System"]')
            if ($null -eq $system) { continue }
            $execution = $system.SelectSingleNode('*[local-name()="Execution"]')
            if ($null -eq $execution -or [int]$execution.GetAttribute('ProcessID') -notin $TargetProcessIds) { continue }
            $provider = $system.SelectSingleNode('*[local-name()="Provider"]')
            if ($null -eq $provider -or $provider.GetAttribute('Guid').Trim('{}') -ine 'E13C0D23-CCBC-4E12-931B-D9CC2EEE27E4') { continue }
            $eventId = [int]$system.SelectSingleNode('*[local-name()="EventID"]').InnerText
            $payload = [ordered]@{}
            foreach ($data in $doc.SelectNodes('//*[local-name()="EventData"]/*[local-name()="Data"]')) { $payload[$data.GetAttribute('Name')] = $data.InnerText }
            # tracerpt's manifested CLR provider emits named UserData fields on
            # the hosted image; also retain the standard EventData representation.
            foreach ($data in $doc.SelectNodes('//*[local-name()="UserData"]/*/*')) { $payload[$data.LocalName] = $data.InnerText }
            if ($schemaSamples.Count -lt 3 -and $eventId -in 145,143,154) { $schemaSamples.Add($doc.OuterXml) }
            $total++; if ($eventId -eq 145) { $starts++ }; if ($eventId -in 136,143) { $loads++ }
            if ($payload.Contains('MethodName') -and [string]$payload.MethodName -like 'QA_RecoveryTrace_*') { $fixtureMethods++ }
            if ($total -gt 150000) { throw 'Target CLR event bound exceeded.' }
            $event = [ordered]@{id=$eventId;version=$system.SelectSingleNode('*[local-name()="Version"]').InnerText;utc=$system.SelectSingleNode('*[local-name()="TimeCreated"]').GetAttribute('SystemTime');pid=[int]$execution.GetAttribute('ProcessID');tid=[int]$execution.GetAttribute('ThreadID');data=$payload}
            $out.WriteLine(($event | ConvertTo-Json -Compress -Depth 5))
        }
    } finally {
        $reader.Dispose(); $out.Dispose()
        ('<TargetSchemaSamples>' + ($schemaSamples -join '') + '</TargetSchemaSamples>') | Set-Content -LiteralPath (Join-Path $OutputDirectory ($Trace.label + '-schema-samples.xml'))
    }
    [ordered]@{events=$total;jitStarts=$starts;methodLoads=$loads;fixtureMethodEvents=$fixtureMethods;targetPids=$TargetProcessIds;rawPublished=$false}
}
$trace = $null
$result = [ordered]@{mode=$Mode;status='FAIL';sourceSha=(& git -C $repo rev-parse HEAD);scope='independent hosted synthetic QA';provider='CLR Runtime Loader|JIT 0x18 level5';rawTraceDirectory=$raw;rawPublished=$false;error=$null}
try {
    if ($Mode -eq 'Qualify') {
        $fixtureSource = Join-Path $raw 'RecoveryTraceFixture.cs'
        @'
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection.Emit;
public static class RecoveryTraceFixture {
  public static int Main(string[] args) {
    if (IntPtr.Size != 4 || args.Length != 1) return 2;
    var watch = Stopwatch.StartNew(); long sum = 0;
    for (int i = 0; i < 4000; ++i) {
      var m = new DynamicMethod("QA_RecoveryTrace_" + i, typeof(int), Type.EmptyTypes);
      var il = m.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, i); il.Emit(OpCodes.Ret);
      sum += ((Func<int>)m.CreateDelegate(typeof(Func<int>)))();
    }
    watch.Stop();
    File.WriteAllText(args[0], "{\"bits\":32,\"clr\":\"" + Environment.Version + "\",\"methods\":4000,\"sum\":" + sum + ",\"workMs\":" + watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + "}");
    return sum == 7998000 ? 0 : 3;
  }
}
'@ | Set-Content -LiteralPath $fixtureSource
        $fixtureExe = Join-Path $raw 'RecoveryTraceFixture.exe'
        $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        & $compiler /nologo /optimize+ /platform:x86 /target:exe "/out:$fixtureExe" $fixtureSource *> (Join-Path $raw 'compile.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic net48-compatible x86 fixture compilation failed.' }
        $arms = @(); $decodeResults = @()
        # Three fixed alternating pairs; qualification runs on another VM from Recovery.
        foreach ($index in 1..6) {
            $traced = $index % 2 -eq 0
            $label = 'fixture-' + $index
            $receipt = Join-Path $OutputDirectory "$label.json"
            if ($traced) { $trace = Start-OwnedTrace $label }
            $process = Invoke-BoundedNative $fixtureExe @(('"'+$receipt+'"')) $label 30
            if ($traced) {
                Stop-OwnedTrace $trace
                $decoded = Export-TargetEvents $trace @($process.identity.pid)
                $decodeResults += $decoded
                $result.decoding = $decodeResults
                if ($decoded.jitStarts -lt 4000 -or $decoded.fixtureMethodEvents -lt 4000) { throw 'Target fixture JIT events did not round-trip through the decoder.' }
                $trace = $null
            }
            $data = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
            $arms += [ordered]@{index=$index;traced=$traced;workMs=$data.workMs;process=$process}
        }
        $baseline = @($arms | Where-Object { -not $_.traced } | ForEach-Object workMs | Sort-Object)[1]
        $instrumented = @($arms | Where-Object traced | ForEach-Object workMs | Sort-Object)[1]
        $overhead = $instrumented - $baseline
        $result.arms = $arms; $result.decoding = $decodeResults
        $result.overhead = [ordered]@{baselineMedianMs=$baseline;tracedMedianMs=$instrumented;differenceMs=$overhead;maxAllowedAbsoluteMs=1500;interpretation='Observed synthetic JIT-work difference, not a universal causal runtime overhead estimate.'}
        if ($overhead -gt 1500) { throw 'Collector synthetic overhead exceeds the preregistered 1500ms diagnostic bound.' }
        $result.status = 'PASS'
    } else {
        $result.hypothesis = 'Locate the cold fixture-to-ContentRendered gap in own-process new Window/Owner.Show/SyncCenterDialog/Center.Show or diagnostic sink. CLR Loader/JIT events discriminate runtime compilation/loading from the measured boundaries; event gaps alone do not establish a native cause. One full cold process, 60000ms, no warmup, unchanged groups/rows/asserts.'
        $env:WIN7POS_QA_TEMP_BASE = $env:RUNNER_TEMP
        $env:WIN7POS_QA_RECOVERY_OPERATIONS = '1'
        $trace = Start-OwnedTrace 'recovery'
        & pwsh -NoProfile -File (Join-Path $repo 'scripts/run-functional-completion-smoke.ps1') -Scenario IMPORT_RECOVERY_completion -OutputDirectory (Join-Path $OutputDirectory 'functional')
        $functionalExit = $LASTEXITCODE
        Stop-OwnedTrace $trace
        $functional = Get-Content -LiteralPath (Join-Path $OutputDirectory 'functional/functional-results.json') -Raw | ConvertFrom-Json
        $result.functional = $functional.results
        $result.decoding = Export-TargetEvents $trace @($functional.results | ForEach-Object pid)
        $trace = $null
        if ($result.decoding.jitStarts -eq 0 -or $result.decoding.methodLoads -eq 0) { throw 'Recovery CLR trace has no decoded JIT starts/method loads.' }
        if ($functionalExit -ne 0) { throw "Recovery functional runner exit $functionalExit; diagnostic evidence retained." }
        $result.status = 'PASS'
    }
} catch { $result.error = $_.Exception.Message }
finally {
    if ($null -ne $trace -and -not $trace.stopped) {
        try { Stop-OwnedTrace $trace } catch { if ($null -eq $result.error) { $result.error=$_.Exception.Message }; $result.cleanupError=$_.Exception.Message }
    }
    $result | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'trace-result.json')
}
if ($result.status -ne 'PASS') { Write-Error $result.error; exit 1 }
Write-Output "RECOVERY_STARTUP_TRACE=$($result.status) mode=$Mode"
