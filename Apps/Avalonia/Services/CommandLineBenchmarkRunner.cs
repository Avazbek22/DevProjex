using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.Reports;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;

namespace DevProjex.Avalonia.Services;

internal sealed class CommandLineBenchmarkRunner(CommandLineBenchmarkContext context)
{
	private const int DefaultMeasuredRuns = 7;
	// The first pass discovers the full topology; the second lets bounded caches converge.
	private const int DefaultWarmupRuns = 2;
	private const int DiagnosticProbeRuns = 2;
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

	public async Task<int> RunAsync(
		string projectPath,
		string? explicitOutputPath,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(projectPath))
		{
			WriteError("Analysis benchmark requires a project folder.");
			return CommandLineExitCodes.UsageError;
		}

		var targetPath = Path.GetFullPath(projectPath);
		if (!Directory.Exists(targetPath))
		{
			WriteError($"Benchmark target folder was not found: {targetPath}");
			return CommandLineExitCodes.RuntimeError;
		}

		var runConfiguration = ResolveRunConfiguration();
		var createdAt = DateTimeOffset.Now;
		var outputPath = ResolveOutputPath(explicitOutputPath, createdAt);
		var coldRequest = BuildColdProcessRequest(targetPath);

		try
		{
			var coldRuns = await RunColdProcessBenchmarksAsync(coldRequest, runConfiguration, cancellationToken)
				.ConfigureAwait(false);
			var warmResult = await RunWarmPipelineBenchmarksAsync(targetPath, runConfiguration, cancellationToken)
				.ConfigureAwait(false);

			var report = CommandLineBenchmarkReport.Create(
				createdAt,
				targetPath,
				context.VersionProvider(),
				coldRequest,
				runConfiguration,
				coldRuns,
				warmResult.Runs,
				warmResult.DiagnosticRuns,
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
			runs.Add(await context.ProcessRunner
				.RunAsync(request, index + 1, isWarmup, cancellationToken)
				.ConfigureAwait(false));
		}

		return runs;
	}

	private async Task<CommandLineBenchmarkWarmPipelineResult> RunWarmPipelineBenchmarksAsync(
		string targetPath,
		CommandLineBenchmarkRunConfiguration configuration,
		CancellationToken cancellationToken)
	{
		using var services = context.ServicesFactory();
		var runs = new List<CommandLineBenchmarkPipelineRun>(configuration.TotalRuns);
		for (var index = 0; index < configuration.TotalRuns; index++)
		{
			var isWarmup = index < configuration.Warmup;
			runs.Add(await RunWarmPipelineOnceAsync(
				services,
				targetPath,
				index + 1,
				isWarmup,
				captureDiagnostics: false,
				cancellationToken)
				.ConfigureAwait(false));
		}

		var diagnosticRuns = new List<CommandLineBenchmarkPipelineRun>(DiagnosticProbeRuns);
		for (var index = 0; index < DiagnosticProbeRuns; index++)
		{
			diagnosticRuns.Add(await RunWarmPipelineOnceAsync(
				services,
				targetPath,
				configuration.TotalRuns + index + 1,
				isWarmup: false,
				captureDiagnostics: true,
				cancellationToken)
				.ConfigureAwait(false));
		}

		return new CommandLineBenchmarkWarmPipelineResult(runs, diagnosticRuns);
	}

