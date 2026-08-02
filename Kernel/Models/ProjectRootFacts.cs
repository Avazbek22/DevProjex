using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed class ProjectRootFacts
{
	// Most project roots contain only a handful of entries. Below this benchmark-backed
	// threshold, linear probes cost less than constructing and retaining six frozen indexes.
	private const int IndexedLookupThreshold = 128;

	private readonly FrozenSet<string>? _fileNames;
	private readonly FrozenSet<string>? _markerFileNames;
	private readonly FrozenSet<string>? _fileExtensions;
	private readonly FrozenSet<string>? _directoryNames;
	private readonly FrozenSet<string>? _markerDirectoryNames;
	private readonly FrozenSet<string>? _nonReparseMarkerDirectoryNames;
	private readonly FrozenDictionary<string, ProjectRootDirectoryFact>? _directoriesByName;
	private readonly bool _hasGitMetadataEntry;
	private readonly bool _hasGitIgnoreFile;

	public ProjectRootFacts(
		string rootPath,
		bool exists,
		bool isAccessible,
		IReadOnlyList<ProjectRootFileFact> files,
		IReadOnlyList<ProjectRootDirectoryFact> directories,
		ProjectRootFileSignature? gitIgnoreSignature)
	{
		RootPath = rootPath;
		Exists = exists;
		IsAccessible = isAccessible;
		Files = files;
		Directories = directories;
		GitIgnoreSignature = gitIgnoreSignature;

		if (files.Count >= IndexedLookupThreshold)
		{
			_fileNames = files.Select(static file => file.Name).ToFrozenSet(PathComparer.Default);
			_markerFileNames = files.Select(static file => file.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
			_fileExtensions = files
				.Select(static file => file.Extension)
				.Where(static extension => !string.IsNullOrWhiteSpace(extension))
				.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		}

		if (directories.Count >= IndexedLookupThreshold)
		{
			_directoryNames = directories
				.Select(static directory => directory.Name)
				.ToFrozenSet(PathComparer.Default);
			_markerDirectoryNames = directories
				.Select(static directory => directory.Name)
				.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
			_nonReparseMarkerDirectoryNames = directories
				.Where(static directory => !directory.IsReparsePoint)
				.Select(static directory => directory.Name)
				.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

			// Filesystems cannot normally expose duplicate sibling names, but synthetic
			// facts and transient observations can. Preserve the former first-entry contract.
			var directoriesByName = new Dictionary<string, ProjectRootDirectoryFact>(
				directories.Count,
				PathComparer.Default);
			foreach (var directory in directories)
				directoriesByName.TryAdd(directory.Name, directory);
			_directoriesByName = directoriesByName.ToFrozenDictionary(PathComparer.Default);
		}

		var hasGitMetadataEntry = false;
		for (var index = 0; index < files.Count; index++)
		{
			var file = files[index];
			if (!file.IsReparsePoint && PathComparer.Default.Equals(file.Name, ".gitignore"))
				_hasGitIgnoreFile = true;

			if (!file.IsReparsePoint && PathComparer.Default.Equals(file.Name, ".git"))
				hasGitMetadataEntry = true;
		}

		if (!hasGitMetadataEntry)
		{
			for (var index = 0; index < directories.Count; index++)
			{
				var directory = directories[index];
				if (directory.IsReparsePoint || !PathComparer.Default.Equals(directory.Name, ".git"))
					continue;

				hasGitMetadataEntry = true;
				break;
			}
		}

		_hasGitMetadataEntry = hasGitMetadataEntry;
	}

	public string RootPath { get; }

	public bool Exists { get; }

	public bool IsAccessible { get; }

	public IReadOnlyList<ProjectRootFileFact> Files { get; }

	public IReadOnlyList<ProjectRootDirectoryFact> Directories { get; }

	public ProjectRootFileSignature? GitIgnoreSignature { get; }

	/// <summary>
	/// Reports only a regular working-tree .gitignore file. Git does not follow a
	/// symbolic link when it accesses this control file, so neither does DevProjex.
	/// </summary>
	public bool HasGitIgnoreFile => _hasGitIgnoreFile;

	public bool HasGitMetadataEntry => _hasGitMetadataEntry;

	public static ProjectRootFacts Missing(string rootPath) =>
		new(
			rootPath,
			exists: false,
			isAccessible: false,
			files: [],
			directories: [],
			gitIgnoreSignature: null);

	public static ProjectRootFacts Inaccessible(string rootPath) =>
		new(
			rootPath,
			exists: true,
			isAccessible: false,
			files: [],
			directories: [],
			gitIgnoreSignature: null);

	public bool HasFile(string fileName) =>
		!string.IsNullOrWhiteSpace(fileName) && ContainsFileName(fileName, markerComparison: false);

	public bool HasMarkerFile(string fileName) =>
		!string.IsNullOrWhiteSpace(fileName) && ContainsFileName(fileName, markerComparison: true);

	public bool HasAnyMarkerFile(IEnumerable<string> fileNames)
	{
		foreach (var fileName in fileNames)
		{
			if (HasMarkerFile(fileName))
				return true;
		}

		return false;
	}

	public bool HasAnyFileExtension(IEnumerable<string> extensions)
	{
		foreach (var extension in extensions)
		{
			if (!string.IsNullOrWhiteSpace(extension) && ContainsFileExtension(extension))
				return true;
		}

		return false;
	}

	public bool HasDirectory(string directoryName) =>
		!string.IsNullOrWhiteSpace(directoryName) && ContainsDirectoryName(
			directoryName,
			markerComparison: false,
			includeReparsePoints: true);

	public bool TryGetDirectory(string directoryName, out ProjectRootDirectoryFact directory)
	{
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			directory = default;
			return false;
		}

		if (_directoriesByName is not null)
			return _directoriesByName.TryGetValue(directoryName, out directory);

		for (var index = 0; index < Directories.Count; index++)
		{
			var candidate = Directories[index];
			if (!PathComparer.Default.Equals(candidate.Name, directoryName))
				continue;

			directory = candidate;
			return true;
		}

		directory = default;
		return false;
	}

	public bool HasAnyDirectoryName(IEnumerable<string> directoryNames, bool includeReparsePoints = false)
	{
		foreach (var directoryName in directoryNames)
			if (!string.IsNullOrWhiteSpace(directoryName) &&
			    ContainsDirectoryName(
				    directoryName,
				    markerComparison: true,
				    includeReparsePoints))
				return true;

		return false;
	}

	private bool ContainsFileName(string fileName, bool markerComparison)
	{
		var index = markerComparison ? _markerFileNames : _fileNames;
		if (index is not null)
			return index.Contains(fileName);

		var comparer = markerComparison ? StringComparer.OrdinalIgnoreCase : PathComparer.Default;
		for (var position = 0; position < Files.Count; position++)
			if (comparer.Equals(Files[position].Name, fileName))
				return true;
		return false;
	}

	private bool ContainsFileExtension(string extension)
	{
		if (_fileExtensions is not null)
			return _fileExtensions.Contains(extension);

		for (var index = 0; index < Files.Count; index++)
			if (StringComparer.OrdinalIgnoreCase.Equals(Files[index].Extension, extension))
				return true;
		return false;
	}

	private bool ContainsDirectoryName(
		string directoryName,
		bool markerComparison,
		bool includeReparsePoints)
	{
		var index = markerComparison
			? includeReparsePoints
				? _markerDirectoryNames
				: _nonReparseMarkerDirectoryNames
			: _directoryNames;
		if (index is not null)
			return index.Contains(directoryName);

		var comparer = markerComparison ? StringComparer.OrdinalIgnoreCase : PathComparer.Default;
		for (var position = 0; position < Directories.Count; position++)
		{
			var directory = Directories[position];
			if ((!includeReparsePoints && directory.IsReparsePoint) ||
			    !comparer.Equals(directory.Name, directoryName))
			{
				continue;
			}

			return true;
		}

		return false;
	}
}

public readonly record struct ProjectRootFileFact(
	string Name,
	string Extension,
	bool IsReparsePoint = false);

public readonly record struct ProjectRootDirectoryFact(
	string Name,
	string FullPath,
	bool IsReparsePoint);

public readonly record struct ProjectRootFileSignature(
	long LastWriteTicksUtc,
	long LengthBytes,
	string LinkTarget,
	string ContentFingerprint);
