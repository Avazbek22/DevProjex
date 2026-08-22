using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

public sealed class HumanOutputRenderer(
	ITerminalEnvironment environment,
	TerminalOutputOptions options,
	LocalizationService? localization = null)
{
	private readonly LocalizationService _localization = localization ?? new LocalizationService(
		new JsonLocalizationCatalog(),
		AppLanguage.En);
	private readonly TerminalCapabilities _capabilities =
		TerminalCapabilities.Resolve(environment, options, forStandardError: false);

	public void WriteAnalysis(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var console = AnsiConsoleFactory.Create(environment.Output, _capabilities);
		var table = new Table()
			.Border(_capabilities.UseUnicode ? TableBorder.Rounded : TableBorder.Ascii)
			.BorderColor(Color.Grey)
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.Field"])}[/]")
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.Value"])}[/]");
		foreach (var row in AnalysisTextFormatter.BuildRows(plan, _localization))
		{
			table.AddRow(
				Markup.Escape(row.Label),
				Markup.Escape(row.Value));
		}
		console.Write(table);
		if (plan.Findings is not { } findings)
			return;

		console.WriteLine();
		var findingsTable = new Table()
			.Border(_capabilities.UseUnicode ? TableBorder.Rounded : TableBorder.Ascii)
			.BorderColor(Color.Grey)
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.FindingCategory"])}[/]")
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.FindingRule"])}[/]")
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.FindingLocation"])}[/]");
		foreach (var finding in findings)
		{
			var columns = AnalysisTextFormatter.CreateFindingColumns(finding);
			findingsTable.AddRow(columns.Select(Markup.Escape).ToArray());
		}
		console.Write(findingsTable);
	}

	public void WriteSuccessPath(string path)
	{
		environment.Output.WriteLine(Path.GetFullPath(path));
	}

	public void WriteStatus(string message)
	{
		if (options.Verbosity is TerminalVerbosity.Quiet or TerminalVerbosity.Minimal)
			return;

		var console = AnsiConsoleFactory.Create(
			environment.Error,
			TerminalCapabilities.Resolve(environment, options, forStandardError: true));
		console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
	}

}
