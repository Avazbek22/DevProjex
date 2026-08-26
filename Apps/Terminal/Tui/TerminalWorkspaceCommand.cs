using Terminal.Gui.Input;

namespace DevProjex.Terminal.Tui;

internal enum TerminalWorkspaceCommandVerb
{
	Set,
	All,
	Type,
	View,
	Format,
	Search,
	Filter,
	Export,
	Copy,
	Analyze,
	Branch,
	Update,
	Recent,
	Profile,
	Refresh,
	Language,
	Help,
	Quit
}

internal sealed record TerminalWorkspaceCommandDefinition(
	string Id,
	TerminalWorkspaceCommandVerb Verb,
	string Token,
	string Syntax,
	string Example,
	string TitleKey,
	string DescriptionKey,
	string SchemaKey);

internal sealed record TerminalWorkspaceCommand(
	TerminalWorkspaceCommandDefinition Definition,
	string? Target = null,
	bool? Enabled = null,
	IReadOnlyList<string>? Values = null,
	ProjectContextView? View = null,
	ProjectContextDocumentFormat? Format = null,
	ProjectCopyExportFormat? ProjectExportFormat = null,
	string? Text = null,
	string? Destination = null);

internal enum TerminalWorkspaceCommandErrorCode
{
	EmptyInput,
	UnterminatedQuote,
	UnknownVerb,
	MissingArgument,
	UnexpectedArgument,
	UnknownToken,
	InvalidValue,
	UnknownLanguage
}

internal sealed record TerminalWorkspaceCommandError(
	TerminalWorkspaceCommandErrorCode Code,
	int Position,
	string? Value,
	IReadOnlyList<string> Candidates);

internal readonly record struct TerminalWorkspaceCommandParseResult(
	TerminalWorkspaceCommand? Command,
	TerminalWorkspaceCommandError? Error)
{
	public bool IsSuccess => Command is not null && Error is null;

	public static TerminalWorkspaceCommandParseResult Success(TerminalWorkspaceCommand command) =>
		new(command, null);

	public static TerminalWorkspaceCommandParseResult Failure(TerminalWorkspaceCommandError error) =>
		new(null, error);
}

internal sealed record TerminalWorkspaceCommandParseContext(
	IReadOnlyList<string> AvailableExtensions)
{
	public static TerminalWorkspaceCommandParseContext Empty { get; } = new([]);
}

internal sealed record TerminalWorkspaceCommandCompletionCandidate(
	string Token,
	string CompletedText,
	int CursorPosition);

internal sealed record TerminalWorkspaceCommandCompletion(
	IReadOnlyList<TerminalWorkspaceCommandCompletionCandidate> Candidates,
	string? GhostSuffix,
	string? SchemaKey)
{
	public static TerminalWorkspaceCommandCompletion Empty { get; } = new([], null, null);
}

internal static class TerminalWorkspaceCommandCatalog
{
	public static IReadOnlyList<TerminalWorkspaceCommandDefinition> All { get; } =
	[
		Define(
			TerminalWorkspaceCommandVerb.Set,
			"set",
			"set <option> <on|off>",
			"set hide-secrets on"),
		Define(
			TerminalWorkspaceCommandVerb.All,
			"all",
			"all <types|exclusions|content> <on|off>",
			"all content on"),
		Define(
			TerminalWorkspaceCommandVerb.Type,
			"type",
			"type <.ext> [<.ext>...] <on|off>",
			"type .cs on"),
		Define(
			TerminalWorkspaceCommandVerb.View,
			"view",
			"view <tree|content|tree-content>",
			"view content"),
		Define(
			TerminalWorkspaceCommandVerb.Format,
			"format",
			"format <text|markdown|json|xml>",
			"format json"),
		Define(
			TerminalWorkspaceCommandVerb.Search,
			"search",
			"search [text]",
			"search TODO"),
		Define(
			TerminalWorkspaceCommandVerb.Filter,
			"filter",
			"filter [text]",
			"filter generated"),
		Define(
			TerminalWorkspaceCommandVerb.Export,
			"export",
			"export <context|zip|folder> ...",
			"export context markdown context.md"),
		Define(
			TerminalWorkspaceCommandVerb.Copy,
			"copy",
			"copy [tree|content|tree-content] [text|markdown|json|xml]",
			"copy content markdown"),
		Define(
			TerminalWorkspaceCommandVerb.Analyze,
			"analyze",
			"analyze",
			"analyze"),
		Define(
			TerminalWorkspaceCommandVerb.Branch,
			"branch",
			"branch [name]",
			"branch feature/review"),
		Define(
			TerminalWorkspaceCommandVerb.Update,
			"update",
			"update",
			"update"),
		Define(
			TerminalWorkspaceCommandVerb.Recent,
			"recent",
			"recent",
			"recent"),
		Define(
			TerminalWorkspaceCommandVerb.Profile,
			"profile",
			"profile save [name]",
			"profile save \"Review Settings\""),
		Define(
			TerminalWorkspaceCommandVerb.Refresh,
			"refresh",
			"refresh",
			"refresh"),
		Define(
			TerminalWorkspaceCommandVerb.Language,
			"language",
			"language [code]",
			"language ja"),
		Define(
			TerminalWorkspaceCommandVerb.Help,
			"help",
			"help [verb]",
			"help set"),
		Define(
			TerminalWorkspaceCommandVerb.Quit,
			"quit",
			"quit",
			"quit")
	];

	public static IReadOnlyList<string> VerbTokens { get; } =
		All.Select(static definition => definition.Token).ToArray();

	public static TerminalWorkspaceCommandDefinition Get(TerminalWorkspaceCommandVerb verb) =>
		All.First(definition => definition.Verb == verb);

	public static bool TryGet(string token, out TerminalWorkspaceCommandDefinition definition)
	{
		definition = All.FirstOrDefault(candidate =>
			string.Equals(candidate.Token, token, StringComparison.OrdinalIgnoreCase))!;
		return definition is not null;
	}

	private static TerminalWorkspaceCommandDefinition Define(
		TerminalWorkspaceCommandVerb verb,
		string token,
		string syntax,
		string example) =>
		new(
			$"workspace.command.{token}",
			verb,
			token,
			syntax,
			example,
			$"Terminal.Tui.Command.{verb}.Title",
			$"Terminal.Tui.Command.{verb}.Description",
			$"Terminal.Tui.Command.{verb}.Schema");
}

internal static class TerminalWorkspaceCommandKey
{
	public static bool IsActivation(Key key) =>
		string.Equals(key.AsGrapheme, ":", StringComparison.Ordinal) ||
		key.AsRune.Value == ':' ||
		key.NoShift.AsRune.Value == ':' ||
		(key.IsShift && key.NoShift.AsRune.Value == ';');
}
