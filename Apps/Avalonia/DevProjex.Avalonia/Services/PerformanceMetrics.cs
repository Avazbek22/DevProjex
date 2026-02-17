using System.Diagnostics;

namespace DevProjex.Avalonia.Services;

/// <summary>
/// Lightweight performance metrics for key operations.
/// Uses Stopwatch for timing without external dependencies.
/// </summary>
public static class PerformanceMetrics
{
    private static readonly bool IsEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// Records timing for an operation using Stopwatch.
    /// Returns a disposable that logs the elapsed time on dispose.
    /// </summary>
    public static IDisposable Measure(string operationName)
    {
        return IsEnabled ? new OperationTimer(operationName) : NoOpTimer.Instance;
    }

    private sealed class OperationTimer : IDisposable
    {
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;

        public OperationTimer(string operationName)
        {
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            var elapsed = _stopwatch.ElapsedMilliseconds;

            // Only log operations that take significant time (>10ms)
            if (elapsed > 10)
            {
                Debug.WriteLine($"[PERF] {_operationName}: {elapsed}ms");
            }
        }
    }

    private sealed class NoOpTimer : IDisposable
    {
        public static readonly NoOpTimer Instance = new();
        private NoOpTimer() { }
        public void Dispose() { }
    }
}
