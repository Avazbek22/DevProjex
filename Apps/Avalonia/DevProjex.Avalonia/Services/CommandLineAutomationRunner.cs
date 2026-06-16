using System.Reflection;

namespace DevProjex.Avalonia.Services;

internal static class CommandLineAutomationRunner
{
	public static async Task<int> RunUtilityOrHeadlessAsync(
		CommandLineParseResult parseResult,
		CancellationToken cancellationToken = default)
	{
		if (parseResult.Options.ShowHelp)
		{
			Console.WriteLine(BuildHelpText());
			return 0;
		}

		if (parseResult.Options.ShowVersion)
		{
			Console.WriteLine(GetVersion());
			return 0;
		}

		if (parseResult.Errors.Count > 0)
			return WriteErrors(parseResult.Errors);

		if (!parseResult.Options.NoUi)
			return 0;

		return await RunHeadlessAnalysisAsync(parseResult.Options, cancellationToken)
			.ConfigureAwait(false);
	}

	public static bool ShouldRunBeforeAvalonia(CommandLineParseResult parseResult) =>
		parseResult.Options.ShowHelp ||
		parseResult.Options.ShowVersion ||
		parseResult.Options.NoUi;

	private static async Task<int> RunHeadlessAnalysisAsync(
		CommandLineOptions options,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(options.Path))
		{
			WriteError("--no-ui requires --path or a positional project path.");
			return 2;
		}

		if (!options.Report.Enabled)
		{
			WriteError("--no-ui requires --report or --report-path because no window is shown.");
			return 2;
		}

		try
		{
			var services = AvaloniaCompositionRoot.CreateDefault(options);
			var report = await services.ProjectAnalysisService.AnalyzeAsync(
					new ProjectAnalysisRequest(
						RootPath: options.Path!,
						SelectedRootFolders: options.HasRootFolderOverrides ? options.IncludeRootFolders : null,
						SelectedExtensions: options.HasExtensionOverrides ? options.IncludeExtensions : null,
						SelectedIgnoreOptions: options.HasIgnoreOverrides ? options.IgnoreOptions : null),
					cancellationToken)
				.ConfigureAwait(false);

			var reportPath = services.ReportPathResolver.Resolve(options.Report);
			await services.ProjectAnalysisReportWriter.WriteAsync(report, reportPath, cancellationToken)
				.ConfigureAwait(false);

			Console.WriteLine(reportPath);
			return 0;
		}
		catch (OperationCanceledException)
		{
			WriteError("Operation was canceled.");
			return 130;
		}
		catch (Exception ex)
		{
			WriteError(ex.Message);
			return 1;
		}
	}

	private static int WriteErrors(IReadOnlyList<CommandLineParseError> errors)
	{
		foreach (var error in errors)
			WriteError(error.Message);

		return 2;
	}

	private static void WriteError(string message) => Console.Error.WriteLine($"DevProjex: {message}");

	private static string GetVersion()
	{
		var assembly = typeof(CommandLineAutomationRunner).Assembly;
		return assembly
			       .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			       ?.InformationalVersion
		       ?? assembly.GetName().Version?.ToString()
		       ?? "unknown";
	}

	private static string BuildHelpText() =>
		"""
		DevProjex

		Usage:
		  DevProjex --path <folder> [options]
		  DevProjex <folder> [options]

		Options:
		  --path <folder>                 Open a project folder.
		  --lang <code>                   UI language: en, ru, uz, tg, kk, fr, de, it.
		  --report [file]                 Write a JSON analysis report.
		  --report-path <file>            Write a JSON analysis report to a specific file.
		  --report-format json            Report format. JSON is the v1 format.
		  --include-root <name>           Include one root folder. Can be repeated.
		  --include-extension <ext>       Include one extension. Can be repeated.
		  --ignore <name|none>            Use exact ignore options for automation. Can be repeated.
		  --no-ui, --silent               Run analysis without showing the window. Requires --report.
		  --version                       Print application version.
		  --help, -h, /?                  Show help.

		Ignore option names:
		  smart-ignore, git-ignore, hidden-folders, hidden-files,
		  dot-folders, dot-files, empty-folders, empty-files, extensionless-files, none
		""";
}
