using System.Reflection;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class GitPhysicalPathCacheIdentityTests
{
	[Theory]
	[InlineData(typeof(GitIgnoreMatcherFileCache), "Cache")]
	[InlineData(typeof(GitTrackedPathIndexCache), "Cache")]
	[InlineData(typeof(GitTrackedPathIndexCache), "InFlightLoads")]
	public void StaticCachesPreserveCaseDistinctPhysicalPaths(Type ownerType, string fieldName)
	{
		var field = ownerType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);
		var cache = field.GetValue(null);
		Assert.NotNull(cache);
		var comparerProperty = cache.GetType().GetProperty("Comparer", BindingFlags.Public | BindingFlags.Instance);
		Assert.NotNull(comparerProperty);
		var comparer = Assert.IsAssignableFrom<IEqualityComparer<string>>(comparerProperty.GetValue(cache));

		Assert.Same(ProjectTreePathIdentity.CanonicalComparer, comparer);
		Assert.False(comparer.Equals("Repo", "repo"));
	}

	[Theory]
	[InlineData(".gitignore", true, true)]
	[InlineData(".GITIGNORE", false, false)]
	[InlineData(".GITIGNORE", true, true)]
	[InlineData("other", true, false)]
	public void ControlAliasRequiresWindowsAndPhysicalResolution(
		string observedName,
		bool expectedPathResolves,
		bool expectedOnWindows)
	{
		var result = FileSystemEntryEnumerator.IsWindowsCompatibleControlAlias(
			observedName,
			".gitignore",
			expectedPathResolves);

		var expected = expectedOnWindows &&
		               OperatingSystem.IsWindows() &&
		               !observedName.Equals(".gitignore", StringComparison.Ordinal);
		Assert.Equal(expected, result);
	}
}
