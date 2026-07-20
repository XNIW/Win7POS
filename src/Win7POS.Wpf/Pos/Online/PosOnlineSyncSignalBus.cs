#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core.Online;

namespace Win7POS.Wpf.Pos.Online
{
    /// <summary>
    /// Process-local bridge from durable POS commits to the one MainWindow-owned
    /// supervisor host. Registration is owner-token scoped so a closing shell
    /// cannot unregister a newer replacement shell.
    /// </summary>
    internal static class PosOnlineSyncSignalBus
    {
        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim MaintenanceTransitionGate =
            new SemaphoreSlim(1, 1);
        private static int _maintenanceDepth;
        private static Task _maintenanceStopTask = Task.CompletedTask;
        private static bool _resumePending;
        private static bool _resumeSuppressed;
        private static Registration _registration;

        public static bool IsMaintenanceActive
        {
            get
            {
                lock (Gate)
                    return _maintenanceDepth > 0 || _resumePending;
            }
        }

        public static IDisposable Register(
            Action<OnlineSyncLane, OnlineSyncLaneTrigger> signal,
            Func<OnlineSyncLane, OnlineSyncLaneTrigger, CancellationToken,
                Task<OnlineSyncLaneOutcome>> trigger,
            Func<Task> stop,
            Func<CancellationToken, Task> resume)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            if (stop == null) throw new ArgumentNullException(nameof(stop));
            if (resume == null) throw new ArgumentNullException(nameof(resume));
            var registration = new Registration(signal, trigger, stop, resume);
            var pauseForMaintenance = false;
            lock (Gate)
            {
                _registration = registration;
                pauseForMaintenance = _maintenanceDepth > 0 || _resumePending;
            }
            if (pauseForMaintenance)
                _ = PauseRegistrationDuringMaintenanceAsync(registration);
            return registration;
        }

        public static void Signal(
            OnlineSyncLane lane,
            OnlineSyncLaneTrigger trigger)
        {
            Action<OnlineSyncLane, OnlineSyncLaneTrigger> handler;
            lock (Gate)
                handler = _maintenanceDepth > 0 || _resumePending
                    ? null
                    : _registration?.SignalHandler;
            handler?.Invoke(lane, trigger);
        }

        public static Task<OnlineSyncLaneOutcome> TriggerAsync(
            OnlineSyncLane lane,
            OnlineSyncLaneTrigger trigger,
            CancellationToken cancellationToken = default)
        {
            Func<OnlineSyncLane, OnlineSyncLaneTrigger, CancellationToken,
                Task<OnlineSyncLaneOutcome>> handler;
            lock (Gate)
            {
                if (_maintenanceDepth > 0 || _resumePending)
                {
                    return Task.FromResult(new OnlineSyncLaneOutcome(
                        false,
                        "sync_maintenance_active",
                        terminal: true));
                }
                handler = _registration?.TriggerHandler;
            }
            return handler == null
                ? Task.FromResult(new OnlineSyncLaneOutcome(
                    false,
                    "sync_supervisor_inactive",
                    terminal: true))
                : handler(lane, trigger, cancellationToken);
        }

        public static async Task StopAsync()
        {
            await MaintenanceTransitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Task stopTask;
                Func<Task> stopHandler = null;
                var firstStop = false;
                lock (Gate)
                {
                    _maintenanceDepth++;
                    if (_maintenanceDepth > 1)
                    {
                        stopTask = _maintenanceStopTask;
                    }
                    else
                    {
                        _resumePending = false;
                        _resumeSuppressed = false;
                        firstStop = true;
                        stopHandler = _registration?.StopHandler;
                        stopTask = null;
                    }
                }
                if (firstStop)
                {
                    try
                    {
                        stopTask = stopHandler == null
                            ? Task.CompletedTask
                            : stopHandler();
                    }
                    catch (Exception ex)
                    {
                        stopTask = Task.FromException(ex);
                    }
                    lock (Gate)
                        _maintenanceStopTask = stopTask;
                }
                await stopTask.ConfigureAwait(false);
            }
            finally
            {
                MaintenanceTransitionGate.Release();
            }
        }

        public static async Task ResumeAsync(
            CancellationToken cancellationToken = default)
        {
            await EndMaintenanceAsync(
                    resume: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task ExitMaintenanceWithoutResumeAsync()
        {
            await EndMaintenanceAsync(
                    resume: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        private static async Task EndMaintenanceAsync(
            bool resume,
            CancellationToken cancellationToken)
        {
            await MaintenanceTransitionGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                Func<CancellationToken, Task> handler = null;
                lock (Gate)
                {
                    if (_maintenanceDepth <= 0 && !_resumePending)
                        return;
                    if (!resume)
                        _resumeSuppressed = true;
                    if (_maintenanceDepth > 0)
                    {
                        _maintenanceDepth--;
                        if (_maintenanceDepth > 0)
                            return;
                    }
                    handler = resume && !_resumeSuppressed
                        ? _registration?.ResumeHandler
                        : null;
                    if (handler == null)
                    {
                        _resumePending = false;
                        _resumeSuppressed = false;
                        _maintenanceStopTask = Task.CompletedTask;
                        return;
                    }
                    _resumePending = true;
                }

                // A pending resume is independent from open StopAsync scopes.
                // Signals remain fail-closed while the handler runs, and failure
                // leaves a retry marker without corrupting nested-scope depth.
                await handler(cancellationToken).ConfigureAwait(false);
                lock (Gate)
                {
                    _resumePending = false;
                    _resumeSuppressed = false;
                    _maintenanceStopTask = Task.CompletedTask;
                }
            }
            finally
            {
                MaintenanceTransitionGate.Release();
            }
        }

        private static async Task PauseRegistrationDuringMaintenanceAsync(
            Registration registration)
        {
            await MaintenanceTransitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (Gate)
                {
                    if ((_maintenanceDepth <= 0 && !_resumePending) ||
                        !ReferenceEquals(_registration, registration))
                    {
                        return;
                    }
                }

                try
                {
                    await registration.StopHandler().ConfigureAwait(false);
                }
                catch
                {
                    // The authorization-maintenance latch and host request guards
                    // remain fail-closed even when a replacement host cannot drain.
                }
            }
            finally
            {
                MaintenanceTransitionGate.Release();
            }
        }

        private sealed class Registration : IDisposable
        {
            public Registration(
                Action<OnlineSyncLane, OnlineSyncLaneTrigger> signalHandler,
                Func<OnlineSyncLane, OnlineSyncLaneTrigger, CancellationToken,
                    Task<OnlineSyncLaneOutcome>> triggerHandler,
                Func<Task> stopHandler,
                Func<CancellationToken, Task> resumeHandler)
            {
                SignalHandler = signalHandler;
                TriggerHandler = triggerHandler;
                StopHandler = stopHandler;
                ResumeHandler = resumeHandler;
            }

            public Action<OnlineSyncLane, OnlineSyncLaneTrigger> SignalHandler { get; }
            public Func<OnlineSyncLane, OnlineSyncLaneTrigger, CancellationToken,
                Task<OnlineSyncLaneOutcome>> TriggerHandler { get; }
            public Func<Task> StopHandler { get; }
            public Func<CancellationToken, Task> ResumeHandler { get; }

            public void Dispose()
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_registration, this))
                        _registration = null;
                }
            }
        }
    }
}

#nullable restore
