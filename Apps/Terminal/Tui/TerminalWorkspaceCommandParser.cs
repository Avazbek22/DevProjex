using System.Text;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalWorkspaceCommandParser
{
	private static readonly string[] ToggleValues = ["on", "off"];
	private static readonly string[] GitModeValues =
		["off", "gitignore", "tracked", "staged", "changes", "diff:<ref>..<ref>"];
	private static readonly string[] AggregateTargets = ["types", "exclusions", "content"];
	private static readonly string[] ExportTargets = ["context", "zip", "folder"];
	private static readonly string[] ProfileTargets = ["save"];
	private static readonly IReadOnlyList<string> LanguageCodes = CliChoiceSets.Language.Tokens;

	private static readonly IReadOnlyList<string> SetTargets =
	[
		.. ProjectPresentationCatalog.ContentTransformations.Select(static item => item.Token),
		.. ProjectPresentationCatalog.Exclusions.Select(static item => item.Token),
		"gitignore",
		"tracked",
		"git"
	];

	private delegate TerminalWorkspaceCommandParseResult GrammarParser(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context);

	private delegate CompletionCandidateSource GrammarCompleter(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context);

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
			[TerminalWorkspaceCommandGrammar.Profile] = new(
				static (definition, tokens, _) => ParseProfile(definition, tokens),
				CompleteProfile),
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
	{
		var target = ResolveCompletionTarget(text, cursorPosition, context);
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
		var target = ResolveCompletionTarget(text, cursorPosition, context);
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
		TerminalWorkspaceCommandParseContext? context)
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
			context);
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
			if (!GitScopeSelection.TryParse(tokens[2].Value, out var mode, out var diffRange))
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

	private static TerminalWorkspaceCommandParseResult ParseProfile(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 2)
			return Missing(tokens, ProfileTargets);
		if (!Contains(ProfileTargets, tokens[1].Value))
			return Unknown(tokens[1], ProfileTargets);
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		return TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(
			definition,
			Target: "save",
			Text: tokens.Count == 3 ? tokens[2].Value : null));
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
		TerminalWorkspaceCommandParseContext context) =>
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
		TerminalWorkspaceCommandParseContext context) =>
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
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex >= 0 ? ResolveTypeCompletions(tokens, context) : default;

	private static CompletionCandidateSource CompleteView(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex == 0 ? new CompletionCandidateSource(CliChoiceSets.ContextView.Tokens) : default;

	private static CompletionCandidateSource CompleteFormat(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex == 0 ? new CompletionCandidateSource(CliChoiceSets.ContextDocumentFormat.Tokens) : default;

	private static CompletionCandidateSource CompleteExport(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context)
	{
		if (argumentIndex == 0)
			return new CompletionCandidateSource(ExportTargets);
		var isContext = tokens.Count > 1 &&
			string.Equals(tokens[1].Value, "context", StringComparison.OrdinalIgnoreCase);
		if (argumentIndex == 1 && isContext)
		{
			return new CompletionCandidateSource(
				CliChoiceSets.ContextDocumentFormat.Tokens,
				ResolvePathCompletions(current, context.WorkingDirectory));
		}
		if (argumentIndex == 2 && isContext || argumentIndex == 1 && tokens.Count > 1)
			return new CompletionCandidateSource(ResolvePathCompletions(current, context.WorkingDirectory));
		return default;
	}

	private static CompletionCandidateSource CompleteCopy(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) =>
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
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex == 0 ? new CompletionCandidateSource(ProfileTargets) : default;

	private static CompletionCandidateSource CompleteLanguage(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex == 0 ? new CompletionCandidateSource(LanguageCodes) : default;

	private static CompletionCandidateSource CompleteHelp(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) =>
		argumentIndex == 0 ? new CompletionCandidateSource(context.VerbTokens) : default;

	private static CompletionCandidateSource NoCompletions(
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		string current,
		TerminalWorkspaceCommandParseContext context) => default;

	private static IReadOnlyList<string> ResolvePathCompletions(string current, string? workingDirectory)
	{
		try
		{
			var baseDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
			var expanded = TerminalPathPickerModel.ExpandPath(current);
			var absolute = Path.GetFullPath(Path.IsPathRooted(expanded)
				? expanded
				: Path.Combine(baseDirectory, expanded));
			var directory = Directory.Exists(absolute) ? absolute : Path.GetDirectoryName(absolute);
			var prefix = Directory.Exists(absolute) ? string.Empty : Path.GetFileName(absolute);
			if (directory is null || !Directory.Exists(directory))
				return [];
			var rooted = Path.IsPathRooted(current);
			return Directory.EnumerateFileSystemEntries(directory)
				.Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
				.OrderBy(static path => path, PathComparer.Default)
				.Take(100)
				.Select(path => rooted ? path : Path.GetRelativePath(baseDirectory, path))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			ArgumentException or NotSupportedException)
		{
			return [];
		}
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
}
