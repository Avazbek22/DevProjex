using DevProjex.Kernel;

namespace DevProjex.Avalonia.Services;

internal static class CommandLineAutomationRunner
{
	public static async Task<int> RunUtilityOrHeadlessAsync(
		CommandLineParseResult parseResult,
		CancellationToken cancellationToken = default)
		=> await RunUtilityOrHeadlessAsync(parseResult, CreateDefaultContext(), cancellationToken)
			.ConfigureAwait(false);

	internal static async Task<int> RunUtilityOrHeadlessAsync(
		CommandLineParseResult parseResult,
		CommandLineAutomationContext context,
		CancellationToken cancellationToken = default)
	{
		if (parseResult.Options.ShowHelp)
		{
			context.Output.WriteLine(context.HelpContentProvider.GetHelpText());
			return CommandLineExitCodes.Success;
		}

		if (parseResult.Options.ShowVersion)
		{
			context.Output.WriteLine(context.VersionProvider());
			return CommandLineExitCodes.Success;
		}

		if (parseResult.Errors.Count > 0)
			return WriteErrors(parseResult.Errors, context.Error);

		if (parseResult.Options.Benchmark.Enabled)
			return await RunBenchmarkAsync(parseResult.Options, context, cancellationToken)
				.ConfigureAwait(false);

		if (parseResult.Options.UiBenchmark.Enabled)
			return await RunUiBenchmarkAsync(parseResult.Options, context, cancellationToken)
				.ConfigureAwait(false);

		if (!ShouldRunHeadlessAnalysis(parseResult.Options))
			return CommandLineExitCodes.Success;

		return await RunHeadlessAnalysisAsync(parseResult.Options, context, cancellationToken)
			.ConfigureAwait(false);
	}

	public static bool ShouldRunBeforeAvalonia(CommandLineParseResult parseResult) =>
		parseResult.Errors.Count > 0 ||
		parseResult.Options.ShowHelp ||
		parseResult.Options.ShowVersion ||
		parseResult.Options.Benchmark.Enabled ||
		parseResult.Options.UiBenchmark.Enabled ||
		ShouldRunHeadlessAnalysis(parseResult.Options);

