using System.Reflection;

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

		if (!parseResult.Options.NoUi)
			return CommandLineExitCodes.Success;

		return await RunHeadlessAnalysisAsync(parseResult.Options, context, cancellationToken)
			.ConfigureAwait(false);
	}

	public static bool ShouldRunBeforeAvalonia(CommandLineParseResult parseResult) =>
		parseResult.Errors.Count > 0 ||
		parseResult.Options.ShowHelp ||
		parseResult.Options.ShowVersion ||
		parseResult.Options.NoUi;

	private static async Task<int> RunHeadlessAnalysisAsync(
		CommandLineOptions options,
		CommandLineAutomationContext context,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(options.Path))
		{
			WriteError(context.Error, "--no-ui requires --path or a positional project path.");
			return CommandLineExitCodes.UsageError;
		}

		if (!options.Report.Enabled)
		{
			WriteError(context.Error, "--no-ui requires --report or --report-path because no window is shown.");
			return CommandLineExitCodes.UsageError;
		}

		try
		{
			var services = context.ServicesFactory(options);
			var report = await services.ProjectAnalysisService.AnalyzeAsync(
					new ProjectAnalysisRequest(
						RootPath: options.Path!,
						SelectedRootFolders: options.HasRootFolderOverrides ? options.IncludeRootFolders : null,
						SelectedExtensions: options.HasExtensionOverrides ? options.IncludeExtensions : null,
						SelectedIgnoreOptions: options.HasIgnoreOverrides ? options.IgnoreOptions : null),
					cancellationToken)
				.ConfigureAwait(false);

			await WriteReportAsync(options, context, services, report, cancellationToken)
				.ConfigureAwait(false);

			return ResolveStrictExitCode(options, report, context.Error);
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

	private static int ResolveStrictExitCode(
		CommandLineOptions options,
		ProjectAnalysisReport report,
		TextWriter errorWriter)
	{
		if (!options.Strict || IsClean(report.Diagnostics))
			return CommandLineExitCodes.Success;

		WriteError(errorWriter, "Strict mode failed because the analysis report contains diagnostics.");
		foreach (var warning in report.Diagnostics.Warnings)
			WriteError(errorWriter, warning);

		if (report.Diagnostics.RootAccessDenied)
			WriteError(errorWriter, "Root path access was denied.");
		if (report.Diagnostics.HadAccessDenied)
			WriteError(errorWriter, "One or more directories could not be read.");

		return CommandLineExitCodes.RuntimeError;
	}

	private static bool IsClean(ProjectAnalysisDiagnosticsReport diagnostics) =>
		!diagnostics.RootAccessDenied &&
		!diagnostics.HadAccessDenied &&
		diagnostics.Warnings.Count == 0;

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
	Func<string> VersionProvider);
