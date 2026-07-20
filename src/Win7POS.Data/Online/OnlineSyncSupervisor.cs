using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Data.Online
{
    public interface IOnlineSyncClock
    {
        DateTimeOffset UtcNow { get; }
        Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    }

    public sealed class SystemOnlineSyncClock : IOnlineSyncClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    public delegate Task<OnlineSyncLaneOutcome> OnlineSyncLaneRunner(
        OnlineSyncLaneExecutionContext context,
        OnlineSyncLaneTrigger trigger,
        CancellationToken cancellationToken);

    public sealed class OnlineSyncStartOfDayResult
    {
        public OnlineSyncLaneOutcome CatalogDelta { get; internal set; }
        public OnlineSyncLaneOutcome CatalogImport { get; internal set; }
        public OnlineSyncLaneOutcome Heartbeat { get; internal set; }
        public OnlineSyncLaneOutcome Sales { get; internal set; }
    }

    /// <summary>
    /// Four independent, generation-scoped lanes. A caller cancellation only stops
    /// that caller's wait; relink, shutdown, or a current-generation auth denial are
    /// the only events that cancel shared lane work.
    /// </summary>
    public sealed class OnlineSyncSupervisor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly OnlineSyncGeneration _generation;
        private readonly Dictionary<OnlineSyncLane, LaneSlot> _lanes;
        private readonly OnlineSyncLaneRunner _runner;
        private readonly Func<OnlineSyncGeneration, Task<bool>> _isCurrent;
        private readonly Func<OnlineSyncGeneration, string, Task> _authenticationStop;
        private readonly Func<OnlineSyncGeneration, Task<OnlineSyncRequestCredentials>>
            _credentialProvider;
        private readonly IOnlineSyncClock _clock;
        private readonly Func<double> _jitter;
        private readonly PriorityOnlineRequestGate _requestGate;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private bool _authenticationStopped;
        private bool _stopping;
        private Task _authenticationStopTask = Task.CompletedTask;
        private Task _stopTask;
        private int _resourcesDisposed;

        public OnlineSyncSupervisor(
            OnlineSyncGeneration generation,
            OnlineSyncLaneRunner runner,
            Func<OnlineSyncGeneration, Task<bool>> isCurrent,
            Func<OnlineSyncGeneration, string, Task> authenticationStop,
            int networkConcurrency = 2,
            IOnlineSyncClock clock = null,
            Func<double> jitter = null,
            Func<OnlineSyncGeneration, Task<OnlineSyncRequestCredentials>>
                credentialProvider = null)
        {
            _generation = generation ?? throw new ArgumentNullException(nameof(generation));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
            _authenticationStop = authenticationStop ?? throw new ArgumentNullException(nameof(authenticationStop));
            _credentialProvider = credentialProvider;
            _clock = clock ?? new SystemOnlineSyncClock();
            _jitter = jitter ?? DefaultJitter;
            _requestGate = new PriorityOnlineRequestGate(networkConcurrency);
            _lanes = Enum.GetValues(typeof(OnlineSyncLane))
                .Cast<OnlineSyncLane>()
                .ToDictionary(lane => lane, lane => new LaneSlot(lane));
        }

        public OnlineSyncGeneration Generation => _generation;

        public void Start()
        {
            Signal(OnlineSyncLane.Heartbeat, OnlineSyncLaneTrigger.StartOfDay);
            Signal(OnlineSyncLane.SalesOutbox, OnlineSyncLaneTrigger.StartOfDay);
            Signal(OnlineSyncLane.CatalogImportOutbox, OnlineSyncLaneTrigger.StartOfDay);
        }

        public void Signal(OnlineSyncLane lane, OnlineSyncLaneTrigger trigger)
        {
            Queue(lane, trigger, null, CancellationToken.None);
        }

        public Task<OnlineSyncLaneOutcome> TriggerAsync(
            OnlineSyncLane lane,
            OnlineSyncLaneTrigger trigger,
            CancellationToken waiterCancellationToken = default)
        {
            if (waiterCancellationToken.IsCancellationRequested)
                return Task.FromCanceled<OnlineSyncLaneOutcome>(waiterCancellationToken);

            var waiter = new TaskCompletionSource<OnlineSyncLaneOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            if (waiterCancellationToken.CanBeCanceled)
            {
                registration = waiterCancellationToken.Register(
                    () => waiter.TrySetCanceled());
            }
            Queue(lane, trigger, waiter, waiterCancellationToken);
            if (waiterCancellationToken.CanBeCanceled)
            {
                _ = waiter.Task.ContinueWith(
                    _ => registration.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
            return waiter.Task;
        }

        public async Task<OnlineSyncStartOfDayResult> TriggerStartOfDayAsync(
            bool catalogRequired,
            CancellationToken waiterCancellationToken)
        {
            var heartbeat = TriggerAsync(
                OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.StartOfDay,
                waiterCancellationToken);
            var sales = TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.StartOfDay,
                waiterCancellationToken);
            var catalogImport = TriggerAsync(
                OnlineSyncLane.CatalogImportOutbox,
                OnlineSyncLaneTrigger.StartOfDay,
                waiterCancellationToken);
            Task<OnlineSyncLaneOutcome> catalog = catalogRequired
                ? TriggerAsync(
                    OnlineSyncLane.CatalogDelta,
                    OnlineSyncLaneTrigger.StartOfDay,
                    waiterCancellationToken)
                : Task.FromResult<OnlineSyncLaneOutcome>(null);

            await Task.WhenAll(heartbeat, sales, catalogImport, catalog).ConfigureAwait(false);
            return new OnlineSyncStartOfDayResult
            {
                Heartbeat = heartbeat.Result,
                Sales = sales.Result,
                CatalogImport = catalogImport.Result,
                CatalogDelta = catalog.Result
            };
        }

        public OnlineSyncSupervisorSnapshot GetSnapshot()
        {
            lock (_gate)
            {
                return new OnlineSyncSupervisorSnapshot(
                    _generation,
                    _authenticationStopped,
                    _lanes.Values
                        .OrderBy(slot => slot.Lane)
                        .Select(slot => new OnlineSyncLaneSnapshot(
                            slot.Lane,
                            slot.InFlight,
                            slot.Pending,
                            slot.NextDueAt,
                            slot.FailureCount,
                            slot.LastOutcome))
                        .ToArray());
            }
        }

        public async Task WhenIdleAsync()
        {
            while (true)
            {
                Task[] tasks;
                lock (_gate)
                {
                    tasks = _lanes.Values
                        .Where(slot => slot.InFlight && slot.DrainTask != null)
                        .Select(slot => slot.DrainTask)
                        .Distinct()
                        .ToArray();
                    if (tasks.Length == 0)
                        return;
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Stop/relink cancellation is the expected idle transition.
                }
            }
        }

        public Task StopAsync()
        {
            CancellationTokenSource[] schedules;
            TaskCompletionSource<object> completion;
            lock (_gate)
            {
                if (_stopTask != null) return _stopTask;
                _stopping = true;
                schedules = CancelSchedulesAndPendingWaiters(
                    new OnlineSyncLaneOutcome(false, "sync_stopped"));
                completion = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
            }

            _requestGate.Stop();
            foreach (var schedule in schedules)
            {
                try { schedule.Cancel(); }
                catch { }
                schedule.Dispose();
            }
            try { _lifetime.Cancel(); }
            catch { }
            _ = CompleteStopAsync(completion);
            return completion.Task;
        }

        private async Task CompleteStopAsync(TaskCompletionSource<object> completion)
        {
            try
            {
                await WhenIdleAsync().ConfigureAwait(false);
                await _authenticationStopTask.ConfigureAwait(false);
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            Task stopTask;
            try { stopTask = StopAsync(); }
            catch { stopTask = Task.CompletedTask; }
            _ = stopTask.ContinueWith(
                _ => DisposeResources(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
                return;
            _requestGate.Dispose();
            _lifetime.Dispose();
        }

        private void Queue(
            OnlineSyncLane lane,
            OnlineSyncLaneTrigger trigger,
            TaskCompletionSource<OnlineSyncLaneOutcome> waiter,
            CancellationToken waiterCancellationToken)
        {
            Task drainTask = null;
            OnlineSyncLaneOutcome stoppedOutcome = null;
            CancellationTokenSource scheduleToCancel = null;
            lock (_gate)
            {
                if (_stopping || _authenticationStopped)
                {
                    stoppedOutcome = _authenticationStopped
                        ? OnlineSyncLaneOutcome.AuthDenied("auth_stopped")
                        : new OnlineSyncLaneOutcome(false, "sync_stopped");
                }
                else
                {
                    var slot = _lanes[lane];
                    if (slot.ScheduleCancellation != null)
                    {
                        scheduleToCancel = slot.ScheduleCancellation;
                        slot.ScheduleCancellation = null;
                        slot.NextDueAt = null;
                    }

                    if (!slot.Pending || TriggerPriority(trigger) >= TriggerPriority(slot.PendingTrigger))
                        slot.PendingTrigger = trigger;
                    slot.Pending = true;
                    if (waiter != null && !waiter.Task.IsCompleted)
                        slot.PendingWaiters.Add(waiter);
                    if (!slot.InFlight)
                    {
                        slot.InFlight = true;
                        drainTask = Task.Run(() => DrainLaneAsync(slot));
                        slot.DrainTask = drainTask;
                    }
                }
            }

            if (scheduleToCancel != null)
            {
                try { scheduleToCancel.Cancel(); }
                catch { }
                scheduleToCancel.Dispose();
            }
            if (stoppedOutcome != null)
                waiter?.TrySetResult(stoppedOutcome);
        }

        private async Task DrainLaneAsync(LaneSlot slot)
        {
            try
            {
                await DrainLaneCoreAsync(slot).ConfigureAwait(false);
            }
            catch
            {
                TaskCompletionSource<OnlineSyncLaneOutcome>[] waiters;
                lock (_gate)
                {
                    waiters = slot.PendingWaiters.ToArray();
                    slot.PendingWaiters.Clear();
                    slot.Pending = false;
                    slot.InFlight = false;
                    slot.DrainTask = null;
                }
                var outcome = new OnlineSyncLaneOutcome(false, "lane_exception");
                foreach (var waiter in waiters)
                    waiter.TrySetResult(outcome);
            }
        }

        private async Task DrainLaneCoreAsync(LaneSlot slot)
        {
            while (true)
            {
                OnlineSyncLaneTrigger trigger;
                TaskCompletionSource<OnlineSyncLaneOutcome>[] waiters;
                lock (_gate)
                {
                    if (!slot.Pending || _stopping || _authenticationStopped)
                    {
                        slot.InFlight = false;
                        slot.DrainTask = null;
                        return;
                    }

                    trigger = slot.PendingTrigger;
                    slot.Pending = false;
                    waiters = slot.PendingWaiters.ToArray();
                    slot.PendingWaiters.Clear();
                }

                OnlineSyncLaneOutcome outcome;
                var current = false;
                try
                {
                    current = await _isCurrent(_generation).ConfigureAwait(false);
                    if (!current)
                    {
                        outcome = new OnlineSyncLaneOutcome(false, "stale_generation");
                    }
                    else
                    {
                        var context = new OnlineSyncLaneExecutionContext(
                            _generation,
                            slot.Lane,
                            _requestGate,
                            _isCurrent,
                            StopAuthenticationAsync,
                            _credentialProvider);
                        outcome = await _runner(context, trigger, _lifetime.Token)
                            .ConfigureAwait(false) ??
                            new OnlineSyncLaneOutcome(false, "lane_returned_null");
                        current = await _isCurrent(_generation).ConfigureAwait(false);
                        if (!current && !outcome.AuthenticationDenied)
                            outcome = new OnlineSyncLaneOutcome(false, "stale_generation");
                    }
                }
                catch (OnlineSyncGenerationChangedException)
                {
                    current = false;
                    outcome = new OnlineSyncLaneOutcome(false, "stale_generation");
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    outcome = new OnlineSyncLaneOutcome(false, "generation_cancelled");
                }
                catch (OperationCanceledException)
                {
                    outcome = new OnlineSyncLaneOutcome(false, "lane_timeout");
                }
                catch
                {
                    lock (_gate)
                    {
                        outcome = _authenticationStopped
                            ? OnlineSyncLaneOutcome.AuthDenied("auth_stop_persist_failed")
                            : new OnlineSyncLaneOutcome(false, "lane_exception");
                    }
                }

                if (current && outcome.AuthenticationDenied)
                {
                    try
                    {
                        await StopAuthenticationAsync(outcome.Code).ConfigureAwait(false);
                    }
                    catch
                    {
                        outcome = OnlineSyncLaneOutcome.AuthDenied(
                            "auth_stop_persist_failed");
                    }
                }

                foreach (var waiter in waiters)
                    waiter.TrySetResult(outcome);

                if (current && !outcome.AuthenticationDenied)
                {
                    if (waiters.Length == 0 &&
                        outcome.RequestCatalogNow &&
                        slot.Lane != OnlineSyncLane.CatalogDelta)
                    {
                        Signal(
                            OnlineSyncLane.CatalogDelta,
                            slot.Lane == OnlineSyncLane.CatalogImportOutbox
                                ? OnlineSyncLaneTrigger.ImportAcknowledged
                                : OnlineSyncLaneTrigger.RevisionChanged);
                    }

                    var schedule = OnlineSyncLaneSchedulePolicy.Evaluate(
                        slot.Lane,
                        outcome,
                        slot.FailureCount,
                        _clock.UtcNow,
                        _jitter());
                    lock (_gate)
                    {
                        if (!_stopping && !_authenticationStopped)
                        {
                            slot.LastOutcome = outcome;
                            slot.FailureCount = schedule.FailureCount;
                        }
                    }
                    if (schedule.ShouldSchedule)
                        Schedule(slot, schedule.Delay, outcome);
                }

                lock (_gate)
                {
                    if (!slot.Pending || _stopping || _authenticationStopped)
                    {
                        slot.InFlight = false;
                        slot.DrainTask = null;
                        return;
                    }
                }
            }
        }

        private void Schedule(
            LaneSlot slot,
            TimeSpan delay,
            OnlineSyncLaneOutcome outcome)
        {
            CancellationTokenSource scheduleCancellation;
            lock (_gate)
            {
                if (_stopping || _authenticationStopped || slot.Pending)
                    return;
                slot.ScheduleCancellation?.Cancel();
                slot.ScheduleCancellation?.Dispose();
                scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetime.Token);
                slot.ScheduleCancellation = scheduleCancellation;
                slot.NextDueAt = _clock.UtcNow.Add(delay);
            }

            _ = RunScheduleAsync(slot, delay, outcome, scheduleCancellation);
        }

        private async Task RunScheduleAsync(
            LaneSlot slot,
            TimeSpan delay,
            OnlineSyncLaneOutcome outcome,
            CancellationTokenSource scheduleCancellation)
        {
            try
            {
                await _clock.DelayAsync(delay, scheduleCancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ReferenceEquals(slot.ScheduleCancellation, scheduleCancellation))
                        return;
                    slot.ScheduleCancellation = null;
                    slot.NextDueAt = null;
                }
                Signal(
                    slot.Lane,
                    slot.Lane == OnlineSyncLane.CatalogDelta && outcome.CatalogHasMore
                        ? OnlineSyncLaneTrigger.PartialResume
                        : OnlineSyncLaneTrigger.Periodic);
            }
            catch (OperationCanceledException)
            {
                // A newer signal, relink, auth-stop, or shutdown replaced this due time.
            }
            finally
            {
                scheduleCancellation.Dispose();
            }
        }

        private Task StopAuthenticationAsync(string code)
        {
            CancellationTokenSource[] schedules;
            TaskCompletionSource<object> completion;
            lock (_gate)
            {
                if (_authenticationStopped)
                    return _authenticationStopTask;
                _authenticationStopped = true;
                // A denial already observed by a request must be persisted even
                // when ordinary shutdown won the race and marked this supervisor
                // stopping first. StopAsync waits _authenticationStopTask after
                // all lanes drain, so the durable generation fence is not lost.
                schedules = CancelSchedulesAndPendingWaiters(
                    OnlineSyncLaneOutcome.AuthDenied(code));
                completion = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _authenticationStopTask = completion.Task;
            }

            _requestGate.Stop();
            foreach (var schedule in schedules)
            {
                try { schedule.Cancel(); }
                catch { }
                schedule.Dispose();
            }
            try { _lifetime.Cancel(); }
            catch { }
            _ = RunAuthenticationStopCallbackAsync(code, completion);
            return completion.Task;
        }

        private async Task RunAuthenticationStopCallbackAsync(
            string code,
            TaskCompletionSource<object> completion)
        {
            try
            {
                await _authenticationStop(_generation, code).ConfigureAwait(false);
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        private CancellationTokenSource[] CancelSchedulesAndPendingWaiters(
            OnlineSyncLaneOutcome outcome)
        {
            var schedules = new List<CancellationTokenSource>();
            foreach (var slot in _lanes.Values)
            {
                if (slot.ScheduleCancellation != null)
                {
                    schedules.Add(slot.ScheduleCancellation);
                    slot.ScheduleCancellation = null;
                    slot.NextDueAt = null;
                }
                slot.Pending = false;
                foreach (var waiter in slot.PendingWaiters)
                    waiter.TrySetResult(outcome);
                slot.PendingWaiters.Clear();
            }
            return schedules.ToArray();
        }

        private static int TriggerPriority(OnlineSyncLaneTrigger trigger)
        {
            switch (trigger)
            {
                case OnlineSyncLaneTrigger.AdministratorRepair: return 100;
                case OnlineSyncLaneTrigger.FirstBootstrap: return 95;
                case OnlineSyncLaneTrigger.Manual: return 90;
                case OnlineSyncLaneTrigger.PartialResume: return 80;
                case OnlineSyncLaneTrigger.RevisionChanged: return 75;
                case OnlineSyncLaneTrigger.ImportAcknowledged: return 70;
                case OnlineSyncLaneTrigger.NetworkRecovered: return 60;
                case OnlineSyncLaneTrigger.LocalCommit: return 55;
                case OnlineSyncLaneTrigger.StartOfDay: return 50;
                case OnlineSyncLaneTrigger.Foreground: return 40;
                default: return 10;
            }
        }

        private static double DefaultJitter()
        {
            unchecked
            {
                var mixed = (uint)(Environment.TickCount ^ DateTime.UtcNow.Ticks.GetHashCode());
                return (mixed % 10001u) / 10000d;
            }
        }

        private sealed class LaneSlot
        {
            public LaneSlot(OnlineSyncLane lane)
            {
                Lane = lane;
                PendingWaiters = new List<TaskCompletionSource<OnlineSyncLaneOutcome>>();
            }

            public Task DrainTask { get; set; }
            public int FailureCount { get; set; }
            public bool InFlight { get; set; }
            public OnlineSyncLane Lane { get; }
            public OnlineSyncLaneOutcome LastOutcome { get; set; }
            public DateTimeOffset? NextDueAt { get; set; }
            public bool Pending { get; set; }
            public OnlineSyncLaneTrigger PendingTrigger { get; set; }
            public List<TaskCompletionSource<OnlineSyncLaneOutcome>> PendingWaiters { get; }
            public CancellationTokenSource ScheduleCancellation { get; set; }
        }
    }
}
