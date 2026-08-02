using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProjex.Avalonia.Services;

internal sealed class CommandLineUiBenchmarkRunner(CommandLineUiBenchmarkContext context)
{
	private const int DefaultMeasuredRuns = 5;
	private const int DefaultWarmupRuns = 1;
	private const int MinimumMeasuredRuns = 1;
	private const int MaximumMeasuredRuns = 20;
	private const int MinimumWarmupRuns = 0;
	private const int MaximumWarmupRuns = 5;
	private const string UiRunsEnvironmentVariable = "DEVPROJEX_UI_BENCHMARK_RUNS";
	private const string UiWarmupEnvironmentVariable = "DEVPROJEX_UI_BENCHMARK_WARMUP";
	private const string SharedRunsEnvironmentVariable = "DEVPROJEX_BENCHMARK_RUNS";
	private const string SharedWarmupEnvironmentVariable = "DEVPROJEX_BENCHMARK_WARMUP";
	private const string StandardScenarioName = "standard-ui";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	public async Task<int> RunAsync(
		string projectPath,
		string? explicitOutputPath,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(projectPath))
		{
			WriteError("UI benchmark requires a project folder.");
			return CommandLineExitCodes.UsageError;
		}

		var targetPath = Path.GetFullPath(projectPath);
		if (!Directory.Exists(targetPath))
		{
			WriteError($"UI benchmark target folder was not found: {targetPath}");
			return CommandLineExitCodes.RuntimeError;
		}

		var configuration = ResolveRunConfiguration();
		var createdAt = DateTimeOffset.Now;
		var outputPath = ResolveOutputPath(explicitOutputPath, createdAt);
		var runReportsDirectory = ResolveRunReportsDirectory(outputPath);

