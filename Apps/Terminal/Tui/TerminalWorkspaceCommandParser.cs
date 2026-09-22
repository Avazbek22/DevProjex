using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalWorkspaceCommandParser
{
	private const int MaximumFileSystemCompletionCandidates = 100;
	private static readonly ConditionalWeakTable<TreeNodeDescriptor, KnownProjectCompletionPaths>
		KnownProjectPathsCache = new();
	private static readonly string[] ToggleValues = ["on", "off"];
	private static readonly string[] GitModeValues =
		["off", "gitignore", "tracked", "staged", "changes", "diff:<ref>..<ref>"];
	private static readonly string[] AggregateTargets = ["types", "exclusions", "content"];
	private static readonly string[] ExportTargets = ["context", "zip", "folder"];
	private static readonly string[] ProfileTargets = ["save", "load", "show", "reset"];
	private static readonly string[] McpTargets = ["connect", "log"];
	private static readonly string[] McpClients = ["claude-code", "codex", "cursor", "vscode", "json"];
	private static readonly string[] McpModes = ["live", "standard"];
	private static readonly string[] McpLogTargets = ["session", "last", "export", "clear"];
	private static readonly string[] McpLogFormats = ["markdown", "json"];
	private static readonly string[] RelatedOptions = ["--direction", "--depth"];
	private static readonly string[] RelatedDirections = ["dependencies", "dependents", "both"];
	private static readonly string[] RelatedDepths = Enumerable.Range(1, 10)
		.Select(static depth => depth.ToString(CultureInfo.InvariantCulture))
		.ToArray();
	private static readonly IReadOnlyList<string> LanguageCodes = CliChoiceSets.Language.Tokens;

	private static readonly IReadOnlyList<string> SetTargets =
	[
		.. ProjectPresentationCatalog.ContentTransformations.Select(static item => item.Token),
		.. ProjectPresentationCatalog.Exclusions.Select(static item => item.Token),
		"gitignore",
		"tracked",
		"git",
		"activity"
	];

	private delegate TerminalWorkspaceCommandParseResult GrammarParser(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context);

	private delegate CompletionCandidateSource GrammarCompleter(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken);

	private sealed record GrammarHandler(GrammarParser Parse, GrammarCompleter Complete);

	private static readonly IReadOnlyDictionary<TerminalWorkspaceCommandGrammar, GrammarHandler>
		GrammarHandlers = new Dictionary<TerminalWorkspaceCommandGrammar, GrammarHandler>
		{
			[TerminalWorkspaceCommandGrammar.ToggleOption] = new(
				static (definition, tokens, _) => ParseSet(definition, tokens),
				CompleteToggleOption),
			[TerminalWorkspaceCommandGrammar.ToggleGroup] = new(
				static (definition, tokens, _) => ParseAll(definition, tokens),
				CompleteToggleGroup),
			[TerminalWorkspaceCommandGrammar.ToggleTypes] = new(
				ParseType,
				CompleteTypes),
			[TerminalWorkspaceCommandGrammar.Select] = new(
				static (definition, tokens, _) => ParseSelect(definition, tokens),
				CompleteSelect),
			[TerminalWorkspaceCommandGrammar.View] = new(
				static (definition, tokens, _) => ParseView(definition, tokens),
				CompleteView),
			[TerminalWorkspaceCommandGrammar.Format] = new(
				static (definition, tokens, _) => ParseFormat(definition, tokens),
				CompleteFormat),
			[TerminalWorkspaceCommandGrammar.Text] = new(
				static (definition, tokens, _) => ParseText(definition, tokens),
				NoCompletions),
			[TerminalWorkspaceCommandGrammar.Export] = new(
				static (definition, tokens, _) => ParseExport(definition, tokens),
				CompleteExport),
			[TerminalWorkspaceCommandGrammar.Copy] = new(
				static (definition, tokens, _) => ParseCopy(definition, tokens),
				CompleteCopy),
			[TerminalWorkspaceCommandGrammar.OptionalText] = new(
				static (definition, tokens, _) => ParseOptionalText(definition, tokens),
				NoCompletions),
			[TerminalWorkspaceCommandGrammar.RequiredText] = new(
				static (definition, tokens, _) => ParseRequiredText(definition, tokens),
				CompleteRequiredPath),
			[TerminalWorkspaceCommandGrammar.Profile] = new(
				static (definition, tokens, _) => ParseProfile(definition, tokens),
				CompleteProfile),
			[TerminalWorkspaceCommandGrammar.McpConnection] = new(
				static (definition, tokens, _) => ParseMcpConnection(definition, tokens),
				CompleteMcpConnection),
			[TerminalWorkspaceCommandGrammar.Related] = new(
				static (definition, tokens, _) => ParseRelated(definition, tokens),
				CompleteRelated),
			[TerminalWorkspaceCommandGrammar.Language] = new(
				static (definition, tokens, _) => ParseLanguage(definition, tokens),
				CompleteLanguage),
			[TerminalWorkspaceCommandGrammar.Help] = new(
				static (definition, tokens, _) => ParseHelp(definition, tokens),
				CompleteHelp),
			[TerminalWorkspaceCommandGrammar.None] = new(
				static (definition, tokens, _) => ParseWithoutArguments(definition, tokens),
				NoCompletions)
		};

	static TerminalWorkspaceCommandParser()
	{
		if (GrammarHandlers.Count != Enum.GetValues<TerminalWorkspaceCommandGrammar>().Length)
			throw new InvalidOperationException("The terminal command grammar registry is incomplete.");
	}

	internal static int RegisteredGrammarCount => GrammarHandlers.Count;

	internal static (
		IReadOnlyList<string> Paths,
		IReadOnlyList<string> Files) GetKnownProjectCompletionPaths(
		TreeNodeDescriptor root,
		string sourceRoot)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
		var known = KnownProjectPathsCache.GetValue(
			root,
			key => BuildKnownProjectCompletionPaths(key, sourceRoot));
		return (known.Paths, known.Files);
	}

	public TerminalWorkspaceCommandParseResult Parse(
		string? text,
		TerminalWorkspaceCommandParseContext? context = null)
	{
		context ??= TerminalWorkspaceCommandParseContext.Empty;
		var tokenization = Tokenize(text ?? string.Empty, tolerateUnterminatedQuote: false);
		if (tokenization.Error is not null)
			return TerminalWorkspaceCommandParseResult.Failure(tokenization.Error);
		if (tokenization.Tokens.Count == 0)
		{
			return Failure(
				TerminalWorkspaceCommandErrorCode.EmptyInput,
				0,
				null,
				context.VerbTokens);
		}

		var verbToken = tokenization.Tokens[0];
		if (!TerminalWorkspaceCommandCatalog.TryGet(verbToken.Value, out var definition) ||
			context.AllowedVerbs is not null && !context.AllowedVerbs.Contains(definition.Verb))
		{
			return Failure(
				TerminalWorkspaceCommandErrorCode.UnknownVerb,
				verbToken.Start,
				verbToken.Value,
				FindSimilar(verbToken.Value, context.VerbTokens));
		}

		return GrammarHandlers[definition.Grammar].Parse(definition, tokenization.Tokens, context);
	}

	public TerminalWorkspaceCommandCompletion GetCompletion(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context = null)
		=> GetCompletionCore(text, cursorPosition, context, CancellationToken.None);

	public ValueTask<TerminalWorkspaceCommandCompletion> GetCompletionAsync(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context,
		CancellationToken cancellationToken)
	{
		return new ValueTask<TerminalWorkspaceCommandCompletion>(Task.Run(
			() => GetCompletionCore(text, cursorPosition, context, cancellationToken),
			cancellationToken));
	}

	private static TerminalWorkspaceCommandCompletion GetCompletionCore(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context,
		CancellationToken cancellationToken)
	{
		var target = ResolveCompletionTarget(text, cursorPosition, context, cancellationToken);
		if (target is null)
			return TerminalWorkspaceCommandCompletion.Empty;

		var completion = BuildCompletion(target.Value);
		return target.Value.SchemaKey is { } schemaKey
			? completion with
			{
				GhostSuffix = null,
				SchemaKey = schemaKey
			}
			: completion;
	}

	public TerminalWorkspaceCommandGhostCompletion GetGhostCompletion(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context = null)
	{
		context ??= TerminalWorkspaceCommandParseContext.Empty;
		var ghostContext = context with
		{
			WorkingDirectory = null,
			ProfileDirectory = null
		};
		var target = ResolveCompletionTarget(
			text,
			cursorPosition,
			ghostContext,
			CancellationToken.None);
		if (target is null)
			return TerminalWorkspaceCommandGhostCompletion.Empty;
		if (target.Value.SchemaKey is { } schemaKey)
			return new TerminalWorkspaceCommandGhostCompletion(null, schemaKey);

		var ghostSuffix = ResolveGhostSuffix(target.Value.Current, target.Value.Candidates);
		return ghostSuffix is null
			? TerminalWorkspaceCommandGhostCompletion.Empty
			: new TerminalWorkspaceCommandGhostCompletion(ghostSuffix, null);
	}

	private static CompletionTarget? ResolveCompletionTarget(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context,
		CancellationToken cancellationToken)
	{
		text ??= string.Empty;
		context ??= TerminalWorkspaceCommandParseContext.Empty;
		cursorPosition = Math.Clamp(cursorPosition, 0, text.Length);
		var prefix = text[..cursorPosition];
		var tokenization = Tokenize(prefix, tolerateUnterminatedQuote: true);
		if (tokenization.Error is not null)
			return null;

		var atNewToken = prefix.Length > 0 && char.IsWhiteSpace(prefix[^1]);
		var tokens = tokenization.Tokens;
		if (tokens.Count == 0)
		{
			return new CompletionTarget(
				text,
				cursorPosition,
				0,
				string.Empty,
				new CompletionCandidateSource(context.VerbTokens),
				null,
				null);
		}

		if (tokens.Count == 1 && !atNewToken)
		{
			return new CompletionTarget(
				text,
				cursorPosition,
				tokens[0].Start,
				tokens[0].Value,
				new CompletionCandidateSource(context.VerbTokens),
				null,
				tokens[0].OpeningQuote);
		}

		if (!TerminalWorkspaceCommandCatalog.TryGet(tokens[0].Value, out var definition) ||
			context.AllowedVerbs is not null && !context.AllowedVerbs.Contains(definition.Verb))
			return null;

		var argumentIndex = atNewToken ? tokens.Count - 1 : tokens.Count - 2;
		var current = atNewToken ? string.Empty : tokens[^1].Value;
		var replacementStart = atNewToken ? cursorPosition : tokens[^1].Start;
		var candidates = GrammarHandlers[definition.Grammar].Complete(
			argumentIndex,
			tokens,
			current,
			context,
			cancellationToken);
		var schemaKey = atNewToken && tokens.Count == 1
			? definition.SchemaKey
			: null;
		return candidates.Count > 0 || schemaKey is not null
			? new CompletionTarget(
				text,
				cursorPosition,
				replacementStart,
				current,
				candidates,
				schemaKey,
				atNewToken ? null : tokens[^1].OpeningQuote)
			: null;
	}

	private static TerminalWorkspaceCommandParseResult ParseSet(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 3)
		{
			var candidates = tokens.Count == 1
				? SetTargets
				: string.Equals(tokens[1].Value, "git", StringComparison.OrdinalIgnoreCase)
					? GitModeValues
					: ToggleValues;
			return Missing(tokens, candidates);
		}
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		if (!Contains(SetTargets, tokens[1].Value))
			return Unknown(tokens[1], SetTargets);
		if (string.Equals(tokens[1].Value, "git", StringComparison.OrdinalIgnoreCase))
		{
			if (!TryParseGitModeValue(tokens[2].Value, out var mode, out var diffRange))
				return Unknown(tokens[2], GitModeValues);
			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
				definition,
				Target: "git",
				Text: GitScopeSelection.ToToken(mode, diffRange)));
		}
		if (!TryParseToggle(tokens[2], out var enabled, out var error))
			return TerminalWorkspaceCommandParseResult.Failure(error!);

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: Normalize(tokens[1].Value, SetTargets),
			Enabled: enabled));
	}

	private static bool TryParseGitModeValue(
		string value,
		out GitFilteringMode mode,
		out string? diffRange)
	{
		var isPublishedValue = value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
							   value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
							   value.Equals("gitignore", StringComparison.OrdinalIgnoreCase) ||
							   value.Equals("tracked", StringComparison.OrdinalIgnoreCase) ||
							   value.Equals("staged", StringComparison.OrdinalIgnoreCase) ||
							   value.Equals("changes", StringComparison.OrdinalIgnoreCase) ||
							   value.StartsWith(GitScopeSelection.DiffPrefix, StringComparison.OrdinalIgnoreCase);
		if (isPublishedValue && GitScopeSelection.TryParse(value, out mode, out diffRange))
			return true;

		mode = default;
		diffRange = null;
		return false;
	}

	private static TerminalWorkspaceCommandParseResult ParseAll(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 3)
			return Missing(tokens, tokens.Count == 1 ? AggregateTargets : ToggleValues);
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		if (!Contains(AggregateTargets, tokens[1].Value))
			return Unknown(tokens[1], AggregateTargets);
		if (!TryParseToggle(tokens[2], out var enabled, out var error))
			return TerminalWorkspaceCommandParseResult.Failure(error!);

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: Normalize(tokens[1].Value, AggregateTargets),
			Enabled: enabled));
	}

	private static TerminalWorkspaceCommandParseResult ParseType(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context)
	{
		if (tokens.Count < 3)
			return Missing(tokens, tokens.Count == 1 ? context.AvailableExtensions : ToggleValues);

		var valueToken = tokens[^1];
		if (!TryParseToggle(valueToken, out var enabled, out var error))
			return TerminalWorkspaceCommandParseResult.Failure(error!);

		var extensions = new List<string>(tokens.Count - 2);
		foreach (var token in tokens.Skip(1).Take(tokens.Count - 2))
		{
			var extension = context.AvailableExtensions.FirstOrDefault(candidate =>
				string.Equals(candidate, token.Value, StringComparison.OrdinalIgnoreCase));
			if (extension is null)
				return Unknown(token, context.AvailableExtensions);
			if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
				extensions.Add(extension);
		}

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Enabled: enabled,
			Values: extensions));
	}

	private static TerminalWorkspaceCommandParseResult ParseSelect(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 3)
			return Missing(tokens, tokens.Count == 1 ? ["all"] : ToggleValues);

		var valueToken = tokens[^1];
		if (!TryParseToggle(valueToken, out var enabled, out var error))
			return TerminalWorkspaceCommandParseResult.Failure(error!);

		var selectors = tokens.Skip(1).Take(tokens.Count - 2)
			.Select(static token => token.Value)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (selectors.Contains("all", StringComparer.OrdinalIgnoreCase) && selectors.Length != 1)
			return Unexpected(tokens[2]);

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Enabled: enabled,
			Values: selectors));
	}

	private static TerminalWorkspaceCommandParseResult ParseView(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, CliChoiceSets.ContextView.Tokens);
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		if (!CliChoiceSets.ContextView.TryParse(tokens[1].Value, out var view))
			return Unknown(tokens[1], CliChoiceSets.ContextView.Tokens);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			View: view));
	}

	private static TerminalWorkspaceCommandParseResult ParseFormat(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, CliChoiceSets.ContextDocumentFormat.Tokens);
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		if (!CliChoiceSets.ContextDocumentFormat.TryParse(tokens[1].Value, out var format))
			return Unknown(tokens[1], CliChoiceSets.ContextDocumentFormat.Tokens);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Format: format));
	}

	private static TerminalWorkspaceCommandParseResult ParseText(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens) =>
		TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Text: tokens.Count == 1
				? string.Empty
				: string.Join(' ', tokens.Skip(1).Select(static token => token.Value))));

	private static TerminalWorkspaceCommandParseResult ParseExport(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, ExportTargets);
		var target = tokens[1].Value.ToLowerInvariant();
		if (!Contains(ExportTargets, target))
			return Unknown(tokens[1], ExportTargets);

		if (target is "zip" or "folder")
		{
			if (tokens.Count < 3)
				return Missing(tokens, []);
			if (tokens.Count > 3)
				return Unexpected(tokens[3]);
			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
				definition,
				Target: target,
				ProjectExportFormat: target == "zip"
					? ProjectCopyExportFormat.Zip
					: ProjectCopyExportFormat.Folder,
				Destination: tokens[2].Value));
		}

		if (tokens.Count > 4)
			return Unexpected(tokens[4]);

		ProjectContextDocumentFormat? format = null;
		string? destination = null;
		if (tokens.Count >= 3)
		{
			if (CliChoiceSets.ContextDocumentFormat.TryParse(tokens[2].Value, out var parsedFormat))
			{
				format = parsedFormat;
				if (tokens.Count == 4)
					destination = tokens[3].Value;
			}
			else
			{
				if (tokens.Count == 4)
					return Unknown(tokens[2], CliChoiceSets.ContextDocumentFormat.Tokens);
				destination = tokens[2].Value;
			}
		}

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: "context",
			Format: format,
			Destination: destination));
	}

	private static TerminalWorkspaceCommandParseResult ParseCopy(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);

		ProjectContextView? view = null;
		ProjectContextDocumentFormat? format = null;
		if (tokens.Count >= 2)
		{
			if (!CliChoiceSets.ContextView.TryParse(tokens[1].Value, out var parsedView))
				return Unknown(tokens[1], CliChoiceSets.ContextView.Tokens);
			view = parsedView;
		}
		if (tokens.Count == 3)
		{
			if (!CliChoiceSets.ContextDocumentFormat.TryParse(tokens[2].Value, out var parsedFormat))
				return Unknown(tokens[2], CliChoiceSets.ContextDocumentFormat.Tokens);
			format = parsedFormat;
		}

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			View: view,
			Format: format));
	}

	private static TerminalWorkspaceCommandParseResult ParseOptionalText(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Text: tokens.Count == 2 ? tokens[1].Value : null));
	}

	private static TerminalWorkspaceCommandParseResult ParseRequiredText(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, []);
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Text: tokens[1].Value));
	}

	private static TerminalWorkspaceCommandParseResult ParseProfile(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, ProfileTargets);
		if (!Contains(ProfileTargets, tokens[1].Value))
			return Unknown(tokens[1], ProfileTargets);
		var target = Normalize(tokens[1].Value, ProfileTargets);
		if (target == "load" && tokens.Count < 3)
			return Missing(tokens, []);
		if (target is "show" or "reset" && tokens.Count > 2)
			return Unexpected(tokens[2]);
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: target,
			Text: tokens.Count == 3 ? tokens[2].Value : null));
	}

	private static TerminalWorkspaceCommandParseResult ParseMcpConnection(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count >= 2 && string.Equals(tokens[1].Value, "log", StringComparison.OrdinalIgnoreCase))
			return ParseMcpLog(definition, tokens);

		if (tokens.Count >= 2 && Contains(McpTargets, tokens[1].Value))
		{
			if (tokens.Count > 4)
				return Unexpected(tokens[4]);
			if (tokens.Count < 3)
				return Missing(tokens, McpClients);
			if (!Contains(McpClients, tokens[2].Value))
				return Unknown(tokens[2], McpClients);
			if (tokens.Count == 4 && !Contains(McpModes, tokens[3].Value))
				return Unknown(tokens[3], McpModes);

			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
				definition,
					Target: Normalize(tokens[2].Value, McpClients),
					Text: tokens.Count == 4 ? Normalize(tokens[3].Value, McpModes) : "live",
					McpAction: TerminalWorkspaceMcpAction.Connect));
		}

		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		if (tokens.Count >= 2 && !Contains(McpClients, tokens[1].Value))
			return Unknown(tokens[1], [.. McpTargets, .. McpClients]);
		if (tokens.Count == 3 && !Contains(McpModes, tokens[2].Value))
			return Unknown(tokens[2], McpModes);

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: tokens.Count >= 2 ? Normalize(tokens[1].Value, McpClients) : "claude-code",
			Text: tokens.Count == 3 ? Normalize(tokens[2].Value, McpModes) : "live"));
	}

	private static TerminalWorkspaceCommandParseResult ParseMcpLog(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count == 2)
		{
			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
				definition,
				McpAction: TerminalWorkspaceMcpAction.ShowLog));
		}

		if (!Contains(McpLogTargets, tokens[2].Value))
			return Unknown(tokens[2], McpLogTargets);
		var target = Normalize(tokens[2].Value, McpLogTargets);
		switch (target)
		{
			case "last":
				if (tokens.Count > 3)
					return Unexpected(tokens[3]);
				return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
					definition,
					Target: "last",
					McpAction: TerminalWorkspaceMcpAction.ShowLog));
			case "session":
				if (tokens.Count < 4)
					return Missing(tokens, ["id"]);
				if (tokens.Count > 4)
					return Unexpected(tokens[4]);
				return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
					definition,
					Target: tokens[3].Value,
					McpAction: TerminalWorkspaceMcpAction.ShowLog));
			case "clear":
				if (tokens.Count > 3)
					return Unexpected(tokens[3]);
				return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
					definition,
					McpAction: TerminalWorkspaceMcpAction.ClearLog));
			case "export":
				return ParseMcpLogExport(definition, tokens);
			default:
				throw new ArgumentOutOfRangeException(nameof(tokens));
		}
	}

	private static TerminalWorkspaceCommandParseResult ParseMcpLogExport(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 4)
			return Missing(tokens, ["path"]);

		var index = 4;
		var format = ProjectContextDocumentFormat.Markdown;
		if (tokens.Count > index && Contains(McpLogFormats, tokens[index].Value))
		{
			format = string.Equals(tokens[index].Value, "json", StringComparison.OrdinalIgnoreCase)
				? ProjectContextDocumentFormat.Json
				: ProjectContextDocumentFormat.Markdown;
			index++;
		}

		var session = "last";
		if (tokens.Count > index)
		{
			if (string.Equals(tokens[index].Value, "last", StringComparison.OrdinalIgnoreCase))
			{
				index++;
			}
			else if (string.Equals(tokens[index].Value, "session", StringComparison.OrdinalIgnoreCase))
			{
				index++;
				if (tokens.Count <= index)
					return Missing(tokens, ["id"]);
				session = tokens[index].Value;
				index++;
			}
			else
			{
				return Unknown(tokens[index], [.. McpLogFormats, "session", "last"]);
			}
		}

		if (tokens.Count > index)
			return Unexpected(tokens[index]);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: session,
			Format: format,
			Destination: tokens[3].Value,
			McpAction: TerminalWorkspaceMcpAction.ExportLog));
	}

	private static TerminalWorkspaceCommandParseResult ParseRelated(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, ["path"]);

		var direction = "both";
		var depth = 1;
		var seenOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (var index = 2; index < tokens.Count; index += 2)
		{
			var option = tokens[index];
			if (!Contains(RelatedOptions, option.Value))
				return Unknown(option, RelatedOptions);
			if (!seenOptions.Add(option.Value))
				return Unexpected(option);
			if (index + 1 >= tokens.Count)
			{
				return Missing(tokens, string.Equals(option.Value, "--direction", StringComparison.OrdinalIgnoreCase)
					? RelatedDirections
					: RelatedDepths);
			}

			var value = tokens[index + 1];
			if (string.Equals(option.Value, "--direction", StringComparison.OrdinalIgnoreCase))
			{
				if (!Contains(RelatedDirections, value.Value))
					return Unknown(value, RelatedDirections);
				direction = Normalize(value.Value, RelatedDirections);
				continue;
			}

			if (!int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out depth) ||
				depth is < 1 or > 10)
			{
				return Failure(
					TerminalWorkspaceCommandErrorCode.InvalidValue,
					value.Start,
					value.Value,
					RelatedDepths);
			}
		}

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: tokens[1].Value,
			Text: direction,
			Depth: depth));
	}

	private static TerminalWorkspaceCommandParseResult ParseHelp(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		if (tokens.Count == 1)
			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(definition));
		if (!TerminalWorkspaceCommandCatalog.TryGet(tokens[1].Value, out var command))
			return Unknown(tokens[1], TerminalWorkspaceCommandCatalog.VerbTokens);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: command.Token));
	}

	private static TerminalWorkspaceCommandParseResult ParseLanguage(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count > 2)
			return Unexpected(tokens[2]);
		if (tokens.Count == 1)
			return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(definition));
		if (!AppLanguageUtility.TryParseCode(tokens[1].Value, out var language))
		{
			return Failure(
				TerminalWorkspaceCommandErrorCode.UnknownLanguage,
				tokens[1].Start,
				tokens[1].Value,
				FindSimilar(tokens[1].Value, LanguageCodes));
		}

		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Text: AppLanguageUtility.ToCode(language)));
	}

	private static TerminalWorkspaceCommandParseResult ParseWithoutArguments(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens) =>
		tokens.Count == 1
			? TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(definition))
			: Unexpected(tokens[1]);

	private static CompletionCandidateSource CompleteToggleOption(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex switch
		{
			0 => new CompletionCandidateSource(SetTargets),
			1 when tokens.Count > 1 &&
				   string.Equals(tokens[1].Value, "git", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(GitModeValues),
			1 => new CompletionCandidateSource(ToggleValues),
			_ => default
		};

	private static CompletionCandidateSource CompleteToggleGroup(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex switch
		{
			0 => new CompletionCandidateSource(AggregateTargets),
			1 => new CompletionCandidateSource(ToggleValues),
			_ => default
		};

	private static CompletionCandidateSource CompleteTypes(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex >= 0 ? ResolveTypeCompletions(tokens, context) : default;

	private static CompletionCandidateSource CompleteSelect(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken)
	{
		if (argumentIndex < 0)
			return default;
		var paths = context.KnownProjectPaths is { } knownPaths
			? ResolveKnownPathCompletions(current, knownPaths)
			: ResolvePathCompletions(current, context.WorkingDirectory, cancellationToken);
		return argumentIndex == 0
			? new CompletionCandidateSource(["all", .. paths], ToggleValues)
			: new CompletionCandidateSource(paths, ToggleValues);
	}

	private static CompletionCandidateSource CompleteView(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex == 0 ? new CompletionCandidateSource(CliChoiceSets.ContextView.Tokens) : default;

	private static CompletionCandidateSource CompleteFormat(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex == 0 ? new CompletionCandidateSource(CliChoiceSets.ContextDocumentFormat.Tokens) : default;

	private static CompletionCandidateSource CompleteExport(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken)
	{
		if (argumentIndex == 0)
			return new CompletionCandidateSource(ExportTargets);
		var isContext = tokens.Count > 1 &&
			string.Equals(tokens[1].Value, "context", StringComparison.OrdinalIgnoreCase);
		if (argumentIndex == 1 && isContext)
		{
			return new CompletionCandidateSource(
				CliChoiceSets.ContextDocumentFormat.Tokens,
				ResolvePathCompletions(current, context.WorkingDirectory, cancellationToken));
		}
		if (argumentIndex == 2 && isContext || argumentIndex == 1 && tokens.Count > 1)
			return new CompletionCandidateSource(ResolvePathCompletions(
				current,
				context.WorkingDirectory,
				cancellationToken));
		return default;
	}

	private static CompletionCandidateSource CompleteCopy(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex switch
		{
			0 => new CompletionCandidateSource(CliChoiceSets.ContextView.Tokens),
			1 => new CompletionCandidateSource(CliChoiceSets.ContextDocumentFormat.Tokens),
			_ => default
		};

	private static CompletionCandidateSource CompleteProfile(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex switch
		{
			0 => new CompletionCandidateSource(ProfileTargets),
			1 when tokens.Count > 1 &&
				string.Equals(tokens[1].Value, "load", StringComparison.OrdinalIgnoreCase) &&
				TerminalPortableProfilePathResolver.IsExplicitPath(current) =>
				new CompletionCandidateSource(ResolvePathCompletions(
					current,
					context.WorkingDirectory,
					cancellationToken)),
			1 when tokens.Count > 1 &&
				string.Equals(tokens[1].Value, "load", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(ResolveProfileNameCompletions(
					current,
					context.ProfileDirectory,
					cancellationToken)),
			_ => default
		};

	private static CompletionCandidateSource CompleteRequiredPath(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex == 0
			? new CompletionCandidateSource(ResolvePathCompletions(
				current,
				context.WorkingDirectory,
				cancellationToken))
			: default;

	private static CompletionCandidateSource CompleteMcpConnection(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex switch
		{
			0 => new CompletionCandidateSource(McpTargets, McpClients),
			1 when tokens.Count > 1 && string.Equals(tokens[1].Value, "connect", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(McpClients),
			2 when tokens.Count > 2 && string.Equals(tokens[1].Value, "connect", StringComparison.OrdinalIgnoreCase) && Contains(McpClients, tokens[2].Value) =>
				new CompletionCandidateSource(McpModes),
			1 when tokens.Count > 1 && string.Equals(tokens[1].Value, "log", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(McpLogTargets),
			2 when tokens.Count > 2 && string.Equals(tokens[1].Value, "log", StringComparison.OrdinalIgnoreCase) &&
								string.Equals(tokens[2].Value, "export", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(ResolvePathCompletions(
					current,
					context.WorkingDirectory,
					cancellationToken)),
			3 when tokens.Count > 3 && string.Equals(tokens[1].Value, "log", StringComparison.OrdinalIgnoreCase) &&
								string.Equals(tokens[2].Value, "export", StringComparison.OrdinalIgnoreCase) =>
				new CompletionCandidateSource(McpLogFormats, ["session", "last"]),
			4 when tokens.Count > 4 && string.Equals(tokens[1].Value, "log", StringComparison.OrdinalIgnoreCase) &&
								string.Equals(tokens[2].Value, "export", StringComparison.OrdinalIgnoreCase) &&
								Contains(McpLogFormats, tokens[4].Value) =>
				new CompletionCandidateSource(["session", "last"]),
			1 when tokens.Count > 1 && Contains(McpClients, tokens[1].Value) =>
				new CompletionCandidateSource(McpModes),
			_ => default
		};

	private static CompletionCandidateSource CompleteRelated(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken)
	{
		if (argumentIndex == 0)
		{
			return new CompletionCandidateSource(context.KnownProjectFiles is { } knownFiles
				? ResolveKnownPathCompletions(current, knownFiles)
				: ResolvePathCompletions(current, context.WorkingDirectory, cancellationToken));
		}
		if ((tokens.Count >= 2 && string.Equals(tokens[^1].Value, "--direction", StringComparison.OrdinalIgnoreCase)) ||
			(current.Length > 0 && tokens.Count >= 3 &&
			 string.Equals(tokens[^2].Value, "--direction", StringComparison.OrdinalIgnoreCase)))
		{
			return new CompletionCandidateSource(RelatedDirections);
		}
		if ((tokens.Count >= 2 && string.Equals(tokens[^1].Value, "--depth", StringComparison.OrdinalIgnoreCase)) ||
			(current.Length > 0 && tokens.Count >= 3 &&
			 string.Equals(tokens[^2].Value, "--depth", StringComparison.OrdinalIgnoreCase)))
		{
			return new CompletionCandidateSource(RelatedDepths);
		}

		var usedOptions = tokens
			.Skip(2)
			.Select(static token => token.Value)
			.Where(value => Contains(RelatedOptions, value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return new CompletionCandidateSource(
			RelatedOptions.Where(option => !usedOptions.Contains(option)).ToArray());
	}

	private static CompletionCandidateSource CompleteLanguage(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex == 0 ? new CompletionCandidateSource(LanguageCodes) : default;

	private static CompletionCandidateSource CompleteHelp(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) =>
		argumentIndex == 0 ? new CompletionCandidateSource(context.VerbTokens) : default;

	private static CompletionCandidateSource NoCompletions(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context,
		CancellationToken cancellationToken) => default;

	private static IReadOnlyList<string> ResolvePathCompletions(
		string current,
		string? workingDirectory,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(workingDirectory))
			return [];
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var baseDirectory = Path.GetFullPath(workingDirectory);
			var absolute = TerminalWorkspacePathResolver.Resolve(current, baseDirectory);
			var directory = Directory.Exists(absolute) ? absolute : Path.GetDirectoryName(absolute);
			var prefix = Directory.Exists(absolute) ? string.Empty : Path.GetFileName(absolute);
			if (directory is null || !Directory.Exists(directory))
				return [];
			var rooted = Path.IsPathRooted(current);
			var separatorIndex = Math.Max(
				current.LastIndexOf(Path.DirectorySeparatorChar),
				current.LastIndexOf(Path.AltDirectorySeparatorChar));
			var displayDirectory = separatorIndex >= 0 ? current[..(separatorIndex + 1)] : null;
			var matches = new List<string>(MaximumFileSystemCompletionCandidates + 1);
			foreach (var path in Directory.EnumerateFileSystemEntries(directory))
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
					continue;
				AddBoundedCompletionCandidate(
					matches,
					displayDirectory is not null
					? displayDirectory + Path.GetFileName(path)
					: rooted ? path : Path.GetRelativePath(baseDirectory, path),
					ProjectTreePathIdentity.CanonicalComparer);
			}
			return matches;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			ArgumentException or NotSupportedException)
		{
			return [];
		}
	}

	private static IReadOnlyList<string> ResolveProfileNameCompletions(
		string current,
		string? profileDirectory,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(profileDirectory))
			return [];
		try
		{
			var names = new List<string>(MaximumFileSystemCompletionCandidates + 1);
			foreach (var path in Directory.EnumerateFiles(
				profileDirectory,
				"*.json",
				SearchOption.TopDirectoryOnly))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var name = Path.GetFileNameWithoutExtension(path);
				if (string.IsNullOrWhiteSpace(name) ||
					!name.StartsWith(current, StringComparison.CurrentCultureIgnoreCase))
					continue;
				AddBoundedCompletionCandidate(
					names,
					name,
					StringComparer.CurrentCultureIgnoreCase);
			}
			return names;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			ArgumentException or NotSupportedException)
		{
			return [];
		}
	}

	private static void AddBoundedCompletionCandidate(
		List<string> candidates,
		string candidate,
		IComparer<string> comparer)
	{
		var index = candidates.BinarySearch(candidate, comparer);
		if (index < 0)
			index = ~index;
		else
		{
			while (index > 0 && comparer.Compare(candidates[index - 1], candidate) == 0)
				index--;
			while (index < candidates.Count && comparer.Compare(candidates[index], candidate) == 0 &&
				   StringComparer.Ordinal.Compare(candidates[index], candidate) <= 0)
			{
				index++;
			}
		}
		candidates.Insert(index, candidate);
		if (candidates.Count > MaximumFileSystemCompletionCandidates)
			candidates.RemoveAt(MaximumFileSystemCompletionCandidates);
	}

	private static IReadOnlyList<string> ResolveKnownPathCompletions(
		string current,
		IReadOnlyList<string> knownPaths)
	{
		var usesBackslash = current.Contains('\\');
		var explicitRelativePrefix = current.StartsWith("./", StringComparison.Ordinal) ||
			current.StartsWith(".\\", StringComparison.Ordinal);
		var normalizedCurrent = current.Replace('\\', '/');
		if (explicitRelativePrefix)
			normalizedCurrent = normalizedCurrent[2..];

		var matches = new List<string>(Math.Min(
			knownPaths.Count,
			MaximumFileSystemCompletionCandidates));
		foreach (var path in knownPaths)
		{
			var normalizedPath = path.Replace('\\', '/');
			if (!normalizedPath.StartsWith(normalizedCurrent, StringComparison.OrdinalIgnoreCase))
				continue;
			var display = explicitRelativePrefix ? "./" + normalizedPath : normalizedPath;
			matches.Add(usesBackslash ? display.Replace('/', '\\') : display);
			if (matches.Count == MaximumFileSystemCompletionCandidates)
				break;
		}
		return matches;
	}

	private static KnownProjectCompletionPaths BuildKnownProjectCompletionPaths(
		TreeNodeDescriptor root,
		string sourceRoot)
	{
		var paths = new List<string>();
		var files = new List<string>();
		var stack = new Stack<TreeNodeDescriptor>();
		for (var index = root.Children.Count - 1; index >= 0; index--)
			stack.Push(root.Children[index]);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			var relativePath = ProjectSelectionPath.NormalizeRelative(
				Path.GetRelativePath(sourceRoot, node.FullPath));
			if (relativePath.Length > 0 && relativePath != ".")
			{
				paths.Add(relativePath);
				if (!node.IsDirectory)
					files.Add(relativePath);
			}
			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}
		paths.Sort(ProjectTreePathIdentity.CanonicalComparer);
		files.Sort(ProjectTreePathIdentity.CanonicalComparer);
		return new KnownProjectCompletionPaths(paths, files);
	}

	private static CompletionCandidateSource ResolveTypeCompletions(
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context)
	{
		var hasExtension = false;
		for (var tokenIndex = 1; tokenIndex < tokens.Count && !hasExtension; tokenIndex++)
		{
			hasExtension = context.AvailableExtensions.Contains(
				tokens[tokenIndex].Value,
				StringComparer.OrdinalIgnoreCase);
		}
		return hasExtension
			? new CompletionCandidateSource(context.AvailableExtensions, ToggleValues)
			: new CompletionCandidateSource(context.AvailableExtensions);
	}

	private static TerminalWorkspaceCommandCompletion BuildCompletion(
		CompletionTarget target)
	{
		List<string>? matches = null;
		HashSet<string>? seen = null;
		for (var candidateIndex = 0; candidateIndex < target.Candidates.Count; candidateIndex++)
		{
			var candidate = target.Candidates[candidateIndex];
			if (!candidate.StartsWith(target.Current, StringComparison.OrdinalIgnoreCase))
				continue;

			seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (seen.Add(candidate))
				(matches ??= []).Add(candidate);
		}
		if (matches is null)
			return TerminalWorkspaceCommandCompletion.Empty;

		var replacementEnd = FindCompletionReplacementEnd(
			target.FullText,
			target.CursorPosition,
			target.OpeningQuote);
		var suffix = target.FullText[replacementEnd..];
		var candidates = matches.Select(candidate =>
		{
			var inserted = QuoteCompletionCandidate(candidate, target.OpeningQuote);
			var completed = target.FullText[..target.ReplacementStart] + inserted + suffix;
			return new TerminalWorkspaceCommandCompletionCandidate(
				candidate,
				completed,
				target.ReplacementStart + inserted.Length);
		}).ToArray();
		var ghost = matches[0].Length > target.Current.Length
			? matches[0][target.Current.Length..]
			: null;
		return new TerminalWorkspaceCommandCompletion(candidates, ghost, null);
	}

	private static int FindCompletionReplacementEnd(
		string text,
		int cursorPosition,
		char? openingQuote)
	{
		var index = cursorPosition;
		if (openingQuote is not { } quote)
		{
			while (index < text.Length && !char.IsWhiteSpace(text[index]))
				index++;
			return index;
		}

		while (index < text.Length)
		{
			if (text[index] != quote)
			{
				index++;
				continue;
			}
			if (index + 1 < text.Length && text[index + 1] == quote)
			{
				index += 2;
				continue;
			}
			return index + 1;
		}
		return index;
	}

	private static string QuoteCompletionCandidate(string candidate, char? openingQuote)
	{
		var quote = openingQuote;
		if (quote is null && candidate.Any(static character =>
			char.IsWhiteSpace(character) || character is '\'' or '"'))
		{
			quote = candidate.Contains('"') && !candidate.Contains('\'') ? '\'' : '"';
		}
		if (quote is null)
			return candidate;

		var escaped = candidate.Replace(
			quote.Value.ToString(),
			new string(quote.Value, 2),
			StringComparison.Ordinal);
		return $"{quote}{escaped}{quote}";
	}

	private static string? ResolveGhostSuffix(
		string current,
		CompletionCandidateSource candidates)
	{
		for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
		{
			var candidate = candidates[candidateIndex];
			if (candidate.StartsWith(current, StringComparison.OrdinalIgnoreCase))
			{
				return candidate.Length > current.Length
					? candidate[current.Length..]
					: null;
			}
		}

		return null;
	}

	private static bool TryParseToggle(
		ParsedToken token,
		out bool enabled,
		out TerminalWorkspaceCommandError? error)
	{
		if (string.Equals(token.Value, "on", StringComparison.OrdinalIgnoreCase))
		{
			enabled = true;
			error = null;
			return true;
		}
		if (string.Equals(token.Value, "off", StringComparison.OrdinalIgnoreCase))
		{
			enabled = false;
			error = null;
			return true;
		}

		enabled = false;
		error = new TerminalWorkspaceCommandError(
			TerminalWorkspaceCommandErrorCode.InvalidValue,
			token.Start,
			token.Value,
			FindSimilar(token.Value, ToggleValues));
		return false;
	}

	private static TerminalWorkspaceCommandParseResult Missing(
		IReadOnlyList<ParsedToken> tokens,
		IReadOnlyList<string> candidates) =>
		Failure(
			TerminalWorkspaceCommandErrorCode.MissingArgument,
			tokens.Count == 0 ? 0 : tokens[^1].End,
			null,
			candidates);

	private static TerminalWorkspaceCommandParseResult Unexpected(ParsedToken token) =>
		Failure(
			TerminalWorkspaceCommandErrorCode.UnexpectedArgument,
			token.Start,
			token.Value,
			[]);

	private static TerminalWorkspaceCommandParseResult Unknown(
		ParsedToken token,
		IReadOnlyList<string> candidates) =>
		Failure(
			TerminalWorkspaceCommandErrorCode.UnknownToken,
			token.Start,
			token.Value,
			FindSimilar(token.Value, candidates));

	private static TerminalWorkspaceCommandParseResult Failure(
		TerminalWorkspaceCommandErrorCode code,
		int position,
		string? value,
		IReadOnlyList<string> candidates) =>
		TerminalWorkspaceCommandParseResult.Failure(
			new TerminalWorkspaceCommandError(code, position, value, candidates));

	private static bool Contains(IEnumerable<string> values, string value) =>
		values.Contains(value, StringComparer.OrdinalIgnoreCase);

	private static string Normalize(string value, IReadOnlyList<string> values) =>
		values.First(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

	private static IReadOnlyList<string> FindSimilar(
		string value,
		IReadOnlyList<string> candidates) =>
		candidates
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(candidate => (Candidate: candidate, Distance: LevenshteinDistance(value, candidate)))
			.OrderBy(static item => item.Distance)
			.ThenBy(static item => item.Candidate, StringComparer.OrdinalIgnoreCase)
			.Take(3)
			.Select(static item => item.Candidate)
			.ToArray();

	private static int LevenshteinDistance(string left, string right)
	{
		left = left.ToLowerInvariant();
		right = right.ToLowerInvariant();
		var previous = Enumerable.Range(0, right.Length + 1).ToArray();
		var current = new int[right.Length + 1];
		for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
		{
			current[0] = leftIndex;
			for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
			{
				var substitution = previous[rightIndex - 1] +
					(left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
				current[rightIndex] = Math.Min(
					Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
					substitution);
			}
			(previous, current) = (current, previous);
		}
		return previous[right.Length];
	}

	private static TokenizationResult Tokenize(string text, bool tolerateUnterminatedQuote)
	{
		var tokens = new List<ParsedToken>();
		var index = 0;
		while (index < text.Length)
		{
			while (index < text.Length && char.IsWhiteSpace(text[index]))
				index++;
			if (index >= text.Length)
				break;

			var start = index;
			var value = new StringBuilder();
			char? quote = null;
			char? openingQuote = null;
			var quoteStart = -1;
			while (index < text.Length && (quote is not null || !char.IsWhiteSpace(text[index])))
			{
				var character = text[index];
				if (quote is null && character is '\'' or '"')
				{
					quote = character;
					if (index == start)
						openingQuote = character;
					quoteStart = index++;
					continue;
				}
				if (quote == character)
				{
					if (index + 1 < text.Length && text[index + 1] == character)
					{
						value.Append(character);
						index += 2;
						continue;
					}
					quote = null;
					index++;
					continue;
				}
				value.Append(character);
				index++;
			}

			if (quote is not null && !tolerateUnterminatedQuote)
			{
				return new TokenizationResult(
					[],
					new TerminalWorkspaceCommandError(
						TerminalWorkspaceCommandErrorCode.UnterminatedQuote,
						quoteStart,
						quote.ToString(),
						[]));
			}

			tokens.Add(new ParsedToken(value.ToString(), start, index, openingQuote));
		}

		return new TokenizationResult(tokens, null);
	}

	private readonly record struct CompletionTarget(
		string FullText,
		int CursorPosition,
		int ReplacementStart,
		string Current,
		CompletionCandidateSource Candidates,
		string? SchemaKey,
		char? OpeningQuote);

	private readonly struct CompletionCandidateSource(
		IReadOnlyList<string>? primary,
		IReadOnlyList<string>? secondary = null)
	{
		public int Count => (primary?.Count ?? 0) + (secondary?.Count ?? 0);

		public string this[int index]
		{
			get
			{
				var primaryCount = primary?.Count ?? 0;
				if ((uint)index >= (uint)Count)
					throw new ArgumentOutOfRangeException(nameof(index));
				return index < primaryCount
					? primary![index]
					: secondary![index - primaryCount];
			}
		}
	}

	private sealed record ParsedToken(string Value, int Start, int End, char? OpeningQuote);

	private sealed record TokenizationResult(
		IReadOnlyList<ParsedToken> Tokens,
		TerminalWorkspaceCommandError? Error);

	private sealed record KnownProjectCompletionPaths(
		IReadOnlyList<string> Paths,
		IReadOnlyList<string> Files);
}

internal static class TerminalWorkspacePathResolver
{
	public static string Resolve(string value, string workingDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		var expanded = TerminalPathPickerModel.ExpandPath(value);
		if (expanded.Length == 0)
			return Path.GetFullPath(workingDirectory);
		return Path.IsPathRooted(expanded)
			? Path.GetFullPath(expanded)
			: Path.GetFullPath(expanded, Path.GetFullPath(workingDirectory));
	}
}
