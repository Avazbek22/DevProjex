using DevProjex.Avalonia.Coordinators;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProjex.Avalonia.Services;

public sealed class SessionMetricsRecorder : ITreeSearchMetricsSink, IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaxSamples = 21_600; // six hours at the default one-second cadence
    private const int MaxEvents = 50_000;
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly StartupSessionMetricsOptions _options;
    private readonly Func<string> _localAppDataProvider;
    private readonly ISessionMetricsProcessSampler _sampler;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _sampleInterval;
    private readonly List<SessionMetricsSample> _samples = [];
    private readonly List<SessionMetricsEvent> _events = [];
    private readonly byte[] _queryFingerprintSalt = RandomNumberGenerator.GetBytes(16);
    private CancellationTokenSource? _samplingCts;
    private Task? _samplingTask;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _endedAt;
    private string? _targetPath;
    private string? _outputPath;
    private SessionProcessMeasurement? _previousMeasurement;
    private DateTimeOffset? _previousMeasurementAt;
    private Func<bool>? _isIdleProvider;
    private int _droppedSamples;
    private int _droppedEvents;
    private bool _started;
    private bool _completed;
    private bool _disposed;

    public SessionMetricsRecorder(
        StartupSessionMetricsOptions options,
        Func<string> localAppDataProvider)
        : this(
            options,
            localAppDataProvider,
            new CurrentProcessSessionMetricsSampler(),
            TimeProvider.System,
            DefaultSampleInterval)
    {
    }

    internal SessionMetricsRecorder(
        StartupSessionMetricsOptions options,
        Func<string> localAppDataProvider,
        ISessionMetricsProcessSampler sampler,
        TimeProvider timeProvider,
        TimeSpan sampleInterval)
    {
        _options = options;
        _localAppDataProvider = localAppDataProvider;
        _sampler = sampler;
        _timeProvider = timeProvider;
        _sampleInterval = sampleInterval;
    }

    public static SessionMetricsRecorder Disabled { get; } = new(
        StartupSessionMetricsOptions.Disabled,
        static () => string.Empty,
        new NoopSessionMetricsProcessSampler(),
        TimeProvider.System,
        TimeSpan.Zero);

    public bool IsEnabled => _options.Enabled;

    public void SetIdleStateProvider(Func<bool> isIdleProvider)
    {
        if (!IsEnabled)
            return;

        _isIdleProvider = isIdleProvider;
    }

    public void Start(string? targetPath, string applicationVersion)
    {
        if (!IsEnabled)
            return;

        lock (_sync)
        {
            if (_started || _completed)
                return;

            _started = true;
            _startedAt = _timeProvider.GetUtcNow();
            _targetPath = NormalizePathForReport(targetPath ?? _options.Path ?? string.Empty);
            _outputPath = ResolveOutputPath(_options.OutputPath);
        }

        CaptureSample();
        RecordEvent(new SessionMetricsEvent
        {
            Name = "session.started",
            AtMilliseconds = GetElapsedMilliseconds(),
            ApplicationVersion = applicationVersion
        });

        if (_sampleInterval > TimeSpan.Zero)
        {
            _samplingCts = new CancellationTokenSource();
            _samplingTask = RunSamplingLoopAsync(_samplingCts.Token);
        }
    }

    public void RecordProjectLoad(TimeSpan duration, bool success, string? errorCode = null)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "project.load",
            AtMilliseconds = GetElapsedMilliseconds(),
            DurationMilliseconds = RoundMilliseconds(duration),
            Success = success,
            ErrorCode = errorCode
        });
    }

    public void RecordTreeSearch(TreeSearchMetrics metrics)
    {
        var query = CreatePrivateQuerySnapshot(metrics.Query);
        RecordEvent(new SessionMetricsEvent
        {
            Name = "tree.search",
            AtMilliseconds = GetElapsedMilliseconds(),
            DurationMilliseconds = RoundMilliseconds(metrics.Duration),
            QueryLength = query.Length,
            QueryFingerprint = query.Fingerprint,
            TotalNodes = metrics.TotalNodes,
            MatchCount = metrics.MatchCount,
            UsedCache = metrics.UsedCache
        });
    }

    public void RecordTreeSearchNavigation(int step, int currentIndex, int matchCount)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "tree.search.navigate",
            AtMilliseconds = GetElapsedMilliseconds(),
            Step = step,
            CurrentIndex = currentIndex,
            MatchCount = matchCount
        });
    }

    public void RecordTreeFilter(string? query, int matchCount, TimeSpan duration, bool usedInMemoryFilter)
    {
        var privateQuery = CreatePrivateQuerySnapshot(query);
        RecordEvent(new SessionMetricsEvent
        {
            Name = "tree.filter",
            AtMilliseconds = GetElapsedMilliseconds(),
            DurationMilliseconds = RoundMilliseconds(duration),
            QueryLength = privateQuery.Length,
            QueryFingerprint = privateQuery.Fingerprint,
            MatchCount = matchCount,
            UsedInMemoryFilter = usedInMemoryFilter
        });
    }

    public void RecordTreeFormatChanged(TreeTextFormat format)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "tree.format.changed",
            AtMilliseconds = GetElapsedMilliseconds(),
            TreeFormat = FormatTreeTextFormat(format)
        });
    }

    public void RecordPreviewModeChanged(PreviewContentMode mode, bool previewVisible)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "preview.mode.changed",
            AtMilliseconds = GetElapsedMilliseconds(),
            PreviewMode = mode.ToString(),
            PreviewVisible = previewVisible
        });
    }

    public void RecordClipboard(string kind, TreeTextFormat? format, int payloadCharacters, bool success)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "clipboard.copy",
            AtMilliseconds = GetElapsedMilliseconds(),
            OperationKind = kind,
            TreeFormat = format is null ? null : FormatTreeTextFormat(format.Value),
            PayloadCharacters = payloadCharacters,
            Success = success
        });
    }

    public void RecordFileExport(string kind, TreeTextFormat? format, int payloadCharacters, bool success)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "file.export",
            AtMilliseconds = GetElapsedMilliseconds(),
            OperationKind = kind,
            TreeFormat = format is null ? null : FormatTreeTextFormat(format.Value),
            PayloadCharacters = payloadCharacters,
            Success = success
        });
    }

    internal void RecordUiBenchmarkStep(string stepName, TimeSpan duration, bool success, string? errorCode = null)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "ui.benchmark.step",
            AtMilliseconds = GetElapsedMilliseconds(),
            StepName = stepName,
            DurationMilliseconds = RoundMilliseconds(duration),
            Success = success,
            ErrorCode = errorCode
        });
    }

    internal void RecordMemoryCleanupScheduled(MemoryCleanupReason reason)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "memory.cleanup.scheduled",
            AtMilliseconds = GetElapsedMilliseconds(),
            CleanupReason = reason.ToString()
        });
    }

    internal void RecordMemoryCleanupCompleted(MemoryCleanupReason reason, TimeSpan duration)
    {
        RecordEvent(new SessionMetricsEvent
        {
            Name = "memory.cleanup.completed",
            AtMilliseconds = GetElapsedMilliseconds(),
            CleanupReason = reason.ToString(),
            DurationMilliseconds = RoundMilliseconds(duration)
        });
    }

    internal SessionMetricsCompletion? Complete()
    {
        if (!IsEnabled)
            return null;

        StopSampling();
        CaptureSample();

        SessionMetricsReport report;
        string outputPath;
        lock (_sync)
        {
            if (_completed)
                return null;

            _completed = true;
            _endedAt = _timeProvider.GetUtcNow();
            outputPath = _outputPath ?? ResolveOutputPath(null);
            report = BuildReport(outputPath);
        }

        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
            return new SessionMetricsCompletion(true, outputPath, NormalizePathForReport(outputPath), null);
        }
        catch (Exception ex)
        {
            return new SessionMetricsCompletion(false, outputPath, NormalizePathForReport(outputPath), ex.Message);
        }
    }

    internal void CaptureSample()
    {
        if (!IsEnabled || !_started)
            return;

        try
        {
            var capturedAt = _timeProvider.GetUtcNow();
            var measurement = _sampler.Capture();
            SessionMetricsSample sample;

            lock (_sync)
            {
                var cpuPercent = CalculateCpuPercent(measurement, capturedAt);
                sample = new SessionMetricsSample
                {
                    AtMilliseconds = GetElapsedMilliseconds(capturedAt),
                    CpuPercent = cpuPercent,
                    WorkingSetBytes = measurement.WorkingSetBytes,
                    PrivateMemoryBytes = measurement.PrivateMemoryBytes,
                    ManagedMemoryBytes = measurement.ManagedMemoryBytes,
                    Gen0Collections = measurement.Gen0Collections,
                    Gen1Collections = measurement.Gen1Collections,
                    Gen2Collections = measurement.Gen2Collections,
                    IsIdle = _isIdleProvider?.Invoke() == true
                };

                _previousMeasurement = measurement;
                _previousMeasurementAt = capturedAt;

                if (_samples.Count >= MaxSamples)
                {
                    _droppedSamples++;
                    return;
                }

                _samples.Add(sample);
            }
        }
        catch
        {
            // Session metrics must never break the app. A skipped sample is better than a UI crash.
        }
    }

    internal SessionMetricsReport BuildCurrentReportForTests(string outputPath)
    {
        lock (_sync)
            return BuildReport(outputPath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopSampling();

        if (_sampler is IDisposable disposable)
            disposable.Dispose();
    }

    private async Task RunSamplingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_sampleInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                CaptureSample();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopSampling()
    {
        var cts = Interlocked.Exchange(ref _samplingCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var task = Interlocked.Exchange(ref _samplingTask, null);
        if (task is not null)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private void RecordEvent(SessionMetricsEvent metricEvent)
    {
        if (!IsEnabled || !_started)
            return;

        lock (_sync)
        {
            if (_completed)
                return;

            if (_events.Count >= MaxEvents)
            {
                _droppedEvents++;
                return;
            }

            _events.Add(metricEvent);
        }
    }

    private double CalculateCpuPercent(SessionProcessMeasurement measurement, DateTimeOffset capturedAt)
    {
        if (_previousMeasurement is null || _previousMeasurementAt is null)
            return 0;

        var elapsed = capturedAt - _previousMeasurementAt.Value;
        if (elapsed <= TimeSpan.Zero)
            return 0;

        var cpuDelta = measurement.TotalProcessorTime - _previousMeasurement.TotalProcessorTime;
        var percent = cpuDelta.TotalMilliseconds / elapsed.TotalMilliseconds / Math.Max(1, _sampler.ProcessorCount) * 100.0;
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0)
            return 0;

        return Math.Round(Math.Min(100.0, percent), 2);
    }

    private SessionMetricsReport BuildReport(string outputPath)
    {
        var endedAt = _endedAt ?? _timeProvider.GetUtcNow();
        var samples = _samples.ToArray();
        var events = _events.ToArray();
        return new SessionMetricsReport
        {
            SchemaVersion = SchemaVersion,
            Kind = "interactive-session",
            TargetPath = _targetPath ?? NormalizePathForReport(_options.Path ?? string.Empty),
            OutputPath = NormalizePathForReport(outputPath),
            StartedAt = _startedAt,
            EndedAt = endedAt,
            DurationMilliseconds = Math.Max(0, GetElapsedMilliseconds(endedAt)),
            Summary = SessionMetricsSummary.From(samples, events.Length, _droppedSamples, _droppedEvents),
            Samples = samples,
            Events = events
        };
    }

    private PrivateQuerySnapshot CreatePrivateQuerySnapshot(string? query)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0)
            return new PrivateQuerySnapshot(0, null);

        var queryBytes = Encoding.UTF8.GetBytes(normalizedQuery);
        var salted = new byte[_queryFingerprintSalt.Length + queryBytes.Length];
        Buffer.BlockCopy(_queryFingerprintSalt, 0, salted, 0, _queryFingerprintSalt.Length);
        Buffer.BlockCopy(queryBytes, 0, salted, _queryFingerprintSalt.Length, queryBytes.Length);
        var hash = SHA256.HashData(salted);
        return new PrivateQuerySnapshot(normalizedQuery.Length, Convert.ToHexString(hash, 0, 8).ToLowerInvariant());
    }

    private long GetElapsedMilliseconds()
        => GetElapsedMilliseconds(_timeProvider.GetUtcNow());

    private long GetElapsedMilliseconds(DateTimeOffset instant)
        => Math.Max(0, (long)Math.Round((instant - _startedAt).TotalMilliseconds));

    private string ResolveOutputPath(string? outputPath)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
            return Path.GetFullPath(outputPath);

        var appData = ResolveLocalAppDataRoot();
        var fileName = $"session-{DateTimeOffset.Now:yyyy-MM-dd-HH-mm-ss-fff}.json";
        return Path.Combine(appData, "DevProjex", "SessionMetrics", fileName);
    }

    private string ResolveLocalAppDataRoot()
    {
        try
        {
            var provided = _localAppDataProvider();
            if (!string.IsNullOrWhiteSpace(provided))
                return provided;
        }
        catch
        {
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(fallback) ? Path.GetTempPath() : fallback;
    }

    internal static string NormalizePathForReport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static double RoundMilliseconds(TimeSpan duration)
        => Math.Round(Math.Max(0, duration.TotalMilliseconds), 2);

    private static string FormatTreeTextFormat(TreeTextFormat format) => format switch
    {
        TreeTextFormat.Json => "json",
        TreeTextFormat.Xml => "xml",
        TreeTextFormat.Markdown => "md",
        _ => "ascii"
    };
}