	private static async Task<CommandLineBenchmarkPipelineRun> RunWarmPipelineOnceAsync(
		BenchmarkAnalysisServices services,
		string targetPath,
		int index,
		bool isWarmup,
		bool captureDiagnostics,
		CancellationToken cancellationToken)
	{
		using var ignoreMeasurement = captureDiagnostics
			? IgnorePipelineDiagnostics.BeginMeasurement()
			: null;
		using var contentMeasurement = captureDiagnostics
			? ContentPipelineDiagnostics.BeginMeasurement()
			: null;
		using var process = Process.GetCurrentProcess();
		var cpuBefore = TryGetTotalProcessorTime(process);
		var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
		var gen0Before = GC.CollectionCount(0);
		var gen1Before = GC.CollectionCount(1);
		var gen2Before = GC.CollectionCount(2);
		var startedAt = DateTimeOffset.Now;
		var stopwatch = Stopwatch.StartNew();
		var stdout = string.Empty;
		ProjectAnalysisReport? analysisReport = null;
		var exportMilliseconds = (double?)null;
		var exportBytes = 0L;
		string? error = null;
		var exitCode = CommandLineExitCodes.Success;

		try
		{
			var loadedProject = services.AnalysisService.Load(
				new ProjectAnalysisRequest(
					RootPath: targetPath,
					SelectedRootFolders: null,
					SelectedExtensions: null,
					SelectedIgnoreOptions: null),
				cancellationToken);
			analysisReport = await services.AnalysisService
				.BuildReportFromTreeAsync(loadedProject, cancellationToken)
				.ConfigureAwait(false);
			using var writer = new StringWriter(CultureInfo.InvariantCulture);
			await services.ReportWriter.WriteAsync(analysisReport, writer, cancellationToken)
				.ConfigureAwait(false);
			stdout = writer.ToString();

			var exportStopwatch = Stopwatch.StartNew();
			var exportPlan = await services.ContextFactory.BuildAsync(
					targetPath,
					ProjectSelectionSpec.Standard,
					cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			await using var exportOutput = new MemoryStream();
			await services.ContextDocumentService.WriteCompleteAsync(
					exportPlan,
					ProjectContextView.TreeContent,
					ProjectContextDocumentFormat.Markdown,
					exportOutput,
					cancellationToken,
					plain: true)
				.ConfigureAwait(false);
			exportStopwatch.Stop();
			exportMilliseconds = exportStopwatch.Elapsed.TotalMilliseconds;
			exportBytes = exportOutput.Length;
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
		var allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
		TryRefresh(process);
		var workload = analysisReport is null
			? null
			: CommandLineBenchmarkWorkload.FromReport(analysisReport);

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
			AllocatedBytes: Math.Max(0, allocatedAfter - allocatedBefore),
			Gen0Collections: GC.CollectionCount(0) - gen0Before,
			Gen1Collections: GC.CollectionCount(1) - gen1Before,
			Gen2Collections: GC.CollectionCount(2) - gen2Before,
			StdoutCharacters: stdout.Length,
			StdoutBytes: Encoding.UTF8.GetByteCount(stdout),
			LoadingMilliseconds: analysisReport?.Timing.LoadingMilliseconds,
			AnalysisMilliseconds: analysisReport?.Timing.AnalysisMilliseconds,
			ReportedTotalMilliseconds: analysisReport?.Timing.TotalMilliseconds,
			ExportMilliseconds: exportMilliseconds,
			ExportBytes: exportBytes,
			Workload: workload,
			Diagnostics: ignoreMeasurement?.Capture() ?? IgnorePipelineDiagnosticSnapshot.Empty,
			ContentDiagnostics: contentMeasurement?.Capture() ?? new ContentPipelineDiagnosticSnapshot(0, 0, 0, 0, 0),
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
		var processPath = ProcessEntryPointResolver.ResolveSelfLaunchPath();
		var assemblyPath = ProcessEntryPointResolver.ResolveManagedAssemblyPath();
		var arguments = new List<string>();
		var fileName = processPath;
		if (string.IsNullOrWhiteSpace(fileName))
		{
			if (string.IsNullOrWhiteSpace(assemblyPath))
				throw new InvalidOperationException("The current DevProjex process entry point is unavailable.");
			fileName = "dotnet";
			arguments.Add(assemblyPath);
		}
		else if (IsDotnetHost(fileName) && !string.IsNullOrWhiteSpace(assemblyPath))
		{
			arguments.Add(assemblyPath);
		}

		arguments.Add("analyze");
		arguments.Add(targetPath);
		arguments.Add("--format");
		arguments.Add("json");
		arguments.Add("--output");
		arguments.Add("-");

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

		var localAppData = context.LocalAppDataProvider();
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
		var diagnostics = report.WarmPipelineDiagnostics.Median;
		var contentDiagnostics = report.WarmContentPipelineDiagnostics.Median;
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
			$"  median cpu: {FormatMilliseconds(report.ColdProcess.Summary.MedianCpuMilliseconds)}",
			$"  peak memory: {FormatMegabytes(report.ColdProcess.Summary.PeakWorkingSetBytes)}",
			string.Empty,
			"Warm pipeline (analyze + export context):",
			$"  avg wall: {FormatMilliseconds(report.WarmPipeline.Summary.AvgWallMilliseconds)}",
			$"  median wall: {FormatMilliseconds(report.WarmPipeline.Summary.MedianWallMilliseconds)}",
			$"  min/max: {FormatMilliseconds(report.WarmPipeline.Summary.MinWallMilliseconds)} / {FormatMilliseconds(report.WarmPipeline.Summary.MaxWallMilliseconds)}",
			$"  avg cpu: {FormatMilliseconds(report.WarmPipeline.Summary.AvgCpuMilliseconds)}",
			$"  median cpu: {FormatMilliseconds(report.WarmPipeline.Summary.MedianCpuMilliseconds)}",
			$"  avg allocated: {FormatMegabytes(report.WarmPipeline.Summary.AvgAllocatedBytes)}",
			$"  median allocated: {FormatMegabytes(report.WarmPipeline.Summary.MedianAllocatedBytes)}",
			$"  avg loading: {FormatMilliseconds(report.WarmPipeline.Summary.AvgLoadingMilliseconds)}",
			$"  median loading: {FormatMilliseconds(report.WarmPipeline.Summary.MedianLoadingMilliseconds)}",
			$"  avg analysis: {FormatMilliseconds(report.WarmPipeline.Summary.AvgAnalysisMilliseconds)}",
			$"  median analysis: {FormatMilliseconds(report.WarmPipeline.Summary.MedianAnalysisMilliseconds)}",
			$"  avg export: {FormatMilliseconds(report.WarmPipeline.Summary.AvgExportMilliseconds)}",
			$"  median export: {FormatMilliseconds(report.WarmPipeline.Summary.MedianExportMilliseconds)}",
			$"  avg export bytes: {report.WarmPipeline.Summary.AvgExportBytes?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}",
			$"  managed memory: {FormatMegabytes(report.WarmPipeline.Summary.ManagedMemoryAfterBytes)}",
			$"  workload stable: {report.WorkloadConsistent.ToString(CultureInfo.InvariantCulture)}",
			$"  selection stable: {report.SelectionConsistent.ToString(CultureInfo.InvariantCulture)}",
			$"  inventory stable: {report.InventoryConsistent.ToString(CultureInfo.InvariantCulture)}",
			$"  metrics stable: {report.MetricsConsistent.ToString(CultureInfo.InvariantCulture)}",
			$"  workload fingerprint: {report.Workload?.Fingerprint ?? "n/a"}",
			string.Empty,
			"Ignore pipeline (post-measurement diagnostic probes):",
			$"  probe count: {report.WarmPipelineDiagnostics.Count}",
			"  counters below are component medians across probes",
			$"  workspace scans: {diagnostics.WorkspaceScans}",
			$"  enumerations: directories={diagnostics.DirectoryEnumerations}, files={diagnostics.FileEnumerations}, combined={diagnostics.CombinedEntryEnumerations}",
			$"  root facts: requests={diagnostics.RootFactsRequests}, hits={diagnostics.RootFactsCacheHits}, builds={diagnostics.RootFactsBuilds}, evictions={diagnostics.RootFactsEvictions}",
			$"  gitignore loads: requests={diagnostics.GitIgnoreLoadRequests}, executions={diagnostics.GitIgnoreLoadExecutions}, reuses={diagnostics.GitIgnoreLoadReuses}",
			$"  gitignore reads: requests={diagnostics.GitIgnoreSourceReadRequests}, bytes={diagnostics.GitIgnoreSourceBytes}",
			$"  diagnostics stable: {report.WarmPipelineDiagnostics.Consistent.ToString(CultureInfo.InvariantCulture)}",
			string.Empty,
			"Content pipeline (post-measurement diagnostic probes):",
			$"  full file reads: {contentDiagnostics.FullFileReads}",
			$"  full file read bytes: {contentDiagnostics.FullFileReadBytes}",
			$"  content fingerprints: {contentDiagnostics.ContentFingerprintComputations}",
			$"  plan applications: {contentDiagnostics.PlanApplications}",
			$"  occurrence IDs: {contentDiagnostics.OccurrenceIdComputations}",
			$"  diagnostics stable: {report.WarmContentPipelineDiagnostics.Consistent.ToString(CultureInfo.InvariantCulture)}",
			string.Empty,
			"Result:",
			$"  {report.OutputPath}"
		};

		foreach (var line in lines)
			await context.Output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
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

	private void WriteError(string message) => context.Error.WriteLine($"DevProjex: {message}");
}

internal sealed class DefaultCommandLineBenchmarkProcessRunner : ICommandLineBenchmarkProcessRunner
{
	private static readonly TimeSpan MemoryPollInterval = TimeSpan.FromMilliseconds(50);
	private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(5);

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
		Task<string>? stdoutTask = null;
		Task<string>? stderrTask = null;

		using var process = new Process
		{
			StartInfo = BuildStartInfo(request),
			EnableRaisingEvents = false
		};

		try
		{
			if (!process.Start())
				throw new InvalidOperationException("Failed to start benchmark child process.");

			stdoutTask = process.StandardOutput.ReadToEndAsync();
			stderrTask = process.StandardError.ReadToEndAsync();
			var exitTask = process.WaitForExitAsync(cancellationToken);
			while (!exitTask.IsCompleted)
			{
				SampleMemory(process, ref peakWorkingSetBytes, ref peakPrivateMemoryBytes);
				var nextSample = Task.Delay(MemoryPollInterval, cancellationToken);
				await Task.WhenAny(exitTask, nextSample).ConfigureAwait(false);
			}

			SampleMemory(process, ref peakWorkingSetBytes, ref peakPrivateMemoryBytes);
			await exitTask.ConfigureAwait(false);
			stopwatch.Stop();

			var output = await Task.WhenAll(stdoutTask, stderrTask)
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);
			var stdout = output[0];
			var stderr = output[1];
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
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await TerminateProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
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
		foreach (var variable in request.Environment ?? new Dictionary<string, string?>())
		{
			if (variable.Value is null)
				startInfo.Environment.Remove(variable.Key);
			else
				startInfo.Environment[variable.Key] = variable.Value;
		}

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

	private static async Task TerminateProcessAsync(
		Process process,
		Task<string>? stdoutTask,
		Task<string>? stderrTask)
	{
		TryKill(process, entireProcessTree: true);
		using var timeout = new CancellationTokenSource(ProcessTerminationTimeout);
		var outputCompletion = ObserveRedirectedOutputAsync(stdoutTask, stderrTask);
		try
		{
			await Task.WhenAll(
					process.WaitForExitAsync(timeout.Token),
					outputCompletion)
				.WaitAsync(timeout.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeout.IsCancellationRequested)
		{
			TryKill(process, entireProcessTree: false);
		}
		catch
		{
			// Cancellation owns the command result; cleanup failures must not replace it.
			TryKill(process, entireProcessTree: false);
		}
	}

	private static async Task ObserveRedirectedOutputAsync(
		Task<string>? stdoutTask,
		Task<string>? stderrTask)
	{
		try
		{
			await Task.WhenAll(
				stdoutTask ?? Task.FromResult(string.Empty),
				stderrTask ?? Task.FromResult(string.Empty)).ConfigureAwait(false);
		}
		catch
		{
			// Cancellation owns the command result; redirected reads still must be observed.
		}
	}

	private static void TryKill(Process process, bool entireProcessTree)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree);
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
	Func<BenchmarkAnalysisServices> ServicesFactory,
	Func<string> VersionProvider,
	ICommandLineBenchmarkProcessRunner ProcessRunner,
	Func<string> LocalAppDataProvider);

internal sealed class BenchmarkAnalysisServices(
	ProjectAnalysisService AnalysisService,
	ProjectAnalysisReportWriter ReportWriter,
	TerminalProjectContextFactory ContextFactory,
	ProjectContextDocumentService ContextDocumentService,
	IDisposable ownedLifetime) : IDisposable
{
	private IDisposable? _ownedLifetime = ownedLifetime;

	public ProjectAnalysisService AnalysisService { get; } = AnalysisService;
	public ProjectAnalysisReportWriter ReportWriter { get; } = ReportWriter;
	public TerminalProjectContextFactory ContextFactory { get; } = ContextFactory;
	public ProjectContextDocumentService ContextDocumentService { get; } = ContextDocumentService;

	public void Dispose() => Interlocked.Exchange(ref _ownedLifetime, null)?.Dispose();
}

internal sealed record CommandLineBenchmarkProcessRequest(
	string FileName,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	string CommandLine,
	IReadOnlyDictionary<string, string?>? Environment = null) : IDisposable
{
	private int _disposed;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0 ||
		    Environment is null ||
		    !Environment.TryGetValue(DesktopDiagnosticRequestStore.EnvironmentVariable, out var path) ||
		    string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		DesktopDiagnosticRequestStore.Delete(path);
	}
}

internal sealed record CommandLineBenchmarkRunConfiguration(
	int Runs,
	int Warmup)
{
	public int TotalRuns => Runs + Warmup;
}

internal sealed record CommandLineBenchmarkWarmPipelineResult(
	IReadOnlyList<CommandLineBenchmarkPipelineRun> Runs,
	IReadOnlyList<CommandLineBenchmarkPipelineRun> DiagnosticRuns);

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
	CommandLineBenchmarkWorkload? Workload,
	bool WorkloadConsistent,
	bool SelectionConsistent,
	bool InventoryConsistent,
	bool MetricsConsistent,
	CommandLineBenchmarkSection<CommandLineBenchmarkProcessRun> ColdProcess,
	CommandLineBenchmarkSection<CommandLineBenchmarkPipelineRun> WarmPipeline,
	CommandLineBenchmarkDiagnosticSummary WarmPipelineDiagnostics,
	CommandLineBenchmarkContentDiagnosticSummary WarmContentPipelineDiagnostics,
	IReadOnlyList<CommandLineBenchmarkPipelineRun> WarmPipelineDiagnosticRuns,
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
		IReadOnlyList<CommandLineBenchmarkPipelineRun> warmDiagnosticRuns,
		string outputPath)
	{
		var coldMeasuredRuns = coldRuns.Where(static run => !run.IsWarmup).ToArray();
		var warmMeasuredRuns = warmRuns.Where(static run => !run.IsWarmup).ToArray();
		var hasFailures =
			coldMeasuredRuns.Any(static run => run.ExitCode != CommandLineExitCodes.Success) ||
			warmMeasuredRuns.Any(static run => run.ExitCode != CommandLineExitCodes.Success) ||
			warmDiagnosticRuns.Any(static run => run.ExitCode != CommandLineExitCodes.Success);
		var successfulWarmRuns = warmMeasuredRuns
			.Where(static run => run.ExitCode == CommandLineExitCodes.Success)
			.ToArray();
		var workload = successfulWarmRuns
			.Select(static run => run.Workload)
			.FirstOrDefault(static value => value is not null);
		var selectionConsistent = workload is not null && successfulWarmRuns.All(
			run => string.Equals(
				run.Workload?.SelectionFingerprint,
				workload.SelectionFingerprint,
				StringComparison.Ordinal));
		var inventoryConsistent = workload is not null && successfulWarmRuns.All(
			run => string.Equals(
				run.Workload?.InventoryFingerprint,
				workload.InventoryFingerprint,
				StringComparison.Ordinal));
		var metricsConsistent = workload is not null && successfulWarmRuns.All(
			run => string.Equals(
				run.Workload?.MetricsFingerprint,
				workload.MetricsFingerprint,
				StringComparison.Ordinal));
		var workloadConsistent = selectionConsistent && inventoryConsistent && metricsConsistent;

		return new CommandLineBenchmarkReport(
			SchemaVersion: 4,
			CreatedAt: createdAt,
			TargetPath: Path.GetFullPath(targetPath).Replace('\\', '/'),
			ApplicationVersion: applicationVersion,
			OsDescription: RuntimeInformation.OSDescription,
			FrameworkDescription: RuntimeInformation.FrameworkDescription,
			ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
			RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
			Configuration: configuration,
			Executable: CommandLineBenchmarkExecutableInfo.Create(coldRequest),
			Workload: workload,
			WorkloadConsistent: workloadConsistent,
			SelectionConsistent: selectionConsistent,
			InventoryConsistent: inventoryConsistent,
			MetricsConsistent: metricsConsistent,
			ColdProcess: new CommandLineBenchmarkSection<CommandLineBenchmarkProcessRun>(
				Summary: CommandLineBenchmarkSummary.FromProcessRuns(coldMeasuredRuns),
				WarmupRuns: coldRuns.Where(static run => run.IsWarmup).ToArray(),
				Runs: coldMeasuredRuns),
			WarmPipeline: new CommandLineBenchmarkSection<CommandLineBenchmarkPipelineRun>(
				Summary: CommandLineBenchmarkSummary.FromPipelineRuns(warmMeasuredRuns),
				WarmupRuns: warmRuns.Where(static run => run.IsWarmup).ToArray(),
				Runs: warmMeasuredRuns),
			WarmPipelineDiagnostics: CommandLineBenchmarkDiagnosticSummary.FromRuns(
				warmDiagnosticRuns
					.Where(static run => run.ExitCode == CommandLineExitCodes.Success)
					.ToArray()),
			WarmContentPipelineDiagnostics: CommandLineBenchmarkContentDiagnosticSummary.FromRuns(
				warmDiagnosticRuns
					.Where(static run => run.ExitCode == CommandLineExitCodes.Success)
					.ToArray()),
			WarmPipelineDiagnosticRuns: warmDiagnosticRuns,
			HasFailures: hasFailures,
			OutputPath: Path.GetFullPath(outputPath).Replace('\\', '/'));
	}
}

