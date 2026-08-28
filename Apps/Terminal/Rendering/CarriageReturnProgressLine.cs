using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

internal sealed class CarriageReturnProgressLine(ITerminalEnvironment environment) : IDisposable
{
	private int _previousFrameWidth;
	private bool _outputUnavailable;

	public void Render(string message)
	{
		if (_outputUnavailable)
			return;

		var availableWidth = Math.Max(1, environment.Width - 1);
		var sanitized = TerminalTextEscaping.EscapeSingleLine(message);
		var frame = TerminalCellWidth.Truncate(sanitized, availableWidth);
		var frameWidth = TerminalCellWidth.Measure(frame);
		var previousVisibleWidth = Math.Min(_previousFrameWidth, availableWidth);

		try
		{
			environment.Error.Write('\r');
			environment.Error.Write(frame);
			if (frameWidth < previousVisibleWidth)
				environment.Error.Write(new string(' ', previousVisibleWidth - frameWidth));
			environment.Error.Flush();
			_previousFrameWidth = frameWidth;
		}
		catch (TerminalBrokenPipeException)
		{
			MarkOutputUnavailable();
		}
	}

	public void Clear()
	{
		if (_previousFrameWidth <= 0)
			return;

		var clearWidth = Math.Min(_previousFrameWidth, Math.Max(1, environment.Width - 1));
		if (_outputUnavailable)
		{
			_previousFrameWidth = 0;
			return;
		}

		try
		{
			environment.Error.Write('\r');
			environment.Error.Write(new string(' ', clearWidth));
			environment.Error.Write('\r');
			environment.Error.Flush();
		}
		catch (TerminalBrokenPipeException)
		{
			MarkOutputUnavailable();
		}
		_previousFrameWidth = 0;
	}

	public void Dispose() => Clear();

	private void MarkOutputUnavailable()
	{
		_outputUnavailable = true;
		_previousFrameWidth = 0;
	}
}
