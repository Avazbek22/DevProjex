using DevProjex.Terminal.Rendering;
using System.Text.RegularExpressions;

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

	public string ResolveInputPath(string input)
	{
		var expanded = ExpandPath(input);
		return Path.GetFullPath(Path.IsPathRooted(expanded)
			? expanded
			: Path.Combine(CurrentDirectory, expanded));
	}

	public string? SelectInputPath(string input)
	{
		try
		{
			var path = ResolveInputPath(input);
			return _mode switch
			{
				TerminalPathPickerMode.Directory when Directory.Exists(path) => path,
				TerminalPathPickerMode.JsonFile when File.Exists(path) &&
					path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) => path,
				_ => null
			};
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
		{
			return null;
		}
	}

	public string CompleteInputPath(string input)
	{
		try
		{
			var expanded = ExpandPath(input);
			var absolute = Path.GetFullPath(Path.IsPathRooted(expanded)
				? expanded
				: Path.Combine(CurrentDirectory, expanded));
			var directory = Directory.Exists(absolute)
				? absolute
				: Path.GetDirectoryName(absolute);
			var prefix = Directory.Exists(absolute) ? string.Empty : Path.GetFileName(absolute);
			if (directory is null || !Directory.Exists(directory))
				return input;
			var matches = Directory.EnumerateFileSystemEntries(directory)
				.Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
				.Where(path => Directory.Exists(path) || IsVisibleFile(path))
				.Take(100)
				.ToArray();
			if (matches.Length == 0)
				return input;
			var completion = matches.Length == 1
				? matches[0]
				: Path.Combine(directory, CommonPrefix(matches.Select(path => Path.GetFileName(path) ?? string.Empty)));
			if (matches.Length == 1 && Directory.Exists(completion))
				completion += Path.DirectorySeparatorChar;
			return completion;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
			ArgumentException or NotSupportedException)
		{
			return input;
		}
	}

	internal static string ExpandPath(string input)
	{
		var value = input.Trim();
		if (value == "~" || value.StartsWith($"~{Path.DirectorySeparatorChar}") ||
			value.StartsWith($"~{Path.AltDirectorySeparatorChar}"))
		{
			value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + value[1..];
		}
		value = Environment.ExpandEnvironmentVariables(value);
		return Regex.Replace(value, @"\$(?:\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))", match =>
			Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? match.Value);
	}

	private static string CommonPrefix(IEnumerable<string> values)
	{
		var items = values.ToArray();
		if (items.Length == 0)
			return string.Empty;
		var prefix = items[0];
		foreach (var item in items.Skip(1))
		{
			var length = 0;
			while (length < prefix.Length && length < item.Length &&
				char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(item[length]))
			{
				length++;
			}
			prefix = prefix[..length];
		}
		return prefix;
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