internal sealed record CommandLineBenchmarkExecutableInfo(
	string FileName,
	IReadOnlyList<string> Arguments,
	string WorkingDirectory,
	string CommandLine,
	string? AssemblyPath,
	string? AssemblySha256)
{
	public static CommandLineBenchmarkExecutableInfo Create(CommandLineBenchmarkProcessRequest request)
	{
		var assemblyPath = ProcessEntryPointResolver.ResolveCurrentArtifactPath();
		return new CommandLineBenchmarkExecutableInfo(
			FileName: request.FileName,
			Arguments: request.Arguments,
			WorkingDirectory: request.WorkingDirectory,
			CommandLine: request.CommandLine,
			AssemblyPath: string.IsNullOrWhiteSpace(assemblyPath)
				? null
				: Path.GetFullPath(assemblyPath).Replace('\\', '/'),
			AssemblySha256: TryGetSha256(assemblyPath));
	}

	private static string? TryGetSha256(string? assemblyPath)
	{
		if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
			return null;

		try
		{
			using var stream = new FileStream(
				assemblyPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 64 * 1024,
				FileOptions.SequentialScan);
			return Convert.ToHexString(SHA256.HashData(stream));
		}
		catch
		{
			return null;
		}
	}
}

internal sealed record CommandLineBenchmarkWorkload(
	string Fingerprint,
	string SelectionFingerprint,
	string InventoryFingerprint,
	string MetricsFingerprint,
	int AvailableRootFolderCount,
	int AvailableExtensionCount,
	int SelectedRootFolderCount,
	int SelectedExtensionCount,
	IReadOnlyList<IgnoreOptionId> SelectedIgnoreOptions,
	int DirectoryCount,
	int FileCount,
	int AccessDeniedDirectoryCount,
	long TreeLines,
	long TreeChars,
	long TreeTokens,
	long ContentLines,
	long ContentChars,
	long ContentTokens)
{
	public static CommandLineBenchmarkWorkload FromReport(ProjectAnalysisReport report)
	{
		var selectionFingerprint = BuildSelectionFingerprint(report);
		var inventoryFingerprint = BuildInventoryFingerprint(report);
		var metricsFingerprint = BuildMetricsFingerprint(report);
		var fingerprint = HashValues(selectionFingerprint, inventoryFingerprint, metricsFingerprint);
		return new CommandLineBenchmarkWorkload(
			Fingerprint: fingerprint,
			SelectionFingerprint: selectionFingerprint,
			InventoryFingerprint: inventoryFingerprint,
			MetricsFingerprint: metricsFingerprint,
			AvailableRootFolderCount: report.Inventory.AvailableRootFolders.Count,
			AvailableExtensionCount: report.Inventory.AvailableExtensions.Count,
			SelectedRootFolderCount: report.Selection.SelectedRootFolders.Count,
			SelectedExtensionCount: report.Selection.SelectedExtensions.Count,
			SelectedIgnoreOptions: report.Selection.SelectedIgnoreOptions,
			DirectoryCount: report.Inventory.Tree.DirectoryCount,
			FileCount: report.Inventory.Tree.FileCount,
			AccessDeniedDirectoryCount: report.Inventory.Tree.AccessDeniedDirectoryCount,
			TreeLines: report.Metrics.Tree.Lines,
			TreeChars: report.Metrics.Tree.Chars,
			TreeTokens: report.Metrics.Tree.Tokens,
			ContentLines: report.Metrics.Content.Lines,
			ContentChars: report.Metrics.Content.Chars,
			ContentTokens: report.Metrics.Content.Tokens);
	}

	private static string BuildSelectionFingerprint(ProjectAnalysisReport report)
	{
		var builder = new StringBuilder(capacity: 1024);
		AppendValue(builder, report.RootPath);
		AppendSortedValues(builder, report.Selection.SelectedRootFolders);
		AppendSortedValues(builder, report.Selection.SelectedExtensions);
		foreach (var option in report.Selection.SelectedIgnoreOptions)
			AppendValue(builder, ((int)option).ToString(CultureInfo.InvariantCulture));
		return Hash(builder);
	}

	private static string BuildInventoryFingerprint(ProjectAnalysisReport report)
	{
		var builder = new StringBuilder(capacity: 1024);
		AppendSortedValues(builder, report.Inventory.AvailableRootFolders);
		AppendSortedValues(builder, report.Inventory.AvailableExtensions);
		AppendValue(builder, report.Inventory.Tree.DirectoryCount.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Inventory.Tree.FileCount.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Inventory.Tree.AccessDeniedDirectoryCount.ToString(CultureInfo.InvariantCulture));
		return Hash(builder);
	}

	private static string BuildMetricsFingerprint(ProjectAnalysisReport report)
	{
		var builder = new StringBuilder(capacity: 256);
		AppendValue(builder, report.Metrics.Tree.Lines.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Metrics.Tree.Chars.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Metrics.Tree.Tokens.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Metrics.Content.Lines.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Metrics.Content.Chars.ToString(CultureInfo.InvariantCulture));
		AppendValue(builder, report.Metrics.Content.Tokens.ToString(CultureInfo.InvariantCulture));
		return Hash(builder);
	}

	private static string HashValues(params string[] values)
	{
		var builder = new StringBuilder(capacity: values.Length * 65);
		AppendValues(builder, values);
		return Hash(builder);
	}

	private static string Hash(StringBuilder builder)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	private static void AppendValues(StringBuilder builder, IReadOnlyList<string> values)
	{
		foreach (var value in values)
			AppendValue(builder, value);
	}

	private static void AppendSortedValues(StringBuilder builder, IReadOnlyList<string> values)
	{
		var sortedValues = new List<string>(values);
		sortedValues.Sort(StringComparer.Ordinal);
		AppendValues(builder, sortedValues);
	}

	private static void AppendValue(StringBuilder builder, string value)
	{
		builder
			.Append(value.Length.ToString(CultureInfo.InvariantCulture))
			.Append(':')
			.Append(value)
			.Append(';');
	}
}

