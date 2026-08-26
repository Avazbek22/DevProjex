using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal enum TerminalPathPickerMode
{
	Directory,
	JsonFile
}

internal enum TerminalPathPickerError
{
	None,
	AccessDenied,
	Unavailable
}

internal sealed record TerminalPathPickerEntry(
	string Path,
	string Name,
	bool IsDirectory,
	bool IsParent)
{
	public override string ToString() =>
		IsParent
			? "[..] .."
			: IsDirectory
				? $"[D]  {TerminalTextEscaping.EscapeSingleLine(Name)}"
				: $"[F]  {TerminalTextEscaping.EscapeSingleLine(Name)}";
}

internal sealed class TerminalPathPickerModel
{
	private const int MaximumEntries = 1_000;
	private readonly TerminalPathPickerMode _mode;

	public TerminalPathPickerModel(TerminalPathPickerMode mode, string? initialPath)
	{
		_mode = mode;
		Open(ResolveInitialDirectory(initialPath));
	}

	public string CurrentDirectory { get; private set; } = string.Empty;

	public IReadOnlyList<TerminalPathPickerEntry> Entries { get; private set; } = [];

	public TerminalPathPickerError Error { get; private set; }

	public bool IsTruncated { get; private set; }

	public void Open(string path)
	{
		var entries = new List<TerminalPathPickerEntry>();
		Error = TerminalPathPickerError.None;
		IsTruncated = false;
		try
		{
			var normalized = Path.GetFullPath(path);
			var parent = Directory.GetParent(normalized);
			if (parent is not null)
			{
				entries.Add(new TerminalPathPickerEntry(
					parent.FullName,
					"..",
					IsDirectory: true,
					IsParent: true));
			}

			var children = TakeOrderedEntries(
				Directory
					.EnumerateFileSystemEntries(normalized)
					.Select(CreateEntry)
					.Where(entry => entry.IsDirectory || IsVisibleFile(entry.Path)),
				MaximumEntries + 1);
			IsTruncated = children.Count > MaximumEntries;
			entries.AddRange(children.Take(MaximumEntries));
			CurrentDirectory = normalized;
			Entries = entries;
		}
		catch (UnauthorizedAccessException)
		{
			Error = TerminalPathPickerError.AccessDenied;
			CurrentDirectory = path;
			Entries = entries;
		}
		catch (Exception exception) when (
			exception is IOException or
			ArgumentException or
			NotSupportedException)
		{
			Error = TerminalPathPickerError.Unavailable;
			CurrentDirectory = path;
			Entries = entries;
		}
	}

	public bool TryOpenEntry(int index, out string? selectedPath)
	{
		selectedPath = null;
		if (index < 0 || index >= Entries.Count)
			return false;

		var entry = Entries[index];
		if (entry.IsDirectory)
		{
			Open(entry.Path);
			return true;
		}

		if (_mode == TerminalPathPickerMode.JsonFile)
			selectedPath = entry.Path;
		return selectedPath is not null;
	}

	public string? SelectCurrentDirectory() =>
		_mode == TerminalPathPickerMode.Directory && Directory.Exists(CurrentDirectory)
			? CurrentDirectory
			: null;

	public string? SelectEntry(int index)
	{
		if (index < 0 || index >= Entries.Count)
			return null;
		var entry = Entries[index];
		return _mode switch
		{
			TerminalPathPickerMode.Directory when entry.IsDirectory => entry.Path,
			TerminalPathPickerMode.JsonFile when !entry.IsDirectory => entry.Path,
			_ => null
		};
	}

	internal static IReadOnlyList<TerminalPathPickerEntry> TakeOrderedEntries(
		IEnumerable<TerminalPathPickerEntry> source,
		int maximumCount)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

		var nameComparer = StringComparer.CurrentCultureIgnoreCase;
		var orderedComparer = new PathPickerCandidateComparer(nameComparer, reverse: false);
		var worstFirstComparer = new PathPickerCandidateComparer(nameComparer, reverse: true);
		var retained = new PriorityQueue<PathPickerCandidate, PathPickerCandidate>(worstFirstComparer);
		long sequence = 0;
		foreach (var entry in source)
		{
			var candidate = new PathPickerCandidate(entry, sequence++);
			if (retained.Count < maximumCount)
			{
				retained.Enqueue(candidate, candidate);
			}
			else if (orderedComparer.Compare(candidate, retained.Peek()) < 0)
			{
				retained.Dequeue();
				retained.Enqueue(candidate, candidate);
			}
		}

		var result = new PathPickerCandidate[retained.Count];
		var index = 0;
		foreach (var item in retained.UnorderedItems)
			result[index++] = item.Element;
		Array.Sort(result, orderedComparer);
		return result.Select(static candidate => candidate.Entry).ToArray();
	}

	private bool IsVisibleFile(string path) =>
		_mode == TerminalPathPickerMode.JsonFile &&
		path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

	private static TerminalPathPickerEntry CreateEntry(string path) =>
		new(
			path,
			Path.GetFileName(path),
			Directory.Exists(path),
			IsParent: false);

	private static string ResolveInitialDirectory(string? initialPath)
	{
		if (!string.IsNullOrWhiteSpace(initialPath))
		{
			if (Directory.Exists(initialPath))
				return initialPath;
			if (File.Exists(initialPath) &&
			    Path.GetDirectoryName(Path.GetFullPath(initialPath)) is { } directory)
			{
				return directory;
			}
		}

		return Directory.GetCurrentDirectory();
	}

	private readonly record struct PathPickerCandidate(
		TerminalPathPickerEntry Entry,
		long Sequence);

	private sealed class PathPickerCandidateComparer(
		StringComparer nameComparer,
		bool reverse) : IComparer<PathPickerCandidate>
	{
		public int Compare(PathPickerCandidate left, PathPickerCandidate right) =>
			reverse ? CompareCore(right, left) : CompareCore(left, right);

		private int CompareCore(PathPickerCandidate left, PathPickerCandidate right)
		{
			if (left.Entry.IsDirectory != right.Entry.IsDirectory)
				return left.Entry.IsDirectory ? -1 : 1;
			var nameOrder = nameComparer.Compare(left.Entry.Name, right.Entry.Name);
			return nameOrder != 0 ? nameOrder : left.Sequence.CompareTo(right.Sequence);
		}
	}
}
