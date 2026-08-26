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

	public override Task WriteAsync(
		ReadOnlyMemory<char> buffer,
		CancellationToken cancellationToken = default)
	{
		var characters = buffer.Span;
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

		return Task.CompletedTask;
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
