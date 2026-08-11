$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$fail = $false

function Fail([string]$message) {
    Write-Host "FAIL: $message" -ForegroundColor Red
    $script:fail = $true
}

function Pass([string]$message) {
    Write-Host "PASS: $message" -ForegroundColor Green
}

function Read-Required([string]$relativePath) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "missing file: $relativePath"
        return ""
    }

    return [System.IO.File]::ReadAllText($path)
}

function Require-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Pass $label } else { Fail "$label missing" }
}

function Forbid-Match([string]$label, [string]$text, [string]$pattern) {
    if ($text -match $pattern) { Fail $label } else { Pass $label }
}

$writer = Read-Required "src/Win7POS.Core/Logging/BoundedAsyncLogWriter.cs"
$sanitizer = Read-Required "src/Win7POS.Core/Logging/LogSanitizer.cs"
$processHost = Read-Required "src/Win7POS.Core/Logging/ProcessFileLog.cs"
$facade = Read-Required "src/Win7POS.Wpf/Infrastructure/FileLogger.cs"
$sink = Read-Required "src/Win7POS.Core/Logging/RotatingFileLogSink.cs"
$dbInitializer = Read-Required "src/Win7POS.Data/DbInitializer.cs"
$app = Read-Required "src/Win7POS.Wpf/App.xaml.cs"
$tests = Read-Required "tests/Win7POS.Core.Tests/Logging/BoundedAsyncLogWriterTests.cs"
$smoke = Read-Required "tests/Win7POS.Wpf.UiSmokeHarness/Program.cs"
$runner = Read-Required "scripts/run-bounded-logging-smoke.ps1"
$ci = Read-Required ".github/workflows/ci.yml"

Require-Match "one bounded in-process queue" $writer 'Queue<ImmutableLogEntry>'
Require-Match "single background writer thread" $writer 'new Thread[\s\S]*IsBackground\s*=\s*true'
Require-Match "INFO admission reserve" $writer 'InfoAdmissionLimit'
Require-Match "WARN admission reserve" $writer 'WarningAdmissionLimit'
Require-Match "bounded queue capacity" $writer 'Capacity'
Require-Match "batch sink contract" $writer 'ILogBatchSink[\s\S]*WriteBatch'
Require-Match "bounded shutdown contract" $writer 'Shutdown\s*\(\s*TimeSpan'
Require-Match "drop and high-water metrics" $writer 'DroppedInfo[\s\S]*DroppedWarning[\s\S]*DroppedError[\s\S]*HighWater'
Require-Match "redaction before immutable entry admission" $writer 'LogSanitizer\.Sanitize[\s\S]*new ImmutableLogEntry[\s\S]*_queue\.Enqueue'
Require-Match "stored message length bound" $sanitizer 'MaxStoredChars'
Require-Match "private-key envelope pattern" $sanitizer 'PRIVATE KEY'
Require-Match "private-key redaction marker" $sanitizer 'PrivateKey\.Replace\(sanitized,\s*PrivateKeyRedactionMarker\)'
Require-Match "newline neutralization" $sanitizer 'Replace\("\\r"[\s\S]*Replace\("\\n"'
Forbid-Match "queued entries contain no Exception objects" $writer 'class ImmutableLogEntry[\s\S]{0,900}\bException\b'

Require-Match "process singleton writer" $processHost 'Lazy<BoundedAsyncLogWriter>'
Require-Match "process host owns rotating sink" $processHost 'new RotatingFileLogSink\(AppPaths\.LogPath\)'
Require-Match "process host catches producer failures" $processHost 'TryWrite[\s\S]*try[\s\S]*Writer\.Value\.TryWrite[\s\S]*catch'
Require-Match "process exit flush is bounded" $processHost 'TimeSpan\.FromMilliseconds[\s\S]*ProcessExit'
Require-Match "facade delegates nonblocking writes" $facade 'ProcessFileLog\.TryWrite\('
Require-Match "bounded exception expansion" $facade 'maxDepth[\s\S]*maxExceptions'
Require-Match "warning/error formatting is no-throw" $facade 'LogWarning[\s\S]*try[\s\S]*ComposeExceptionDetail[\s\S]*LogError[\s\S]*try[\s\S]*ComposeExceptionDetail'
Require-Match "exception getters use safe fallbacks" $facade 'SafeExceptionMessage[\s\S]*exception-message-unavailable[\s\S]*SafeExceptionStackTrace[\s\S]*exception-stack-unavailable'
Forbid-Match "facade performs no file I/O" $facade '\b(File|Directory|FileInfo|FileStream|StreamWriter)\.'
Forbid-Match "facade performs no rotation" $facade 'RotateIfNeeded'
Forbid-Match "facade performs no producer wait" $facade '\.(Wait|WaitOne|Join)\s*\('
Require-Match "DB migration logger uses shared process host" $dbInitializer 'ProcessFileLog\.TryWrite\('
Require-Match "DB migration logger delegates shared sanitizer" $dbInitializer 'LogSanitizer\.Sanitize\('
Forbid-Match "DB migration logger performs no direct app-log append" $dbInitializer 'File\.AppendAllText\(AppPaths\.LogPath'

Require-Match "rotation stays in writer sink" $sink 'RotateIfNeeded'
Require-Match "file append stays in writer sink" $sink 'new FileStream\('
Require-Match "sink batches appends" $sink 'WriteBatch'
Require-Match "application performs bounded shutdown" $app 'FileLogger\.Shutdown\s*\(\s*TimeSpan\.From'

foreach ($marker in @(
    'InfoSaturation_DropsInfoAndPreservesCriticalReserve',
    'AcceptedEntries_AreWrittenInSequenceOrder',
    'SlowSink_DoesNotBlockProducer',
    'SinkFailure_DoesNotCrashOrSpin',
    'Shutdown_WithBlockedSink_IsBounded',
    'ConcurrentProducers_PreserveUniqueSequence',
    'Flood_RemainsBounded'
)) {
    Require-Match "Core test marker: $marker" $tests ([regex]::Escape($marker))
}

Require-Match "x86 bounded logging smoke argument" $smoke 'bounded-logging-smoke'
Require-Match "x86 smoke records queue metrics" $smoke 'dropped[\s\S]*highWater'
Require-Match "x86 smoke exercises hostile exception getters" $smoke 'HostileLogException[\s\S]*exception-message-unavailable[\s\S]*exception-stack-unavailable'
Require-Match "x86 smoke tracks every-call producer max" $smoke 'producerMaxTicks[\s\S]*Math\.Max\(producerMaxTicks'
Require-Match "bounded logging smoke runner" $runner '--bounded-logging-smoke'
Require-Match "CI executes bounded logging smoke" $ci 'run-bounded-logging-smoke\.ps1'

if ($fail) {
    Write-Host "`nRESULT: FAIL" -ForegroundColor Red
    exit 1
}

Write-Host "`nRESULT: PASS" -ForegroundColor Green
exit 0
