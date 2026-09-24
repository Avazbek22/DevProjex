using DevProjex.Infrastructure.LiveContext;
using System.Security;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private static readonly TimeSpan LiveSessionPollingInterval = TimeSpan.FromSeconds(5);
    private readonly LiveSessionRegistry _liveSessionRegistry;
    private IReadOnlyList<LiveSessionRecord> _liveSessions = [];
    private FileSystemWatcher? _liveSessionWatcher;
    private DispatcherTimer? _liveSessionPollingTimer;

    private void StartLiveSessionObservation()
    {
        RefreshLiveSessionSnapshot();
        try
        {
            Directory.CreateDirectory(_liveSessionRegistry.DirectoryPath);
            _liveSessionWatcher = new FileSystemWatcher(
                _liveSessionRegistry.DirectoryPath,
                "*.json")
            {
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            _liveSessionWatcher.Created += OnLiveSessionFilesChanged;
            _liveSessionWatcher.Changed += OnLiveSessionFilesChanged;
            _liveSessionWatcher.Deleted += OnLiveSessionFilesChanged;
            _liveSessionWatcher.Renamed += OnLiveSessionFilesRenamed;
            _liveSessionWatcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            Trace.TraceWarning(
                "Live context session directory could not be watched: {0}",
                exception.GetType().Name);
            _liveSessionWatcher?.Dispose();
            _liveSessionWatcher = null;
        }

        _liveSessionPollingTimer = new DispatcherTimer
        {
            Interval = LiveSessionPollingInterval
        };
        _liveSessionPollingTimer.Tick += OnLiveSessionPollingTick;
        _liveSessionPollingTimer.Start();
    }

    private void StopLiveSessionObservation()
    {
        if (_liveSessionPollingTimer is not null)
        {
            _liveSessionPollingTimer.Stop();
            _liveSessionPollingTimer.Tick -= OnLiveSessionPollingTick;
            _liveSessionPollingTimer = null;
        }

        if (_liveSessionWatcher is null)
            return;
        _liveSessionWatcher.EnableRaisingEvents = false;
        _liveSessionWatcher.Created -= OnLiveSessionFilesChanged;
        _liveSessionWatcher.Changed -= OnLiveSessionFilesChanged;
        _liveSessionWatcher.Deleted -= OnLiveSessionFilesChanged;
        _liveSessionWatcher.Renamed -= OnLiveSessionFilesRenamed;
        _liveSessionWatcher.Dispose();
        _liveSessionWatcher = null;
    }

    private void OnLiveSessionFilesChanged(object sender, FileSystemEventArgs e) =>
        ScheduleLiveSessionRefresh();

    private void OnLiveSessionFilesRenamed(object sender, RenamedEventArgs e) =>
        ScheduleLiveSessionRefresh();

    private void OnLiveSessionPollingTick(object? sender, EventArgs e)
    {
        RefreshLiveSessionPresentation();
    }

    private void ScheduleLiveSessionRefresh()
    {
        Dispatcher.UIThread.Post(
            RefreshLiveSessionPresentation,
            DispatcherPriority.Background);
    }

    private void RefreshLiveSessionPresentation()
    {
        if (RefreshLiveSessionSnapshot())
            ApplyWindowTitle();
        if (_viewModel.IsAgentActivityEnabled)
            RefreshAgentActivityPresentation();
    }

    private bool RefreshLiveSessionSnapshot()
    {
        var sessions = string.IsNullOrWhiteSpace(_currentPath)
            ? Array.Empty<LiveSessionRecord>()
            : _liveSessionRegistry.ReadActive(_currentPath)
                .Where(static session => session.Mode == AgentJournalMode.Live)
                .ToArray();
        if (HaveSameLiveSessions(_liveSessions, sessions))
            return false;

        _liveSessions = sessions;
        return true;
    }

    private static bool HaveSameLiveSessions(
        IReadOnlyList<LiveSessionRecord> current,
        IReadOnlyList<LiveSessionRecord> next)
    {
        if (current.Count != next.Count)
            return false;
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index].Pid != next[index].Pid ||
                current[index].ProcessStartUtc != next[index].ProcessStartUtc ||
                !StringComparer.Ordinal.Equals(current[index].ClientName, next[index].ClientName) ||
                !StringComparer.Ordinal.Equals(current[index].ClientVersion, next[index].ClientVersion))
            {
                return false;
            }
        }

        return true;
    }
}
