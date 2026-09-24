namespace DevProjex.Application.Context;

internal sealed class ProjectContextLineTrackingStream(Stream destination) : Stream
{
	private readonly List<ProjectContextFileLineRange> _ranges = [];
	private string? _currentPath;
	private int _currentStartLine;
	private int _lineBreaks;
	private bool _previousCarriageReturn;
	private bool _endsWithLineBreak;

	public IReadOnlyList<ProjectContextFileLineRange> Ranges => _ranges;
	public int CurrentLine => checked(_lineBreaks + 1);

	public void BeginFile(string path, int? startLine = null)
	{
		if (_currentPath is not null)
			throw new InvalidOperationException("A project context file range is already open.");
		_currentPath = path;
		_currentStartLine = startLine ?? CurrentLine;
	}

	public void EndFile()
	{
		if (_currentPath is null)
			throw new InvalidOperationException("No project context file range is open.");
		var endLine = _endsWithLineBreak
			? Math.Max(_currentStartLine, CurrentLine - 1)
			: CurrentLine;
		_ranges.Add(new ProjectContextFileLineRange(_currentPath, _currentStartLine, endLine));
		_currentPath = null;
	}

	public override bool CanRead => false;
	public override bool CanSeek => destination.CanSeek;
	public override bool CanWrite => destination.CanWrite;
	public override long Length => destination.Length;

	public override long Position
	{
		get => destination.Position;
		set => destination.Position = value;
	}

	public override void Flush() => destination.Flush();
	public override Task FlushAsync(CancellationToken cancellationToken) =>
		destination.FlushAsync(cancellationToken);

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => destination.Seek(offset, origin);
	public override void SetLength(long value) => destination.SetLength(value);

	public override void Write(byte[] buffer, int offset, int count)
	{
		Append(buffer.AsSpan(offset, count));
		destination.Write(buffer, offset, count);
	}

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		Append(buffer);
		destination.Write(buffer);
	}

	public override async Task WriteAsync(
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken)
	{
		Append(buffer.AsSpan(offset, count));
		await destination.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
	}

	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		Append(buffer.Span);
		await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
	}

	protected override void Dispose(bool disposing)
	{
		// The document service owns only the tracking view, not the caller's destination.
		base.Dispose(disposing);
	}

	private void Append(ReadOnlySpan<byte> bytes)
	{
		foreach (var value in bytes)
		{
			if (value == (byte)'\n')
			{
				if (!_previousCarriageReturn)
					_lineBreaks++;
				_previousCarriageReturn = false;
				_endsWithLineBreak = true;
				continue;
			}

			if (value == (byte)'\r')
			{
				_lineBreaks++;
				_previousCarriageReturn = true;
				_endsWithLineBreak = true;
				continue;
			}

			_previousCarriageReturn = false;
			_endsWithLineBreak = false;
		}
	}
}
