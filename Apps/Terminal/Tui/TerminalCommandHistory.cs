namespace DevProjex.Terminal.Tui;

internal sealed class TerminalCommandHistory
{
	public const int MaximumEntries = 50;
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
		var normalized = command?.Trim() ?? string.Empty;
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
			var value = entry?.Trim() ?? string.Empty;
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
}
