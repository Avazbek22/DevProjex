namespace DevProjex.Tests.Unit;

public sealed class ProjectScopeDiscoverySmartArtifactMatrixTests
{
	[Theory]
	[MemberData(nameof(ConfirmedPortableStores))]
	public void Discover_ConfirmedPortableStoreNeverConsumesNestedProjectScopeBudget(
		PortableStoreKind storeKind,
		string artifactRootRelativePath)
	{
		using var temp = new TemporaryDirectory();
		SeedConfirmedStore(temp, storeKind, artifactRootRelativePath);
		temp.CreateFile(
			Path.Combine(artifactRootRelativePath, "vendor", "fake", "package.json"),
			"{}");
		temp.CreateFile("apps/real/package.json", "{}");
		var discovery = new ProjectScopeDiscoveryService(new SmartIgnoreService([]));

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, "apps/real"));
		Assert.DoesNotContain(context.Scopes, scope =>
			IsPathInside(scope.RootPath, Path.Combine(temp.Path, artifactRootRelativePath)));
	}

	[Theory]
	[InlineData("packages")]
	[InlineData("repository")]
	[InlineData("registry")]
	[InlineData("_cacache")]
	[InlineData("modules-2")]
	public void Discover_SourceLookalikeWithoutSignatureRemainsTraversable(string candidateName)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine(candidateName, "service", "package.json"), "{}");
		temp.CreateFile(Path.Combine(candidateName, "service", "src", "index.ts"), "export {};");
		var discovery = new ProjectScopeDiscoveryService(new SmartIgnoreService([]));

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, $"{candidateName}/service"));
	}

	[Fact]
	public void Discover_MixedSourceAndArtifactCandidatesPreservesOnlyRealProjectScopes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("packages/source-app/package.json", "{}");
		temp.CreateFile("packages/source-app/src/index.ts", "export {};");
		temp.CreateFile("dependencies/packages/repositories.config", "<repositories />");
		temp.CreateFile("dependencies/packages/Alpha/Alpha.nupkg", "package");
		temp.CreateFolder("dependencies/packages/Alpha/lib");
		temp.CreateFile("dependencies/packages/Beta/Beta.nupkg", "package");
		temp.CreateFolder("dependencies/packages/Beta/ref");
		temp.CreateFile("dependencies/packages/vendor/fake/package.json", "{}");
		var discovery = new ProjectScopeDiscoveryService(new SmartIgnoreService([]));

		var context = discovery.Discover(temp.Path, selectedRootFolders: null);

		Assert.Contains(context.Scopes, scope => ScopeEndsWith(scope, "packages/source-app"));
		Assert.DoesNotContain(context.Scopes, scope => ScopeContains(scope, "dependencies/packages/vendor"));
	}

	public static TheoryData<PortableStoreKind, string> ConfirmedPortableStores() => new()
	{
		{ PortableStoreKind.LegacyNuGet, "packages" },
		{ PortableStoreKind.OfficialNuGet, ".nuget/packages" },
		{ PortableStoreKind.Maven, ".m2/repository" },
		{ PortableStoreKind.Cargo, ".cargo/registry" },
		{ PortableStoreKind.NpmCache, ".npm/_cacache" },
		{ PortableStoreKind.GradleModules, ".gradle/caches/modules-2" }
	};

	private static void SeedConfirmedStore(
		TemporaryDirectory temp,
		PortableStoreKind storeKind,
		string artifactRootRelativePath)
	{
		switch (storeKind)
		{
			case PortableStoreKind.LegacyNuGet:
				temp.CreateFile(Path.Combine(artifactRootRelativePath, "repositories.config"), "<repositories />");
				break;
			case PortableStoreKind.OfficialNuGet:
			case PortableStoreKind.Maven:
			case PortableStoreKind.Cargo:
				_ = temp.CreateFolder(artifactRootRelativePath);
				break;
			case PortableStoreKind.NpmCache:
				temp.CreateFolder(Path.Combine(artifactRootRelativePath, "content-v2"));
				break;
			case PortableStoreKind.GradleModules:
				temp.CreateFolder(Path.Combine(artifactRootRelativePath, "files-2.1"));
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(storeKind), storeKind, null);
		}
	}

	private static bool ScopeEndsWith(ProjectScope scope, string relativePath) =>
		scope.RootPath.EndsWith(Normalize(relativePath), StringComparison.OrdinalIgnoreCase);

	private static bool ScopeContains(ProjectScope scope, string relativePath) =>
		scope.RootPath.Contains(Normalize(relativePath), StringComparison.OrdinalIgnoreCase);

	private static bool IsPathInside(string path, string parentPath)
	{
		var relative = Path.GetRelativePath(parentPath, path);
		return relative == "." ||
		       (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
	}

	private static string Normalize(string relativePath) =>
		relativePath.Replace('/', Path.DirectorySeparatorChar);

	public enum PortableStoreKind
	{
		LegacyNuGet,
		OfficialNuGet,
		Maven,
		Cargo,
		NpmCache,
		GradleModules
	}
}
