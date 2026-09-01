using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeInventoryReuseScopeTests
{
	[Fact]
	public void Create_PreservesCaseDistinctProjectRootEntries()
	{
		var options = new TreeFilterOptions(
			AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			AllowedRootFolders: new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer)
			{
				"Foo",
				"foo"
			},
			IgnoreRules: new IgnoreRules(
				IgnoreHiddenFolders: false,
				IgnoreHiddenFiles: false,
				IgnoreDotFolders: false,
				IgnoreDotFiles: false,
				SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

		var scope = ProjectTreeInventoryReuseScope.Create(
			Path.GetFullPath("project"),
			options,
			supportsHiddenDotFolderVariants: true);

		Assert.Equal(2, scope.AllowedRootFolders.Count);
		Assert.Contains("Foo", scope.AllowedRootFolders, ProjectTreePathIdentity.CanonicalComparer);
		Assert.Contains("foo", scope.AllowedRootFolders, ProjectTreePathIdentity.CanonicalComparer);
	}
}
