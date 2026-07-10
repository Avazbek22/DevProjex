using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevProjex.Avalonia.Services;

internal sealed class CommandLineBenchmarkRunner
{
	private const int DefaultMeasuredRuns = 7;
	private const int DefaultWarmupRuns = 1;
	private const int MinimumMeasuredRuns = 1;
	private const int MaximumMeasuredRuns = 50;
	private const int MinimumWarmupRuns = 0;
	private const int MaximumWarmupRuns = 10;
	private const string RunsEnvironmentVariable = "DEVPROJEX_BENCHMARK_RUNS";
	private const string WarmupEnvironmentVariable = "DEVPROJEX_BENCHMARK_WARMUP";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly CommandLineBenchmarkContext _context;

	public CommandLineBenchmarkRunner(CommandLineBenchmarkContext context)
	{
		_context = context;
	}

	public async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
	{
		var benchmark = options.Benchmark;
		if (!benchmark.Enabled || string.IsNullOrWhiteSpace(benchmark.Path))
		{
			WriteError("Benchmark requires --benchmark <folder>.");
			return CommandLineExitCodes.UsageError;
		}

		var targetPath = Path.GetFullPath(benchmark.Path);
		if (!Directory.Exists(targetPath))
		{
			WriteError($"Benchmark target folder was not found: {targetPath}");
			return CommandLineExitCodes.RuntimeError;
		}

		var runConfiguration = ResolveRunConfiguration();
		var createdAt = DateTimeOffset.Now;
		var outputPath = ResolveOutputPath(benchmark.OutputPath, createdAt);
		var coldRequest = BuildColdProcessRequest(targetPath);

		try
		{
			var coldRuns = await RunColdProcessBenchmarksAsync(coldRequest, runConfiguration, cancellationToken)
				.ConfigureAwait(false);
			var warmRuns = await RunWarmPipelineBenchmarksAsync(targetPath, runConfiguration, cancellationToken)
				.ConfigureAwait(false);

			var report = CommandLineBenchmarkReport.Create(
				createdAt,
				targetPath,
				_context.VersionProvider(),
				coldRequest,
				runConfiguration,
				coldRuns,
				warmRuns,
				outputPath);

			await WriteReportAsync(report, outputPath, cancellationToken).ConfigureAwait(false);
			await WriteSummaryAsync(report, cancellationToken).ConfigureAwait(false);

			return report.HasFailures ? CommandLineExitCodes.RuntimeError : CommandLineExitCodes.Success;
		}
		catch (OperationCanceledException)
		{
			WriteError("Benchmark was canceled.");
			return CommandLineExitCodes.Canceled;
		}
		catch (Exception ex)
		{
			WriteError(ex.Message);
			return CommandLineExitCodes.RuntimeError;
		}
	}

	private async Task<IReadOnlyList<CommandLineBenchmarkProcessRun>> RunColdProcessBenchmarksAsync(
		CommandLineBenchmarkProcessRequest request,
		CommandLineBenchmarkRunConfiguration configuration,
		CancellationToken cancellationToken)
	{
		var runs = new List<CommandLineBenchmarkProcessRun>(configuration.TotalRuns);
		for (var index = 0; index < configuration.TotalRuns; index++)
		{
			var isWarmup = index < configuration.Warmup;
			runs.Add(await _context.ProcessRunner
				.RunAsync(request, index + 1, isWarmup, cancellationToken)
				.ConfigureAwait(false));
		}

		return runs;
	}

	private async Task<IReadOnlyList<CommandLineBenchmarkPipelineRun>> RunWarmPipelineBenchmarksAsync(
		string targetPath,
		CommandLineBenchmarkRunConfiguration configuration,
		CancellationToken cancellationToken)
	{
		var services = _context.ServicesFactory(CommandLineOptions.Empty);
		var runs = new List<CommandLineBenchmarkPipelineRun>(configuration.TotalRuns);
		for (var index = 0; index < configuration.TotalRuns; index++)
		{
			var isWarmup = index < configuration.Warmup;
			runs.Add(await RunWarmPipelineOnceAsync(services, targetPath, index + 1, isWarmup, cancellationToken)
				.ConfigureAwait(false));
		}

		return runs;
	}

