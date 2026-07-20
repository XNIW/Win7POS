using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public sealed class OnlineSyncGenerationChangedException : OperationCanceledException
    {
        public OnlineSyncGenerationChangedException()
            : base("The trusted-session generation is no longer current.")
        {
        }
    }

    public sealed class OnlineSyncCredentialsChangedException : Exception
    {
        public OnlineSyncCredentialsChangedException()
            : base("Trusted credentials changed while the request was in flight.")
        {
        }
    }

    public sealed class PriorityOnlineRequestGate : IDisposable
    {
        private readonly object _gate = new object();
        private readonly int _capacity;
        private readonly List<Waiter> _waiters = new List<Waiter>();
        private long _nextSequence;
        private int _active;
        private bool _stopped;
        private bool _disposed;

        public PriorityOnlineRequestGate(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Capacity => _capacity;

        public Task<IDisposable> EnterAsync(
            OnlineSyncLane lane,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_active < _capacity && _waiters.Count == 0)
                {
                    _active++;
                    return Task.FromResult<IDisposable>(new Lease(this));
                }

                var waiter = new Waiter(lane, _nextSequence++);
                _waiters.Add(waiter);
                if (cancellationToken.CanBeCanceled)
                {
                    waiter.Cancellation = cancellationToken.Register(
                        () => CancelWaiter(waiter));
                }
                return waiter.Completion.Task;
            }
        }

        public void Stop()
        {
            Waiter[] pending;
            lock (_gate)
            {
                if (_disposed || _stopped) return;
                _stopped = true;
                pending = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var waiter in pending)
            {
                waiter.Cancellation.Dispose();
                waiter.Completion.TrySetException(
                    new OnlineSyncGenerationChangedException());
            }
        }

        public void Dispose()
        {
            Waiter[] pending;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stopped = true;
                pending = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var waiter in pending)
            {
                waiter.Cancellation.Dispose();
                waiter.Completion.TrySetCanceled();
            }
        }

        private void CancelWaiter(Waiter waiter)
        {
            var cancelled = false;
            lock (_gate)
            {
                if (!waiter.Granted)
                    cancelled = _waiters.Remove(waiter);
            }
            if (cancelled)
                waiter.Completion.TrySetCanceled();
        }

        private void Release()
        {
            Waiter next = null;
            lock (_gate)
            {
                if (_active <= 0)
                    throw new InvalidOperationException("The request gate was released too many times.");
                _active--;
                if (_disposed || _stopped || _waiters.Count == 0)
                    return;

                next = _waiters
                    .OrderByDescending(item => item.Lane == OnlineSyncLane.Heartbeat)
                    .ThenBy(item => item.Sequence)
                    .First();
                _waiters.Remove(next);
                next.Granted = true;
                _active++;
            }

            next.Cancellation.Dispose();
            next.Completion.TrySetResult(new Lease(this));
        }

        private void ThrowIfUnavailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PriorityOnlineRequestGate));
            if (_stopped) throw new OnlineSyncGenerationChangedException();
        }

        private sealed class Waiter
        {
            public Waiter(OnlineSyncLane lane, long sequence)
            {
                Lane = lane;
                Sequence = sequence;
                Completion = new TaskCompletionSource<IDisposable>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public CancellationTokenRegistration Cancellation { get; set; }
            public TaskCompletionSource<IDisposable> Completion { get; }
            public bool Granted { get; set; }
            public OnlineSyncLane Lane { get; }
            public long Sequence { get; }
        }

        private sealed class Lease : IDisposable
        {
            private PriorityOnlineRequestGate _owner;

            public Lease(PriorityOnlineRequestGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release();
            }
        }
    }

    public sealed class OnlineSyncLaneExecutionContext
    {
        private readonly PriorityOnlineRequestGate _requestGate;
        private readonly Func<OnlineSyncGeneration, Task<bool>> _isCurrent;
        private readonly Func<string, Task> _authenticationStop;
        private readonly Func<OnlineSyncGeneration, Task<OnlineSyncRequestCredentials>>
            _credentialProvider;

        internal OnlineSyncLaneExecutionContext(
            OnlineSyncGeneration generation,
            OnlineSyncLane lane,
            PriorityOnlineRequestGate requestGate,
            Func<OnlineSyncGeneration, Task<bool>> isCurrent,
            Func<string, Task> authenticationStop,
            Func<OnlineSyncGeneration, Task<OnlineSyncRequestCredentials>> credentialProvider)
        {
            Generation = generation ?? throw new ArgumentNullException(nameof(generation));
            Lane = lane;
            _requestGate = requestGate ?? throw new ArgumentNullException(nameof(requestGate));
            _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
            _authenticationStop = authenticationStop ??
                throw new ArgumentNullException(nameof(authenticationStop));
            _credentialProvider = credentialProvider;
        }

        public OnlineSyncGeneration Generation { get; }
        public OnlineSyncLane Lane { get; }

        public Task<bool> IsCurrentAsync()
        {
            return _isCurrent(Generation);
        }

        public Task StopAuthenticationAsync(string code)
        {
            return _authenticationStop(code);
        }

        public async Task<T> ExecuteRequestAsync<T>(
            Func<CancellationToken, Task<T>> request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            using (await _requestGate.EnterAsync(Lane, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await _isCurrent(Generation).ConfigureAwait(false))
                    throw new OnlineSyncGenerationChangedException();
                cancellationToken.ThrowIfCancellationRequested();
                return await request(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<T> ExecuteCredentialedRequestAsync<T>(
            Func<OnlineSyncRequestCredentials, CancellationToken, Task<T>> request,
            Func<T, string> authenticationDenialCode,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (authenticationDenialCode == null)
                throw new ArgumentNullException(nameof(authenticationDenialCode));
            if (_credentialProvider == null)
                throw new InvalidOperationException("A credential provider is required.");

            using (await _requestGate.EnterAsync(Lane, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await _isCurrent(Generation).ConfigureAwait(false))
                    throw new OnlineSyncGenerationChangedException();

                var credentials = await _credentialProvider(Generation).ConfigureAwait(false);
                if (credentials == null || !credentials.Matches(Generation))
                    throw new OnlineSyncGenerationChangedException();

                cancellationToken.ThrowIfCancellationRequested();
                var result = await request(credentials, cancellationToken).ConfigureAwait(false);
                var denialCode = (authenticationDenialCode(result) ?? string.Empty).Trim();
                if (denialCode.Length == 0)
                    return result;

                var currentCredentials = await _credentialProvider(Generation).ConfigureAwait(false);
                if (currentCredentials == null ||
                    !currentCredentials.Matches(Generation) ||
                    !string.Equals(
                        currentCredentials.CredentialStamp,
                        credentials.CredentialStamp,
                        StringComparison.Ordinal))
                {
                    throw new OnlineSyncCredentialsChangedException();
                }

                await _authenticationStop(denialCode).ConfigureAwait(false);
                return result;
            }
        }
    }
}
