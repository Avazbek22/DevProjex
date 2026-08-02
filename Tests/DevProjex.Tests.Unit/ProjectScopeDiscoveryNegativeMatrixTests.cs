namespace DevProjex.Tests.Unit;

public sealed class ProjectScopeDiscoveryNegativeMatrixTests
{
	[Theory]
	[InlineData("nuget-backup/packages/service")]
	[InlineData("m2-backup/repository/service")]
	[InlineData("cargo-backup/registry/service")]
	public void Discover_NearMissPortableStorePathRemainsTraversable(string projectRelativePath)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine(projectRelativePath, "package.json"), "{}");
		temp.CreateFile(Path.Combine(projectRelativePath, "src", "index.ts"), "export {};");
		var discovery = CreateDiscovery();

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, projectRelativePath));
	}

	[Fact]
	public void Discover_IncompleteLegacyStoreDoesNotPruneRealSourceProject()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("packages/Alpha.1.0.0/Alpha.1.0.0.nupkg", "package");
		temp.CreateFolder("packages/Alpha.1.0.0/lib");
		temp.CreateFile("packages/source-service/package.json", "{}");
		temp.CreateFile("packages/source-service/src/index.ts", "export {};");
		var discovery = CreateDiscovery();

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, "packages/source-service"));
		Assert.DoesNotContain(context.Scopes, scope => ScopeEndsWith(scope, "packages/Alpha.1.0.0"));
	}

	[Fact]
	public void Discover_SiblingConfirmedStoreDoesNotPruneSourceLookalikeBranch()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("artifacts/.m2/repository/acme/module.pom", "<project />");
		temp.CreateFile("source/repository/service/package.json", "{}");
		temp.CreateFile("source/repository/service/src/index.ts", "export {};");
		var discovery = CreateDiscovery();

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, "source/repository/service"));
		Assert.DoesNotContain(context.Scopes, scope => ScopeContains(scope, "artifacts/.m2/repository"));
	}

	[Fact]
	public void Discover_SelectedMissingAndNearMissStoreRootsDoNotFallbackToUnselectedArtifactScope()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("m2-backup/repository/service/package.json", "{}");
		temp.CreateFile(".m2/repository/acme/module.pom", "<project />");
		var discovery = CreateDiscovery();

		var missing = discovery.Discover(temp.Path, ["missing"]);
		var sourceOnly = discovery.Discover(temp.Path, ["m2-backup"]);

		Assert.Empty(missing.Scopes);
		Assert.Contains(sourceOnly.Scopes, scope => ScopeEndsWith(scope, "m2-backup/repository/service"));
		Assert.DoesNotContain(sourceOnly.Scopes, scope => ScopeContains(scope, ".m2/repository"));
	}

	private static ProjectScopeDiscoveryService CreateDiscovery() =>
		new(new SmartIgnoreService([]));

	private static bool ScopeEndsWith(ProjectScope scope, string relativePath) =>
		scope.RootPath.EndsWith(Normalize(relativePath), StringComparison.OrdinalIgnoreCase);

	private static bool ScopeContains(ProjectScope scope, string relativePath) =>
		scope.RootPath.Contains(Normalize(relativePath), StringComparison.OrdinalIgnoreCase);

	private static string Normalize(string relativePath) =>
		relativePath
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);
}
