using System.Collections.Concurrent;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Infrastructure.FileSystem;

/// <summary>
/// Gives one filesystem operation a stable, single-flight view of each .gitignore source.
/// Process-wide matcher caching remains responsible for cross-operation reuse and revalidation.
/// </summary>
internal sealed class GitIgnoreMatcherLoadSession
{
	private readonly ConcurrentDictionary<string, Lazy<GitIgnoreMatcherLoadResult>> _loads =
		new(PathComparer.Default);
	private readonly Func<string, string, GitIgnoreMatcherLoadResult> _loader;

	public GitIgnoreMatcherLoadSession()
		: this(static (scopeRootPath, gitIgnorePath) =>
			GitIgnoreMatcherFileCache.Load(scopeRootPath, gitIgnorePath))
	{
	}

	internal GitIgnoreMatcherLoadSession(Func<string, string, GitIgnoreMatcherLoadResult> loader)
	{
		_loader = loader ?? throw new ArgumentNullException(nameof(loader));
	}

	internal void Seed(IReadOnlyList<ScopedGitIgnoreMatcher> matchers)
	{
		foreach (var matcher in matchers)
		{
			string sourcePath;
			try
			{
				sourcePath = Path.GetFullPath(Path.Combine(matcher.ScopeRootPath, ".gitignore"));
			}
			catch (Exception exception) when (exception is
			       NotSupportedException or
			       ArgumentException or
			       System.Security.SecurityException)
			{
				continue;
			}

			if (_loads.ContainsKey(sourcePath))
				continue;

			// Rules created for this operation already own the parsed matcher. Seeding keeps
			// later traversal from revalidating the same source while preserving the exact
			// matcher instance and typed load result for every parallel consumer.
			_loads.TryAdd(
				sourcePath,
				new Lazy<GitIgnoreMatcherLoadResult>(
					() => GitIgnoreMatcherLoadResult.Loaded(matcher),
					LazyThreadSafetyMode.ExecutionAndPublication));
		}
	}

	public GitIgnoreMatcherLoadResult Load(string scopeRootPath, string gitIgnorePath)
	{
		IgnorePipelineDiagnostics.RecordGitIgnoreLoadRequest();

		string normalizedPath;
		try
		{
			normalizedPath = Path.GetFullPath(gitIgnorePath);
		}
		catch (Exception exception) when (exception is
		       NotSupportedException or
		       ArgumentException or
		       System.Security.SecurityException)
		{
			return Execute(scopeRootPath, gitIgnorePath);
		}

		if (_loads.TryGetValue(normalizedPath, out var cached))
		{
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();
			return cached.Value;
		}

		var candidate = new Lazy<GitIgnoreMatcherLoadResult>(
			() => Execute(scopeRootPath, normalizedPath),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var selected = _loads.GetOrAdd(normalizedPath, candidate);
		if (!ReferenceEquals(selected, candidate))
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();

		return selected.Value;
	}

	private GitIgnoreMatcherLoadResult Execute(string scopeRootPath, string gitIgnorePath)
	{
		IgnorePipelineDiagnostics.RecordGitIgnoreLoadExecution();
		return _loader(scopeRootPath, gitIgnorePath);
	}
}
