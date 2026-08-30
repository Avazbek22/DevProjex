namespace DevProjex.Mcp;

internal sealed class McpLineLimitReachedException : Exception;

internal sealed class McpBoundedLineTextWriter : TextWriter
{
	private readonly int _maximumLines;
	private readonly List<string> _lines;
	private readonly StringBuilder _currentLine = new();
	private bool _previousWasCarriageReturn;

	public McpBoundedLineTextWriter(int maximumLines)
	{
		if (maximumLines <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumLines));

		_maximumLines = maximumLines;
		_lines = new List<string>(maximumLines);
	}

	public override Encoding Encoding => Encoding.UTF8;
	public bool IsTruncated { get; private set; }
	public string Text => _currentLine.Length == 0
		? string.Join('\n', _lines)
		: string.Join('\n', _lines.Append(_currentLine.ToString()));

	public override void Write(char value)
	{
		Span<char> character = stackalloc char[1];
		character[0] = value;
		WriteCharacters(character, CancellationToken.None);
	}

	public override void Write(char[] buffer, int index, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		WriteCharacters(buffer.AsSpan(index, count), CancellationToken.None);
	}

	public override void Write(string? value)
	{
		if (value is not null)
			WriteCharacters(value.AsSpan(), CancellationToken.None);
	}

	public override void Write(ReadOnlySpan<char> buffer) =>
		WriteCharacters(buffer, CancellationToken.None);

	public override Task WriteAsync(
		ReadOnlyMemory<char> buffer,
		CancellationToken cancellationToken = default)
	{
		WriteCharacters(buffer.Span, cancellationToken);
		return Task.CompletedTask;
	}

	private void WriteCharacters(
		ReadOnlySpan<char> characters,
		CancellationToken cancellationToken)
	{
		for (var index = 0; index < characters.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var character = characters[index];
			if (character == '\n')
			{
				if (_previousWasCarriageReturn)
				{
					_previousWasCarriageReturn = false;
					continue;
				}

				CompleteLine();
				continue;
			}

			if (character == '\r')
			{
				CompleteLine();
				_previousWasCarriageReturn = true;
				continue;
			}

			_previousWasCarriageReturn = false;
			if (_lines.Count >= _maximumLines)
				ThrowLimitReached();
			_currentLine.Append(character);
		}
	}

	private void CompleteLine()
	{
		if (_lines.Count >= _maximumLines)
			ThrowLimitReached();

		_lines.Add(_currentLine.ToString());
		_currentLine.Clear();
	}

	private void ThrowLimitReached()
	{
		IsTruncated = true;
		throw new McpLineLimitReachedException();
	}
}
