using System.Collections.Concurrent;
using System.Diagnostics;
using Win7POS.Core.Logging;

namespace Win7POS.Core.Tests.Logging;

[TestClass]
public sealed class BoundedAsyncLogWriterTests
{
    [TestMethod]
    public void InfoSaturation_DropsInfoAndPreservesCriticalReserve()
    {
        var sink = new BlockingSink();
        using var writer = CreateWriter(sink, capacity: 8, infoLimit: 4, warningLimit: 6, batchSize: 1);

        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Test", "info-0"));
        Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(2)), "Writer did not reach the blocking sink.");
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Test", "info-1"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Test", "info-2"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Test", "info-3"));
        Assert.IsFalse(writer.TryWrite(LogLevel.Info, "Test", "info-dropped"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Warning, "Test", "warn-0"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Warning, "Test", "warn-1"));
        Assert.IsFalse(writer.TryWrite(LogLevel.Warning, "Test", "warn-dropped"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Error, "Test", "error-0"));
        Assert.IsTrue(writer.TryWrite(LogLevel.Error, "Test", "error-1"));
        Assert.IsFalse(writer.TryWrite(LogLevel.Error, "Test", "error-dropped"));

        var saturated = writer.GetMetrics();
        Assert.AreEqual(8, saturated.CurrentPending);
        Assert.AreEqual(8, saturated.HighWaterMark);
        Assert.AreEqual(1, saturated.DroppedInfo);
        Assert.AreEqual(1, saturated.DroppedWarning);
        Assert.AreEqual(1, saturated.DroppedError);

        Assert.IsFalse(writer.Shutdown(TimeSpan.FromMilliseconds(30)));
        sink.Release.Set();
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(8, writer.GetMetrics().WrittenTotal);
    }

    [TestMethod]
    public void AcceptedEntries_AreWrittenInSequenceOrder()
    {
        var sink = new RecordingSink();
        var clock = new FixedClock(new DateTime(2026, 7, 22, 9, 8, 7, 654));
        using var writer = new BoundedAsyncLogWriter(
            sink,
            capacity: 32,
            infoAdmissionLimit: 24,
            warningAdmissionLimit: 28,
            batchSize: 3,
            clock: clock);

        for (var i = 0; i < 10; i++)
        {
            Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Catalog", "item-" + i));
        }

        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
        var lines = sink.Lines.ToArray();
        Assert.AreEqual(10, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.AreEqual(
                "2026-07-22 09:08:07.654 [INFO][Catalog] item-" + i + Environment.NewLine,
                lines[i]);
        }
    }

    [TestMethod]
    public void Producer_RedactsBoundsAndNormalizesBeforeEnqueue()
    {
        var sink = new RecordingSink();
        using var writer = new BoundedAsyncLogWriter(
            sink,
            capacity: 8,
            infoAdmissionLimit: 6,
            warningAdmissionLimit: 7,
            batchSize: 4,
            maxMessageLength: 512,
            maxSourceLength: 16,
            clock: new FixedClock(new DateTime(2026, 7, 22, 1, 2, 3, 4)));

        var secret = "password=hunter2; Authorization: Bearer abc.def.ghi C:\\Users\\alice\\db.txt\r\nforged " + // gitleaks:allow -- synthetic redaction-test values
            new string('x', 200);
        Assert.IsTrue(writer.TryWrite(LogLevel.Error, "Bad][Source\r\n", secret));
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));

        var line = sink.Lines.Single();
        StringAssert.StartsWith(line, "2026-07-22 01:02:03.004 [ERROR][Bad__Source  ] ");
        StringAssert.Contains(line, "password=[redacted]");
        StringAssert.Contains(line, "Bearer [redacted]");
        StringAssert.Contains(line, "[path]");
        Assert.IsFalse(line.Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(line.Contains("alice", StringComparison.Ordinal));
        var content = line.TrimEnd('\r', '\n');
        Assert.IsFalse(content.Contains("\r", StringComparison.Ordinal));
        Assert.IsFalse(content.Contains("\n", StringComparison.Ordinal));
        Assert.IsTrue(MessagePart(line).Length <= 512);
    }

    [TestMethod]
    public void SlowSink_DoesNotBlockProducer()
    {
        var sink = new BlockingSink();
        using var writer = CreateWriter(sink, capacity: 256, infoLimit: 192, warningLimit: 224, batchSize: 1);
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Latency", "first"));
        Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = Stopwatch.StartNew();
        var perCallTicks = new long[2000];
        for (var i = 0; i < 2000; i++)
        {
            var started = Stopwatch.GetTimestamp();
            writer.TryWrite(LogLevel.Info, "Latency", "message-" + i);
            perCallTicks[i] = Stopwatch.GetTimestamp() - started;
        }

        stopwatch.Stop();
        Array.Sort(perCallTicks);
        var p95Milliseconds = perCallTicks[(int)(perCallTicks.Length * 0.95)] * 1000d / Stopwatch.Frequency;
        var maxMilliseconds = perCallTicks[^1] * 1000d / Stopwatch.Frequency;
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Producer elapsed: " + stopwatch.Elapsed);
        Assert.IsTrue(p95Milliseconds < 5d, "Producer p95 milliseconds: " + p95Milliseconds);
        Assert.IsTrue(maxMilliseconds < 250d, "Producer max milliseconds: " + maxMilliseconds);
        Assert.IsTrue(writer.GetMetrics().DroppedInfo > 0);
        sink.Release.Set();
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void SinkFailure_DoesNotCrashOrSpin()
    {
        var sink = new FailingSink();
        using var writer = CreateWriter(sink, capacity: 8, infoLimit: 4, warningLimit: 6, batchSize: 1);

        Assert.IsTrue(writer.TryWrite(LogLevel.Error, "Failure", "critical"));
        Assert.IsTrue(SpinWait.SpinUntil(() => writer.GetMetrics().WriterFailures > 0, TimeSpan.FromSeconds(2)));
        var producerStopwatch = Stopwatch.StartNew();
        writer.TryWrite(LogLevel.Error, "Failure", "still-nonblocking");
        producerStopwatch.Stop();
        Assert.IsTrue(
            producerStopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            "Fault-racing producer elapsed: " + producerStopwatch.Elapsed);
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));

        var metrics = writer.GetMetrics();
        Assert.IsTrue(metrics.WriterFailures > 0);
        Assert.IsTrue(metrics.WriterFailures <= 3, "Failure retry policy was not bounded.");
        Assert.IsTrue(metrics.IsFaulted);
        Assert.IsFalse(metrics.IsWriterAlive);
        Assert.AreEqual(0, metrics.WrittenTotal);
        Assert.IsTrue(metrics.AcceptedError == 1 || metrics.AcceptedError == 2);
        Assert.AreEqual(2, metrics.DroppedError);
        Assert.AreEqual(0, metrics.CurrentPending);
    }

    [TestMethod]
    public void Shutdown_WithBlockedSink_IsBounded()
    {
        var sink = new BlockingSink();
        using var writer = CreateWriter(sink, capacity: 8, infoLimit: 4, warningLimit: 6, batchSize: 1);
        Assert.IsTrue(writer.TryWrite(LogLevel.Error, "Shutdown", "critical"));
        Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = Stopwatch.StartNew();
        Assert.IsFalse(writer.Shutdown(TimeSpan.FromMilliseconds(40)));
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "Shutdown elapsed: " + stopwatch.Elapsed);

        sink.Release.Set();
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void ConcurrentProducers_PreserveUniqueSequence()
    {
        const int producerCount = 8;
        const int attemptsPerProducer = 1000;
        var sink = new BlockingSink();
        using var writer = CreateWriter(sink, capacity: 128, infoLimit: 96, warningLimit: 112, batchSize: 1);
        var acceptedMessages = new ConcurrentBag<string>();
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Concurrent", "prime"));
        acceptedMessages.Add("prime");
        Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(2)));

        Parallel.For(0, producerCount, producer =>
        {
            for (var i = 0; i < attemptsPerProducer; i++)
            {
                var message = producer + "-" + i;
                if (writer.TryWrite(LogLevel.Info, "Concurrent", message))
                {
                    acceptedMessages.Add(message);
                }
            }
        });

        var metrics = writer.GetMetrics();
        Assert.IsTrue(metrics.HighWaterMark <= 128);
        Assert.AreEqual(1L + producerCount * attemptsPerProducer, metrics.AcceptedInfo + metrics.DroppedInfo);
        Assert.AreEqual(96, metrics.CurrentPending);

        sink.Release.Set();
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
        var writtenMessages = sink.Lines.Select(MessagePart).ToArray();
        Assert.AreEqual(acceptedMessages.Count, writtenMessages.Length);
        CollectionAssert.AreEquivalent(acceptedMessages.ToArray(), writtenMessages);
        Assert.AreEqual(writtenMessages.Length, writtenMessages.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void Flood_RemainsBounded()
    {
        const int attempts = 100000;
        var sink = new BlockingSink();
        using var writer = CreateWriter(sink, capacity: 256, infoLimit: 192, warningLimit: 224, batchSize: 1);
        Assert.IsTrue(writer.TryWrite(LogLevel.Info, "Flood", "prime"));
        Assert.IsTrue(sink.Entered.Wait(TimeSpan.FromSeconds(2)));

        for (var i = 0; i < attempts; i++)
        {
            writer.TryWrite(LogLevel.Info, "Flood", "bounded");
        }

        var metrics = writer.GetMetrics();
        Assert.IsTrue(metrics.HighWaterMark <= 256);
        Assert.AreEqual(192, metrics.CurrentPending);
        Assert.AreEqual(1L + attempts, metrics.AcceptedInfo + metrics.DroppedInfo);

        sink.Release.Set();
        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public void Metrics_CumulativeCountersAndHighWaterAreMonotonic()
    {
        var sink = new RecordingSink();
        using var writer = CreateWriter(sink, capacity: 32, infoLimit: 24, warningLimit: 28, batchSize: 4);
        var snapshots = new List<LogWriterMetrics> { writer.GetMetrics() };

        for (var i = 0; i < 20; i++)
        {
            writer.TryWrite((LogLevel)(i % 3), "Metrics", "item-" + i);
            snapshots.Add(writer.GetMetrics());
        }

        Assert.IsTrue(writer.Shutdown(TimeSpan.FromSeconds(2)));
        snapshots.Add(writer.GetMetrics());

        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.IsTrue(snapshots[i].AcceptedTotal >= snapshots[i - 1].AcceptedTotal);
            Assert.IsTrue(snapshots[i].WrittenTotal >= snapshots[i - 1].WrittenTotal);
            Assert.IsTrue(snapshots[i].DroppedTotal >= snapshots[i - 1].DroppedTotal);
            Assert.IsTrue(snapshots[i].WriterFailures >= snapshots[i - 1].WriterFailures);
            Assert.IsTrue(snapshots[i].HighWaterMark >= snapshots[i - 1].HighWaterMark);
        }

        var final = snapshots[^1];
        Assert.AreEqual(final.AcceptedTotal, final.WrittenTotal + final.DroppedTotal);
        Assert.AreEqual(0, final.CurrentPending);
        Assert.IsFalse(final.IsAccepting);
        Assert.IsFalse(final.IsWriterAlive);
    }

    [TestMethod]
    public void Sanitizer_RedactsStructuredSecretsPrivateKeysJwtAndPersonalPaths()
    {
        var json = LogSanitizer.Sanitize("{\"api_key\":\"secret-value\"}", 512); // gitleaks:allow -- synthetic redaction-test value
        var token = LogSanitizer.Sanitize("token=abc123", 512); // gitleaks:allow -- synthetic redaction-test value
        var jwt = LogSanitizer.Sanitize("eyJabcdefgh.abcdefgh.abcdefgh", 512); // gitleaks:allow -- synthetic redaction-test value
        var privateKey = LogSanitizer.Sanitize(
            "-----BEGIN PRIVATE KEY-----raw-key-----END PRIVATE KEY-----", // gitleaks:allow -- synthetic redaction-test envelope
            512);
        var path = LogSanitizer.Sanitize("/Users/alice/file.db", 512); // gitleaks:allow -- synthetic personal-path test value

        StringAssert.Contains(json, "[redacted]");
        StringAssert.Contains(token, "[redacted]");
        StringAssert.Contains(jwt, "[jwt-redacted]");
        StringAssert.Contains(privateKey, "[private-key-redacted]");
        StringAssert.Contains(path, "[path]");
        Assert.IsFalse(json.Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(privateKey.Contains("raw-key", StringComparison.Ordinal));
        Assert.IsFalse(path.Contains("alice", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_RedactsJsonSecretTruncatedBeforeClosingQuote()
    {
        var result = LogSanitizer.Sanitize("prefix {\"password\":\"never-close", 32);

        StringAssert.Contains(result, "\"password\":[redacted]");
        Assert.IsFalse(result.Contains("never-close", StringComparison.Ordinal));
        Assert.IsTrue(result.Length <= 32);
    }

    [TestMethod]
    public void Sanitizer_RedactsNumericAndUnquotedStructuredValues()
    {
        var result = LogSanitizer.Sanitize("{\"pin\":12345678,\"safe\":true}");

        StringAssert.Contains(result, "\"pin\":[redacted]");
        Assert.IsFalse(result.Contains("12345678", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("safe", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_MalformedEscapedQuoteRedactsConservativeLineRemainder()
    {
        var result = LogSanitizer.Sanitize("{\"password\":\"abc\\\"LEAKTAIL\"}");

        StringAssert.Contains(result, "\"password\":[redacted]");
        Assert.IsFalse(result.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("LEAKTAIL", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_ConnectionPasswordConsumesSpacesThroughSemicolon()
    {
        var result = LogSanitizer.Sanitize("Server=test;Password=alpha beta;User Id=safe");

        StringAssert.Contains(result, "Password=[redacted];User Id=safe");
        Assert.IsFalse(result.Contains("alpha", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("beta", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_SpacedTokenAndApiKeyConsumeThroughConservativeDelimiters()
    {
        var result = LogSanitizer.Sanitize(
            "token=alpha beta; api_key=gamma delta|safe tail"); // gitleaks:allow -- synthetic redaction-test values

        StringAssert.Contains(result, "token=[redacted]; api_key=[redacted]|safe tail");
        Assert.IsFalse(result.Contains("alpha", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("beta", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("gamma", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("delta", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_PersonalPathWithSpacesConsumesConservativeLineRemainder()
    {
        var result = LogSanitizer.Sanitize("open C:\\Users\\Jane Doe\\Secret\\file.db then LEAKSUFFIX");

        StringAssert.Contains(result, "open [path]");
        Assert.IsFalse(result.Contains("Jane", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("Secret", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("LEAKSUFFIX", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_WorkBoundaryMasksPartialTokenTailAfterRedactionShrink()
    {
        const int fixedChars = 9 + 2 + 7; // Password=, semicolon+space, sk-LEAK.
        var input = "Password="
            + new string('a', LogSanitizer.MaxInputChars - fixedChars)
            + "; sk-LEAK"
            + "OUTSIDE-WORK-BOUND";

        var result = LogSanitizer.Sanitize(input, LogSanitizer.MaxStoredChars);

        StringAssert.Contains(result, "[truncated]");
        Assert.IsFalse(result.Contains("LEAK", StringComparison.Ordinal));
        Assert.IsTrue(result.Length <= LogSanitizer.MaxStoredChars);
    }

    [TestMethod]
    public void Sanitizer_MultilineConnectionSecretCannotLeakContinuation()
    {
        var result = LogSanitizer.Sanitize("Password=alpha\r\nbeta;User Id=safe");

        StringAssert.Contains(result, "Password=[redacted];User Id=safe");
        Assert.IsFalse(result.Contains("alpha", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("beta", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("\r", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_MultilineStructuredSecretCannotLeakContinuation()
    {
        var result = LogSanitizer.Sanitize("{\"password\":\"alpha\r\nbeta\"}");

        StringAssert.Contains(result, "\"password\":[redacted]");
        Assert.IsFalse(result.Contains("alpha", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("beta", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("\r", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sanitizer_LongIncompleteJwtAcrossWorkOrStorageBoundaryReturnsOnlyMarker()
    {
        var incompleteJwt = "eyJ" + new string('J', 256);
        var workBoundaryInput = new string('p', LogSanitizer.MaxInputChars - 160)
            + incompleteJwt;
        var storageBoundaryInput = new string('q', 200) + incompleteJwt;

        var workResult = LogSanitizer.Sanitize(
            workBoundaryInput,
            LogSanitizer.MaxStoredChars);
        var storageResult = LogSanitizer.Sanitize(storageBoundaryInput, 256);

        Assert.AreEqual("[truncated]", workResult);
        Assert.AreEqual("[truncated]", storageResult);
        Assert.IsFalse(workResult.Contains("eyJ", StringComparison.Ordinal));
        Assert.IsFalse(storageResult.Contains("eyJ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConfiguredBounds_CannotExceedFailClosedHardLimits()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LogSanitizer.Sanitize("value", LogSanitizer.MaxStoredChars + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LogSanitizer.SanitizeSource("source", LogSanitizer.DefaultMaxSourceLength + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new BoundedAsyncLogWriter(
                new RecordingSink(),
                maxMessageLength: LogSanitizer.MaxStoredChars + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new BoundedAsyncLogWriter(
                new RecordingSink(),
                maxSourceLength: LogSanitizer.DefaultMaxSourceLength + 1));
    }

    private static BoundedAsyncLogWriter CreateWriter(
        ILogBatchSink sink,
        int capacity,
        int infoLimit,
        int warningLimit,
        int batchSize)
    {
        return new BoundedAsyncLogWriter(
            sink,
            capacity,
            infoLimit,
            warningLimit,
            batchSize,
            maxMessageLength: 512,
            maxSourceLength: 64,
            clock: new FixedClock(new DateTime(2026, 7, 22, 0, 0, 0)),
            retryDelayMilliseconds: 5);
    }

    private static string MessagePart(string line)
    {
        var marker = "] ";
        var start = line.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        return line.Substring(start + marker.Length).TrimEnd('\r', '\n');
    }

    private sealed class FixedClock : ILogClock
    {
        public FixedClock(DateTime now)
        {
            Now = now;
        }

        public DateTime Now { get; }
    }

    private sealed class RecordingSink : ILogBatchSink
    {
        public ConcurrentQueue<string> Lines { get; } = new ConcurrentQueue<string>();

        public void WriteBatch(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
            {
                Lines.Enqueue(line);
            }
        }
    }

    private sealed class BlockingSink : ILogBatchSink
    {
        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);
        public ManualResetEventSlim Release { get; } = new ManualResetEventSlim(false);
        public ConcurrentQueue<string> Lines { get; } = new ConcurrentQueue<string>();

        public void WriteBatch(IReadOnlyList<string> lines)
        {
            Entered.Set();
            Release.Wait();
            foreach (var line in lines)
            {
                Lines.Enqueue(line);
            }
        }
    }

    private sealed class FailingSink : ILogBatchSink
    {
        public void WriteBatch(IReadOnlyList<string> lines)
        {
            throw new IOException("deliberate sink failure");
        }
    }
}
