using DevProjex.Terminal.CommandLine;
using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

public sealed record TerminalError(
	string Code,
	string Message,
	string? Hint = null,
	int ExitCode = CommandLineExitCodes.RuntimeError,
	Exception? Exception = null,
	string? ContextPath = null);

public sealed class ErrorRenderer(
	ITerminalEnvironment environment,
	TerminalOutputOptions options,
	LocalizationService? localization = null)
{
	private readonly LocalizationService _localization = localization ?? new LocalizationService(
		new JsonLocalizationCatalog(),
		AppLanguage.En);

	public void Write(TerminalError error)
	{
		ArgumentNullException.ThrowIfNull(error);
		var console = AnsiConsoleFactory.Create(
			environment.Error,
			TerminalCapabilities.Resolve(environment, options, forStandardError: true));
		console.MarkupLine(
			$"[red]{Markup.Escape(_localization["Terminal.Label.Error"])}[[{Markup.Escape(error.Code)}]][/]:");
		console.WriteLine(error.Message);
		if (!string.IsNullOrWhiteSpace(error.ContextPath))
			console.WriteLine($"{_localization["Terminal.Label.Path"]}: {error.ContextPath}");
		if (!string.IsNullOrWhiteSpace(error.Hint))
		{
			console.WriteLine();
			console.MarkupLine($"[cyan]{Markup.Escape(_localization["Terminal.Label.Hint"])}:[/]");
			console.WriteLine(error.Hint);
		}

		if (options.Verbosity == TerminalVerbosity.Diagnostic && error.Exception is not null)
		{
			console.WriteLine();
			console.MarkupLine(
				$"[grey]{Markup.Escape(_localization["Terminal.Label.Exception"])}: " +
				$"{Markup.Escape(error.Exception.GetType().FullName ?? error.Exception.GetType().Name)}[/]");
			if (!string.IsNullOrWhiteSpace(error.Exception.StackTrace))
				console.WriteLine(error.Exception.StackTrace);
		}
	}
}
