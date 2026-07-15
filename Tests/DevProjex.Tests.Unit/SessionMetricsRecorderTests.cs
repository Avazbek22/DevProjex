using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
public sealed class SessionMetricsRecorderTests
{
	[Fact]
	public void Complete_WritesJsonReportWithPrivateTreeSearchAndFilterEvents()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "Project With Spaces")).FullName;
		var outputPath = Path.Combine(temp.Path, "reports", "session.json");
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero));
		var sampler = new FakeSessionMetricsSampler();
		var recorder = CreateRecorder(projectPath, outputPath, sampler, timeProvider);

		recorder.SetIdleStateProvider(static () => true);
		recorder.Start(projectPath, "4.9.5-test");
		timeProvider.Advance(TimeSpan.FromMilliseconds(500));
		sampler.TotalProcessorTime = TimeSpan.FromMilliseconds(125);
		sampler.WorkingSetBytes = 256 * 1024 * 1024;
		sampler.PrivateMemoryBytes = 192 * 1024 * 1024;
		sampler.ManagedMemoryBytes = 64 * 1024 * 1024;
		sampler.Gen0Collections = 2;
		recorder.CaptureSample();

		recorder.RecordTreeSearch(new TreeSearchMetrics(
			Query: "SecretService",
			Duration: TimeSpan.FromMilliseconds(12),
			TotalNodes: 42,
			MatchCount: 3,
			UsedCache: true));
		recorder.RecordTreeFilter(
			query: "SecretFilter",
			matchCount: 2,
			duration: TimeSpan.FromMilliseconds(15),
			usedInMemoryFilter: true);
		recorder.RecordClipboard("tree", TreeTextFormat.Json, payloadCharacters: 100, success: true);

		var completion = recorder.Complete();

		Assert.NotNull(completion);
		Assert.True(completion.Success, completion.ErrorMessage);
		Assert.True(File.Exists(outputPath));
		var json = File.ReadAllText(outputPath);
		Assert.DoesNotContain("SecretService", json, StringComparison.Ordinal);
		Assert.DoesNotContain("SecretFilter", json, StringComparison.Ordinal);
		Assert.Contains("\"queryLength\"", json, StringComparison.Ordinal);
		Assert.Contains("\"queryFingerprint\"", json, StringComparison.Ordinal);

		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("interactive-session", root.GetProperty("kind").GetString());
		Assert.Equal(SessionMetricsRecorder.NormalizePathForReport(projectPath), root.GetProperty("targetPath").GetString());
		Assert.Equal(SessionMetricsRecorder.NormalizePathForReport(outputPath), root.GetProperty("outputPath").GetString());

		var summary = root.GetProperty("summary");
		Assert.Equal(3, summary.GetProperty("sampleCount").GetInt32());
		Assert.Equal(4, summary.GetProperty("eventCount").GetInt32());
		Assert.True(summary.GetProperty("peakWorkingSetBytes").GetInt64() >= 256 * 1024 * 1024);
		Assert.Equal(2, summary.GetProperty("gen0Collections").GetInt32());

		var events = root.GetProperty("events").EnumerateArray().ToArray();
		Assert.Contains(events, static item => item.GetProperty("name").GetString() == "session.started");
		var search = Assert.Single(events, static item => item.GetProperty("name").GetString() == "tree.search");
		Assert.Equal(13, search.GetProperty("queryLength").GetInt32());
		Assert.Equal(42, search.GetProperty("totalNodes").GetInt32());
		Assert.Equal(3, search.GetProperty("matchCount").GetInt32());
		Assert.True(search.GetProperty("usedCache").GetBoolean());
		Assert.DoesNotContain("query", search.EnumerateObject().Select(static property => property.Name));

		var filter = Assert.Single(events, static item => item.GetProperty("name").GetString() == "tree.filter");
		Assert.Equal(12, filter.GetProperty("queryLength").GetInt32());
		Assert.True(filter.GetProperty("usedInMemoryFilter").GetBoolean());
	}

	[Fact]
	public void Summary_ComputesCpuPercentAndIdleCpuFromSamples()
	{
		using var temp = new TemporaryDirectory();
		var outputPath = Path.Combine(temp.Path, "session.json");
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero));
		var sampler = new FakeSessionMetricsSampler { ProcessorCountValue = 2 };
		var recorder = CreateRecorder(temp.Path, outputPath, sampler, timeProvider);

		var idle = true;
		recorder.SetIdleStateProvider(() => idle);
		recorder.Start(temp.Path, "test");

		timeProvider.Advance(TimeSpan.FromSeconds(1));
		sampler.TotalProcessorTime = TimeSpan.FromMilliseconds(1_000);
		sampler.WorkingSetBytes = 100;
		sampler.PrivateMemoryBytes = 80;
		sampler.ManagedMemoryBytes = 60;
		recorder.CaptureSample();

		idle = false;
		timeProvider.Advance(TimeSpan.FromSeconds(1));
		sampler.TotalProcessorTime = TimeSpan.FromMilliseconds(2_000);
		sampler.WorkingSetBytes = 120;
		sampler.PrivateMemoryBytes = 90;
		sampler.ManagedMemoryBytes = 70;
		sampler.Gen1Collections = 1;
		sampler.Gen2Collections = 1;
		recorder.CaptureSample();

		var completion = recorder.Complete();

		Assert.NotNull(completion);
		Assert.True(completion.Success, completion.ErrorMessage);
		using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
		var summary = document.RootElement.GetProperty("summary");
		Assert.Equal(4, summary.GetProperty("sampleCount").GetInt32());
		Assert.Equal(50, summary.GetProperty("peakCpuPercent").GetDouble());
		Assert.True(summary.GetProperty("averageCpuPercent").GetDouble() > 0);
		Assert.Equal(50, summary.GetProperty("peakIdleCpuPercent").GetDouble());
		Assert.Equal(120, summary.GetProperty("peakWorkingSetBytes").GetInt64());
		Assert.Equal(1, summary.GetProperty("gen1Collections").GetInt32());
		Assert.Equal(1, summary.GetProperty("gen2Collections").GetInt32());
	}

	[Fact]
	public void Complete_WithoutExplicitOutputWritesUnderLocalAppDataSessionMetricsFolder()
	{
		using var temp = new TemporaryDirectory();
		var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero));
		var recorder = new SessionMetricsRecorder(
			new StartupSessionMetricsOptions(true, temp.Path, null),
			() => temp.Path,
			new FakeSessionMetricsSampler(),
			timeProvider,
			TimeSpan.Zero);

		recorder.Start(temp.Path, "test");

		var completion = recorder.Complete();

		Assert.NotNull(completion);
		Assert.True(completion.Success, completion.ErrorMessage);
		Assert.StartsWith(
			SessionMetricsRecorder.NormalizePathForReport(Path.Combine(temp.Path, "DevProjex", "SessionMetrics")),
			completion.NormalizedOutputPath,
			StringComparison.Ordinal);
		Assert.True(File.Exists(completion.OutputPath));
	}

	[Fact]
	public void DisabledRecorder_DoesNotWriteReportOrCreateSamples()
	{
		var completion = SessionMetricsRecorder.Disabled.Complete();

		Assert.Null(completion);
	}

	private static SessionMetricsRecorder CreateRecorder(
		string projectPath,
		string outputPath,
		FakeSessionMetricsSampler sampler,
		ManualTimeProvider timeProvider)
		=> new(
			new StartupSessionMetricsOptions(true, projectPath, outputPath),
			() => Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
			sampler,
			timeProvider,
			TimeSpan.Zero);

	private sealed class FakeSessionMetricsSampler : ISessionMetricsProcessSampler
	{
		public int ProcessorCountValue { get; set; } = 1;
		public TimeSpan TotalProcessorTime { get; set; }
		public long WorkingSetBytes { get; set; }
		public long PrivateMemoryBytes { get; set; }
		public long ManagedMemoryBytes { get; set; }
		public int Gen0Collections { get; set; }
		public int Gen1Collections { get; set; }
		public int Gen2Collections { get; set; }

		public int ProcessorCount => ProcessorCountValue;

		public SessionProcessMeasurement Capture() => new(
			TotalProcessorTime,
			WorkingSetBytes,
			PrivateMemoryBytes,
			ManagedMemoryBytes,
			Gen0Collections,
			Gen1Collections,
			Gen2Collections);
	}

	private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
	{
		private DateTimeOffset _utcNow = initialUtcNow;

		public override DateTimeOffset GetUtcNow() => _utcNow;

		public void Advance(TimeSpan duration) => _utcNow += duration;
	}
}
