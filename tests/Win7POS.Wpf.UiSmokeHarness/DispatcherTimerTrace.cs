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
        internal static string Compatibility => "timer_reflection_method=" + (Method != null) + ";argument_fields=" + string.Join("|", Arguments.Select(field => field.Name)) +
            ";tick_delegate=" + (Tick != null) + ";native_due_int32=" + (Due?.FieldType == typeof(int));
        internal static int NativeDueDelta(int due, int now) => unchecked(due - now);

        internal sealed class Sample
        {
            internal long Timer, FirstSeen, Timestamp, Posted, Eligible, Started;
            internal double Interval, DueOffset = double.NaN, ClockBracket;
            internal int Priority;
            internal string Phase, Status, Source, ReflectionStatus;
            internal MethodInfo Callback;
            internal Tuple<Type, MethodInfo>[] Handlers;
            internal Type[] ArgumentTypes;
            internal bool Enabled, HandlersTruncated;
            internal long Origin;
            private double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
            public override string ToString() => string.Format(CultureInfo.InvariantCulture,
                "TIMER,{0:F3},id={1},phase={2},priority={3},status={4},source={5},handlers={6},callback={7},interval_ms={8:F3},first_seen_ms={9:F3},posted_ms={10:F3},eligible_ms={11:F3},start_ms={12:F3},native_due_offset_ms={13:F3},clock_bracket_ms={14:F3},enabled={15},argument_types={16},handlers_truncated={17}",
                Ms(Timestamp - Origin), Timer, Phase, Priority, Status, Source,
                Handlers == null ? ReflectionStatus : string.Join("|", Handlers.Select(handler => (handler.Item1?.FullName ?? "static") + ":" + handler.Item2.DeclaringType?.FullName + "." + handler.Item2.Name)),
                Callback?.DeclaringType?.FullName + "." + Callback?.Name, Interval,
                FirstSeen == 0 ? double.NaN : Ms(FirstSeen - Origin), Ms(Posted - Origin), Eligible == 0 ? double.NaN : Ms(Eligible - Origin),
                Started == 0 ? double.NaN : Ms(Started - Origin), DueOffset, ClockBracket, Enabled,
                ArgumentTypes == null ? "unknown" : string.Join("|", ArgumentTypes.Select(type => type?.FullName ?? "null")), HandlersTruncated);
        }

        internal Sample Capture(DispatcherOperation operation, string phase, long origin, long posted, long eligible, long started)
        {
            try
            {
                var callback = Method?.GetValue(operation) as Delegate;
                var timer = callback?.Target as DispatcherTimer;
                var source = timer == null ? "unknown" : "delegate_target";
                // Inspect both mechanisms, including when the target already
                // identifies the timer. Preserve types only, never argument data.
                foreach (var field in Arguments)
                {
                    var value = field.GetValue(operation);
                    var argumentTimer = value as DispatcherTimer;
                    if (argumentTimer == null && value is object[] array) argumentTimer = array.OfType<DispatcherTimer>().FirstOrDefault();
                    if (timer == null && argumentTimer != null) { timer = argumentTimer; source = field.Name; }
                }
                if (timer == null && (callback == null || callback.Method.Name.IndexOf("FireTick", StringComparison.Ordinal) < 0)) return null;
                var argumentTypes = Arguments.Select(field => field.GetValue(operation)?.GetType()).ToArray();
                if (timer == null) return new Sample { Phase = phase, Source = "timer_unknown", ReflectionStatus = "unknown", Callback = callback?.Method, ArgumentTypes = argumentTypes,
                    Timestamp = Stopwatch.GetTimestamp(), Origin = origin, Posted = posted, Eligible = eligible, Started = started };
                var now = Stopwatch.GetTimestamp();
                var id = _timers.GetValue(timer, _ => new Identity { Id = ++_next, FirstSeen = now });
                var handlers = Tick?.GetValue(timer) as Delegate;
                var invocation = handlers?.GetInvocationList();
                var sample = new Sample { Timer = id.Id, FirstSeen = id.FirstSeen, Timestamp = now, Origin = origin,
                    Posted = posted, Eligible = eligible, Started = started, Phase = phase, Priority = (int)operation.Priority,
                    Status = operation.Status.ToString(), Source = source, Interval = timer.Interval.TotalMilliseconds, Enabled = timer.IsEnabled,
                    Callback = callback?.Method, ReflectionStatus = Tick == null ? "reflection_unavailable" : "none",
                    ArgumentTypes = argumentTypes, HandlersTruncated = invocation?.Length > 16,
                    Handlers = invocation?.Take(16).Select(handler => Tuple.Create(handler.Target?.GetType(), handler.Method)).ToArray() };
                // Restart assigns the native due field AFTER OperationPosted. Do not read stale due on Posted.
                // Convert only a signed modulo-2^32 DELTA to our monotonic clock, never subtract clock epochs.
                // TickCount quantization is platform-dependent and is not measured
                // here. Due offsets are approximate, never acceptance timestamps.
                if (phase != "posted" && Due?.FieldType == typeof(int))
                {
                    var before = Stopwatch.GetTimestamp();
                    var nativeNow = Environment.TickCount;
                    var due = (int)Due.GetValue(timer);
                    sample.DueOffset = NativeDueDelta(due, nativeNow);
                    sample.Timestamp = before;
                    sample.ClockBracket = (Stopwatch.GetTimestamp() - before) * 1000d / Stopwatch.Frequency;
                }
                return sample;
            }
            catch (Exception error) when (error is MemberAccessException || error is TargetException || error is System.Security.SecurityException || error is NotSupportedException)
            {
                return new Sample { Phase = phase, Source = "reflection_unavailable:" + error.GetType().Name,
                    Timestamp = Stopwatch.GetTimestamp(), Origin = origin, Posted = posted, Eligible = eligible, Started = started };
            }
        }
    }
}