	private static async Task<CommandLineBenchmarkPipelineRun> RunWarmPipelineOnceAsync(
		AvaloniaAppServices services,
		string targetPath,
		int index,
		bool isWarmup,
		CancellationToken cancellationToken)
	{
		var process = Process.GetCurrentProcess();
		var cpuBefore = TryGetTotalProcessorTime(process);
		var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
		var gen0Before = GC.CollectionCount(0);
		var gen1Before = GC.CollectionCount(1);
		var gen2Before = GC.CollectionCount(2);
		var startedAt = DateTimeOffset.Now;
		var stopwatch = Stopwatch.StartNew();
		var stdout = string.Empty;
		string? error = null;
		var exitCode = CommandLineExitCodes.Success;

		try
		{
			var loadedProject = services.ProjectAnalysisService.Load(
				new ProjectAnalysisRequest(
					RootPath: targetPath,
					SelectedRootFolders: null,
					SelectedExtensions: null,
					SelectedIgnoreOptions: null),
				cancellationToken);
			var report = await services.ProjectAnalysisService
				.BuildReportFromTreeAsync(loadedProject, cancellationToken)
				.ConfigureAwait(false);
			using var writer = new StringWriter(CultureInfo.InvariantCulture);
			await services.ProjectAnalysisReportWriter.WriteAsync(report, writer, cancellationToken)
				.ConfigureAwait(false);
			stdout = writer.ToString();
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			exitCode = CommandLineExitCodes.RuntimeError;
			error = ex.Message;
		}
		finally
		{
			stopwatch.Stop();
		}

		var cpuAfter = TryGetTotalProcessorTime(process);
		var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
		TryRefresh(process);

		return new CommandLineBenchmarkPipelineRun(
			Index: index,
			IsWarmup: isWarmup,
			StartedAt: startedAt,
			WallMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
			CpuMilliseconds: CalculateCpuMilliseconds(cpuBefore, cpuAfter),
			WorkingSetBytes: TryReadWorkingSet(process),
			PrivateMemoryBytes: TryReadPrivateMemory(process),
			ManagedMemoryBeforeBytes: managedBefore,
			ManagedMemoryAfterBytes: managedAfter,
			ManagedMemoryDeltaBytes: managedAfter - managedBefore,
			Gen0Collections: GC.CollectionCount(0) - gen0Before,
			Gen1Collections: GC.CollectionCount(1) - gen1Before,
			Gen2Collections: GC.CollectionCount(2) - gen2Before,
			StdoutCharacters: stdout.Length,
			StdoutBytes: Encoding.UTF8.GetByteCount(stdout),
			ExitCode: exitCode,
			Error: error);
	}

	private static CommandLineBenchmarkRunConfiguration ResolveRunConfiguration()
	{
		var measuredRuns = ReadBoundedIntegerEnvironmentVariable(
			RunsEnvironmentVariable,
			DefaultMeasuredRuns,
			MinimumMeasuredRuns,
			MaximumMeasuredRuns);
		var warmupRuns = ReadBoundedIntegerEnvironmentVariable(
			WarmupEnvironmentVariable,
			DefaultWarmupRuns,
			MinimumWarmupRuns,
			MaximumWarmupRuns);
		return new CommandLineBenchmarkRunConfiguration(measuredRuns, warmupRuns);
	}

	private static int ReadBoundedIntegerEnvironmentVariable(string name, int fallback, int minimum, int maximum)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
			return fallback;

