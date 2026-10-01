using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Win7POS.Wpf.Pos
{
    /// <summary>Opt-in QA timing, with no barcode, SQL text or logging. Inactive in normal operation.</summary>
    public sealed class PosScanMeasurement : IDisposable
    {
        private static readonly AsyncLocal<PosScanMeasurement> Active = new AsyncLocal<PosScanMeasurement>();
        private readonly PosScanMeasurement _previous;
        private readonly Dictionary<string, double> _stages = new Dictionary<string, double>(StringComparer.Ordinal);
        private long _serviceStarted;
        private long _serviceCompleted;
        private long _applyStarted;
        private PosScanMeasurement() { _previous = Active.Value; Active.Value = this; }
        public static PosScanMeasurement Begin() => new PosScanMeasurement();
        internal static PosScanMeasurement Current => Active.Value;
        public long ServiceCompletedTimestamp => Interlocked.Read(ref _serviceCompleted);
        public long ApplyStartedTimestamp => Interlocked.Read(ref _applyStarted);
        public double ServiceMilliseconds => Milliseconds(Interlocked.Read(ref _serviceCompleted) - Interlocked.Read(ref _serviceStarted));
        public double this[string stage] { get { lock (_stages) return _stages.TryGetValue(stage, out var value) ? value : 0; } }
        internal void ServiceStarted() => Interlocked.Exchange(ref _serviceStarted, Stopwatch.GetTimestamp());
        internal void ServiceCompleted() => Interlocked.Exchange(ref _serviceCompleted, Stopwatch.GetTimestamp());
        internal static IDisposable Measure(string stage) => Current == null ? null : new Stage(Current, stage);
        private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        private sealed class Stage : IDisposable
        {
            private readonly PosScanMeasurement _owner;
            private readonly string _name;
            private readonly long _start = Stopwatch.GetTimestamp();
            public Stage(PosScanMeasurement owner, string name)
            {
                _owner = owner; _name = name;
                if (name == "apply_snapshot") Interlocked.Exchange(ref owner._applyStarted, _start);
            }
            public void Dispose()
            {
                var elapsed = Milliseconds(Stopwatch.GetTimestamp() - _start);
                lock (_owner._stages) _owner._stages[_name] = _owner[_name] + elapsed;
            }
        }
        public void Dispose() => Active.Value = _previous;
    }
}
