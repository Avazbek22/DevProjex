using System.Text;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalWorkspaceCommandParser
{
	private static readonly string[] ToggleValues = ["on", "off"];
	private static readonly string[] AggregateTargets = ["types", "exclusions", "content"];
	private static readonly string[] ExportTargets = ["context", "zip", "folder"];
	private static readonly string[] ProfileTargets = ["save"];
	private static readonly IReadOnlyList<string> LanguageCodes = CliChoiceSets.Language.Tokens;

	private static readonly IReadOnlyList<string> SetTargets =
	[
		.. ProjectPresentationCatalog.ContentTransformations.Select(static item => item.Token),
		.. ProjectPresentationCatalog.Exclusions.Select(static item => item.Token),
		"gitignore",
		"tracked"
	];

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
				TerminalWorkspaceCommandCatalog.VerbTokens);
		}

		var verbToken = tokenization.Tokens[0];
		if (!TerminalWorkspaceCommandCatalog.TryGet(verbToken.Value, out var definition))
		{
			return Failure(
				TerminalWorkspaceCommandErrorCode.UnknownVerb,
				verbToken.Start,
				verbToken.Value,
				FindSimilar(verbToken.Value, TerminalWorkspaceCommandCatalog.VerbTokens));
		}

		return definition.Verb switch
		{
			TerminalWorkspaceCommandVerb.Set => ParseSet(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.All => ParseAll(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Type => ParseType(definition, tokenization.Tokens, context),
			TerminalWorkspaceCommandVerb.View => ParseView(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Format => ParseFormat(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Search or TerminalWorkspaceCommandVerb.Filter =>
				ParseText(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Export => ParseExport(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Copy => ParseCopy(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Branch => ParseOptionalText(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Profile => ParseProfile(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Language => ParseLanguage(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Help => ParseHelp(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Analyze or
			TerminalWorkspaceCommandVerb.Update or
			TerminalWorkspaceCommandVerb.Recent or
			TerminalWorkspaceCommandVerb.Refresh or
			TerminalWorkspaceCommandVerb.Quit => ParseWithoutArguments(definition, tokenization.Tokens),
			_ => throw new ArgumentOutOfRangeException()
		};
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
				new CompletionCandidateSource(TerminalWorkspaceCommandCatalog.VerbTokens),
				null);
		}

		if (tokens.Count == 1 && !atNewToken)
		{
			return new CompletionTarget(
				text,
				cursorPosition,
				tokens[0].Start,
				tokens[0].Value,
				new CompletionCandidateSource(TerminalWorkspaceCommandCatalog.VerbTokens),
				null);
		}

		if (!TerminalWorkspaceCommandCatalog.TryGet(tokens[0].Value, out var definition))
			return null;

		var argumentIndex = atNewToken ? tokens.Count - 1 : tokens.Count - 2;
		var current = atNewToken ? string.Empty : tokens[^1].Value;
		var replacementStart = atNewToken ? cursorPosition : tokens[^1].Start;
		var candidates = ResolveCompletionCandidates(definition.Verb, argumentIndex, tokens, context);
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
				schemaKey)
			: null;
	}

	private static TerminalWorkspaceCommandParseResult ParseSet(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens)
	{
		if (tokens.Count < 3)
			return Missing(tokens, tokens.Count == 1 ? SetTargets : ToggleValues);
		if (tokens.Count > 3)
			return Unexpected(tokens[3]);
		if (!Contains(SetTargets, tokens[1].Value))
			return Unknown(tokens[1], SetTargets);
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
				LanguageCodes);
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

	private static CompletionCandidateSource ResolveCompletionCandidates(
		TerminalWorkspaceCommandVerb verb,
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context) =>
		verb switch
		{
			TerminalWorkspaceCommandVerb.Set when argumentIndex == 0 => new(SetTargets),
			TerminalWorkspaceCommandVerb.Set when argumentIndex == 1 => new(ToggleValues),
			TerminalWorkspaceCommandVerb.All when argumentIndex == 0 => new(AggregateTargets),
			TerminalWorkspaceCommandVerb.All when argumentIndex == 1 => new(ToggleValues),
			TerminalWorkspaceCommandVerb.Type when argumentIndex >= 0 => ResolveTypeCompletions(tokens, context),
			TerminalWorkspaceCommandVerb.View when argumentIndex == 0 => new(CliChoiceSets.ContextView.Tokens),
			TerminalWorkspaceCommandVerb.Format when argumentIndex == 0 => new(CliChoiceSets.ContextDocumentFormat.Tokens),
			TerminalWorkspaceCommandVerb.Export when argumentIndex == 0 => new(ExportTargets),
			TerminalWorkspaceCommandVerb.Export when argumentIndex == 1 &&
				tokens.Count > 1 && string.Equals(tokens[1].Value, "context", StringComparison.OrdinalIgnoreCase) =>
				new(CliChoiceSets.ContextDocumentFormat.Tokens),
			TerminalWorkspaceCommandVerb.Copy when argumentIndex == 0 => new(CliChoiceSets.ContextView.Tokens),
			TerminalWorkspaceCommandVerb.Copy when argumentIndex == 1 => new(CliChoiceSets.ContextDocumentFormat.Tokens),
			TerminalWorkspaceCommandVerb.Profile when argumentIndex == 0 => new(ProfileTargets),
			TerminalWorkspaceCommandVerb.Language when argumentIndex == 0 => new(LanguageCodes),
			TerminalWorkspaceCommandVerb.Help when argumentIndex == 0 => new(TerminalWorkspaceCommandCatalog.VerbTokens),
			_ => default
		};

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

		var replacementEnd = target.CursorPosition;
		while (replacementEnd < target.FullText.Length && !char.IsWhiteSpace(target.FullText[replacementEnd]))
			replacementEnd++;
		var suffix = target.FullText[replacementEnd..];
		var candidates = matches.Select(candidate =>
		{
			var completed = target.FullText[..target.ReplacementStart] + candidate + suffix;
			return new TerminalWorkspaceCommandCompletionCandidate(
				candidate,
				completed,
				target.ReplacementStart + candidate.Length);
		}).ToArray();
		var ghost = matches[0].Length > target.Current.Length
			? matches[0][target.Current.Length..]
			: null;
		return new TerminalWorkspaceCommandCompletion(candidates, ghost, null);
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
			var quoteStart = -1;
			while (index < text.Length && (quote is not null || !char.IsWhiteSpace(text[index])))
			{
				var character = text[index];
				if (quote is null && character is '\'' or '"')
				{
					quote = character;
					quoteStart = index++;
					continue;
				}
				if (quote == character)
				{
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

			tokens.Add(new ParsedToken(value.ToString(), start, index));
		}

		return new TokenizationResult(tokens, null);
	}

	private readonly record struct CompletionTarget(
		string FullText,
		int CursorPosition,
		int ReplacementStart,
		string Current,
		CompletionCandidateSource Candidates,
		string? SchemaKey);

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

	private sealed record ParsedToken(string Value, int Start, int End);

	private sealed record TokenizationResult(
		IReadOnlyList<ParsedToken> Tokens,
		TerminalWorkspaceCommandError? Error);
}
