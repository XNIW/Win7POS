using System;
using System.Threading.Tasks;

namespace Win7POS.Core.Hardware
{
    public sealed class DiagnosticResult<T>
    {
        public T Value { get; internal set; }
        public string ResultCode { get; internal set; }
        public bool Completed => ResultCode == "completed";
    }

    /// <summary>The worker keeps its slot after a caller times out, until the real operation ends.</summary>
    public sealed class SingleFlightDiagnostic<T>
    {
        private readonly object _sync = new object();
        private Task<T> _worker;
        private string _key;

        public async Task<DiagnosticResult<T>> RunAsync(string key, Func<T> read, TimeSpan uiTimeout)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (uiTimeout <= TimeSpan.Zero || uiTimeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(uiTimeout));
            Task<T> worker;
            lock (_sync)
            {
                if (_worker != null && !_worker.IsCompleted && !string.Equals(_key, key, StringComparison.OrdinalIgnoreCase))
                    return new DiagnosticResult<T> { ResultCode = "busy" };
                if (_worker == null || _worker.IsCompleted)
                {
                    _key = key;
                    _worker = Task.Run(read);
                    _ = _worker.ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
                worker = _worker;
            }
            if (await Task.WhenAny(worker, Task.Delay(uiTimeout)).ConfigureAwait(false) != worker)
                return new DiagnosticResult<T> { ResultCode = "timeout" };
            try { return new DiagnosticResult<T> { Value = await worker.ConfigureAwait(false), ResultCode = "completed" }; }
            catch { return new DiagnosticResult<T> { ResultCode = "unavailable" }; }
        }
    }
}
