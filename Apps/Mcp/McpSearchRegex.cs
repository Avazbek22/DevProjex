namespace DevProjex.Mcp;

internal sealed class McpSearchRegex
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
	private readonly Regex _regex;

	public McpSearchRegex(string pattern, bool ignoreCase, TimeSpan? timeout = null)
	{
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
		try
		{
			return _regex.IsMatch(input);
		}
		catch (RegexMatchTimeoutException)
		{
			throw new McpToolException(
				McpErrorCodes.InvalidPattern,
				$"{McpErrorCodes.InvalidPattern}: regex evaluation exceeded 2 seconds; simplify the pattern and retry.");
		}
	}
}