		return Math.Clamp(parsed, minimum, maximum);
	}

	private static CommandLineBenchmarkProcessRequest BuildColdProcessRequest(string targetPath)
	{
		var processPath = Environment.ProcessPath;
		var assemblyPath = typeof(CommandLineBenchmarkRunner).Assembly.Location;
		var arguments = new List<string>();
		var fileName = processPath;
		if (string.IsNullOrWhiteSpace(fileName))
		{
			fileName = "dotnet";
			arguments.Add(assemblyPath);
		}
		else if (IsDotnetHost(fileName) && !string.IsNullOrWhiteSpace(assemblyPath))
		{
			arguments.Add(assemblyPath);
		}

		arguments.Add(CommandLineOptionTokens.NoUi);
		arguments.Add(CommandLineOptionTokens.Path);
		arguments.Add(targetPath);
		arguments.Add(CommandLineOptionTokens.Report);
		arguments.Add(CommandLineOptionTokens.StandardOutputReportPath);

		return new CommandLineBenchmarkProcessRequest(
			FileName: fileName,
			Arguments: arguments.ToArray(),
			WorkingDirectory: Directory.GetCurrentDirectory(),
			CommandLine: BuildCommandLine(fileName, arguments));
	}

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

		var localAppData = _context.LocalAppDataProvider();
		if (string.IsNullOrWhiteSpace(localAppData))
			localAppData = Path.GetTempPath();

		return Path.Combine(
			localAppData,
			"DevProjex",
			"Benchmarks",
			$"benchmark-{createdAt.ToLocalTime():yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.json");
	}

	private static async Task WriteReportAsync(
		CommandLineBenchmarkReport report,
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

	private async Task WriteSummaryAsync(CommandLineBenchmarkReport report, CancellationToken cancellationToken)
	{
		var lines = new[]
		{
			"DevProjex benchmark",
			string.Empty,
			$"Target: {report.TargetPath}",
			$"Runs: {report.Configuration.Runs}",
			$"Warmup: {report.Configuration.Warmup}",
			string.Empty,
			"Cold process:",
			$"  avg wall: {FormatMilliseconds(report.ColdProcess.Summary.AvgWallMilliseconds)}",
			$"  median wall: {FormatMilliseconds(report.ColdProcess.Summary.MedianWallMilliseconds)}",
			$"  min/max: {FormatMilliseconds(report.ColdProcess.Summary.MinWallMilliseconds)} / {FormatMilliseconds(report.ColdProcess.Summary.MaxWallMilliseconds)}",
			$"  avg cpu: {FormatMilliseconds(report.ColdProcess.Summary.AvgCpuMilliseconds)}",
			$"  peak memory: {FormatMegabytes(report.ColdProcess.Summary.PeakWorkingSetBytes)}",
			string.Empty,
			"Warm pipeline:",
			$"  avg wall: {FormatMilliseconds(report.WarmPipeline.Summary.AvgWallMilliseconds)}",
			$"  median wall: {FormatMilliseconds(report.WarmPipeline.Summary.MedianWallMilliseconds)}",
			$"  min/max: {FormatMilliseconds(report.WarmPipeline.Summary.MinWallMilliseconds)} / {FormatMilliseconds(report.WarmPipeline.Summary.MaxWallMilliseconds)}",
			$"  avg cpu: {FormatMilliseconds(report.WarmPipeline.Summary.AvgCpuMilliseconds)}",
			$"  managed memory: {FormatMegabytes(report.WarmPipeline.Summary.ManagedMemoryAfterBytes)}",
			string.Empty,
			"Result:",
			$"  {report.OutputPath}"
		};

		foreach (var line in lines)
			await _context.Output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

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

	private static TimeSpan? TryGetTotalProcessorTime(Process process)
	{
		try
		{
			return process.TotalProcessorTime;
		}
		catch
		{
			return null;
		}
	}

	private static double? CalculateCpuMilliseconds(TimeSpan? before, TimeSpan? after)
	{
		if (before is null || after is null)
			return null;

		return Math.Max(0, (after.Value - before.Value).TotalMilliseconds);
	}

	private static long? TryReadWorkingSet(Process process)
	{
		try
		{
			return process.WorkingSet64;
		}
		catch
		{
			return null;
		}
	}

	private static long? TryReadPrivateMemory(Process process)
	{
		try
		{
			return process.PrivateMemorySize64;
		}
		catch
		{
			return null;
		}
	}

	private static void TryRefresh(Process process)
	{
		try
		{
			process.Refresh();
		}
		catch
		{
			// Process counters are best-effort diagnostics; the benchmark result stays useful without them.
		}
	}

	private void WriteError(string message) => _context.Error.WriteLine($"DevProjex: {message}");
}

internal sealed class DefaultCommandLineBenchmarkProcessRunner : ICommandLineBenchmarkProcessRunner
{
	private static readonly TimeSpan MemoryPollInterval = TimeSpan.FromMilliseconds(50);

	public async Task<CommandLineBenchmarkProcessRun> RunAsync(
		CommandLineBenchmarkProcessRequest request,
		int index,
		bool isWarmup,
		CancellationToken cancellationToken)
	{
		var startedAt = DateTimeOffset.Now;
		var stopwatch = Stopwatch.StartNew();
		var peakWorkingSetBytes = 0L;
		var peakPrivateMemoryBytes = 0L;

		using var process = new Process
		{
			StartInfo = BuildStartInfo(request),
			EnableRaisingEvents = false
		};

		try
		{
			if (!process.Start())
				throw new InvalidOperationException("Failed to start benchmark child process.");

			var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			while (!process.HasExited)
			{
				SampleMemory(process, ref peakWorkingSetBytes, ref peakPrivateMemoryBytes);
				await Task.Delay(MemoryPollInterval, cancellationToken).ConfigureAwait(false);
			}

			SampleMemory(process, ref peakWorkingSetBytes, ref peakPrivateMemoryBytes);
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			stopwatch.Stop();

			var stdout = await stdoutTask.ConfigureAwait(false);
			var stderr = await stderrTask.ConfigureAwait(false);
			return new CommandLineBenchmarkProcessRun(
				Index: index,
				IsWarmup: isWarmup,
				StartedAt: startedAt,
				WallMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
				CpuMilliseconds: TryGetCpuMilliseconds(process),
				PeakWorkingSetBytes: ZeroToNull(peakWorkingSetBytes),
				PeakPrivateMemoryBytes: ZeroToNull(peakPrivateMemoryBytes),
				StdoutCharacters: stdout.Length,
				StdoutBytes: Encoding.UTF8.GetByteCount(stdout),
				StderrCharacters: stderr.Length,
				ExitCode: process.ExitCode,
				Error: string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			return new CommandLineBenchmarkProcessRun(
				Index: index,
				IsWarmup: isWarmup,
				StartedAt: startedAt,
				WallMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
				CpuMilliseconds: null,
				PeakWorkingSetBytes: ZeroToNull(peakWorkingSetBytes),
				PeakPrivateMemoryBytes: ZeroToNull(peakPrivateMemoryBytes),
				StdoutCharacters: 0,
				StdoutBytes: 0,
				StderrCharacters: 0,
				ExitCode: CommandLineExitCodes.RuntimeError,
				Error: ex.Message);
		}
	}

	private static ProcessStartInfo BuildStartInfo(CommandLineBenchmarkProcessRequest request)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = request.FileName,
			WorkingDirectory = request.WorkingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			UseShellExecute = false,
			CreateNoWindow = true
		};

		foreach (var argument in request.Arguments)
			startInfo.ArgumentList.Add(argument);

		return startInfo;
	}

	private static void SampleMemory(Process process, ref long peakWorkingSetBytes, ref long peakPrivateMemoryBytes)
	{
		try
		{
			process.Refresh();
			peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.WorkingSet64);
			peakPrivateMemoryBytes = Math.Max(peakPrivateMemoryBytes, process.PrivateMemorySize64);
		}
		catch
		{
			// Native process counters can disappear right as the child exits.
		}
	}

	private static double? TryGetCpuMilliseconds(Process process)
	{
		try
		{
			return process.TotalProcessorTime.TotalMilliseconds;
		}
		catch
		{
			return null;
		}
	}

	private static long? ZeroToNull(long value) => value <= 0 ? null : value;

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Cancellation already controls the command result; cleanup is best effort.
		}
	}
}