		try
		{
			var runs = await RunColdUiProcessBenchmarksAsync(
					targetPath,
					runReportsDirectory,
					configuration,
					cancellationToken)
				.ConfigureAwait(false);

			var firstRequest = BuildUiProcessRequest(targetPath, Path.Combine(runReportsDirectory, "session-template.json"));
			var report = CommandLineUiBenchmarkReport.Create(
				createdAt,
				targetPath,
				context.VersionProvider(),
				firstRequest,
				configuration,
				runs,
				outputPath,
				runReportsDirectory);

			await WriteReportAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
			await WriteSummaryAsync(report, cancellationToken).ConfigureAwait(false);

			return report.HasFailures ? CommandLineExitCodes.RuntimeError : CommandLineExitCodes.Success;
		}
		catch (OperationCanceledException)
		{
			WriteError("UI benchmark was canceled.");
			return CommandLineExitCodes.Canceled;
		}
		catch (Exception ex)
		{
			WriteError(ex.Message);
			return CommandLineExitCodes.RuntimeError;
		}
	}

	private async Task<IReadOnlyList<CommandLineUiBenchmarkProcessRun>> RunColdUiProcessBenchmarksAsync(
		string targetPath,
		string runReportsDirectory,
		CommandLineBenchmarkRunConfiguration configuration,
		CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(runReportsDirectory);
		var runs = new List<CommandLineUiBenchmarkProcessRun>(configuration.TotalRuns);
		for (var index = 0; index < configuration.TotalRuns; index++)
		{
			var isWarmup = index < configuration.Warmup;
			var sessionReportPath = Path.Combine(runReportsDirectory, $"session-{index + 1:000}.json");
			var request = BuildUiProcessRequest(targetPath, sessionReportPath);
			var processRun = await context.ProcessRunner
				.RunAsync(request, index + 1, isWarmup, cancellationToken)
				.ConfigureAwait(false);

			runs.Add(BuildUiBenchmarkRun(processRun, sessionReportPath));
		}

		return runs;
	}

	private static CommandLineUiBenchmarkProcessRun BuildUiBenchmarkRun(
		CommandLineBenchmarkProcessRun processRun,
		string sessionReportPath)
	{
		CommandLineUiBenchmarkSessionSnapshot? session = null;
		string? error = processRun.Error;

		if (File.Exists(sessionReportPath))
		{
			try
			{
				session = ReadSessionSnapshot(sessionReportPath);
			}
			catch (Exception ex)
			{
				error = CombineErrors(error, $"Failed to read session metrics report: {ex.Message}");
			}
		}
		else if (processRun.ExitCode == CommandLineExitCodes.Success)
		{
			error = CombineErrors(error, "Session metrics report was not created.");
		}

		return new CommandLineUiBenchmarkProcessRun(
			Index: processRun.Index,
			IsWarmup: processRun.IsWarmup,
			StartedAt: processRun.StartedAt,
			WallMilliseconds: processRun.WallMilliseconds,
			CpuMilliseconds: processRun.CpuMilliseconds,
			PeakWorkingSetBytes: processRun.PeakWorkingSetBytes,
			PeakPrivateMemoryBytes: processRun.PeakPrivateMemoryBytes,
			StdoutCharacters: processRun.StdoutCharacters,
			StdoutBytes: processRun.StdoutBytes,
			StderrCharacters: processRun.StderrCharacters,
			ExitCode: processRun.ExitCode,
			Error: error,
			SessionReportPath: NormalizePath(sessionReportPath),
			Session: session);
	}

	private static CommandLineUiBenchmarkSessionSnapshot ReadSessionSnapshot(string sessionReportPath)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(sessionReportPath, Encoding.UTF8));
		var root = document.RootElement;
		var summary = root.GetProperty("summary");
		var steps = ReadStepDurations(root);
		var retainedMemory = ReadRetainedMemory(root);

		return new CommandLineUiBenchmarkSessionSnapshot(
			TargetPath: root.GetProperty("targetPath").GetString() ?? string.Empty,
			OutputPath: root.GetProperty("outputPath").GetString() ?? NormalizePath(sessionReportPath),
			DurationMilliseconds: ReadLong(root, "durationMilliseconds"),
			SampleCount: ReadInt(summary, "sampleCount"),
			EventCount: ReadInt(summary, "eventCount"),
			DroppedSamples: ReadInt(summary, "droppedSamples"),
			DroppedEvents: ReadInt(summary, "droppedEvents"),
			AverageCpuPercent: ReadDouble(summary, "averageCpuPercent"),
			PeakCpuPercent: ReadDouble(summary, "peakCpuPercent"),
			AverageIdleCpuPercent: ReadDouble(summary, "averageIdleCpuPercent"),
			PeakIdleCpuPercent: ReadDouble(summary, "peakIdleCpuPercent"),
			PeakWorkingSetBytes: ReadLong(summary, "peakWorkingSetBytes"),
			PeakPrivateMemoryBytes: ReadLong(summary, "peakPrivateMemoryBytes"),
			PeakManagedMemoryBytes: ReadLong(summary, "peakManagedMemoryBytes"),
			Gen0Collections: ReadInt(summary, "gen0Collections"),
			Gen1Collections: ReadInt(summary, "gen1Collections"),
			Gen2Collections: ReadInt(summary, "gen2Collections"),
			ProjectLoadMilliseconds: ReadFirstEventDuration(root, "project.load"),
			PreviewOpenMilliseconds: steps.GetValueOrDefault("preview.open"),
			PreviewFirstContentMilliseconds: ReadPreviewFirstContentDelay(root),
			PreviewCloseMilliseconds: steps.GetValueOrDefault("preview.close"),
			SearchMilliseconds: steps.GetValueOrDefault("search.apply") ?? ReadFirstEventDuration(root, "tree.search"),
			FilterMilliseconds: steps.GetValueOrDefault("filter.apply") ?? ReadFirstEventDuration(root, "tree.filter"),
			IdleSettleMilliseconds: steps.GetValueOrDefault("idle.settle"),
			StepDurations: steps,
			RetainedMemory: retainedMemory);
	}

	private static Dictionary<string, double?> ReadStepDurations(JsonElement root)
	{
		var steps = new Dictionary<string, double?>(StringComparer.Ordinal);
		if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
			return steps;

		foreach (var item in events.EnumerateArray())
		{
			if (!item.TryGetProperty("name", out var name) ||
			    !string.Equals(name.GetString(), "ui.benchmark.step", StringComparison.Ordinal))
			{
				continue;
			}

			if (!item.TryGetProperty("stepName", out var stepNameElement))
				continue;

			var stepName = stepNameElement.GetString();
			if (string.IsNullOrWhiteSpace(stepName))
				continue;

			steps[stepName] = ReadNullableDouble(item, "durationMilliseconds");
		}

		return steps;
	}

	private static double? ReadFirstEventDuration(JsonElement root, string eventName)
	{
		if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var item in events.EnumerateArray())
		{
			if (!item.TryGetProperty("name", out var name) ||
			    !string.Equals(name.GetString(), eventName, StringComparison.Ordinal))
			{
				continue;
			}

			return ReadNullableDouble(item, "durationMilliseconds");
		}

		return null;
	}

	private static double? ReadPreviewFirstContentDelay(JsonElement root)
	{
		if (!root.TryGetProperty("events", out var events) ||
		    events.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		long? previewOpenedAt = null;
		foreach (var item in events.EnumerateArray())
		{
			if (!item.TryGetProperty("name", out var name))
				continue;

			var eventName = name.GetString();
			if (previewOpenedAt is null &&
			    string.Equals(eventName, "preview.mode.changed", StringComparison.Ordinal) &&
			    item.TryGetProperty("previewVisible", out var visible) &&
			    visible.ValueKind == JsonValueKind.True)
			{
				previewOpenedAt = ReadLong(item, "atMilliseconds");
				continue;
			}

			if (previewOpenedAt is not null &&
			    string.Equals(eventName, "preview.content.published", StringComparison.Ordinal))
			{
				return Math.Max(
					0,
					ReadLong(item, "atMilliseconds") - previewOpenedAt.Value);
			}
		}

		return null;
	}

	private static CommandLineUiBenchmarkRetainedMemory? ReadRetainedMemory(
		JsonElement root)
	{
		var postCloseAt = ReadBenchmarkStepCompletedAt(
			root,
			"preview.close");
		var endOfIdleAt = ReadBenchmarkStepCompletedAt(
			root,
			"idle.settle");
		if (postCloseAt is null ||
		    endOfIdleAt is null ||
		    !root.TryGetProperty("samples", out var samples) ||
		    samples.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		JsonElement? postCloseSample = null;
		JsonElement? endOfIdleSample = null;
		var postCloseSampleAt = long.MaxValue;
		var endOfIdleSampleAt = long.MinValue;

		foreach (var sample in samples.EnumerateArray())
		{
			var sampleAt = ReadLong(sample, "atMilliseconds");
			if (sampleAt >= postCloseAt.Value &&
			    sampleAt <= postCloseSampleAt)
			{
				postCloseSample = sample;
				postCloseSampleAt = sampleAt;
			}

			if (sampleAt >= endOfIdleAt.Value &&
			    sampleAt >= endOfIdleSampleAt)
			{
				endOfIdleSample = sample;
				endOfIdleSampleAt = sampleAt;
			}
		}

		var postClose = postCloseSample is null
			? null
			: ReadMemorySnapshot(postCloseSample.Value);
		var endOfIdle = endOfIdleSample is null
			? null
			: ReadMemorySnapshot(endOfIdleSample.Value);
		return new CommandLineUiBenchmarkRetainedMemory(
			PostClose: postClose,
			EndOfIdle: endOfIdle,
			Rebound: postClose is not null && endOfIdle is not null
				? CommandLineUiBenchmarkMemorySnapshot.Subtract(
					endOfIdle,
					postClose)
				: null);
	}

	private static long? ReadBenchmarkStepCompletedAt(
		JsonElement root,
		string stepName)
	{
		if (!root.TryGetProperty("events", out var events) ||
		    events.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		foreach (var item in events.EnumerateArray())
		{
			if (!item.TryGetProperty("name", out var name) ||
			    !string.Equals(
				    name.GetString(),
				    "ui.benchmark.step",
				    StringComparison.Ordinal) ||
			    !item.TryGetProperty("stepName", out var actualStepName) ||
			    !string.Equals(
				    actualStepName.GetString(),
				    stepName,
				    StringComparison.Ordinal))
			{
				continue;
			}

			return ReadLong(item, "atMilliseconds");
		}

		return null;
	}

	private static CommandLineUiBenchmarkMemorySnapshot ReadMemorySnapshot(
		JsonElement sample) =>
		new(
			WorkingSetBytes: ReadLong(sample, "workingSetBytes"),
			PrivateMemoryBytes: ReadLong(sample, "privateMemoryBytes"),
			ManagedMemoryBytes: ReadLong(sample, "managedMemoryBytes"),
			ManagedHeapSizeBytes: ReadLong(
				sample,
				"managedHeapSizeBytes"),
			ManagedFragmentedBytes: ReadLong(
				sample,
				"managedFragmentedBytes"),
			ManagedCommittedBytes: ReadLong(
				sample,
				"managedCommittedBytes"),
			MemoryLoadBytes: ReadLong(sample, "memoryLoadBytes"));

	private static CommandLineBenchmarkRunConfiguration ResolveRunConfiguration()
	{
		var measuredRuns = ReadBoundedIntegerEnvironmentVariable(
			UiRunsEnvironmentVariable,
			SharedRunsEnvironmentVariable,
			DefaultMeasuredRuns,
			MinimumMeasuredRuns,
			MaximumMeasuredRuns);
		var warmupRuns = ReadBoundedIntegerEnvironmentVariable(
			UiWarmupEnvironmentVariable,
			SharedWarmupEnvironmentVariable,
			DefaultWarmupRuns,
			MinimumWarmupRuns,
			MaximumWarmupRuns);
		return new CommandLineBenchmarkRunConfiguration(measuredRuns, warmupRuns);
	}

	private static int ReadBoundedIntegerEnvironmentVariable(
		string primaryName,
		string fallbackName,
		int fallback,
		int minimum,
		int maximum)
	{
		var value = Environment.GetEnvironmentVariable(primaryName);
		if (string.IsNullOrWhiteSpace(value))
			value = Environment.GetEnvironmentVariable(fallbackName);

		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			return fallback;

		return Math.Clamp(parsed, minimum, maximum);
	}

	private static CommandLineBenchmarkProcessRequest BuildUiProcessRequest(string targetPath, string sessionReportPath)
		=> DesktopDiagnosticProcessRequestFactory.Create(
			targetPath,
			sessionReportPath,
			"standard");

	private static bool IsDotnetHost(string processPath)
	{
		var fileName = Path.GetFileNameWithoutExtension(processPath);
		return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
	{
		var builder = new StringBuilder(QuoteCommandLineArgument(fileName));
		foreach (var argument in arguments)
		{
			builder.Append(' ');
			builder.Append(QuoteCommandLineArgument(argument));
		}

		return builder.ToString();
	}

	private static string QuoteCommandLineArgument(string value)
	{
		if (value.Length > 0 && !value.Any(static ch => char.IsWhiteSpace(ch) || ch == '"'))
			return value;

		return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private string ResolveOutputPath(string? explicitPath, DateTimeOffset createdAt)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath))
			return Path.GetFullPath(explicitPath);

		var localAppData = context.LocalAppDataProvider();
		if (string.IsNullOrWhiteSpace(localAppData))
			localAppData = Path.GetTempPath();

		return Path.Combine(
			localAppData,
			"DevProjex",
			"Benchmarks",
			$"ui-benchmark-{createdAt.ToLocalTime():yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.json");
	}

	private static string ResolveRunReportsDirectory(string outputPath)
	{
		var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
		if (string.IsNullOrWhiteSpace(directory))
			directory = Directory.GetCurrentDirectory();

		var fileName = Path.GetFileNameWithoutExtension(outputPath);
		return Path.Combine(directory, $"{fileName}-sessions");
	}

	private static async Task WriteReportAsync(
		CommandLineUiBenchmarkReport report,
		string outputPath,
		CancellationToken cancellationToken)
	{
		var directory = Path.GetDirectoryName(outputPath);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);

		await using var stream = new FileStream(
			outputPath,
			FileMode.Create,
			FileAccess.Write,
			FileShare.Read,
			bufferSize: 16 * 1024,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task WriteSummaryAsync(CommandLineUiBenchmarkReport report, CancellationToken cancellationToken)
	{
		var lines = new[]
		{
			"DevProjex UI benchmark",
			string.Empty,
			$"Target: {report.TargetPath}",
			$"Runs: {report.Configuration.Runs}",
			$"Warmup: {report.Configuration.Warmup}",
			$"Scenario: {report.Scenario}",
			string.Empty,
			"Cold UI process:",
			$"  avg wall: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgWallMilliseconds)}",
			$"  median wall: {FormatMilliseconds(report.ColdUiProcess.Summary.MedianWallMilliseconds)}",
			$"  avg project load: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgProjectLoadMilliseconds)}",
			$"  avg preview open: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgPreviewOpenMilliseconds)}",
			$"  avg preview first content: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgPreviewFirstContentMilliseconds)}",
			$"  avg search: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgSearchMilliseconds)}",
			$"  avg filter: {FormatMilliseconds(report.ColdUiProcess.Summary.AvgFilterMilliseconds)}",
			$"  peak memory: {FormatMegabytes(report.ColdUiProcess.Summary.PeakWorkingSetBytes)}",
			$"  end-of-idle retained: {FormatMegabytes(report.ColdUiProcess.Summary.AverageRetainedMemory?.EndOfIdle?.WorkingSetBytes)}",
			$"  post-close rebound: {FormatMegabytes(report.ColdUiProcess.Summary.AverageRetainedMemory?.Rebound?.WorkingSetBytes)}",
			string.Empty,
			"Result:",
			$"  {report.OutputPath}"
		};

		foreach (var line in lines)
			await context.Output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static int ReadInt(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : 0;

	private static long ReadLong(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value) ? value : 0;

	private static double ReadDouble(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) ? value : 0;

	private static double? ReadNullableDouble(JsonElement element, string propertyName) =>
		element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) ? value : null;

	private static string NormalizePath(string path) => Path.GetFullPath(path).Replace('\\', '/');

	private static string? CombineErrors(string? left, string right) =>
		string.IsNullOrWhiteSpace(left) ? right : $"{left}{Environment.NewLine}{right}";

	private static string FormatMilliseconds(double value) =>
		double.IsFinite(value)
			? $"{Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} ms"
			: "n/a";

	private static string FormatMilliseconds(double? value) =>
		value is null ? "n/a" : FormatMilliseconds(value.Value);

	private static string FormatMegabytes(long? bytes)
	{
		if (bytes is null)
			return "n/a";

		var megabytes = bytes.Value / 1024d / 1024d;
		return $"{Math.Round(megabytes, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} MB";
	}

	private void WriteError(string message) => context.Error.WriteLine($"DevProjex: {message}");
}

internal sealed record CommandLineUiBenchmarkContext(
	TextWriter Output,
	TextWriter Error,
	Func<string> VersionProvider,
	ICommandLineBenchmarkProcessRunner ProcessRunner,
	Func<string> LocalAppDataProvider);

internal sealed record CommandLineUiBenchmarkReport(
	int SchemaVersion,
	string Kind,
	DateTimeOffset CreatedAt,
	string TargetPath,
	string Scenario,
	string ApplicationVersion,
	string OsDescription,
	string FrameworkDescription,
	string ProcessArchitecture,
	string RuntimeIdentifier,
	CommandLineBenchmarkRunConfiguration Configuration,
	CommandLineBenchmarkExecutableInfo Executable,
	CommandLineUiBenchmarkSection ColdUiProcess,
	bool HasFailures,
	string OutputPath,
	string SessionReportsDirectory)
{
	public static CommandLineUiBenchmarkReport Create(
		DateTimeOffset createdAt,
		string targetPath,
		string applicationVersion,
		CommandLineBenchmarkProcessRequest request,
		CommandLineBenchmarkRunConfiguration configuration,
		IReadOnlyList<CommandLineUiBenchmarkProcessRun> runs,
		string outputPath,
		string sessionReportsDirectory)
	{
		var measuredRuns = runs.Where(static run => !run.IsWarmup).ToArray();
		var hasFailures = measuredRuns.Any(static run =>
			run.ExitCode != CommandLineExitCodes.Success ||
			run.Session is null ||
			!string.IsNullOrWhiteSpace(run.Error));

		return new CommandLineUiBenchmarkReport(
			SchemaVersion: 1,
			Kind: "ui-benchmark",
			CreatedAt: createdAt,
			TargetPath: Path.GetFullPath(targetPath).Replace('\\', '/'),
			Scenario: "standard-ui",
			ApplicationVersion: applicationVersion,
			OsDescription: RuntimeInformation.OSDescription,
			FrameworkDescription: RuntimeInformation.FrameworkDescription,
			ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
			RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
			Configuration: configuration,
			Executable: CommandLineBenchmarkExecutableInfo.Create(request),
			ColdUiProcess: new CommandLineUiBenchmarkSection(
				Summary: CommandLineUiBenchmarkSummary.FromRuns(measuredRuns),
				WarmupRuns: runs.Where(static run => run.IsWarmup).ToArray(),
				Runs: measuredRuns),
			HasFailures: hasFailures,
			OutputPath: Path.GetFullPath(outputPath).Replace('\\', '/'),
			SessionReportsDirectory: Path.GetFullPath(sessionReportsDirectory).Replace('\\', '/'));
	}
}

internal sealed record CommandLineUiBenchmarkSection(
	CommandLineUiBenchmarkSummary Summary,
	IReadOnlyList<CommandLineUiBenchmarkProcessRun> WarmupRuns,
	IReadOnlyList<CommandLineUiBenchmarkProcessRun> Runs);

internal sealed record CommandLineUiBenchmarkSummary(
	int Count,
	int SuccessfulCount,
	int FailedCount,
	double AvgWallMilliseconds,
	double MedianWallMilliseconds,
	double MinWallMilliseconds,
	double MaxWallMilliseconds,
	double? AvgCpuMilliseconds,
	long? PeakWorkingSetBytes,
	long? PeakPrivateMemoryBytes,
	long? PeakManagedMemoryBytes,
	double? AvgProjectLoadMilliseconds,
	double? AvgPreviewOpenMilliseconds,
	double? AvgPreviewFirstContentMilliseconds,
	double? AvgSearchMilliseconds,
	double? AvgFilterMilliseconds,
	double? AvgIdleSettleMilliseconds,
	double? AvgSessionCpuPercent,
	double? PeakSessionCpuPercent,
	CommandLineUiBenchmarkRetainedMemory? AverageRetainedMemory)
{
	public static CommandLineUiBenchmarkSummary FromRuns(IReadOnlyList<CommandLineUiBenchmarkProcessRun> runs)
	{
		var successfulRuns = runs
			.Where(static run => run.ExitCode == CommandLineExitCodes.Success && run.Session is not null && string.IsNullOrWhiteSpace(run.Error))
			.ToArray();
		var wallMilliseconds = successfulRuns.Select(static run => run.WallMilliseconds).ToArray();
		return new CommandLineUiBenchmarkSummary(
			Count: runs.Count,
			SuccessfulCount: successfulRuns.Length,
			FailedCount: runs.Count - successfulRuns.Length,
			AvgWallMilliseconds: Average(wallMilliseconds),
			MedianWallMilliseconds: Median(wallMilliseconds),
			MinWallMilliseconds: wallMilliseconds.Length == 0 ? 0 : wallMilliseconds.Min(),
			MaxWallMilliseconds: wallMilliseconds.Length == 0 ? 0 : wallMilliseconds.Max(),
			AvgCpuMilliseconds: AverageNullable(successfulRuns.Select(static run => run.CpuMilliseconds)),
			PeakWorkingSetBytes: MaxNullable(successfulRuns.Select(static run => run.PeakWorkingSetBytes)),
			PeakPrivateMemoryBytes: MaxNullable(successfulRuns.Select(static run => run.PeakPrivateMemoryBytes)),
			PeakManagedMemoryBytes: MaxNullable(successfulRuns.Select(static run => (long?)run.Session!.PeakManagedMemoryBytes)),
			AvgProjectLoadMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.ProjectLoadMilliseconds)),
			AvgPreviewOpenMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.PreviewOpenMilliseconds)),
			AvgPreviewFirstContentMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.PreviewFirstContentMilliseconds)),
			AvgSearchMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.SearchMilliseconds)),
			AvgFilterMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.FilterMilliseconds)),
			AvgIdleSettleMilliseconds: AverageNullable(successfulRuns.Select(static run => run.Session!.IdleSettleMilliseconds)),
			AvgSessionCpuPercent: AverageNullable(successfulRuns.Select(static run => (double?)run.Session!.AverageCpuPercent)),
			PeakSessionCpuPercent: MaxNullableDouble(successfulRuns.Select(static run => (double?)run.Session!.PeakCpuPercent)),
			AverageRetainedMemory: CommandLineUiBenchmarkRetainedMemory.Average(
				successfulRuns
					.Select(static run => run.Session!.RetainedMemory)
					.Where(static retained => retained is not null)
					.Select(static retained => retained!)
					.ToArray()));
	}

	private static double Average(IReadOnlyList<double> values) =>
		values.Count == 0 ? 0 : values.Average();

	private static double Median(IReadOnlyList<double> values)
	{
		if (values.Count == 0)
			return 0;

		var sorted = values.OrderBy(static value => value).ToArray();
		var middle = sorted.Length / 2;
		return sorted.Length % 2 == 0
			? (sorted[middle - 1] + sorted[middle]) / 2d
			: sorted[middle];
	}

	private static double? AverageNullable(IEnumerable<double?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		return actual.Length == 0 ? null : actual.Average();
	}

	private static long? MaxNullable(IEnumerable<long?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		return actual.Length == 0 ? null : actual.Max();
	}

	private static double? MaxNullableDouble(IEnumerable<double?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		return actual.Length == 0 ? null : actual.Max();
	}
}

