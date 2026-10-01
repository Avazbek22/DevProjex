namespace DevProjex.Mcp;

internal sealed class McpBoundedStringTextWriter(int maximumCharacters) : TextWriter
{
	private readonly StringBuilder _output = new(Math.Min(maximumCharacters, 16 * 1024));

	public override Encoding Encoding => Encoding.UTF8;
	public bool IsTruncated { get; private set; }
	public string Text => _output.ToString();
	internal int BufferedCharacters => _output.Length;

	public override void Write(char value)
	{
		Span<char> character = stackalloc char[1];
		character[0] = value;
		Write(character);
	}

	public override void Write(string? value)
	{
		if (value is not null)
			Write(value.AsSpan());
	}

	public override void Write(ReadOnlySpan<char> buffer)
	{
		if (IsTruncated)
			throw new McpLineLimitReachedException();
		var remaining = maximumCharacters - _output.Length;
		if (buffer.Length <= remaining)
		{
			_output.Append(buffer);
			return;
		}

		var length = Math.Max(0, remaining);
		if (length > 0 && length < buffer.Length &&
		    char.IsHighSurrogate(buffer[length - 1]) && char.IsLowSurrogate(buffer[length]))
		{
			length--;
		}
		if (length > 0)
			_output.Append(buffer[..length]);
		IsTruncated = true;
		throw new McpLineLimitReachedException();
	}
}
