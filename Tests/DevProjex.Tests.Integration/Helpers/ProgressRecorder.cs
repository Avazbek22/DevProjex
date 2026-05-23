namespace DevProjex.Tests.Integration.Helpers;

internal sealed class ProgressRecorder(Action<string>? onReport = null) : IProgress<string>
{
    private readonly object _gate = new();
    private readonly List<string> _reports = [];

    public IReadOnlyList<string> Reports
    {
        get
        {
            lock (_gate)
                return _reports.ToArray();
        }
    }

    public void Report(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        lock (_gate)
            _reports.Add(value);

        // Tests use this instead of Progress<T> because Progress<T> dispatches
        // callbacks asynchronously, which can leave assertions racing the callback.
        onReport?.Invoke(value);
    }
}