	private static async Task<int> RunBenchmarkAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		CancellationToken cancellationToken)
	{
		var benchmarkContext = new CommandLineBenchmarkContext(
			Output: context.Output,
			Error: context.Error,
			ServicesFactory: context.ServicesFactory,
			VersionProvider: context.VersionProvider,
			ProcessRunner: context.BenchmarkProcessRunner ?? new DefaultCommandLineBenchmarkProcessRunner(),
			LocalAppDataProvider: context.BenchmarkLocalAppDataProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
		var runner = new CommandLineBenchmarkRunner(benchmarkContext);
		return await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<int> RunUiBenchmarkAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		CancellationToken cancellationToken)
	{
		var benchmarkContext = new CommandLineUiBenchmarkContext(
			Output: context.Output,
			Error: context.Error,
			VersionProvider: context.VersionProvider,
			ProcessRunner: context.UiBenchmarkProcessRunner ?? context.BenchmarkProcessRunner ?? new DefaultCommandLineBenchmarkProcessRunner(),
			LocalAppDataProvider: context.BenchmarkLocalAppDataProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
		var runner = new CommandLineUiBenchmarkRunner(benchmarkContext);
		return await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<int> RunHeadlessAnalysisAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(options.Path))
		{
			WriteError(context.Error, "Headless analysis requires --path or a positional project path.");
			return CommandLineExitCodes.UsageError;
		}

		var validationError = ValidateHeadlessOptions(options);
		if (validationError is not null)
		{
			WriteError(context.Error, validationError);
			return CommandLineExitCodes.UsageError;
		}

		try
		{
			var services = context.ServicesFactory(options);
			var loadedProject = services.ProjectAnalysisService.Load(
				new ProjectAnalysisRequest(
					RootPath: options.Path!,
					SelectedRootFolders: options.HasRootFolderOverrides ? options.IncludeRootFolders : null,
					SelectedExtensions: options.HasExtensionOverrides ? options.IncludeExtensions : null,
					SelectedIgnoreOptions: options.HasIgnoreOverrides ? options.IgnoreOptions : null),
				cancellationToken);

			var writesImplicitStdoutReport = ShouldWriteImplicitStdoutReport(options);
			ProjectAnalysisReport? report = null;
			if (options.Report.Enabled || writesImplicitStdoutReport)
			{
				report = await services.ProjectAnalysisService
					.BuildReportFromTreeAsync(loadedProject, cancellationToken)
					.ConfigureAwait(false);

				if (writesImplicitStdoutReport)
				{
					// Headless analysis should be immediately useful in a terminal while file output stays opt-in.
					await services.ProjectAnalysisReportWriter.WriteAsync(report, context.Output, cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					await WriteReportAsync(options, context, services, report, cancellationToken)
						.ConfigureAwait(false);
				}
			}

			if (options.Export.Enabled)
			{
				var exportPayload = await services.ProjectExportService
					.BuildAsync(loadedProject, options.Export, cancellationToken)
					.ConfigureAwait(false);

				await WriteExportAsync(options, context, services, exportPayload, cancellationToken)
					.ConfigureAwait(false);
			}

			if (options.ProjectCopy.Enabled)
			{
				var copyResult = await ExportProjectCopyAsync(options, services, loadedProject, cancellationToken)
					.ConfigureAwait(false);
				context.Output.WriteLine(copyResult.DestinationPath);
			}

			var diagnostics = report?.Diagnostics ?? ProjectAnalysisService.BuildDiagnostics(loadedProject);
			return ResolveStrictExitCode(options, diagnostics, context.Error);
		}
		catch (OperationCanceledException)
		{
			WriteError(context.Error, "Operation was canceled.");
			return CommandLineExitCodes.Canceled;
		}
		catch (Exception ex)
		{
			WriteError(context.Error, ex.Message);
			return CommandLineExitCodes.RuntimeError;
		}
	}

	private static bool ShouldRunHeadlessAnalysis(CommandLineOptions options) =>
		options.NoUi ||
		options.Export.Enabled ||
		options.ProjectCopy.Enabled ||
		HasDetachedExportOptions(options);

	private static bool ShouldWriteImplicitStdoutReport(CommandLineOptions options) =>
		options.NoUi &&
		!options.Report.Enabled &&
		!options.Export.Enabled &&
		!options.ProjectCopy.Enabled;

	private static bool HasDetachedExportOptions(CommandLineOptions options) =>
		!options.Export.Enabled &&
		(options.Export.HasOutputPath || options.Export.FormatSpecified || options.Export.Format != TreeTextFormat.Ascii);

	private static string? ValidateHeadlessOptions(CommandLineOptions options)
	{
		if (!options.Export.Enabled && options.Export.HasOutputPath)
			return "--output requires --export or --copy.";

		if (!options.Export.Enabled &&
		    (options.Export.FormatSpecified || options.Export.Format != TreeTextFormat.Ascii))
			return "--export-format and --format require --export.";

		if (options.Ui.HasStartupActions)
			return "UI startup options cannot be combined with --no-ui, --silent, --export, or --copy.";

		if (options.Export.Enabled &&
		    options.Export.Mode == StartupExportMode.Content &&
		    options.Export.Format != TreeTextFormat.Ascii)
			return "--export-format applies only to tree and tree-content exports.";

		if (ReportAndExportUseSameExplicitFile(options))
			return "--report-path and --output must point to different files.";

		if (options.Report.Enabled && options.ProjectCopy.Enabled)
			return "--copy cannot be combined with --report or --report-path. Run separate commands.";

		var reportWritesToStdout = options.Report.Enabled && options.Report.WriteToStandardOutput;
		var exportWritesToStdout = options.Export.Enabled && options.Export.WriteToStandardOutput;
		if (!reportWritesToStdout && !exportWritesToStdout)
			return null;

		// Stdout must stay machine-safe: it can contain exactly one payload and no extra path lines.
		if (reportWritesToStdout && options.Export.Enabled)
			return "Cannot combine --report - with --export. Write one result to a file.";

		if (exportWritesToStdout && options.Report.Enabled)
			return "Cannot combine stdout export with --report. Use --output for export or write the report to a separate command.";

		return null;
	}

	private static bool ReportAndExportUseSameExplicitFile(CommandLineOptions options)
	{
		if (!options.Report.Enabled ||
		    !options.Export.Enabled ||
		    string.IsNullOrWhiteSpace(options.Report.Path) ||
		    string.IsNullOrWhiteSpace(options.Export.Path) ||
		    options.Report.WriteToStandardOutput ||
		    options.Export.WriteToStandardOutput)
		{
			return false;
		}

		var reportPath = ResolveExplicitOutputPath(options.Report.Path);
		var exportPath = ResolveExplicitOutputPath(options.Export.Path);
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(reportPath, exportPath, comparison);
	}

	private static int WriteErrors(IReadOnlyList<CommandLineParseError> errors, TextWriter errorWriter)
	{
		foreach (var parseError in errors)
			WriteError(errorWriter, parseError.Message);

		return CommandLineExitCodes.UsageError;
	}

	private static async Task WriteReportAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		AvaloniaAppServices services,
		ProjectAnalysisReport report,
		CancellationToken cancellationToken)
	{
		if (options.Report.WriteToStandardOutput)
		{
			await services.ProjectAnalysisReportWriter.WriteAsync(report, context.Output, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		var reportPath = services.ReportPathResolver.Resolve(options.Report);
		await services.ProjectAnalysisReportWriter.WriteAsync(report, reportPath, cancellationToken)
			.ConfigureAwait(false);
		context.Output.WriteLine(reportPath);
	}

	private static async Task WriteExportAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		AvaloniaAppServices services,
		string exportPayload,
		CancellationToken cancellationToken)
	{
		if (options.Export.WriteToStandardOutput)
		{
			if (exportPayload.Length == 0)
				return;

			await context.Output.WriteAsync(exportPayload.AsMemory(), cancellationToken).ConfigureAwait(false);
			if (!exportPayload.EndsWith(Environment.NewLine, StringComparison.Ordinal))
				await context.Output.WriteLineAsync().ConfigureAwait(false);
			return;
		}

		var exportPath = ResolveExplicitOutputPath(options.Export.Path!);
		var directory = Path.GetDirectoryName(exportPath);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);

		await using (var stream = new FileStream(
			             exportPath,
			             FileMode.Create,
			             FileAccess.Write,
			             FileShare.Read,
			             bufferSize: 16 * 1024,
			             FileOptions.Asynchronous | FileOptions.SequentialScan))
		{
			await services.TextFileExportService.WriteAsync(stream, exportPayload, cancellationToken)
				.ConfigureAwait(false);
		}

		context.Output.WriteLine(exportPath);
	}

	private static async Task<ProjectCopyExportResult> ExportProjectCopyAsync(
		CommandLineOptions options,
		AvaloniaAppServices services,
		LoadedProjectAnalysisRequest project,
		CancellationToken cancellationToken)
	{
		var destinationPath = ResolveExplicitOutputPath(options.ProjectCopy.DestinationPath!);
		var projectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(project.RootPath));
		var format = options.ProjectCopy.Mode switch
		{
			StartupProjectCopyMode.Folder => ProjectCopyExportFormat.Folder,
			StartupProjectCopyMode.Zip => ProjectCopyExportFormat.Zip,
			_ => throw new InvalidOperationException($"Unsupported project copy mode: {options.ProjectCopy.Mode}.")
		};
		var request = new ProjectCopyExportRequest(
			ProjectRootPath: project.RootPath,
			ProjectName: projectName,
			TreeRoot: project.Tree.Root,
			SelectedPaths: new HashSet<string>(PathComparer.Default),
			DestinationPath: destinationPath,
			Format: format);

		return await services.ProjectCopyExportService
			.ExportAsync(request, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
	}

	private static int ResolveStrictExitCode(
		CommandLineOptions options,
		ProjectAnalysisDiagnosticsReport diagnostics,
		TextWriter errorWriter)
	{
		if (!options.Strict || IsClean(diagnostics))
			return CommandLineExitCodes.Success;

		WriteError(errorWriter, "Strict mode failed because the analysis result contains diagnostics.");
		foreach (var warning in diagnostics.Warnings)
			WriteError(errorWriter, warning);

		if (diagnostics.RootAccessDenied)
			WriteError(errorWriter, "Root path access was denied.");
		if (diagnostics.HadAccessDenied)
			WriteError(errorWriter, "One or more directories could not be read.");

		return CommandLineExitCodes.RuntimeError;
	}

	private static bool IsClean(ProjectAnalysisDiagnosticsReport diagnostics) =>
		!diagnostics.RootAccessDenied &&
		!diagnostics.HadAccessDenied &&
		diagnostics.Warnings.Count == 0;

	private static string ResolveExplicitOutputPath(string outputPath)
	{
		if (Path.IsPathRooted(outputPath))
			return Path.GetFullPath(outputPath);

		return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputPath));
	}

	private static void WriteError(TextWriter error, string message) => error.WriteLine($"DevProjex: {message}");

	private static CommandLineAutomationContext CreateDefaultContext() =>
		new(
			Output: Console.Out,
			Error: Console.Error,
			ServicesFactory: AvaloniaCompositionRoot.CreateDefault,
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: GetVersion);

	private static string GetVersion()
	{
		var assembly = typeof(CommandLineAutomationRunner).Assembly;
		return assembly
			       .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			       ?.InformationalVersion
		       ?? assembly.GetName().Version?.ToString()
		       ?? "unknown";
	}
}

internal sealed record CommandLineAutomationContext(
	TextWriter Output,
	TextWriter Error,
	Func<CommandLineOptions, AvaloniaAppServices> ServicesFactory,
	CommandLineHelpContentProvider HelpContentProvider,
	Func<string> VersionProvider,
	ICommandLineBenchmarkProcessRunner? BenchmarkProcessRunner = null,
	ICommandLineBenchmarkProcessRunner? UiBenchmarkProcessRunner = null,
	Func<string>? BenchmarkLocalAppDataProvider = null);