internal sealed record CommandLineUiBenchmarkProcessRun(
	int Index,
	bool IsWarmup,
	DateTimeOffset StartedAt,
	double WallMilliseconds,
	double? CpuMilliseconds,
	long? PeakWorkingSetBytes,
	long? PeakPrivateMemoryBytes,
	int StdoutCharacters,
	int StdoutBytes,
	int StderrCharacters,
	int ExitCode,
	string? Error,
	string SessionReportPath,
	CommandLineUiBenchmarkSessionSnapshot? Session);

internal sealed record CommandLineUiBenchmarkSessionSnapshot(
	string TargetPath,
	string OutputPath,
	long DurationMilliseconds,
	int SampleCount,
	int EventCount,
	int DroppedSamples,
	int DroppedEvents,
	double AverageCpuPercent,
	double PeakCpuPercent,
	double AverageIdleCpuPercent,
	double PeakIdleCpuPercent,
	long PeakWorkingSetBytes,
	long PeakPrivateMemoryBytes,
	long PeakManagedMemoryBytes,
	int Gen0Collections,
	int Gen1Collections,
	int Gen2Collections,
	double? ProjectLoadMilliseconds,
	double? PreviewOpenMilliseconds,
	double? PreviewFirstContentMilliseconds,
	double? PreviewCloseMilliseconds,
	double? SearchMilliseconds,
	double? FilterMilliseconds,
	double? IdleSettleMilliseconds,
	IReadOnlyDictionary<string, double?> StepDurations,
	CommandLineUiBenchmarkRetainedMemory? RetainedMemory);

