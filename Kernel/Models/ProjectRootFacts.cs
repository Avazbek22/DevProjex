using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed class ProjectRootFacts
{
	private static readonly FrozenSet<string> EmptyPathNameSet =
		Array.Empty<string>().ToFrozenSet(PathComparer.Default);

	private static readonly FrozenSet<string> EmptyMarkerNameSet =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private readonly FrozenSet<string> _fileNames;
	private readonly FrozenSet<string> _markerFileNames;
	private readonly FrozenSet<string> _fileExtensions;
	private readonly FrozenSet<string> _directoryNames;
	private readonly FrozenSet<string> _markerDirectoryNames;
	private readonly FrozenSet<string> _nonReparseMarkerDirectoryNames;
	private readonly FrozenDictionary<string, ProjectRootDirectoryFact> _directoriesByName;

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

		_fileNames = files.Count == 0
			? EmptyPathNameSet
			: files.Select(static file => file.Name).ToFrozenSet(PathComparer.Default);
		_markerFileNames = files.Count == 0
			? EmptyMarkerNameSet
			: files.Select(static file => file.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		_fileExtensions = files.Count == 0
			? EmptyMarkerNameSet
			: files
				.Select(static file => file.Extension)
				.Where(static extension => !string.IsNullOrWhiteSpace(extension))
				.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		_directoryNames = directories.Count == 0
			? EmptyPathNameSet
			: directories.Select(static directory => directory.Name).ToFrozenSet(PathComparer.Default);
		_markerDirectoryNames = directories.Count == 0
			? EmptyMarkerNameSet
			: directories.Select(static directory => directory.Name).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		_nonReparseMarkerDirectoryNames = directories.Count == 0
			? EmptyMarkerNameSet
			: directories
				.Where(static directory => !directory.IsReparsePoint)
				.Select(static directory => directory.Name)
				.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		_directoriesByName = directories.Count == 0
			? FrozenDictionary<string, ProjectRootDirectoryFact>.Empty
			: directories
				.GroupBy(static directory => directory.Name, PathComparer.Default)
				.ToFrozenDictionary(
					static group => group.Key,
					static group => group.First(),
					PathComparer.Default);
	}

	public string RootPath { get; }

	public bool Exists { get; }

	public bool IsAccessible { get; }

	public IReadOnlyList<ProjectRootFileFact> Files { get; }

	public IReadOnlyList<ProjectRootDirectoryFact> Directories { get; }

	public ProjectRootFileSignature? GitIgnoreSignature { get; }

	public bool HasGitIgnoreFile => HasFile(".gitignore");

	public bool HasGitMetadataEntry => HasFile(".git") || HasDirectory(".git");

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
		!string.IsNullOrWhiteSpace(fileName) && _fileNames.Contains(fileName);

	public bool HasMarkerFile(string fileName) =>
		!string.IsNullOrWhiteSpace(fileName) && _markerFileNames.Contains(fileName);

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
			if (!string.IsNullOrWhiteSpace(extension) && _fileExtensions.Contains(extension))
				return true;
		}

		return false;
	}

	public bool HasDirectory(string directoryName) =>
		!string.IsNullOrWhiteSpace(directoryName) && _directoryNames.Contains(directoryName);

	public bool TryGetDirectory(string directoryName, out ProjectRootDirectoryFact directory)
	{
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			directory = default;
			return false;
		}

		return _directoriesByName.TryGetValue(directoryName, out directory);
	}

	public bool HasAnyDirectoryName(IEnumerable<string> directoryNames, bool includeReparsePoints = false)
	{
		var availableNames = includeReparsePoints
			? _markerDirectoryNames
			: _nonReparseMarkerDirectoryNames;
		foreach (var directoryName in directoryNames)
			if (!string.IsNullOrWhiteSpace(directoryName) && availableNames.Contains(directoryName))
				return true;

		return false;
	}
}

public readonly record struct ProjectRootFileFact(
	string Name,
	string Extension);

public readonly record struct ProjectRootDirectoryFact(
	string Name,
	string FullPath,
	bool IsReparsePoint);

public readonly record struct ProjectRootFileSignature(
	long LastWriteTicksUtc,
	long LengthBytes,
	string LinkTarget,
	string ContentFingerprint);