internal sealed record CommandLineBenchmarkSection<TRun>(
	CommandLineBenchmarkSummary Summary,
	IReadOnlyList<TRun> WarmupRuns,
	IReadOnlyList<TRun> Runs);

internal sealed record CommandLineBenchmarkDiagnosticSummary(
	int Count,
	bool Consistent,
	IgnorePipelineDiagnosticSnapshot Minimum,
	IgnorePipelineDiagnosticSnapshot Median,
	IgnorePipelineDiagnosticSnapshot Maximum)
{
	public static CommandLineBenchmarkDiagnosticSummary FromRuns(
		IReadOnlyList<CommandLineBenchmarkPipelineRun> runs)
	{
		if (runs.Count == 0)
		{
			return new CommandLineBenchmarkDiagnosticSummary(
				0,
				Consistent: true,
				IgnorePipelineDiagnosticSnapshot.Empty,
				IgnorePipelineDiagnosticSnapshot.Empty,
				IgnorePipelineDiagnosticSnapshot.Empty);
		}

		var first = runs[0].Diagnostics;
		return new CommandLineBenchmarkDiagnosticSummary(
			runs.Count,
			Consistent: runs.Skip(1).All(run => run.Diagnostics == first),
			Minimum: Aggregate(runs, static values => values.Min()),
			Median: Aggregate(runs, MedianValue),
			Maximum: Aggregate(runs, static values => values.Max()));
	}

	private static IgnorePipelineDiagnosticSnapshot Aggregate(
		IReadOnlyList<CommandLineBenchmarkPipelineRun> runs,
		Func<long[], long> aggregate)
	{
		long Read(Func<IgnorePipelineDiagnosticSnapshot, long> selector) =>
			aggregate(runs.Select(run => selector(run.Diagnostics)).ToArray());

		return new IgnorePipelineDiagnosticSnapshot(
			Read(static value => value.RootFactsRequests),
			Read(static value => value.RootFactsCacheHits),
			Read(static value => value.RootFactsBuilds),
			Read(static value => value.RootFactsEvictions),
			Read(static value => value.ProjectScopeDiscoveries),
			Read(static value => value.IgnoreRulesBuilds),
			Read(static value => value.FullSelectionRefreshes),
			Read(static value => value.LiveSelectionRefreshes),
			Read(static value => value.DynamicSelectionPasses),
			Read(static value => value.WorkspaceScans),
			Read(static value => value.DirectoryEnumerations),
			Read(static value => value.FileEnumerations),
			Read(static value => value.CombinedEntryEnumerations),
			Read(static value => value.GitIgnoreSourceReadRequests),
			Read(static value => value.GitIgnoreSourceBytes),
			Read(static value => value.GitIgnoreLoadRequests),
			Read(static value => value.GitIgnoreLoadExecutions),
			Read(static value => value.GitIgnoreLoadReuses));
	}

	private static long MedianValue(long[] values)
	{
		Array.Sort(values);
		var middle = values.Length / 2;
		return values.Length % 2 == 0
			? (long)Math.Round(
				values[middle - 1] / 2d + values[middle] / 2d,
				MidpointRounding.AwayFromZero)
			: values[middle];
	}
}

