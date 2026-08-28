namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesPolyglotScopeSelectionStressTests
{
	private static readonly string[] ScopeNames =
	[
		"rust-core",
		"web-app",
		"service-dotnet",
		"python-worker",
		"go-tool"
	];

	public static IEnumerable<object[]> SelectedScopeSubsets()
	{
		var max = 1 << ScopeNames.Length;
		for (var mask = 1; mask < max; mask++)
		{
			var selected = new List<string>(ScopeNames.Length);
			for (var bit = 0; bit < ScopeNames.Length; bit++)
			{
				if ((mask & (1 << bit)) != 0)
					selected.Add(ScopeNames[bit]);
			}

			yield return [ selected.ToArray() ];
		}
	}

	public static IEnumerable<object[]> GitIgnorePathCases()
	{
		var cases = new (string Name, string RelativePath, bool IsDirectory, bool ExpectedIgnoredWhenEnabled)[]
		{
			("rust-target-dir", "workspace/rust-core/target", true, true),
			("rust-target-file", "workspace/rust-core/target/debug/app.dll", false, true),
			("rust-source", "workspace/rust-core/src/main.rs", false, false),
			("web-generated-dir", "workspace/web-app/generated", true, true),
			("web-generated-keep-stays-hidden-with-parent", "workspace/web-app/generated/keep.txt", false, true),
			("web-node-modules-dir", "workspace/web-app/node_modules", true, true),
			("web-source", "workspace/web-app/src/main.tsx", false, false),
			("web-cache-negation", "workspace/web-app/src/keep.cache", false, false),
			("dotnet-bin-dir", "workspace/service-dotnet/bin", true, true),
			("dotnet-source", "workspace/service-dotnet/src/Program.cs", false, false),
			("python-pyc-dir", "workspace/python-worker/__pycache__", true, true),
			("python-source", "workspace/python-worker/app.py", false, false),
			("go-bin-dir", "workspace/go-tool/bin", true, true),
			("go-source", "workspace/go-tool/cmd/main.go", false, false),
			("root-cache-hidden", "workspace/service-dotnet/src/hot.cache", false, true),
			("root-cache-negation", "workspace/global.cache", false, false),
			("module-dist-dir", "workspace/monolith/modules/a/dist", true, true),
			("module-dist-file", "workspace/monolith/modules/a/dist/app.js", false, true),
			("module-source", "workspace/monolith/modules/a/src/main.ts", false, false)
		};

		foreach (var openRootAsWorkspace in new[] { false, true })
		{
			foreach (var useGitIgnore in new[] { false, true })
			{
				foreach (var testCase in cases)
				{
					yield return
					[
						openRootAsWorkspace,
						useGitIgnore,
						testCase.Name,
						testCase.RelativePath,
						testCase.IsDirectory,
						useGitIgnore && testCase.ExpectedIgnoredWhenEnabled
					];
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(SelectedScopeSubsets))]
	public void GetIgnoreOptionsAvailability_PolyglotScopeSubsets_FollowsContract(string[] selectedScopes)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var service = new IgnoreRulesService(new SmartIgnoreService([
			new DotNetArtifactsIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule()
		]));

		var availability = service.GetIgnoreOptionsAvailability(Path.Combine(temp.Path, "workspace"), selectedScopes);

		var hasGit = true;
		Assert.Equal(hasGit, availability.IncludeGitIgnore);
		Assert.True(availability.IncludeSmartIgnore);
	}

	[Theory]
	[MemberData(nameof(GitIgnorePathCases))]
	public void Build_PolyglotGitIgnoreToggle_MatchesExpectedPaths(
		bool openRootAsWorkspace,
		bool useGitIgnore,
		string _,
		string relativePath,
		bool isDirectory,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var openedRootPath = openRootAsWorkspace
			? Path.Combine(temp.Path, "workspace")
			: temp.Path;

		var selectedRootFolders = openRootAsWorkspace
			? new[] { "rust-core", "web-app", "service-dotnet", "python-worker", "go-tool", "monolith" }
			: new[] { "workspace" };

		var selectedOptions = useGitIgnore
			? new[] { IgnoreOptionId.UseGitIgnore }
			: Array.Empty<IgnoreOptionId>();

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var rules = service.Build(openedRootPath, selectedOptions, selectedRootFolders);
		var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
		var fullPath = Path.Combine(temp.Path, normalizedRelativePath);
		var nodeName = Path.GetFileName(fullPath);

		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory, nodeName));
	}

	[Fact]
	public void Build_WhenGitIgnoreEnabled_DiscoversMultipleScopes()
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var rules = service.Build(
			Path.Combine(temp.Path, "workspace"),
			[IgnoreOptionId.UseGitIgnore],
			["rust-core", "web-app", "service-dotnet", "python-worker", "go-tool", "monolith"]);

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.ScopedGitIgnoreMatchers.Count >= 2);
		Assert.Equal(
			rules.ScopedGitIgnoreMatchers
				.OrderBy(static matcher => matcher.ScopeRootPath.Length)
				.ThenBy(static matcher => matcher.ScopeRootPath, PathComparer.Default),
			rules.ScopedGitIgnoreMatchers);
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("workspace/.gitignore", string.Join('\n', new[]
		{
			"**/target/",
			"**/node_modules/",
			"**/bin/",
			"**/obj/",
			"**/__pycache__/",
			"**/dist/",
			"*.cache",
			"!global.cache"
		}));

		temp.CreateFile("workspace/rust-core/.gitignore", "target/");
		temp.CreateFile("workspace/rust-core/Cargo.toml", "[package]");
		temp.CreateFile("workspace/rust-core/src/main.rs", "fn main() {}");
		temp.CreateFile("workspace/rust-core/target/debug/app.dll", "binary");

		temp.CreateFile("workspace/web-app/.gitignore", string.Join('\n', new[]
		{
			"generated/",
			"!generated/keep.txt",
			"!src/keep.cache"
		}));
		temp.CreateFile("workspace/web-app/package.json", "{}");
		temp.CreateFile("workspace/web-app/src/main.tsx", "export const app = 1;");
		temp.CreateFile("workspace/web-app/src/keep.cache", "keep");
		temp.CreateFile("workspace/web-app/generated/file.ts", "blocked");
		temp.CreateFile("workspace/web-app/generated/keep.txt", "keep");
		temp.CreateFile("workspace/web-app/node_modules/lib/index.js", "blocked");

		temp.CreateFile("workspace/service-dotnet/App.csproj", "<Project />");
		temp.CreateFile("workspace/service-dotnet/src/Program.cs", "class Program {}");
		temp.CreateFile("workspace/service-dotnet/bin/Release/app.dll", "blocked");
		temp.CreateFile("workspace/service-dotnet/src/hot.cache", "blocked");

		temp.CreateFile("workspace/python-worker/pyproject.toml", "[project]");
		temp.CreateFile("workspace/python-worker/app.py", "print('ok')");
		temp.CreateFile("workspace/python-worker/__pycache__/app.pyc", "blocked");

		temp.CreateFile("workspace/go-tool/go.mod", "module sample");
		temp.CreateFile("workspace/go-tool/cmd/main.go", "package main");
		temp.CreateFile("workspace/go-tool/bin/app.exe", "blocked");

		temp.CreateFile("workspace/monolith/modules/a/src/main.ts", "export {}");
		temp.CreateFile("workspace/monolith/modules/a/dist/app.js", "blocked");
		temp.CreateFile("workspace/monolith/modules/b/.gitignore", "!cache/keep.cache");
		temp.CreateFile("workspace/monolith/modules/b/cache/keep.cache", "keep");

		temp.CreateFile("workspace/global.cache", "keep");
	}
}