internal interface ISessionMetricsProcessSampler
{
    int ProcessorCount { get; }

    SessionProcessMeasurement Capture();
}

internal sealed class CurrentProcessSessionMetricsSampler : ISessionMetricsProcessSampler, IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();

    public int ProcessorCount => Environment.ProcessorCount;

    public SessionProcessMeasurement Capture()
    {
        _process.Refresh();
        return new SessionProcessMeasurement(
            TotalProcessorTime: _process.TotalProcessorTime,
            WorkingSetBytes: _process.WorkingSet64,
            PrivateMemoryBytes: _process.PrivateMemorySize64,
            ManagedMemoryBytes: GC.GetTotalMemory(forceFullCollection: false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2));
    }

    public void Dispose() => _process.Dispose();
}

internal sealed class NoopSessionMetricsProcessSampler : ISessionMetricsProcessSampler
{
    public int ProcessorCount => 1;

    public SessionProcessMeasurement Capture() => new(
        TotalProcessorTime: TimeSpan.Zero,
        WorkingSetBytes: 0,
        PrivateMemoryBytes: 0,
        ManagedMemoryBytes: 0,
        Gen0Collections: 0,
        Gen1Collections: 0,
        Gen2Collections: 0);
}

internal sealed record SessionProcessMeasurement(
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedMemoryBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

internal sealed record SessionMetricsCompletion(
    bool Success,
    string OutputPath,
    string NormalizedOutputPath,
    string? ErrorMessage);

internal sealed class SessionMetricsReport
{
    public int SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string TargetPath { get; init; }
    public required string OutputPath { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public long DurationMilliseconds { get; init; }
    public required SessionMetricsSummary Summary { get; init; }
    public required IReadOnlyList<SessionMetricsSample> Samples { get; init; }
    public required IReadOnlyList<SessionMetricsEvent> Events { get; init; }
}

internal sealed class SessionMetricsSummary
{
    public int SampleCount { get; init; }
    public int EventCount { get; init; }
    public int DroppedSamples { get; init; }
    public int DroppedEvents { get; init; }
    public double AverageCpuPercent { get; init; }
    public double PeakCpuPercent { get; init; }
    public double AverageIdleCpuPercent { get; init; }
    public double PeakIdleCpuPercent { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public long PeakPrivateMemoryBytes { get; init; }
    public long PeakManagedMemoryBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }

    public static SessionMetricsSummary From(
        IReadOnlyList<SessionMetricsSample> samples,
        int eventCount,
        int droppedSamples,
        int droppedEvents)
    {
        if (samples.Count == 0)
        {
            return new SessionMetricsSummary
            {
                EventCount = eventCount,
                DroppedSamples = droppedSamples,
                DroppedEvents = droppedEvents
            };
        }

        var cpuTotal = 0.0;
        var peakCpu = 0.0;
        var idleCpuTotal = 0.0;
        var idlePeakCpu = 0.0;
        var idleSamples = 0;
        var peakWorkingSet = 0L;
        var peakPrivateMemory = 0L;
        var peakManagedMemory = 0L;

        foreach (var sample in samples)
        {
            cpuTotal += sample.CpuPercent;
            peakCpu = Math.Max(peakCpu, sample.CpuPercent);
            peakWorkingSet = Math.Max(peakWorkingSet, sample.WorkingSetBytes);
            peakPrivateMemory = Math.Max(peakPrivateMemory, sample.PrivateMemoryBytes);
            peakManagedMemory = Math.Max(peakManagedMemory, sample.ManagedMemoryBytes);

            if (!sample.IsIdle)
                continue;

            idleSamples++;
            idleCpuTotal += sample.CpuPercent;
            idlePeakCpu = Math.Max(idlePeakCpu, sample.CpuPercent);
        }

        var first = samples[0];
        var last = samples[^1];
        return new SessionMetricsSummary
        {
            SampleCount = samples.Count,
            EventCount = eventCount,
            DroppedSamples = droppedSamples,
            DroppedEvents = droppedEvents,
            AverageCpuPercent = Math.Round(cpuTotal / samples.Count, 2),
            PeakCpuPercent = Math.Round(peakCpu, 2),
            AverageIdleCpuPercent = idleSamples == 0 ? 0 : Math.Round(idleCpuTotal / idleSamples, 2),
            PeakIdleCpuPercent = Math.Round(idlePeakCpu, 2),
            PeakWorkingSetBytes = peakWorkingSet,
            PeakPrivateMemoryBytes = peakPrivateMemory,
            PeakManagedMemoryBytes = peakManagedMemory,
            Gen0Collections = Math.Max(0, last.Gen0Collections - first.Gen0Collections),
            Gen1Collections = Math.Max(0, last.Gen1Collections - first.Gen1Collections),
            Gen2Collections = Math.Max(0, last.Gen2Collections - first.Gen2Collections)
        };
    }
}

internal sealed class SessionMetricsSample
{
    public long AtMilliseconds { get; init; }
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public long ManagedMemoryBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public bool IsIdle { get; init; }
}

internal sealed class SessionMetricsEvent
{
    public required string Name { get; init; }
    public long AtMilliseconds { get; init; }
    public string? ApplicationVersion { get; init; }
    public double? DurationMilliseconds { get; init; }
    public int? QueryLength { get; init; }
    public string? QueryFingerprint { get; init; }
    public int? TotalNodes { get; init; }
    public int? MatchCount { get; init; }
    public int? Step { get; init; }
    public int? CurrentIndex { get; init; }
    public bool? UsedCache { get; init; }
    public bool? UsedInMemoryFilter { get; init; }
    public string? TreeFormat { get; init; }
    public string? PreviewMode { get; init; }
    public bool? PreviewVisible { get; init; }
    public string? OperationKind { get; init; }
    public string? StepName { get; init; }
    public int? PayloadCharacters { get; init; }
    public bool? Success { get; init; }
    public string? CleanupReason { get; init; }
    public string? ErrorCode { get; init; }
}

internal readonly record struct PrivateQuerySnapshot(int Length, string? Fingerprint);
