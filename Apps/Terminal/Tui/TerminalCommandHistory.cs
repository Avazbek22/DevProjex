namespace DevProjex.Terminal.Tui;

internal sealed class TerminalCommandHistory
{
	public const int MaximumEntries = 50;
	public const int MaximumCommandLength = 4_096;
	private readonly List<string> _entries;
	private int _navigationIndex;
	private string _draft = string.Empty;

	public TerminalCommandHistory(IEnumerable<string>? entries = null)
	{
		_entries = Normalize(entries ?? []);
		_navigationIndex = _entries.Count;
	}

	public IReadOnlyList<string> Entries => _entries;

	public bool Add(string? command)
	{
		var normalized = NormalizeCommand(command);
		ResetNavigation();
		if (normalized.Length == 0 ||
			_entries.Count > 0 && string.Equals(_entries[^1], normalized, StringComparison.Ordinal))
		{
			return false;
		}

		_entries.Add(normalized);
		if (_entries.Count > MaximumEntries)
			_entries.RemoveRange(0, _entries.Count - MaximumEntries);
		_navigationIndex = _entries.Count;
		return true;
	}

	public string Previous(string currentText)
	{
		if (_entries.Count == 0)
			return currentText;
		if (_navigationIndex == _entries.Count)
			_draft = currentText;
		_navigationIndex = Math.Max(0, _navigationIndex - 1);
		return _entries[_navigationIndex];
	}

	public string Next()
	{
		if (_entries.Count == 0 || _navigationIndex >= _entries.Count)
			return _draft;
		_navigationIndex++;
		return _navigationIndex == _entries.Count
			? _draft
			: _entries[_navigationIndex];
	}

	public void ResetNavigation()
	{
		_navigationIndex = _entries.Count;
		_draft = string.Empty;
	}

	private static List<string> Normalize(IEnumerable<string> entries)
	{
		var normalized = new List<string>();
		foreach (var entry in entries)
		{
			var value = NormalizeCommand(entry);
			if (value.Length == 0 ||
				normalized.Count > 0 && string.Equals(normalized[^1], value, StringComparison.Ordinal))
			{
				continue;
			}
			normalized.Add(value);
		}
		if (normalized.Count > MaximumEntries)
			normalized.RemoveRange(0, normalized.Count - MaximumEntries);
		return normalized;
	}

	private static string NormalizeCommand(string? command)
	{
		var normalized = command?.Trim() ?? string.Empty;
		return LimitLength(normalized);
	}

	internal static string LimitLength(string value, int maximumLength = MaximumCommandLength)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
		if (value.Length <= maximumLength)
			return value;
		if (maximumLength == 0)
			return string.Empty;

		var length = maximumLength;
		if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
			length--;
		return value[..length];
	}
}
