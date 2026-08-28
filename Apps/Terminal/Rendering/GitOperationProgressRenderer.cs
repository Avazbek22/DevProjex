using System.Globalization;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

internal sealed class GitOperationProgressRenderer : IProgress<string>, IDisposable
{
	private static readonly int[] Milestones = [25, 50, 75];
	private readonly ITerminalEnvironment _environment;
	private readonly string _startMessage;
	private readonly string _completionMessage;
	private readonly bool _interactive;
	private readonly CarriageReturnProgressLine _interactiveLine;
	private readonly object _sync = new();
	private int _nextMilestoneIndex;
	private string? _milestonePhase;
	private bool _started;
	private bool _completed;
	private bool _outputUnavailable;

	private GitOperationProgressRenderer(
		ITerminalEnvironment environment,
		TerminalOutputOptions options,
		string startMessage,
		string completionMessage)
	{
		_environment = environment;
		_interactiveLine = new CarriageReturnProgressLine(environment);
		_startMessage = startMessage;
		_completionMessage = completionMessage;
		_interactive = environment.IsErrorInteractive &&
		               !environment.IsCi &&
		               !environment.IsTermDumb &&
		               !options.Plain;
	}

	public static GitOperationProgressRenderer? Create(
		ITerminalEnvironment environment,
		TerminalOutputOptions options,
		string startMessage,
		string completionMessage)
	{
		if (options.Progress == TerminalProgressMode.Never ||
		    options.Verbosity is TerminalVerbosity.Quiet or TerminalVerbosity.Minimal)
		{
			return null;
		}

		return new GitOperationProgressRenderer(
			environment,
			options,
			startMessage,
			completionMessage);
	}

	public void Start()
	{
		lock (_sync)
		{
			if (_started)
				return;
			_started = true;
			if (_interactive)
				RenderFrame(_startMessage);
			else
				TryWriteLine(Sanitize(_startMessage));
		}
	}

	public void Report(string value)
	{
		if (string.IsNullOrEmpty(value))
			return;

		lock (_sync)
		{
			if (_completed)
				return;
			if (!_started)
				Start();

			foreach (var message in SplitMessages(value))
			{
				if (string.IsNullOrWhiteSpace(message))
					continue;
				if (_interactive)
					RenderFrame(message);
				else
					WriteMilestone(Sanitize(message));
			}
		}
	}

	public void Complete()
	{
		lock (_sync)
		{
			if (_completed)
				return;
			_completed = true;
			if (_interactive)
				ClearFrame();
			else if (_started)
				TryWriteLine(Sanitize(_completionMessage));
		}
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (!_completed && _interactive)
				ClearFrame();
			_interactiveLine.Dispose();
			_completed = true;
		}
	}

	private void RenderFrame(string message) => _interactiveLine.Render(message);

	private void ClearFrame() => _interactiveLine.Clear();

	private void WriteMilestone(string message)
	{
		if (_nextMilestoneIndex >= Milestones.Length ||
		    !TryExtractPercent(message, out var percent, out var phase))
		{
			return;
		}
		if (_milestonePhase is null)
		{
			if (percent >= 100)
				return;
			_milestonePhase = phase;
		}
		if (!string.Equals(_milestonePhase, phase, StringComparison.Ordinal) ||
		    percent < Milestones[_nextMilestoneIndex])
		{
			return;
		}

		if (!TryWriteLine(message))
			return;
		while (_nextMilestoneIndex < Milestones.Length &&
		       percent >= Milestones[_nextMilestoneIndex])
		{
			_nextMilestoneIndex++;
		}
	}

	private static IEnumerable<string> SplitMessages(string value) =>
		value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

	private static string Sanitize(string value) =>
		TerminalTextEscaping.EscapeSingleLine(value);

	private static bool TryExtractPercent(
		string value,
		out int percent,
		out string phase)
	{
		percent = -1;
		phase = string.Empty;
		var percentIndex = value.IndexOf('%');
		while (percentIndex >= 0)
		{
			var end = percentIndex - 1;
			while (end >= 0 && char.IsWhiteSpace(value[end]))
				end--;
			var start = end;
			while (start >= 0 && char.IsDigit(value[start]))
				start--;
			var hasInvalidNumericPrefix = start >= 0 &&
			                              (char.IsLetterOrDigit(value[start]) ||
			                               value[start] is '+' or '-' or '.' or '_');
			if (end >= 0 &&
			    end > start &&
			    !hasInvalidNumericPrefix &&
			    int.TryParse(
				    value.AsSpan(start + 1, end - start),
				    NumberStyles.None,
				    CultureInfo.InvariantCulture,
				    out var candidate) &&
			    candidate is >= 0 and <= 100)
			{
				percent = candidate;
				phase = value[..(start + 1)].TrimEnd();
				return true;
			}
			percentIndex = value.IndexOf('%', percentIndex + 1);
		}

		return false;
	}

	private bool TryWriteLine(string value)
	{
		if (_outputUnavailable)
			return false;

		try
		{
			_environment.Error.WriteLine(value);
			return true;
		}
		catch (TerminalBrokenPipeException)
		{
			MarkOutputUnavailable();
			return false;
		}
	}

	private void MarkOutputUnavailable()
	{
		_outputUnavailable = true;
	}
}
