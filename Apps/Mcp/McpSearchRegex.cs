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
		}
		catch (ArgumentException exception)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidPattern,
				$"{McpErrorCodes.InvalidPattern}: pattern is not a valid .NET regular expression ({exception.Message}).");
		}
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
}
