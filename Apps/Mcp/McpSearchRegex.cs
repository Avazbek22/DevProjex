namespace DevProjex.Mcp;

internal sealed class McpSearchRegex
{
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
