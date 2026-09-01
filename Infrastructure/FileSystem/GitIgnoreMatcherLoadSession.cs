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
		new(ProjectTreePathIdentity.CanonicalComparer);
	private readonly Func<string, string, CancellationToken, GitIgnoreMatcherLoadResult> _loader;

	public GitIgnoreMatcherLoadSession()
		: this(static (scopeRootPath, gitIgnorePath, cancellationToken) =>
			GitIgnoreMatcherFileCache.LoadWithCancellation(
				scopeRootPath,
				gitIgnorePath,
				cancellationToken))
	{
	}

	internal GitIgnoreMatcherLoadSession(Func<string, string, GitIgnoreMatcherLoadResult> loader)
		: this((scopeRootPath, gitIgnorePath, _) => loader(scopeRootPath, gitIgnorePath))
	{
	}

	internal GitIgnoreMatcherLoadSession(
		Func<string, string, CancellationToken, GitIgnoreMatcherLoadResult> loader)
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

	public GitIgnoreMatcherLoadResult Load(string scopeRootPath, string gitIgnorePath) =>
		LoadWithCancellation(scopeRootPath, gitIgnorePath, CancellationToken.None);

	public GitIgnoreMatcherLoadResult LoadWithCancellation(
		string scopeRootPath,
		string gitIgnorePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
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
			return Execute(scopeRootPath, gitIgnorePath, cancellationToken);
		}

		if (_loads.TryGetValue(normalizedPath, out var cached))
		{
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();
			cancellationToken.ThrowIfCancellationRequested();
			return GetValueOrRemoveCanceled(normalizedPath, cached);
		}

		var candidate = new Lazy<GitIgnoreMatcherLoadResult>(
			() => Execute(scopeRootPath, normalizedPath, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var selected = _loads.GetOrAdd(normalizedPath, candidate);
		if (!ReferenceEquals(selected, candidate))
			IgnorePipelineDiagnostics.RecordGitIgnoreLoadReuse();

		if (cancellationToken.IsCancellationRequested)
		{
			if (ReferenceEquals(selected, candidate))
				RemoveExact(normalizedPath, selected);
			cancellationToken.ThrowIfCancellationRequested();
		}

		return GetValueOrRemoveCanceled(normalizedPath, selected);
	}

	private GitIgnoreMatcherLoadResult GetValueOrRemoveCanceled(
		string normalizedPath,
		Lazy<GitIgnoreMatcherLoadResult> load)
	{
		try
		{
			return load.Value;
		}
		catch (OperationCanceledException)
		{
			RemoveExact(normalizedPath, load);
			throw;
		}
	}

	private void RemoveExact(string normalizedPath, Lazy<GitIgnoreMatcherLoadResult> load) =>
		((ICollection<KeyValuePair<string, Lazy<GitIgnoreMatcherLoadResult>>>)_loads)
		.Remove(new KeyValuePair<string, Lazy<GitIgnoreMatcherLoadResult>>(normalizedPath, load));

	private GitIgnoreMatcherLoadResult Execute(
		string scopeRootPath,
		string gitIgnorePath,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		IgnorePipelineDiagnostics.RecordGitIgnoreLoadExecution();
		return _loader(scopeRootPath, gitIgnorePath, cancellationToken);
	}
}
