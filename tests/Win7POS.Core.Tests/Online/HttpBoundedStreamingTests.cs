using System.Net;
using System.Net.Http;
using System.Text;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class HttpBoundedStreamingTests
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxErrorBytes = 64 * 1024;

    [TestMethod]
    public void ClientsReuseTransportWithinEndpointConfigurationScope()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var firstOptions = new PosAdminWebOptions(
            new Uri("https://" + suffix + ".example.invalid/"),
            PosAdminWebBaseUrlSource.ConfigFile,
            "staging");
        var otherEndpoint = new PosAdminWebOptions(
            new Uri("https://other-" + suffix + ".example.invalid/"),
            PosAdminWebBaseUrlSource.ConfigFile,
            "staging");

        using var first = new PosAdminWebClient(firstOptions);
        using var second = new PosAdminWebClient(firstOptions);
        using var other = new PosAdminWebClient(otherEndpoint);

        Assert.AreSame(first.TransportForTests, second.TransportForTests);
        Assert.AreNotSame(first.TransportForTests, other.TransportForTests);
    }

    [TestMethod]
    public async Task SuccessUsesHeadersReadAndDeserializesDirectlyFromStream()
    {
        var content = new StreamingOnlyContent(
            () => new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"ok\":true,\"code\":\"heartbeat_ok\",\"serverTime\":\"2026-07-20T00:00:00Z\"}")));
        using var client = CreateClient(HttpStatusCode.OK, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value.Ok);
        Assert.AreEqual("heartbeat_ok", result.Value.Code);
        Assert.AreEqual(0, content.BufferedCopyAttempts);
        Assert.AreEqual(1, content.StreamOpenCount);
    }

    [TestMethod]
    public async Task ExcessiveDeclaredContentLengthIsRejectedBeforeBodyRead()
    {
        var content = new StreamingOnlyContent(() => new MemoryStream(Array.Empty<byte>()));
        content.Headers.ContentLength = MaxResponseBytes + 1L;
        using var client = CreateClient(HttpStatusCode.OK, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("response_too_large", result.Code);
        Assert.AreEqual(0, content.StreamOpenCount);
        Assert.AreEqual(0, content.BufferedCopyAttempts);
    }

    [TestMethod]
    public async Task ChunkedBodyOverEightMiBIsStoppedByCountingStream()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"ok\":true,\"code\":\"heartbeat_ok\",\"serverTime\":\"2026-07-20T00:00:00Z\"}");
        var content = new StreamingOnlyContent(
            () => new PrefixPaddingStream(prefix, MaxResponseBytes + 1L));
        using var client = CreateClient(HttpStatusCode.OK, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("response_too_large", result.Code);
        Assert.AreEqual(0, content.BufferedCopyAttempts);
        Assert.AreEqual(1, content.StreamOpenCount);
    }

    [TestMethod]
    public async Task OversizedUnauthorizedErrorRemainsAuthenticationDenied()
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"ok\":false,\"code\":\"device_revoked\",\"message\":\"denied\"}");
        var content = new StreamingOnlyContent(
            () => new PrefixPaddingStream(prefix, MaxErrorBytes + 1L));
        using var client = CreateClient(HttpStatusCode.Unauthorized, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Denied);
        Assert.AreEqual("response_too_large", result.Code);
        Assert.AreEqual(0, content.BufferedCopyAttempts);
    }

    [TestMethod]
    public async Task BoundedErrorDtoPreservesSafeFieldsOnly()
    {
        var content = new StreamingOnlyContent(
            () => new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"ok\":false,\"code\":\"device_revoked\",\"message\":\"Access denied\",\"requestId\":\"server-123\",\"ignoredBody\":\"secret-not-exposed\"}")));
        using var client = CreateClient(HttpStatusCode.Forbidden, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Denied);
        Assert.AreEqual("device_revoked", result.Code);
        Assert.AreEqual("Access denied", result.Message);
        Assert.AreEqual("server-123", result.ServerRequestId);
    }

    [TestMethod]
    public async Task CallerCancellationInterruptsAnInFlightBodyRead()
    {
        using var blockingStream = new BlockingReadStream();
        var content = new StreamingOnlyContent(() => blockingStream);
        using var client = CreateClient(HttpStatusCode.OK, content);
        using var cancellation = new CancellationTokenSource();

        var request = client.HeartbeatAsync(new PosHeartbeatRequest(), cancellation.Token);
        Assert.IsTrue(
            blockingStream.ReadStarted.Wait(TimeSpan.FromSeconds(2)),
            "The response body read did not start.");

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => request);
    }

    [TestMethod]
    public async Task ErrorCodeAndMessageAreControlFreeAndFieldBounded()
    {
        var json =
            "{\"ok\":false,\"code\":\"bad code\\r\\n\",\"message\":\"line one\\r\\n" +
            new string('x', 700) + "\"}";
        var content = new StreamingOnlyContent(
            () => new MemoryStream(Encoding.UTF8.GetBytes(json)));
        using var client = CreateClient(HttpStatusCode.BadRequest, content);

        var result = await client.HeartbeatAsync(new PosHeartbeatRequest(), CancellationToken.None);

        Assert.AreEqual("bad_code", result.Code);
        Assert.IsTrue(result.Message.Length <= 512);
        Assert.IsFalse(result.Message.Contains('\r'));
        Assert.IsFalse(result.Message.Contains('\n'));
    }

    private static PosAdminWebClient CreateClient(HttpStatusCode statusCode, HttpContent content)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = content
        });
        return new PosAdminWebClient(
            new PosAdminWebOptions(new Uri("https://streaming.example.invalid/")),
            handler);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_response(request));
        }
    }

    private sealed class StreamingOnlyContent : HttpContent
    {
        private readonly Func<Stream> _streamFactory;

        internal StreamingOnlyContent(Func<Stream> streamFactory)
        {
            _streamFactory = streamFactory;
        }

        internal int BufferedCopyAttempts { get; private set; }
        internal int StreamOpenCount { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            BufferedCopyAttempts++;
            throw new InvalidOperationException("ResponseContentRead attempted to buffer the body.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamOpenCount++;
            return Task.FromResult(_streamFactory());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PrefixPaddingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly long _length;
        private long _position;

        internal PrefixPaddingStream(byte[] prefix, long length)
        {
            _prefix = prefix;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
            {
                return 0;
            }

            var available = (int)Math.Min(count, _length - _position);
            var written = 0;
            while (written < available && _position < _prefix.Length)
            {
                buffer[offset + written] = _prefix[_position];
                written++;
                _position++;
            }

            while (written < available)
            {
                buffer[offset + written] = (byte)' ';
                written++;
                _position++;
            }

            return written;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly ManualResetEventSlim _disposed = new(false);

        internal ManualResetEventSlim ReadStarted { get; } = new(false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted.Set();
            _disposed.Wait();
            throw new ObjectDisposedException(nameof(BlockingReadStream));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.Set();
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
