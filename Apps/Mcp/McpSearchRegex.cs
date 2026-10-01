using System.Buffers;

namespace DevProjex.Mcp;

internal sealed class McpSearchRegex
{
	private static readonly SearchValues<string> DeclarationKeywords = SearchValues.Create(
		["class", "interface", "struct", "record", "enum", "delegate", "type", "trait", "def", "function", "func", "fn"],
		StringComparison.Ordinal);
	private static readonly SearchValues<char> DeclarationSeparators = SearchValues.Create(" \t({;:");
	internal const int MaximumPatternLength = 4096;
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
	private readonly Regex _regex;
	private readonly IReadOnlyList<DeclarationBodyLiteralTerm> declarationBodyLiteralTerms;

	public McpSearchRegex(string pattern, bool ignoreCase, TimeSpan? timeout = null)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (McpUnicodeLength.ExceedsScalarValueCount(pattern, MaximumPatternLength))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidPattern,
				$"{McpErrorCodes.InvalidPattern}: pattern must be at most {MaximumPatternLength} characters; shorten it and retry.");
		}
		try
		{
			_regex = new Regex(
				pattern,
				RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
				timeout ?? DefaultTimeout);
			declarationBodyLiteralTerms = ExtractDeclarationBodyLiteralTerms(pattern);
		}
		catch (ArgumentException exception)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidPattern,
				$"{McpErrorCodes.InvalidPattern}: pattern is not a valid .NET regular expression ({exception.Message}).");
		}
	}

	public bool HasDeclarationBodyLiteralTerms => declarationBodyLiteralTerms.Count > 0;

	public McpDeclarationBodyNameMatchQuality DeclarationBodyNameMatchQuality(string declaredName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(declaredName);
		var separator = declaredName.AsSpan().LastIndexOfAny('.', ':', '#');
		var lastSegment = separator < 0 ? declaredName.AsSpan() : declaredName.AsSpan(separator + 1);
		foreach (var term in declarationBodyLiteralTerms)
		{
			if (declaredName.Equals(term.Text, StringComparison.Ordinal) ||
				lastSegment.Equals(term.Text, StringComparison.Ordinal))
			{
				return McpDeclarationBodyNameMatchQuality.Equal;
			}
		}
		foreach (var term in declarationBodyLiteralTerms)
		{
			if (declaredName.Contains(term.Text, StringComparison.Ordinal))
				return McpDeclarationBodyNameMatchQuality.Exact;
		}

		var normalizedName = NormalizeDeclarationName(declaredName);
		foreach (var term in declarationBodyLiteralTerms)
		{
			if (term.Normalized.Length >= 3 &&
				normalizedName.Contains(term.Normalized, StringComparison.OrdinalIgnoreCase))
			{
				return McpDeclarationBodyNameMatchQuality.SeparatorInsensitive;
			}
		}

		return McpDeclarationBodyNameMatchQuality.None;
	}

	public bool IsMatch(string input)
	{
		ArgumentNullException.ThrowIfNull(input);
		return IsMatch(input.AsSpan());
	}

	public bool IsMatch(string input, int start, int length)
	{
		ArgumentNullException.ThrowIfNull(input);
		return IsMatch(input.AsSpan(start, length));
	}

	public McpDeclarationMatchQuality DeclarationMatchQuality(string declaredName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(declaredName);
		var separator = declaredName.AsSpan().LastIndexOfAny('.', '#', '/');
		var simpleName = separator < 0 ? declaredName : declaredName[(separator + 1)..];
		if (MatchesWhole(declaredName))
			return McpDeclarationMatchQuality.FullyQualified;
		return MatchesWhole(simpleName)
			? McpDeclarationMatchQuality.SimpleName
			: McpDeclarationMatchQuality.None;
	}

	public bool HasDeclarationHint(string input, int start, int length)
	{
		ArgumentNullException.ThrowIfNull(input);
		try
		{
			foreach (var match in _regex.EnumerateMatches(input.AsSpan(start, length)))
			{
				var prefix = input.AsSpan(start, match.Index).TrimEnd();
				var wordStart = prefix.LastIndexOfAny(DeclarationSeparators);
				var word = prefix[(wordStart + 1)..];
				if (DeclarationKeywords.Contains(word.ToString()))
					return true;
			}
			return false;
		}
		catch (RegexMatchTimeoutException)
		{
			throw InvalidPatternTimeout();
		}
	}

	private bool MatchesWhole(string input)
	{
		try
		{
			foreach (var match in _regex.EnumerateMatches(input))
			{
				if (match.Index == 0 && match.Length == input.Length)
					return true;
			}
			return false;
		}
		catch (RegexMatchTimeoutException)
		{
			throw InvalidPatternTimeout();
		}
	}

	public bool IsMatch(
		string input,
		int start,
		int length,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		ref int protectedRangeIndex,
		ref long protectedRangeComparisons,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(protectedRanges);
		ArgumentOutOfRangeException.ThrowIfNegative(protectedRangeIndex);
		try
		{
			foreach (var match in _regex.EnumerateMatches(input.AsSpan(start, length)))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var absoluteStart = checked(start + match.Index);
				var absoluteEnd = checked(absoluteStart + match.Length);
				while (protectedRangeIndex < protectedRanges.Count)
				{
					protectedRangeComparisons++;
					if ((protectedRangeComparisons & 0xFF) == 0)
						cancellationToken.ThrowIfCancellationRequested();
					if (protectedRanges[protectedRangeIndex].End > absoluteStart)
						break;
					protectedRangeIndex++;
				}

				var overlapsProtectedText = false;
				if (protectedRangeIndex < protectedRanges.Count)
				{
					protectedRangeComparisons++;
					var range = protectedRanges[protectedRangeIndex];
					overlapsProtectedText = match.Length == 0
						? absoluteStart >= range.Start && absoluteStart < range.End
						: absoluteStart < range.End && absoluteEnd > range.Start;
				}
				if (!overlapsProtectedText)
					return true;
			}
			return false;
		}
		catch (RegexMatchTimeoutException)
		{
			throw InvalidPatternTimeout();
		}
	}

	private bool IsMatch(ReadOnlySpan<char> input)
	{
		try
		{
			return _regex.IsMatch(input);
		}
		catch (RegexMatchTimeoutException)
		{
			throw InvalidPatternTimeout();
		}
	}

	private static McpToolException InvalidPatternTimeout() =>
		new(
			McpErrorCodes.InvalidPattern,
			$"{McpErrorCodes.InvalidPattern}: regex evaluation exceeded 2 seconds; simplify the pattern and retry.");

	private static IReadOnlyList<DeclarationBodyLiteralTerm> ExtractDeclarationBodyLiteralTerms(string pattern)
	{
		var terms = new List<DeclarationBodyLiteralTerm>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var literal = new StringBuilder();
		var groupSuppression = new Stack<bool>();
		var suppressed = false;
		for (var index = 0; index < pattern.Length; index++)
		{
			var current = pattern[index];
			if (current == '\\')
			{
				ReadEscape(pattern, ref index, literal, suppressed, terms, seen);
				continue;
			}
			if (current == '[')
			{
				AddLiteralTerm(literal, terms, seen);
				SkipCharacterClass(pattern, ref index);
				continue;
			}
			if (current == '(')
			{
				AddLiteralTerm(literal, terms, seen);
				if (TrySkipCommentGroup(pattern, ref index))
					continue;

				groupSuppression.Push(suppressed);
				suppressed |= SuppressesLiteralTerms(pattern, index);
				SkipGroupPrefix(pattern, ref index);
				continue;
			}
			if (current == ')')
			{
				AddLiteralTerm(literal, terms, seen);
				if (groupSuppression.Count > 0)
					suppressed = groupSuppression.Pop();
				continue;
			}
			if (current == '{')
			{
				AddLiteralTerm(literal, terms, seen);
				var close = pattern.IndexOf('}', index + 1);
				if (close >= 0)
					index = close;
				continue;
			}
			if (current is '.' or '^' or '$' or '|' or '?' or '*' or '+')
			{
				AddLiteralTerm(literal, terms, seen);
				continue;
			}
			if (!suppressed)
				literal.Append(current);
		}

		AddLiteralTerm(literal, terms, seen);
		return terms;
	}

	private static void ReadEscape(
		string pattern,
		ref int index,
		StringBuilder literal,
		bool suppressed,
		List<DeclarationBodyLiteralTerm> terms,
		HashSet<string> seen)
	{
		if (index + 1 >= pattern.Length)
		{
			AddLiteralTerm(literal, terms, seen);
			return;
		}

		var escaped = pattern[++index];
		if (!char.IsLetterOrDigit(escaped))
		{
			if (!suppressed)
				literal.Append(escaped);
			return;
		}

		AddLiteralTerm(literal, terms, seen);
		if (escaped is 'p' or 'P' && index + 1 < pattern.Length && pattern[index + 1] == '{')
		{
			var close = pattern.IndexOf('}', index + 2);
			if (close >= 0)
				index = close;
		}
		else if (escaped == 'k' && index + 1 < pattern.Length && pattern[index + 1] is '<' or '\'')
		{
			var closeToken = pattern[index + 1] == '<' ? '>' : '\'';
			var close = pattern.IndexOf(closeToken, index + 2);
			if (close >= 0)
				index = close;
		}
		else if (escaped is 'x' or 'u')
		{
			var remainingDigits = 4;
			while (remainingDigits-- > 0 && index + 1 < pattern.Length && Uri.IsHexDigit(pattern[index + 1]))
				index++;
		}
		else if (escaped == 'c' && index + 1 < pattern.Length)
			index++;
		else if (char.IsDigit(escaped))
		{
			var remainingDigits = 2;
			while (remainingDigits-- > 0 && index + 1 < pattern.Length && char.IsDigit(pattern[index + 1]))
				index++;
		}
	}

	private static void SkipCharacterClass(string pattern, ref int index)
	{
		for (index++; index < pattern.Length; index++)
		{
			if (pattern[index] == '\\' && index + 1 < pattern.Length)
			{
				index++;
				continue;
			}
			if (pattern[index] == ']')
				return;
		}
	}

	private static bool TrySkipCommentGroup(string pattern, ref int index)
	{
		if (index + 2 >= pattern.Length || pattern[index + 1] != '?' || pattern[index + 2] != '#')
			return false;
		var close = pattern.IndexOf(')', index + 3);
		index = close >= 0 ? close : pattern.Length;
		return true;
	}

	private static bool SuppressesLiteralTerms(string pattern, int index) =>
		(index + 2 < pattern.Length && pattern[index + 1] == '?' && pattern[index + 2] == '!') ||
		(index + 2 < pattern.Length && pattern[index + 1] == '?' && pattern[index + 2] == '(') ||
		(index + 3 < pattern.Length && pattern[index + 1] == '?' && pattern[index + 2] == '<' &&
		 pattern[index + 3] == '!');

	private static void SkipGroupPrefix(string pattern, ref int index)
	{
		if (index + 1 >= pattern.Length || pattern[index + 1] != '?')
			return;

		var cursor = index + 2;
		if (cursor < pattern.Length && pattern[cursor] == '<')
		{
			if (cursor + 1 < pattern.Length && pattern[cursor + 1] is '=' or '!')
				index = cursor + 1;
			else
			{
				var close = pattern.IndexOf('>', cursor + 1);
				if (close >= 0)
					index = close;
			}
			return;
		}
		if (cursor < pattern.Length && pattern[cursor] == '\'')
		{
			var close = pattern.IndexOf('\'', cursor + 1);
			if (close >= 0)
				index = close;
			return;
		}
		if (cursor < pattern.Length && pattern[cursor] is ':' or '=' or '!' or '>')
		{
			index = cursor;
			return;
		}

		while (cursor < pattern.Length &&
			(pattern[cursor] is 'i' or 'm' or 'n' or 's' or 'x' or '-'))
		{
			cursor++;
		}
		if (cursor < pattern.Length && pattern[cursor] == ':')
			index = cursor;
		else if (cursor < pattern.Length && pattern[cursor] == ')')
			index = cursor - 1;
	}

	private static void AddLiteralTerm(
		StringBuilder literal,
		List<DeclarationBodyLiteralTerm> terms,
		HashSet<string> seen)
	{
		if (literal.Length == 0)
			return;
		var text = literal.ToString();
		literal.Clear();
		if (text.EnumerateRunes().Take(3).Count() < 3 || !seen.Add(text))
			return;
		terms.Add(new DeclarationBodyLiteralTerm(text, NormalizeDeclarationName(text)));
	}

	private static string NormalizeDeclarationName(string value)
	{
		var normalized = new StringBuilder(value.Length);
		foreach (var character in value)
		{
			if (char.IsLetterOrDigit(character))
				normalized.Append(character);
		}
		return normalized.ToString();
	}

	private sealed record DeclarationBodyLiteralTerm(string Text, string Normalized);
}

internal enum McpDeclarationBodyNameMatchQuality
{
	None,
	SeparatorInsensitive,
	Exact,
	Equal
}
