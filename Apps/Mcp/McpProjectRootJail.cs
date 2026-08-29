namespace DevProjex.Mcp;

internal sealed class McpProjectRootJail(
	McpRootRegistry localRoots,
	McpProjectSourceResolver? projectSources = null)
{
	public string ResolveExistingPath(
		string projectRoot,
		string path,
		bool requireDirectory = false)
	{
		var scope = ResolveScope(projectRoot);
		try
		{
			return scope.Registry.ResolveExistingPath(projectRoot, path, requireDirectory);
		}
		catch (McpToolException exception)
		{
			throw Translate(exception, scope);
		}
	}

	public McpRootJailScope? FindLexicalRoot(string path)
	{
		var candidates = new List<McpRootJailScope>();
		var localRoot = localRoots.FindLexicalRoot(path);
		if (localRoot is not null)
			candidates.Add(new McpRootJailScope(localRoots, localRoot, localRoot));
		foreach (var source in projectSources?.GetRemoteRootsSnapshot() ?? [])
		{
			var remoteRoot = source.Registry.FindLexicalRoot(path);
			if (remoteRoot is not null)
				candidates.Add(new McpRootJailScope(source.Registry, remoteRoot, source.Address));
		}
		return candidates.OrderByDescending(static candidate => candidate.Root.Length).FirstOrDefault();
	}

	public McpRootJailScope ResolveLexicalRoot(string path)
	{
		var scope = FindLexicalRoot(path);
		if (scope is not null)
			return scope;

		var validRoots = localRoots.Roots
			.Concat(projectSources?.GetRemoteRootsSnapshot().Select(static source => source.Address) ?? [])
			.Distinct(StringComparer.Ordinal)
			.Select(static root => $"'{root}'");
		throw new McpToolException(
			McpErrorCodes.RootViolation,
			$"{McpErrorCodes.RootViolation}: path '{path}' is outside every allowed project root. " +
			$"Valid roots: {string.Join(", ", validRoots)}.");
	}

	public void EnsureOpenedPathIsWithin(
		McpRootJailScope scope,
		string requestedPath,
		string openedPath)
	{
		try
		{
			scope.Registry.EnsureOpenedPathIsWithin(scope.Root, requestedPath, openedPath);
		}
		catch (McpToolException exception)
		{
			throw Translate(exception, scope);
		}
	}

	private McpRootJailScope ResolveScope(string projectRoot)
	{
		if (projectSources is not null &&
		    projectSources.TryGetRemoteRoot(projectRoot, out var remote))
			return new McpRootJailScope(remote.Registry, remote.Root, remote.Address);

		var localRoot = localRoots.ResolveProject(projectRoot);
		return new McpRootJailScope(localRoots, localRoot, localRoot);
	}

	private static McpToolException Translate(
		McpToolException exception,
		McpRootJailScope scope)
	{
		if (PathComparer.Default.Equals(scope.Root, scope.Address))
			return exception;
		return new McpToolException(
			exception.Code,
			exception.Message.Replace(scope.Root, scope.Address, StringComparison.Ordinal));
	}
}

internal sealed record McpRootJailScope(
	McpRootRegistry Registry,
	string Root,
	string Address);
