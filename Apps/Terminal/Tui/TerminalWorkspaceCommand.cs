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
	Diagnostics,
	Help,
	Quit
}

internal sealed record TerminalWorkspaceCommandDefinition(
	string Id,
	TerminalWorkspaceCommandVerb Verb,
	TerminalWorkspaceCommandGrammar Grammar,
	TerminalWorkspaceCommandHandler Handler,
	TerminalWorkspaceCommandAvailability Availability,
	string Token,
	string Syntax,
	string Example,
	string TitleKey,
	string DescriptionKey,
	string SchemaKey);

internal delegate TerminalWorkspaceCommandExecutionResult TerminalWorkspaceCommandHandler(
	TerminalWorkspaceSession session,
	TerminalWorkspaceCommand command);

internal enum TerminalWorkspaceCommandAvailability
{
	Workspace,
	GitClone,
	Always
}

internal enum TerminalWorkspaceCommandGrammar
{
	ToggleOption,
	ToggleGroup,
	ToggleTypes,
	View,
	Format,
	Text,
	Export,
	Copy,
	OptionalText,
	Profile,
	Language,
	Help,
	None
}

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
	IReadOnlyList<string> AvailableExtensions,
	IReadOnlySet<TerminalWorkspaceCommandVerb>? AllowedVerbs = null,
	string? WorkingDirectory = null)
{
	public static TerminalWorkspaceCommandParseContext Empty { get; } = new([]);
	public IReadOnlyList<string> VerbTokens => AllowedVerbs is null
		? TerminalWorkspaceCommandCatalog.VerbTokens
		: TerminalWorkspaceCommandCatalog.All
			.Where(definition => AllowedVerbs.Contains(definition.Verb))
			.Select(static definition => definition.Token)
			.ToArray();
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
			"set hide-secrets on",
			static (session, command) => session.ExecuteSetCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.All,
			"all",
			"all <types|exclusions|content> <on|off>",
			"all content on",
			static (session, command) => session.ExecuteAllCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Type,
			"type",
			"type <.ext> [<.ext>...] <on|off>",
			"type .cs on",
			static (session, command) => session.ExecuteTypeCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.View,
			"view",
			"view <tree|content|tree-content>",
			"view content",
			static (session, command) => session.ExecuteViewCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Format,
			"format",
			"format <text|markdown|json|xml>",
			"format json",
			static (session, command) => session.ExecuteFormatCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Search,
			"search",
			"search [text]",
			"search TODO",
			static (session, command) => session.ExecuteSearchCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Filter,
			"filter",
			"filter [text]",
			"filter generated",
			static (session, command) => session.ExecuteFilterCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Export,
			"export",
			"export <context|zip|folder> ...",
			"export context markdown context.md",
			static (session, command) => session.ExecuteExportCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Copy,
			"copy",
			"copy [tree|content|tree-content] [text|markdown|json|xml]",
			"copy content markdown",
			static (session, command) => session.ExecuteCopyCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Analyze,
			"analyze",
			"analyze",
			"analyze",
			static (session, command) => session.ExecuteAnalyzeCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Branch,
			"branch",
			"branch [name]",
			"branch feature/review",
			static (session, command) => session.ExecuteBranchCommand(command),
			TerminalWorkspaceCommandAvailability.GitClone),
		Define(
			TerminalWorkspaceCommandVerb.Update,
			"update",
			"update",
			"update",
			static (session, command) => session.ExecuteUpdateCommand(command),
			TerminalWorkspaceCommandAvailability.GitClone),
		Define(
			TerminalWorkspaceCommandVerb.Recent,
			"recent",
			"recent",
			"recent",
			static (session, command) => session.ExecuteRecentCommand(command),
			TerminalWorkspaceCommandAvailability.Always),
		Define(
			TerminalWorkspaceCommandVerb.Profile,
			"profile",
			"profile save [name]",
			"profile save \"Review Settings\"",
			static (session, command) => session.ExecuteProfileCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Refresh,
			"refresh",
			"refresh",
			"refresh",
			static (session, command) => session.ExecuteRefreshCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Language,
			"language",
			"language [code]",
			"language ja",
			static (session, command) => session.ExecuteLanguageCommand(command),
			TerminalWorkspaceCommandAvailability.Always),
		Define(
			TerminalWorkspaceCommandVerb.Diagnostics,
			"diagnostics",
			"diagnostics",
			"diagnostics",
			static (session, command) => session.ExecuteDiagnosticsCommand(command)),
		Define(
			TerminalWorkspaceCommandVerb.Help,
			"help",
			"help [verb]",
			"help set",
			static (session, command) => session.ExecuteHelpCommand(command),
			TerminalWorkspaceCommandAvailability.Always),
		Define(
			TerminalWorkspaceCommandVerb.Quit,
			"quit",
			"quit",
			"quit",
			static (session, command) => session.ExecuteQuitCommand(command),
			TerminalWorkspaceCommandAvailability.Always)
	];

	public static IReadOnlyList<string> VerbTokens { get; } =
		All.Select(static definition => definition.Token).ToArray();
	private static readonly IReadOnlyDictionary<TerminalWorkspaceCommandVerb, TerminalWorkspaceCommandDefinition>
		ByVerb = All.ToDictionary(static definition => definition.Verb);
	private static readonly IReadOnlyDictionary<string, TerminalWorkspaceCommandDefinition> ByToken =
		All.ToDictionary(static definition => definition.Token, StringComparer.OrdinalIgnoreCase);

	public static TerminalWorkspaceCommandDefinition Get(TerminalWorkspaceCommandVerb verb) =>
		ByVerb[verb];

	public static bool TryGet(string token, out TerminalWorkspaceCommandDefinition definition)
	{
		return ByToken.TryGetValue(token, out definition!);
	}

	private static TerminalWorkspaceCommandDefinition Define(
		TerminalWorkspaceCommandVerb verb,
		string token,
		string syntax,
		string example,
		TerminalWorkspaceCommandHandler handler,
		TerminalWorkspaceCommandAvailability availability = TerminalWorkspaceCommandAvailability.Workspace) =>
		new(
			$"workspace.command.{token}",
			verb,
			ResolveGrammar(verb),
			handler,
			availability,
			token,
			syntax,
			example,
			$"Terminal.Tui.Command.{verb}.Title",
			$"Terminal.Tui.Command.{verb}.Description",
			$"Terminal.Tui.Command.{verb}.Schema");

	private static TerminalWorkspaceCommandGrammar ResolveGrammar(TerminalWorkspaceCommandVerb verb) =>
		verb switch
		{
			TerminalWorkspaceCommandVerb.Set => TerminalWorkspaceCommandGrammar.ToggleOption,
			TerminalWorkspaceCommandVerb.All => TerminalWorkspaceCommandGrammar.ToggleGroup,
			TerminalWorkspaceCommandVerb.Type => TerminalWorkspaceCommandGrammar.ToggleTypes,
			TerminalWorkspaceCommandVerb.View => TerminalWorkspaceCommandGrammar.View,
			TerminalWorkspaceCommandVerb.Format => TerminalWorkspaceCommandGrammar.Format,
			TerminalWorkspaceCommandVerb.Search or TerminalWorkspaceCommandVerb.Filter =>
				TerminalWorkspaceCommandGrammar.Text,
			TerminalWorkspaceCommandVerb.Export => TerminalWorkspaceCommandGrammar.Export,
			TerminalWorkspaceCommandVerb.Copy => TerminalWorkspaceCommandGrammar.Copy,
			TerminalWorkspaceCommandVerb.Branch => TerminalWorkspaceCommandGrammar.OptionalText,
			TerminalWorkspaceCommandVerb.Profile => TerminalWorkspaceCommandGrammar.Profile,
			TerminalWorkspaceCommandVerb.Language => TerminalWorkspaceCommandGrammar.Language,
			TerminalWorkspaceCommandVerb.Help => TerminalWorkspaceCommandGrammar.Help,
			_ => TerminalWorkspaceCommandGrammar.None
		};
}

internal static class TerminalWorkspaceCommandKey
{
	public static bool IsActivation(Key key) =>
		string.Equals(key.AsGrapheme, ":", StringComparison.Ordinal) ||
		key.AsRune.Value == ':' ||
		key.NoShift.AsRune.Value == ':' ||
		(key.IsShift && key.NoShift.AsRune.Value == ';');
}
