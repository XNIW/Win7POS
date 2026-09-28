using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Diagnostic only. Values and weak keys: never retain a timer or its delegates.
    internal sealed class DispatcherTimerTrace
    {
        private sealed class Identity { internal long Id; internal long FirstSeen; }
        private readonly ConditionalWeakTable<DispatcherTimer, Identity> _timers = new ConditionalWeakTable<DispatcherTimer, Identity>();
        private long _next;
        private static readonly FieldInfo Method = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo[] Arguments = typeof(DispatcherOperation).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.StartsWith("_arg", StringComparison.Ordinal)).ToArray();
        private static readonly FieldInfo Tick = typeof(DispatcherTimer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(field => field.FieldType == typeof(EventHandler) && field.Name.IndexOf("Tick", StringComparison.OrdinalIgnoreCase) >= 0);
        private static readonly FieldInfo Due = typeof(DispatcherTimer).GetField("_dueTimeInTicks", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        internal sealed class Sample
        {
            internal long Timer, FirstSeen, Timestamp, Posted, Eligible, Started;
            internal double Interval, DueOffset = double.NaN, ClockBracket;
            internal int Priority;
            internal string Phase, Status, Source, Handlers, Callback;
            internal bool Enabled;
            internal long Origin;
            private double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
            public override string ToString() => string.Format(CultureInfo.InvariantCulture,
                "TIMER,{0:F3},id={1},phase={2},priority={3},status={4},source={5},handlers={6},callback={7},interval_ms={8:F3},first_seen_ms={9:F3},posted_ms={10:F3},eligible_ms={11:F3},start_ms={12:F3},native_due_offset_ms={13:F3},clock_bracket_ms={14:F3},enabled={15}",
                Ms(Timestamp - Origin), Timer, Phase, Priority, Status, Source, Handlers, Callback, Interval,
                Ms(FirstSeen - Origin), Ms(Posted - Origin), Eligible == 0 ? double.NaN : Ms(Eligible - Origin),
                Started == 0 ? double.NaN : Ms(Started - Origin), DueOffset, ClockBracket, Enabled);
        }

        internal Sample Capture(DispatcherOperation operation, string phase, long origin, long posted, long eligible, long started)
        {
            try
            {
                var callback = Method?.GetValue(operation) as Delegate;
                var timer = callback?.Target as DispatcherTimer;
                var source = timer == null ? "unknown" : "delegate_target";
                if (timer == null)
                    foreach (var field in Arguments)
                    {
                        var value = field.GetValue(operation);
                        timer = value as DispatcherTimer;
                        if (timer == null && value is object[] array) timer = array.OfType<DispatcherTimer>().FirstOrDefault();
                        if (timer != null) { source = field.Name; break; }
                    }
                if (timer == null && (callback == null || callback.Method.Name.IndexOf("FireTick", StringComparison.Ordinal) < 0)) return null;
                if (timer == null) return new Sample { Phase = phase, Source = "timer_unknown", Callback = callback?.Method.Name,
                    Timestamp = Stopwatch.GetTimestamp(), Origin = origin, Posted = posted, Eligible = eligible, Started = started };
                var now = Stopwatch.GetTimestamp();
                var id = _timers.GetValue(timer, _ => new Identity { Id = ++_next, FirstSeen = now });
                var handlers = Tick?.GetValue(timer) as Delegate;
                var sample = new Sample { Timer = id.Id, FirstSeen = id.FirstSeen, Timestamp = now, Origin = origin,
                    Posted = posted, Eligible = eligible, Started = started, Phase = phase, Priority = (int)operation.Priority,
                    Status = operation.Status.ToString(), Source = source, Interval = timer.Interval.TotalMilliseconds, Enabled = timer.IsEnabled,
                    Callback = callback?.Method.DeclaringType?.FullName + "." + callback?.Method.Name,
                    Handlers = Tick == null ? "reflection_unavailable" : handlers == null ? "none" : string.Join("|", handlers.GetInvocationList().Take(16)
                        .Select(handler => (handler.Target?.GetType().FullName ?? "static") + ":" + handler.Method.DeclaringType?.FullName + "." + handler.Method.Name)) };
                // Restart assigns the native due field AFTER OperationPosted. Do not read stale due on Posted.
                // Convert only a signed modulo-2^32 DELTA to our monotonic clock, never subtract clock epochs.
                // TickCount quantization (typically up to 15.625ms) plus this bracket bounds conversion precision.
                if (phase != "posted" && Due?.FieldType == typeof(int))
                {
                    var before = Stopwatch.GetTimestamp();
                    var nativeNow = Environment.TickCount;
                    var due = (int)Due.GetValue(timer);
                    sample.DueOffset = unchecked(due - nativeNow);
                    sample.Timestamp = before;
                    sample.ClockBracket = (Stopwatch.GetTimestamp() - before) * 1000d / Stopwatch.Frequency;
                }
                return sample;
            }
            catch (Exception error) when (error is MemberAccessException || error is TargetException || error is NotSupportedException)
            {
                return new Sample { Phase = phase, Source = "reflection_unavailable:" + error.GetType().Name,
                    Timestamp = Stopwatch.GetTimestamp(), Origin = origin, Posted = posted, Eligible = eligible, Started = started };
            }
        }
    }
}
