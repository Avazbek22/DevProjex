namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesNestedScopeIsolationMatrixTests
{
	public static IEnumerable<object[]> ComplexHierarchyCases()
	{
		yield return [ "root-target-hidden", "workspace/web-app/target", true, true ];
		yield return [ "web-target-child-negation-cannot-bypass-parent", "workspace/web-app/target/web-keep", true, true ];
		yield return [ "web-target-child-file-stays-hidden", "workspace/web-app/target/web-keep/app.dll", false, true ];
		yield return [ "sibling-target-stays-hidden", "workspace/service-dotnet/target", true, true ];
		yield return [ "root-tmp-hidden", "workspace/web-app/src/random.tmp", false, true ];
		yield return [ "root-name-negation-unhides", "workspace/web-app/src/global.keep.tmp", false, false ];
		yield return [ "web-generated-hidden", "workspace/web-app/generated", true, true ];
		yield return [ "web-generated-keep-stays-hidden-with-parent", "workspace/web-app/generated/keep.txt", false, true ];
		yield return [ "sibling-generated-not-affected", "workspace/service-dotnet/generated", true, false ];
		yield return [ "web-cache-hidden", "workspace/web-app/src/hot.cache", false, true ];
		yield return [ "web-cache-unhidden-by-negation", "workspace/web-app/src/web.cache", false, false ];
		yield return [ "service-cache-hidden-by-root", "workspace/service-dotnet/src/hot.cache", false, true ];
		yield return [ "root-global-cache-unhidden", "global.cache", false, false ];
			yield return [ "nested-package-tmp-hidden", "workspace/web-app/packages/local.tmp", false, true ];
			yield return [ "nested-package-important-unhidden", "workspace/web-app/packages/important.tmp", false, false ];
			yield return [ "nested-package-reignored-hidden", "workspace/web-app/packages/reignored.tmp", false, true ];
		yield return [ "typescript-not-gitignored", "workspace/web-app/src/main.tsx", false, false ];
		yield return [ "rust-source-not-gitignored", "workspace/rust-core/src/main.rs", false, false ];
		yield return [ "dotnet-source-not-gitignored", "workspace/service-dotnet/Program.cs", false, false ];
		yield return [ "python-source-not-gitignored", "workspace/python-worker/app.py", false, false ];
	}

	[Theory]
	[MemberData(nameof(ComplexHierarchyCases))]
	public void IsGitIgnored_ComplexNestedHierarchy_ResolvesExpectedState(
		string _,
		string relativePath,
		bool isDirectory,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		SeedComplexWorkspace(temp);

		var rules = BuildGitRules(temp.Path);
		var normalizedPath = NormalizeRelativePath(relativePath);
		CreatePathIfMissing(temp, normalizedPath, isDirectory);
		var fullPath = Path.Combine(temp.Path, normalizedPath);

		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory, Path.GetFileName(fullPath)));
	}

	[Fact]
	public void Build_ComplexWorkspace_DiscoversMultipleScopedMatchers()
	{
		using var temp = new TemporaryDirectory();
		SeedComplexWorkspace(temp);

		var rules = BuildGitRules(temp.Path);

		Assert.True(rules.UseGitIgnore);
			// root + web-app + web-app/packages
			Assert.True(rules.ScopedGitIgnoreMatchers.Count >= 3);
	}

	[Fact]
	public void Build_WhenRootHasMarkerAndNestedScopesExist_KeepsNestedMatchers()
	{
		using var temp = new TemporaryDirectory();
		SeedComplexWorkspace(temp);
		temp.CreateFile("Cargo.toml", "[package]");

		var rules = BuildGitRules(temp.Path);

		Assert.True(rules.ScopedGitIgnoreMatchers.Count >= 3);

		var nestedPath = Path.Combine(temp.Path, "workspace", "web-app", "src", "web.cache");
		Assert.False(rules.IsGitIgnored(nestedPath, isDirectory: false, "web.cache"));
	}

	[Fact]
	public void IsGitIgnored_SiblingScopeIsolation_DoesNotBleedRulesAcrossProjects()
	{
		using var temp = new TemporaryDirectory();
		SeedComplexWorkspace(temp);

		var rules = BuildGitRules(temp.Path);
		var webGenerated = Path.Combine(temp.Path, "workspace", "web-app", "generated");
		var serviceGenerated = Path.Combine(temp.Path, "workspace", "service-dotnet", "generated");

		Assert.True(rules.IsGitIgnored(webGenerated, isDirectory: true, "generated"));
		Assert.False(rules.IsGitIgnored(serviceGenerated, isDirectory: true, "generated"));
	}

	private static IgnoreRules BuildGitRules(string rootPath)
	{
		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		return service.Build(
			rootPath,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: []);
	}

	private static void SeedComplexWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(".gitignore", string.Join('\n', new[]
		{
			"**/target/",
			"*.tmp",
			"!global.keep.tmp",
			"*.cache",
			"!global.cache"
		}));

		temp.CreateFile("workspace/web-app/.gitignore", string.Join('\n', new[]
		{
			"generated/",
			"!generated/keep.txt",
			"!target/web-keep/",
			"*.cache",
			"!web.cache"
		}));

			temp.CreateFile("workspace/web-app/packages/.gitignore", string.Join('\n', new[]
			{
				"*.tmp",
				"!important.tmp",
				"reignored.tmp"
			}));
			temp.CreateFile("workspace/web-app/packages/package.json", "{}");

		temp.CreateFile("workspace/rust-core/Cargo.toml", "[package]");
		temp.CreateFile("workspace/rust-core/src/main.rs", "fn main() {}");
		temp.CreateFile("workspace/service-dotnet/Program.cs", "class Program {}");
		temp.CreateFile("workspace/python-worker/app.py", "print('ok')");
		temp.CreateFile("workspace/web-app/src/main.tsx", "export const app = 1;");
	}

	private static void CreatePathIfMissing(TemporaryDirectory temp, string relativePath, bool isDirectory)
	{
		if (isDirectory)
		{
			temp.CreateFolder(relativePath);
			return;
		}

		temp.CreateFile(relativePath, "x");
	}

	private static string NormalizeRelativePath(string relativePath)
	{
		return relativePath
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);
	}
}