internal interface ICommandLineBenchmarkProcessRunner
{
	Task<CommandLineBenchmarkProcessRun> RunAsync(
		CommandLineBenchmarkProcessRequest request,
		int index,
		bool isWarmup,
		CancellationToken cancellationToken);
}

internal sealed record CommandLineBenchmarkContext(
	TextWriter Output,
	TextWriter Error,
	Func<CommandLineOptions, AvaloniaAppServices> ServicesFactory,
	Func<string> VersionProvider,
	ICommandLineBenchmarkProcessRunner ProcessRunner,
	Func<string> LocalAppDataProvider);

internal sealed record CommandLineBenchmarkProcessRequest(
	string FileName,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	string CommandLine);

internal sealed record CommandLineBenchmarkRunConfiguration(
	int Runs,
	int Warmup)
{
	public int TotalRuns => Runs + Warmup;
}

internal sealed record CommandLineBenchmarkReport(
	int SchemaVersion,
	DateTimeOffset CreatedAt,
	string TargetPath,
	string ApplicationVersion,
	string OsDescription,
	string FrameworkDescription,
	string ProcessArchitecture,
	string RuntimeIdentifier,
	CommandLineBenchmarkRunConfiguration Configuration,
	CommandLineBenchmarkExecutableInfo Executable,
	CommandLineBenchmarkSection<CommandLineBenchmarkProcessRun> ColdProcess,
	CommandLineBenchmarkSection<CommandLineBenchmarkPipelineRun> WarmPipeline,
	bool HasFailures,
	string OutputPath)
{
	public static CommandLineBenchmarkReport Create(
		DateTimeOffset createdAt,
		string targetPath,
		string applicationVersion,
		CommandLineBenchmarkProcessRequest coldRequest,
		CommandLineBenchmarkRunConfiguration configuration,
		IReadOnlyList<CommandLineBenchmarkProcessRun> coldRuns,
		IReadOnlyList<CommandLineBenchmarkPipelineRun> warmRuns,
		string outputPath)
	{
		var coldMeasuredRuns = coldRuns.Where(static run => !run.IsWarmup).ToArray();
		var warmMeasuredRuns = warmRuns.Where(static run => !run.IsWarmup).ToArray();
		var hasFailures =
			coldMeasuredRuns.Any(static run => run.ExitCode != CommandLineExitCodes.Success) ||
			warmMeasuredRuns.Any(static run => run.ExitCode != CommandLineExitCodes.Success);

		return new CommandLineBenchmarkReport(
			SchemaVersion: 1,
			CreatedAt: createdAt,
			TargetPath: Path.GetFullPath(targetPath).Replace('\\', '/'),
			ApplicationVersion: applicationVersion,
			OsDescription: RuntimeInformation.OSDescription,
			FrameworkDescription: RuntimeInformation.FrameworkDescription,
			ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
			RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
			Configuration: configuration,
			Executable: new CommandLineBenchmarkExecutableInfo(
				FileName: coldRequest.FileName,
				Arguments: coldRequest.Arguments,
				WorkingDirectory: coldRequest.WorkingDirectory,
				CommandLine: coldRequest.CommandLine),
			ColdProcess: new CommandLineBenchmarkSection<CommandLineBenchmarkProcessRun>(
				Summary: CommandLineBenchmarkSummary.FromProcessRuns(coldMeasuredRuns),
				WarmupRuns: coldRuns.Where(static run => run.IsWarmup).ToArray(),
				Runs: coldMeasuredRuns),
			WarmPipeline: new CommandLineBenchmarkSection<CommandLineBenchmarkPipelineRun>(
				Summary: CommandLineBenchmarkSummary.FromPipelineRuns(warmMeasuredRuns),
				WarmupRuns: warmRuns.Where(static run => run.IsWarmup).ToArray(),
				Runs: warmMeasuredRuns),
			HasFailures: hasFailures,
			OutputPath: Path.GetFullPath(outputPath).Replace('\\', '/'));
	}
}