internal sealed record CommandLineBenchmarkContentDiagnosticSummary(
	int Count,
	bool Consistent,
	ContentPipelineDiagnosticSnapshot Minimum,
	ContentPipelineDiagnosticSnapshot Median,
	ContentPipelineDiagnosticSnapshot Maximum)
{
	public static CommandLineBenchmarkContentDiagnosticSummary FromRuns(
		IReadOnlyList<CommandLineBenchmarkPipelineRun> runs)
	{
		if (runs.Count == 0)
		{
			var empty = new ContentPipelineDiagnosticSnapshot(0, 0, 0, 0, 0);
			return new CommandLineBenchmarkContentDiagnosticSummary(0, true, empty, empty, empty);
		}

		var first = runs[0].ContentDiagnostics;
		return new CommandLineBenchmarkContentDiagnosticSummary(
			runs.Count,
			Consistent: runs.Skip(1).All(run => run.ContentDiagnostics == first),
			Minimum: Aggregate(runs, static values => values.Min()),
			Median: Aggregate(runs, MedianValue),
			Maximum: Aggregate(runs, static values => values.Max()));
	}

	private static ContentPipelineDiagnosticSnapshot Aggregate(
		IReadOnlyList<CommandLineBenchmarkPipelineRun> runs,
		Func<long[], long> aggregate)
	{
		long Read(Func<ContentPipelineDiagnosticSnapshot, long> selector) =>
			aggregate(runs.Select(run => selector(run.ContentDiagnostics)).ToArray());

		return new ContentPipelineDiagnosticSnapshot(
			Read(static value => value.FullFileReads),
			Read(static value => value.FullFileReadBytes),
			Read(static value => value.ContentFingerprintComputations),
			Read(static value => value.PlanApplications),
			Read(static value => value.OccurrenceIdComputations));
	}

	private static long MedianValue(long[] values)
	{
		Array.Sort(values);
		var middle = values.Length / 2;
		return values.Length % 2 == 0
			? (long)Math.Round(values[middle - 1] / 2d + values[middle] / 2d, MidpointRounding.AwayFromZero)
			: values[middle];
	}
}

