namespace DevProjex.Mcp;

public sealed class McpRootRegistry
{
	private readonly IReadOnlyList<string> _roots;

	public McpRootRegistry(IEnumerable<string> roots)
	{
		ArgumentNullException.ThrowIfNull(roots);
		var normalized = new List<string>();
		foreach (var root in roots)
		{
			if (IsMissingPath(root))
				continue;
			var physical = ResolvePhysicalExistingPath(root, requireDirectory: true);
			if (!normalized.Contains(physical, PathComparer.Default))
				normalized.Add(physical);
		}

		if (normalized.Count == 0)
			throw new ArgumentException("At least one existing MCP root is required.", nameof(roots));
		_roots = normalized;
	}

	public IReadOnlyList<string> Roots => _roots;

	public string ResolveProject(string? project)
	{
		if (string.IsNullOrWhiteSpace(project))
		{
			if (_roots.Count == 1)
				return _roots[0];
			throw new McpToolException(
				McpErrorCodes.UnknownProject,
				$"{McpErrorCodes.UnknownProject}: 'project' is required because multiple roots are available. " +
				$"Call list_projects and use one of: {FormatRoots()}.");
		}

		string physical;
		try
		{
			physical = ResolvePhysicalExistingPath(project, requireDirectory: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			throw UnknownProject(project);
		}

		var match = _roots.FirstOrDefault(root => PathComparer.Default.Equals(root, physical));
		return match ?? throw UnknownProject(project);
	}

	public string ResolveExistingPath(string projectRoot, string path, bool requireDirectory = false)
	{
		if (IsMissingPath(path))
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
		if (!IsWithin(projectRoot, lexicalPath))
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

	private static bool IsWithin(string root, string path)
	{
		if (PathComparer.Default.Equals(root, path))
			return true;
		var prefix = Path.EndsInDirectorySeparator(root)
			? root
			: root + Path.DirectorySeparatorChar;
		return path.StartsWith(
			prefix,
			OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal);
	}

	private static bool IsMissingPath(string? path) =>
		string.IsNullOrEmpty(path) ||
		(OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(path));

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
