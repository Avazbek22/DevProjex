using DevProjex.Terminal.CommandLine;
using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

public sealed class ContextDiagnosticRenderer(
	ITerminalEnvironment environment,
	TerminalOutputOptions options,
	LocalizationService localization)
{
	public void Write(IReadOnlyList<ContextDiagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
		{
			if (options.Verbosity == TerminalVerbosity.Quiet &&
			    diagnostic.Severity != ContextDiagnosticSeverity.Error)
			{
				continue;
			}
			if (options.Verbosity == TerminalVerbosity.Minimal &&
			    diagnostic.Severity == ContextDiagnosticSeverity.Information)
			{
				continue;
			}

			var console = AnsiConsoleFactory.Create(
				environment.Error,
				TerminalCapabilities.Resolve(environment, options, forStandardError: true));
			var (label, color) = diagnostic.Severity switch
			{
				ContextDiagnosticSeverity.Error => (localization["Terminal.Label.Error"], "red"),
				ContextDiagnosticSeverity.Warning => (localization["Terminal.Label.Warning"], "yellow"),
				_ => (localization["Terminal.Label.Info"], "cyan")
			};
			console.MarkupLine(
				$"[{color}]{label}[[{Markup.Escape(diagnostic.Code)}]][/]:");
			console.WriteLine(ResolveMessage(localization, diagnostic));
			if (!string.IsNullOrWhiteSpace(diagnostic.Path))
			{
				var labelKey = diagnostic.Code == "DPX-PROJECT-SELECTION-WARNING"
					? "Terminal.Label.Value"
					: "Terminal.Label.Path";
				environment.Error.WriteLine(
					$"{localization[labelKey]}: {TerminalTextEscaping.EscapeSingleLine(diagnostic.Path)}");
			}
		}
	}

	internal static string ResolveMessage(
		LocalizationService localization,
		string code)
	{
		var key = code switch
		{
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE" =>
				"Terminal.Diagnostic.TrackedIndexUnavailable",
			"DPX-GIT-TRACKED-INDEX-PARTIAL" =>
				"Terminal.Diagnostic.TrackedIndexPartial",
			GitScopeFilter.UnavailableDiagnosticCode =>
				"Terminal.Diagnostic.GitStateUnavailable",
			GitScopeFilter.DeletedDiagnosticCode =>
				"Terminal.Diagnostic.GitStateDeleted",
			"DPX-SELECTION-PATH-MISSING" =>
				"Terminal.Diagnostic.SelectedPathMissing",
			"DPX-PROJECT-ROOT-ACCESS-DENIED" =>
				"Terminal.Diagnostic.ProjectRootAccessDenied",
			"DPX-PROJECT-PARTIAL-ACCESS" =>
				"Terminal.Diagnostic.ProjectPartialAccess",
			"DPX-PROJECT-SELECTION-WARNING" =>
				"Terminal.Diagnostic.SelectionUnavailable",
			_ => "Terminal.Diagnostic.Generic"
		};
		return localization[key];
	}

	internal static string ResolveMessage(
		LocalizationService localization,
		ContextDiagnostic diagnostic) =>
		diagnostic.Code switch
		{
			GitScopeFilter.DeletedDiagnosticCode =>
				localization.Format("Terminal.Diagnostic.GitStateDeleted", diagnostic.Count ?? 0),
			CodeCompressionAvailabilitySnapshot.DiagnosticCode =>
				localization.Format("Compression.Diagnostic.Unavailable", diagnostic.Message),
			_ => ResolveMessage(localization, diagnostic.Code)
		};
}
