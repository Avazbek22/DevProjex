using DevProjex.Terminal.CommandLine;
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
			.Border(TableBorder.Rounded)
			.BorderColor(Color.Grey)
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.Field"])}[/]")
			.AddColumn($"[cyan]{Markup.Escape(_localization["Terminal.Analysis.Value"])}[/]");
		table.AddRow(_localization["Terminal.Analysis.Project"], Markup.Escape(plan.SourceRoot));
		table.AddRow(_localization["Terminal.Analysis.Profile"], Markup.Escape(FormatProfile(plan.Selection.ProfileSource)));
		table.AddRow(_localization["Terminal.Analysis.GitMode"], Markup.Escape(FormatGitMode(plan.Selection.GitMode!.Value)));
		table.AddRow(_localization["Terminal.Analysis.Exclusions"], Markup.Escape(string.Join(", ", plan.Selection.Exclusions!)));
		table.AddRow(_localization["Terminal.Analysis.Roots"], Markup.Escape(string.Join(", ", plan.SelectedRoots)));
		table.AddRow(_localization["Terminal.Analysis.Extensions"], Markup.Escape(string.Join(", ", plan.SelectedExtensions)));
		table.AddRow(_localization["Terminal.Analysis.Files"], plan.IncludedFiles.Count.ToString());
		table.AddRow(_localization["Terminal.Analysis.Folders"], plan.IncludedFolders.Count.ToString());
		table.AddRow(_localization["Terminal.Analysis.Size"], $"{plan.IncludedBytes:N0} B");
		table.AddRow(_localization["Terminal.Analysis.Characters"], plan.Analysis.Metrics.Content.Chars.ToString());
		table.AddRow(_localization["Terminal.Analysis.Tokens"], plan.Analysis.Metrics.Content.Tokens.ToString());
		table.AddRow(_localization["Terminal.Analysis.Fingerprint"], plan.Fingerprint);
		console.Write(table);
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

	private static string FormatProfile(ProjectProfileReference? profile) =>
		profile?.Kind switch
		{
			ProjectProfileSourceKind.Local => "local",
			ProjectProfileSourceKind.Portable => profile.Path ?? "portable",
			_ => "standard"
		};

	private static string FormatGitMode(GitFilteringMode mode) =>
		mode switch
		{
			GitFilteringMode.RespectGitIgnore => "gitignore",
			GitFilteringMode.TrackedFilesOnly => "tracked",
			_ => "none"
		};
}