internal sealed record CommandLineBenchmarkSummary(
	int Count,
	int SuccessfulCount,
	int FailedCount,
	double AvgWallMilliseconds,
	double MedianWallMilliseconds,
	double MinWallMilliseconds,
	double MaxWallMilliseconds,
	double? AvgCpuMilliseconds,
	double? MedianCpuMilliseconds,
	long? PeakWorkingSetBytes,
	long? PeakPrivateMemoryBytes,
	long? ManagedMemoryAfterBytes,
	long? AvgAllocatedBytes,
	long? MedianAllocatedBytes,
	double? AvgLoadingMilliseconds,
	double? MedianLoadingMilliseconds,
	double? AvgAnalysisMilliseconds,
	double? MedianAnalysisMilliseconds,
	double? AvgReportedTotalMilliseconds,
	long? AvgStdoutBytes,
	double? AvgExportMilliseconds,
	double? MedianExportMilliseconds,
	long? AvgExportBytes)
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
			allocatedBytes: null,
			loadingMilliseconds: null,
			analysisMilliseconds: null,
			reportedTotalMilliseconds: null,
			stdoutBytes: successfulRuns.Select(static run => (long?)run.StdoutBytes).ToArray(),
			exportMilliseconds: null,
			exportBytes: null);
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
			allocatedBytes: successfulRuns.Select(static run => (long?)run.AllocatedBytes).ToArray(),
			loadingMilliseconds: successfulRuns.Select(static run => run.LoadingMilliseconds).ToArray(),
			analysisMilliseconds: successfulRuns.Select(static run => run.AnalysisMilliseconds).ToArray(),
			reportedTotalMilliseconds: successfulRuns.Select(static run => run.ReportedTotalMilliseconds).ToArray(),
			stdoutBytes: successfulRuns.Select(static run => (long?)run.StdoutBytes).ToArray(),
			exportMilliseconds: successfulRuns.Select(static run => run.ExportMilliseconds).ToArray(),
			exportBytes: successfulRuns.Select(static run => (long?)run.ExportBytes).ToArray());
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
		IReadOnlyList<long?>? allocatedBytes,
		IReadOnlyList<double?>? loadingMilliseconds,
		IReadOnlyList<double?>? analysisMilliseconds,
		IReadOnlyList<double?>? reportedTotalMilliseconds,
		IReadOnlyList<long?> stdoutBytes,
		IReadOnlyList<double?>? exportMilliseconds,
		IReadOnlyList<long?>? exportBytes)
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
			MedianCpuMilliseconds: MedianNullable(cpuMilliseconds),
			PeakWorkingSetBytes: peakWorkingSetBytes,
			PeakPrivateMemoryBytes: peakPrivateMemoryBytes,
			ManagedMemoryAfterBytes: managedMemoryAfterBytes,
			AvgAllocatedBytes: allocatedBytes is null ? null : AverageNullableLong(allocatedBytes),
			MedianAllocatedBytes: allocatedBytes is null ? null : MedianNullableLong(allocatedBytes),
			AvgLoadingMilliseconds: loadingMilliseconds is null ? null : AverageNullable(loadingMilliseconds),
			MedianLoadingMilliseconds: loadingMilliseconds is null ? null : MedianNullable(loadingMilliseconds),
			AvgAnalysisMilliseconds: analysisMilliseconds is null ? null : AverageNullable(analysisMilliseconds),
			MedianAnalysisMilliseconds: analysisMilliseconds is null ? null : MedianNullable(analysisMilliseconds),
			AvgReportedTotalMilliseconds: reportedTotalMilliseconds is null
				? null
				: AverageNullable(reportedTotalMilliseconds),
			AvgStdoutBytes: AverageNullableLong(stdoutBytes),
			AvgExportMilliseconds: exportMilliseconds is null ? null : AverageNullable(exportMilliseconds),
			MedianExportMilliseconds: exportMilliseconds is null ? null : MedianNullable(exportMilliseconds),
			AvgExportBytes: exportBytes is null ? null : AverageNullableLong(exportBytes));
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

	private static double? MedianNullable(IEnumerable<double?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		return actual.Length == 0 ? null : Median(actual);
	}

	private static long? AverageNullableLong(IEnumerable<long?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		if (actual.Length == 0)
			return null;

		return (long)Math.Round(actual.Average(), MidpointRounding.AwayFromZero);
	}

	private static long? MedianNullableLong(IEnumerable<long?> values)
	{
		var actual = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
		if (actual.Length == 0)
			return null;

		Array.Sort(actual);
		var middle = actual.Length / 2;
		return actual.Length % 2 == 0
			? (long)Math.Round(
				actual[middle - 1] / 2d + actual[middle] / 2d,
				MidpointRounding.AwayFromZero)
			: actual[middle];
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
	long AllocatedBytes,
	int Gen0Collections,
	int Gen1Collections,
	int Gen2Collections,
	int StdoutCharacters,
	int StdoutBytes,
	double? LoadingMilliseconds,
	double? AnalysisMilliseconds,
	double? ReportedTotalMilliseconds,
	double? ExportMilliseconds,
	long ExportBytes,
	CommandLineBenchmarkWorkload? Workload,
	IgnorePipelineDiagnosticSnapshot Diagnostics,
	ContentPipelineDiagnosticSnapshot ContentDiagnostics,
	int ExitCode,
	string? Error);
