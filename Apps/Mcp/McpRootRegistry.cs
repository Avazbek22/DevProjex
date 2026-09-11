namespace DevProjex.Mcp;

public sealed class McpRootRegistry
{
	private readonly IReadOnlyList<string> _roots;
	private readonly Dictionary<string, List<string>> _lexicalRootsByPhysical;
	private readonly Dictionary<string, List<string>> _rootsByName;

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
		_rootsByName = normalized
			.GroupBy(GetProjectName, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => group.ToList(),
				StringComparer.Ordinal);
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
				$"Call list_projects and use a listed name or path: {FormatRoots()}.");
		}
		var requestedProject = project!;
		if (_rootsByName.TryGetValue(requestedProject, out var namedRoots))
		{
			if (namedRoots.Count == 1)
				return namedRoots[0];
			throw new McpToolException(
				McpErrorCodes.UnknownProject,
				$"{McpErrorCodes.UnknownProject}: project name '{requestedProject}' is ambiguous. " +
				$"Call list_projects and use one of these paths: {string.Join(", ", namedRoots.Select(static root => $"'{root}'"))}.");
		}

		// A path that names a host is refused here, ahead of the resolution below rather than
		// after it. Everything from this point opens the path, and opening such a form is what
		// makes the operating system contact the host: the answer would already have travelled
		// over the network by the time the containment comparison at the end of this method could
		// reject it. A root listed at startup stays addressable through the name lookup above and
		// through the comparison here, both of them in memory.
		if (McpRemoteProviderPath.ReachesRemoteProvider(requestedProject) &&
		    !IsConfiguredRootSpelling(requestedProject))
		{
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: 'project' is written as a path that names a host. " +
				"Such a form is refused before 'project' is resolved against the allowed roots, so that " +
				"naming one cannot make the server reach it. " +
				$"Call list_projects and use a listed name or path: {FormatRoots()}.");
		}

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

	/// <summary>
	/// Whether the string names one of the roots this server was started with, as written or in
	/// any spelling that resolves to the same recorded one.
	/// </summary>
	/// <remarks>
	/// A comparison over the table the constructor built: it opens nothing, which is what makes it
	/// safe to ask about a path shape that has not yet been cleared for filesystem access. Roots
	/// given at startup are the operator's own choice, so a remote one among them stays usable,
	/// and under every spelling of itself rather than only the one the operator typed.
	/// </remarks>
	internal bool IsConfiguredRootSpelling(string? path)
	{
		if (path is null)
			return false;
		return IsRecordedSpelling(path) ||
		       (CanonicalSpelling(path) is { } canonical && IsRecordedSpelling(canonical));
	}

	private bool IsRecordedSpelling(string spelling) =>
		_lexicalRootsByPhysical.Values.Any(
			spellings => spellings.Contains(spelling, StringComparer.Ordinal));

	/// <summary>
	/// The spelling a recorded root would carry if it were written this way, or null when the
	/// string cannot be read as a path at all.
	/// </summary>
	/// <remarks>
	/// Roots are recorded as <see cref="Path.GetFullPath(string)"/> produced them, so a client that writes
	/// the same root with forward slashes, a trailing separator, or the extended-length prefix is
	/// naming something already listed and has to keep resolving. Every step here is lexical:
	/// <see cref="Path.GetFullPath(string)"/> consults the filesystem for nothing, which is what
	/// allows the question to be asked about a form that has not been cleared to touch it.
	/// </remarks>
	private static string? CanonicalSpelling(string path)
	{
		const string uncDevicePrefix = @"\\?\UNC\";
		const string extendedPrefix = @"\\?\";
		try
		{
			var candidate = Path.GetFullPath(path.Trim());
			if (candidate.StartsWith(uncDevicePrefix, StringComparison.OrdinalIgnoreCase))
				candidate = @"\\" + candidate[uncDevicePrefix.Length..];
			else if (candidate.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
				candidate = candidate[extendedPrefix.Length..];
			return Path.TrimEndingDirectorySeparator(candidate);
		}
		catch (Exception exception) when (
			exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
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
		McpProjectPathProbe.Record();
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
			// Each segment is opened in turn, so each one reports itself.
			McpProjectPathProbe.Record();
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
			$"{McpErrorCodes.UnknownProject}: project '{project}' is not an allowed root name or path. " +
			$"Call list_projects and use a listed name or path: {FormatRoots()}.");

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

	private string FormatRoots() => string.Join(
		", ",
		_roots.Select(static root => $"'{GetProjectName(root)}' ('{root}')"));

	internal static string GetProjectName(string root)
	{
		var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
		return string.IsNullOrEmpty(name) ? root : name;
	}
}