internal sealed record CommandLineBenchmarkExecutableInfo(
	string FileName,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	string CommandLine);

internal sealed record CommandLineBenchmarkSection<TRun>(
	CommandLineBenchmarkSummary Summary,
	IReadOnlyList<TRun> WarmupRuns,
	IReadOnlyList<TRun> Runs);

internal sealed record CommandLineBenchmarkSummary(
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
	long? ManagedMemoryAfterBytes,
	long? AvgStdoutBytes)
{
	public static CommandLineBenchmarkSummary FromProcessRuns(IReadOnlyList<CommandLineBenchmarkProcessRun> runs)
	{
		var successfulRuns = runs.Where(static run => run.ExitCode == CommandLineExitCodes.Success).ToArray();
		return Create(
			count: runs.Count,
			successfulRuns.Length,
			failedCount: runs.Count - successfulRuns.Length,
			wallMilliseconds: successfulRuns.Select(static run => run.WallMilliseconds).ToArray(),
			cpuMilliseconds: successfulRuns.Select(static run => run.CpuMilliseconds).ToArray(),
			peakWorkingSetBytes: MaxNullable(successfulRuns.Select(static run => run.PeakWorkingSetBytes)),
			peakPrivateMemoryBytes: MaxNullable(successfulRuns.Select(static run => run.PeakPrivateMemoryBytes)),
			managedMemoryAfterBytes: null,
			stdoutBytes: successfulRuns.Select(static run => (long?)run.StdoutBytes).ToArray());
	}

	public static CommandLineBenchmarkSummary FromPipelineRuns(IReadOnlyList<CommandLineBenchmarkPipelineRun> runs)
	{
		var successfulRuns = runs.Where(static run => run.ExitCode == CommandLineExitCodes.Success).ToArray();
		return Create(
			count: runs.Count,
			successfulRuns.Length,
			failedCount: runs.Count - successfulRuns.Length,
			wallMilliseconds: successfulRuns.Select(static run => run.WallMilliseconds).ToArray(),
			cpuMilliseconds: successfulRuns.Select(static run => run.CpuMilliseconds).ToArray(),
			peakWorkingSetBytes: MaxNullable(successfulRuns.Select(static run => run.WorkingSetBytes)),
			peakPrivateMemoryBytes: MaxNullable(successfulRuns.Select(static run => run.PrivateMemoryBytes)),
			managedMemoryAfterBytes: MaxNullable(successfulRuns.Select(static run => (long?)run.ManagedMemoryAfterBytes)),
			stdoutBytes: successfulRuns.Select(static run => (long?)run.StdoutBytes).ToArray());
	}

	private static CommandLineBenchmarkSummary Create(
		int count,
		int successfulCount,
		int failedCount,
		IReadOnlyList<double> wallMilliseconds,
		IReadOnlyList<double?> cpuMilliseconds,
		long? peakWorkingSetBytes,
		long? peakPrivateMemoryBytes,
		long? managedMemoryAfterBytes,
		IReadOnlyList<long?> stdoutBytes)
	{
		return new CommandLineBenchmarkSummary(
			Count: count,
			SuccessfulCount: successfulCount,
			FailedCount: failedCount,
			AvgWallMilliseconds: Average(wallMilliseconds),
			MedianWallMilliseconds: Median(wallMilliseconds),
			MinWallMilliseconds: wallMilliseconds.Count == 0 ? 0 : wallMilliseconds.Min(),
			MaxWallMilliseconds: wallMilliseconds.Count == 0 ? 0 : wallMilliseconds.Max(),
			AvgCpuMilliseconds: AverageNullable(cpuMilliseconds),
			PeakWorkingSetBytes: peakWorkingSetBytes,
			PeakPrivateMemoryBytes: peakPrivateMemoryBytes,
			ManagedMemoryAfterBytes: managedMemoryAfterBytes,
			AvgStdoutBytes: AverageNullableLong(stdoutBytes));
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

	private static long? AverageNullableLong(IEnumerable<long?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		if (actual.Length == 0)
			return null;

		return (long)Math.Round(actual.Average(), MidpointRounding.AwayFromZero);
	}

	private static long? MaxNullable(IEnumerable<long?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		return actual.Length == 0 ? null : actual.Max();
	}
}

internal sealed record CommandLineBenchmarkProcessRun(
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
	string? Error);

internal sealed record CommandLineBenchmarkPipelineRun(
	int Index,
	bool IsWarmup,
	DateTimeOffset StartedAt,
	double WallMilliseconds,
	double? CpuMilliseconds,
	long? WorkingSetBytes,
	long? PrivateMemoryBytes,
	long ManagedMemoryBeforeBytes,
	long ManagedMemoryAfterBytes,
	long ManagedMemoryDeltaBytes,
	int Gen0Collections,
	int Gen1Collections,
	int Gen2Collections,
	int StdoutCharacters,
	int StdoutBytes,
	int ExitCode,
	string? Error);
