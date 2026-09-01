namespace DevProjex.Infrastructure.FileSystem;

internal sealed class ScopedGitIgnoreMatcherAccumulator
{
	private readonly List<ScopedGitIgnoreMatcher> _items = [];
	private readonly HashSet<string> _scopeRootPaths = new(ProjectTreePathIdentity.CanonicalComparer);

	public List<ScopedGitIgnoreMatcher> Items => _items;

	public bool Add(ScopedGitIgnoreMatcher matcher)
	{
		ArgumentNullException.ThrowIfNull(matcher);
		if (!_scopeRootPaths.Add(matcher.ScopeRootPath))
			return false;

		_items.Add(matcher);
		return true;
	}
}
