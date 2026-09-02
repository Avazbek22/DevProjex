namespace DevProjex.Mcp;

public sealed class McpRootRegistry
{
	private readonly IReadOnlyList<string> _roots;
	private readonly Dictionary<string, List<string>> _lexicalRootsByPhysical;

	public McpRootRegistry(IEnumerable<string> roots)
	{
		ArgumentNullException.ThrowIfNull(roots);
		var normalized = new List<string>();
		var lexicalRootsByPhysical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var root in roots)
		{
			if (PathUtility.IsMissingPath(root))
				throw new ArgumentException("MCP roots cannot be empty.", nameof(roots));
			var lexicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var physical = McpRootJailFileStreamOpener.ResolveDirectoryPath(
				ResolvePhysicalExistingPath(root, requireDirectory: true));
			if (!normalized.Contains(physical, StringComparer.Ordinal))
				normalized.Add(physical);
			AddLexicalRoot(lexicalRootsByPhysical, physical, lexicalRoot);
			AddLexicalRoot(lexicalRootsByPhysical, physical, physical);
		}

		if (normalized.Count == 0)
			throw new ArgumentException("At least one existing MCP root is required.", nameof(roots));
		_roots = normalized.AsReadOnly();
		_lexicalRootsByPhysical = lexicalRootsByPhysical;
	}

	public IReadOnlyList<string> Roots => _roots;

	public string ResolveProject(string? project)
	{
		if (PathUtility.IsMissingPath(project))
		{
			if (_roots.Count == 1)
				return ResolveProject(_roots[0]);
			throw new McpToolException(
				McpErrorCodes.UnknownProject,
				$"{McpErrorCodes.UnknownProject}: 'project' is required because multiple roots are available. " +
				$"Call list_projects and use one of: {FormatRoots()}.");
		}
		var requestedProject = project!;

		string physical;
		try
		{
			physical = McpRootJailFileStreamOpener.ResolveDirectoryPath(
				ResolvePhysicalExistingPath(requestedProject, requireDirectory: true));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			throw UnknownProject(requestedProject);
		}

		var match = _roots.FirstOrDefault(root => StringComparer.Ordinal.Equals(root, physical));
		return match ?? throw UnknownProject(requestedProject);
	}

	public string ResolveExistingPath(string projectRoot, string path, bool requireDirectory = false)
	{
		if (PathUtility.IsMissingPath(path))
			throw InvalidPath();

		string candidate;
		string lexicalPath;
		try
		{
			candidate = Path.IsPathFullyQualified(path)
				? path
				: Path.Combine(projectRoot, path);
			lexicalPath = Path.GetFullPath(candidate);
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw InvalidPath();
		}
		if (!IsWithinConfiguredLexicalRoot(projectRoot, lexicalPath))
			throw RootViolation(path);
		string physical;
		try
		{
			physical = ResolvePhysicalExistingPath(candidate, requireDirectory);
		}
		catch (FileNotFoundException)
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: path '{path}' does not exist inside project '{projectRoot}'.");
		}
		catch (DirectoryNotFoundException)
		{
			throw new McpToolException(
				McpErrorCodes.PathNotFound,
				$"{McpErrorCodes.PathNotFound}: path '{path}' does not exist inside project '{projectRoot}'.");
		}

		if (!IsWithin(projectRoot, physical))
			throw RootViolation(path);
		return physical;
	}

	internal string? FindLexicalRoot(string path)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(path);
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}

		string? match = null;
		var matchLength = -1;
		foreach (var pair in _lexicalRootsByPhysical)
		{
			foreach (var lexicalRoot in pair.Value)
			{
				if (lexicalRoot.Length <= matchLength || !IsWithin(lexicalRoot, fullPath))
					continue;
				match = pair.Key;
				matchLength = lexicalRoot.Length;
			}
		}
		return match;
	}

	internal void EnsureOpenedPathIsWithin(string projectRoot, string requestedPath, string openedPath)
	{
		if (!IsWithin(projectRoot, Path.GetFullPath(openedPath)))
			throw RootViolation(requestedPath);
	}

	public static string ResolvePhysicalExistingPath(string path, bool requireDirectory)
	{
		var fullPath = Path.GetFullPath(path);
		if (requireDirectory && !Directory.Exists(fullPath))
			throw new DirectoryNotFoundException(fullPath);
		if (!requireDirectory && !Directory.Exists(fullPath) && !File.Exists(fullPath))
			throw new FileNotFoundException("Path was not found.", fullPath);

		var pathRoot = Path.GetPathRoot(fullPath) ??
		               throw new ArgumentException("The path has no filesystem root.", nameof(path));
		var current = Path.TrimEndingDirectorySeparator(pathRoot);
		if (current.Length == 0)
			current = pathRoot;
		var relative = Path.GetRelativePath(pathRoot, fullPath);
		foreach (var segment in relative.Split(
			         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			         StringSplitOptions.RemoveEmptyEntries))
		{
			var candidate = Path.Combine(current, segment);
			FileSystemInfo info = Directory.Exists(candidate)
				? new DirectoryInfo(candidate)
				: new FileInfo(candidate);
			var target = info.ResolveLinkTarget(returnFinalTarget: true);
			current = Path.GetFullPath(target?.FullName ?? candidate);
		}
		return Path.TrimEndingDirectorySeparator(current);
	}

	private bool IsWithinConfiguredLexicalRoot(string projectRoot, string path)
	{
		if (!_lexicalRootsByPhysical.TryGetValue(projectRoot, out var lexicalRoots))
			return IsWithin(projectRoot, path);
		return lexicalRoots.Any(root => IsWithin(root, path));
	}

	private static void AddLexicalRoot(
		Dictionary<string, List<string>> aliases,
		string physicalRoot,
		string lexicalRoot)
	{
		if (!aliases.TryGetValue(physicalRoot, out var roots))
		{
			roots = [];
			aliases.Add(physicalRoot, roots);
		}
		if (!roots.Contains(lexicalRoot, StringComparer.Ordinal))
			roots.Add(lexicalRoot);
	}

	private static bool IsWithin(string root, string path)
	{
		if (StringComparer.Ordinal.Equals(root, path))
			return true;
		var prefix = Path.EndsInDirectorySeparator(root)
			? root
			: root + Path.DirectorySeparatorChar;
		return path.StartsWith(prefix, StringComparison.Ordinal);
	}

	private McpToolException UnknownProject(string project) =>
		new(
			McpErrorCodes.UnknownProject,
			$"{McpErrorCodes.UnknownProject}: project '{project}' is not an allowed root. " +
			$"Call list_projects and use one of: {FormatRoots()}.");

	private McpToolException RootViolation(string path) =>
		new(
			McpErrorCodes.RootViolation,
			$"{McpErrorCodes.RootViolation}: path '{path}' resolves outside the allowed project root. " +
			$"Valid roots: {FormatRoots()}.");

	private static McpToolException InvalidPath() =>
		new(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: 'path' is not a valid filesystem path; " +
			"provide a valid path inside the project.");

	private string FormatRoots() => string.Join(", ", _roots.Select(static root => $"'{root}'"));
}
