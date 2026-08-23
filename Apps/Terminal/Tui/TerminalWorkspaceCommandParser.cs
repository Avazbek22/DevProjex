using System.Text;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalWorkspaceCommandParser
{
	private static readonly string[] ToggleValues = ["on", "off"];
	private static readonly string[] AggregateTargets = ["types", "exclusions", "content"];
	private static readonly string[] ExportTargets = ["context", "zip", "folder"];

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
			TerminalWorkspaceCommandVerb.Help => ParseHelp(definition, tokenization.Tokens),
			TerminalWorkspaceCommandVerb.Quit => ParseWithoutArguments(definition, tokenization.Tokens),
			_ => throw new ArgumentOutOfRangeException()
		};
	}

	public TerminalWorkspaceCommandCompletion GetCompletion(
		string? text,
		int cursorPosition,
		TerminalWorkspaceCommandParseContext? context = null)
	{
		text ??= string.Empty;
		context ??= TerminalWorkspaceCommandParseContext.Empty;
		cursorPosition = Math.Clamp(cursorPosition, 0, text.Length);
		var prefix = text[..cursorPosition];
		var tokenization = Tokenize(prefix, tolerateUnterminatedQuote: true);
		if (tokenization.Error is not null)
			return TerminalWorkspaceCommandCompletion.Empty;

		var atNewToken = prefix.Length > 0 && char.IsWhiteSpace(prefix[^1]);
		var tokens = tokenization.Tokens;
		if (tokens.Count == 0)
			return BuildCompletion(text, cursorPosition, 0, string.Empty, TerminalWorkspaceCommandCatalog.VerbTokens);

		if (tokens.Count == 1 && !atNewToken)
		{
			return BuildCompletion(
				text,
				cursorPosition,
				tokens[0].Start,
				tokens[0].Value,
				TerminalWorkspaceCommandCatalog.VerbTokens);
		}

		if (!TerminalWorkspaceCommandCatalog.TryGet(tokens[0].Value, out var definition))
			return TerminalWorkspaceCommandCompletion.Empty;

		var argumentIndex = atNewToken ? tokens.Count - 1 : tokens.Count - 2;
		var current = atNewToken ? string.Empty : tokens[^1].Value;
		var replacementStart = atNewToken ? cursorPosition : tokens[^1].Start;
		var candidates = ResolveCompletionCandidates(definition.Verb, argumentIndex, tokens, context);
		if (atNewToken && tokens.Count == 1)
		{
			var completion = BuildCompletion(
				text,
				cursorPosition,
				replacementStart,
				current,
				candidates);
			return completion with
			{
				GhostSuffix = null,
				SchemaKey = definition.SchemaKey
			};
		}
		if (candidates.Count > 0)
			return BuildCompletion(text, cursorPosition, replacementStart, current, candidates);

		return TerminalWorkspaceCommandCompletion.Empty;
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

	private static TerminalWorkspaceCommandParseResult ParseWithoutArguments(
		TerminalWorkspaceCommandDefinition definition,
		IReadOnlyList<ParsedToken> tokens) =>
		tokens.Count == 1
			? TerminalWorkspaceCommandParseResult.Success(new TerminalWorkspaceCommand(definition))
			: Unexpected(tokens[1]);

	private static IReadOnlyList<string> ResolveCompletionCandidates(
		TerminalWorkspaceCommandVerb verb,
		int argumentIndex,
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context) =>
		verb switch
		{
			TerminalWorkspaceCommandVerb.Set when argumentIndex == 0 => SetTargets,
			TerminalWorkspaceCommandVerb.Set when argumentIndex == 1 => ToggleValues,
			TerminalWorkspaceCommandVerb.All when argumentIndex == 0 => AggregateTargets,
			TerminalWorkspaceCommandVerb.All when argumentIndex == 1 => ToggleValues,
			TerminalWorkspaceCommandVerb.Type when argumentIndex >= 0 => ResolveTypeCompletions(tokens, context),
			TerminalWorkspaceCommandVerb.View when argumentIndex == 0 => CliChoiceSets.ContextView.Tokens,
			TerminalWorkspaceCommandVerb.Format when argumentIndex == 0 => CliChoiceSets.ContextDocumentFormat.Tokens,
			TerminalWorkspaceCommandVerb.Export when argumentIndex == 0 => ExportTargets,
			TerminalWorkspaceCommandVerb.Export when argumentIndex == 1 &&
				tokens.Count > 1 && string.Equals(tokens[1].Value, "context", StringComparison.OrdinalIgnoreCase) =>
				CliChoiceSets.ContextDocumentFormat.Tokens,
			TerminalWorkspaceCommandVerb.Help when argumentIndex == 0 => TerminalWorkspaceCommandCatalog.VerbTokens,
			_ => []
		};

	private static IReadOnlyList<string> ResolveTypeCompletions(
		IReadOnlyList<ParsedToken> tokens,
		TerminalWorkspaceCommandParseContext context)
	{
		var hasExtension = tokens.Skip(1).Any(token =>
			context.AvailableExtensions.Contains(token.Value, StringComparer.OrdinalIgnoreCase));
		return hasExtension
			? [.. context.AvailableExtensions, .. ToggleValues]
			: context.AvailableExtensions;
	}

	private static TerminalWorkspaceCommandCompletion BuildCompletion(
		string fullText,
		int cursorPosition,
		int replacementStart,
		string current,
		IReadOnlyList<string> source)
	{
		var matches = source
			.Where(candidate => candidate.StartsWith(current, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (matches.Length == 0)
			return TerminalWorkspaceCommandCompletion.Empty;

		var suffix = fullText[cursorPosition..];
		var candidates = matches.Select(candidate =>
		{
			var completed = fullText[..replacementStart] + candidate + suffix;
			return new TerminalWorkspaceCommandCompletionCandidate(
				candidate,
				completed,
				replacementStart + candidate.Length);
		}).ToArray();
		var ghost = matches[0].Length > current.Length
			? matches[0][current.Length..]
			: null;
		return new TerminalWorkspaceCommandCompletion(candidates, ghost, null);
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

	private sealed record ParsedToken(string Value, int Start, int End);

	private sealed record TokenizationResult(
		IReadOnlyList<ParsedToken> Tokens,
		TerminalWorkspaceCommandError? Error);
}