internal sealed record CommandLineUiBenchmarkRetainedMemory(
	CommandLineUiBenchmarkMemorySnapshot? PostClose,
	CommandLineUiBenchmarkMemorySnapshot? EndOfIdle,
	CommandLineUiBenchmarkMemorySnapshot? Rebound)
{
	public static CommandLineUiBenchmarkRetainedMemory? Average(
		IReadOnlyList<CommandLineUiBenchmarkRetainedMemory> values)
	{
		if (values.Count == 0)
			return null;

		return new CommandLineUiBenchmarkRetainedMemory(
			PostClose: CommandLineUiBenchmarkMemorySnapshot.Average(
				values
					.Select(static value => value.PostClose)
					.Where(static value => value is not null)
					.Select(static value => value!)
					.ToArray()),
			EndOfIdle: CommandLineUiBenchmarkMemorySnapshot.Average(
				values
					.Select(static value => value.EndOfIdle)
					.Where(static value => value is not null)
					.Select(static value => value!)
					.ToArray()),
			Rebound: CommandLineUiBenchmarkMemorySnapshot.Average(
				values
					.Select(static value => value.Rebound)
					.Where(static value => value is not null)
					.Select(static value => value!)
					.ToArray()));
	}
}

internal sealed record CommandLineUiBenchmarkMemorySnapshot(
	long WorkingSetBytes,
	long PrivateMemoryBytes,
	long ManagedMemoryBytes,
	long ManagedHeapSizeBytes,
	long ManagedFragmentedBytes,
	long ManagedCommittedBytes,
	long MemoryLoadBytes)
{
	public static CommandLineUiBenchmarkMemorySnapshot Subtract(
		CommandLineUiBenchmarkMemorySnapshot left,
		CommandLineUiBenchmarkMemorySnapshot right) =>
		new(
			WorkingSetBytes:
				left.WorkingSetBytes - right.WorkingSetBytes,
			PrivateMemoryBytes:
				left.PrivateMemoryBytes - right.PrivateMemoryBytes,
			ManagedMemoryBytes:
				left.ManagedMemoryBytes - right.ManagedMemoryBytes,
			ManagedHeapSizeBytes:
				left.ManagedHeapSizeBytes - right.ManagedHeapSizeBytes,
			ManagedFragmentedBytes:
				left.ManagedFragmentedBytes - right.ManagedFragmentedBytes,
			ManagedCommittedBytes:
				left.ManagedCommittedBytes - right.ManagedCommittedBytes,
			MemoryLoadBytes:
				left.MemoryLoadBytes - right.MemoryLoadBytes);

	public static CommandLineUiBenchmarkMemorySnapshot? Average(
		IReadOnlyList<CommandLineUiBenchmarkMemorySnapshot> values)
	{
		if (values.Count == 0)
			return null;

		return new CommandLineUiBenchmarkMemorySnapshot(
			WorkingSetBytes: Average(
				values.Select(static value => value.WorkingSetBytes)),
			PrivateMemoryBytes: Average(
				values.Select(static value => value.PrivateMemoryBytes)),
			ManagedMemoryBytes: Average(
				values.Select(static value => value.ManagedMemoryBytes)),
			ManagedHeapSizeBytes: Average(
				values.Select(static value => value.ManagedHeapSizeBytes)),
			ManagedFragmentedBytes: Average(
				values.Select(static value => value.ManagedFragmentedBytes)),
			ManagedCommittedBytes: Average(
				values.Select(static value => value.ManagedCommittedBytes)),
			MemoryLoadBytes: Average(
				values.Select(static value => value.MemoryLoadBytes)));
	}

	private static long Average(IEnumerable<long> values)
	{
		var actual = values.ToArray();
		return (long)Math.Round(
			actual.Average(),
			MidpointRounding.AwayFromZero);
	}
}
