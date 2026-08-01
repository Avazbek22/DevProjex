namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesHierarchicalGitIgnoreMatrixTests
{
	public static IEnumerable<object[]> ParentScopeCases()
	{
		yield return ["rust-target-dir", new[] { "**/target/" }, "src-tauri/target", true, true];
		yield return [ "rust-target-file", new[] { "**/target/" }, "src-tauri/target/debug/app.exe", false, true ];
		yield return [ "frontend-node-modules-dir", new[] { "**/node_modules/" }, "web-app/node_modules", true, true ];
		yield return [ "frontend-node-modules-file", new[] { "**/node_modules/" }, "web-app/node_modules/pkg/index.js", false, true ];
		yield return [ "frontend-dist-dir", new[] { "**/dist/" }, "web-app/dist", true, true ];
		yield return [ "frontend-dist-file", new[] { "**/dist/" }, "web-app/dist/app.js", false, true ];
		yield return [ "dotnet-bin-dir", new[] { "**/bin/" }, "service/bin", true, true ];
		yield return [ "dotnet-obj-dir", new[] { "**/obj/" }, "service/obj", true, true ];
		yield return [ "python-venv-dir", new[] { "**/.venv/" }, "python/.venv", true, true ];
		yield return [ "python-cache-dir", new[] { "**/__pycache__/" }, "python/__pycache__", true, true ];
		yield return [ "logs-by-name", new[] { "*.log" }, "logs/build.log", false, true ];
		yield return [ "tmp-by-extension", new[] { "*.tmp" }, "cache/index.tmp", false, true ];
		yield return [ "cache-by-extension", new[] { "*.cache" }, "cache/hot.cache", false, true ];
		yield return [ "transport-stream-ts-not-gitignored", new[] { "**/target/" }, "web-app/src/main.ts", false, false ];
		yield return [ "tsx-not-gitignored", new[] { "**/target/" }, "web-app/src/App.tsx", false, false ];
		yield return [ "rust-source-not-gitignored", new[] { "**/target/" }, "sendme/src/main.rs", false, false ];
		yield return [ "cs-source-not-gitignored", new[] { "**/bin/" }, "service/Program.cs", false, false ];
		yield return [ "py-source-not-gitignored", new[] { "**/__pycache__/" }, "python/app.py", false, false ];
		yield return [ "json-config-not-gitignored", new[] { "**/node_modules/" }, "web-app/package.json", false, false ];
		yield return [ "readme-not-gitignored", new[] { "**/dist/" }, "README.md", false, false ];
	}

	public static IEnumerable<object[]> ParentChildOverrideCases()
	{
		yield return
		[
			"child-unignore-log",
			new[] { "*.log" },
			"web-app",
			new[] { "!keep.log" },
			"web-app/keep.log",
			false,
			false
		];
		yield return
		[
			"child-unignore-cache",
			new[] { "*.cache" },
			"web-app",
			new[] { "!hot.cache" },
			"web-app/hot.cache",
			false,
			false
		];
		yield return
		[
			"child-ignore-ts-while-parent-allows",
			new[] { "!*.ts" },
			"web-app/src",
			new[] { "*.ts" },
			"web-app/src/main.ts",
			false,
			true
		];
		yield return
		[
			"child-ignore-tsx-while-parent-allows",
			new[] { "!*.tsx" },
			"web-app/src",
			new[] { "*.tsx" },
			"web-app/src/App.tsx",
			false,
			true
		];
		yield return
		[
			"child-no-match-parent-rule-stays",
			new[] { "*.log" },
			"web-app",
			new[] { "!other.log" },
			"web-app/keep.log",
			false,
			true
		];
		yield return
		[
			"child-directory-rule-overrides-parent-allow",
			new[] { "!web-app/cache/" },
			"web-app",
			new[] { "cache/" },
			"web-app/cache",
			true,
			true
		];
		yield return
		[
			"child-directory-unignore-overrides-parent-ignore",
			new[] { "web-app/cache/" },
			"web-app",
			new[] { "!cache/" },
			"web-app/cache",
			true,
			false
		];
		yield return
		[
			"nested-child-unignore-deep-file",
			new[] { "*.tmp" },
			"web-app/src",
			new[] { "!index.tmp" },
			"web-app/src/index.tmp",
			false,
			false
		];
		yield return
		[
			"nested-child-reignore-after-unignore",
			new[] { "*.tmp" },
			"web-app/src",
			new[] { "!index.tmp", "index.tmp" },
			"web-app/src/index.tmp",
			false,
			true
		];
		yield return
		[
			"child-targeted-allow-does-not-affect-neighbor",
			new[] { "*.log" },
			"web-app",
			new[] { "!keep.log" },
			"web-app/other.log",
			false,
			true
		];
	}

	[Theory]
	[MemberData(nameof(ParentScopeCases))]
	public void IsGitIgnored_ParentScopeRules_Matrix(
		string _,
		string[] rootGitIgnoreLines,
		string relativePath,
		bool isDirectory,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", string.Join('\n', rootGitIgnoreLines));
		var normalizedRelativePath = NormalizeRelativePath(relativePath);
		CreatePath(temp, normalizedRelativePath, isDirectory);

		var rules = BuildGitIgnoreRules(temp.Path);
		var fullPath = Path.Combine(temp.Path, normalizedRelativePath);
		var nodeName = Path.GetFileName(fullPath);

		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory, nodeName));
	}

	[Theory]
	[MemberData(nameof(ParentChildOverrideCases))]
	public void IsGitIgnored_ParentChildScopes_Matrix(
		string _,
		string[] rootGitIgnoreLines,
		string childScopeRelativePath,
		string[] childGitIgnoreLines,
		string targetRelativePath,
		bool isDirectory,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", string.Join('\n', rootGitIgnoreLines));
		var normalizedChildScopePath = NormalizeRelativePath(childScopeRelativePath);
		var normalizedTargetPath = NormalizeRelativePath(targetRelativePath);
		temp.CreateFile(Path.Combine(normalizedChildScopePath, ".gitignore"), string.Join('\n', childGitIgnoreLines));
		CreatePath(temp, normalizedTargetPath, isDirectory);

		var rules = BuildGitIgnoreRules(temp.Path);
		var fullPath = Path.Combine(temp.Path, normalizedTargetPath);
		var nodeName = Path.GetFileName(fullPath);

		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory, nodeName));
	}

	[Fact]
	public void Build_WhenRootAndNestedGitIgnoreExist_UsesScopedMatchers()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile("web-app/.gitignore", "!keep.log");
		temp.CreateFile("web-app/keep.log", "ok");

		var rules = BuildGitIgnoreRules(temp.Path);

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.ScopedGitIgnoreMatchers.Count >= 2);
	}

	[Fact]
	public void Build_DeepMonorepoGitIgnoreScope_AppliesOnlyToOwningProject()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/domain/team/api/.gitignore", "generated/\n*.cache\n");
		temp.CreateFile("apps/domain/team/api/generated/drop.txt", "ignored");
		temp.CreateFile("apps/domain/team/api/src/hot.cache", "ignored");
		temp.CreateFile("apps/domain/team/sibling/generated/drop.txt", "visible");
		temp.CreateFile("apps/domain/team/sibling/src/hot.cache", "visible");

		var rules = BuildGitIgnoreRules(temp.Path);

		Assert.True(rules.UseGitIgnore);
		Assert.Contains(rules.ScopedGitIgnoreMatchers, matcher =>
			matcher.ScopeRootPath.EndsWith(
				NormalizeRelativePath("apps/domain/team/api"),
				StringComparison.OrdinalIgnoreCase));

		Assert.True(rules.IsGitIgnored(
			Path.Combine(temp.Path, NormalizeRelativePath("apps/domain/team/api/generated")),
			isDirectory: true,
			"generated"));
		Assert.True(rules.IsGitIgnored(
			Path.Combine(temp.Path, NormalizeRelativePath("apps/domain/team/api/src/hot.cache")),
			isDirectory: false,
			"hot.cache"));
		Assert.False(rules.IsGitIgnored(
			Path.Combine(temp.Path, NormalizeRelativePath("apps/domain/team/sibling/generated")),
			isDirectory: true,
			"generated"));
		Assert.False(rules.IsGitIgnored(
			Path.Combine(temp.Path, NormalizeRelativePath("apps/domain/team/sibling/src/hot.cache")),
			isDirectory: false,
			"hot.cache"));
	}

	[Fact]
	public void Build_DeepMonorepoPrunedDependencyGitIgnore_IsNotUsedAsProjectController()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("pnpm-workspace.yaml", "packages:\n  - apps/**\n");
		temp.CreateFile("apps/product/node_modules/pkg/.gitignore", "*.cache\n");
		temp.CreateFile("apps/product/node_modules/pkg/src/hot.cache", "dependency");
		temp.CreateFile("apps/product/service/.gitignore", "generated/\n");
		temp.CreateFile("apps/product/service/generated/drop.txt", "ignored");

		var rules = BuildGitIgnoreRules(temp.Path);

		Assert.True(rules.UseGitIgnore);
		Assert.Contains(rules.ScopedGitIgnoreMatchers, matcher =>
			matcher.ScopeRootPath.EndsWith(
				NormalizeRelativePath("apps/product/service"),
				StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(rules.ScopedGitIgnoreMatchers, matcher =>
			matcher.ScopeRootPath.Contains(
				NormalizeRelativePath("node_modules/pkg"),
				StringComparison.OrdinalIgnoreCase));
		Assert.False(rules.IsGitIgnored(
			Path.Combine(temp.Path, NormalizeRelativePath("apps/product/node_modules/pkg/src/hot.cache")),
			isDirectory: false,
			"hot.cache"));
	}

	[Fact]
	public void IsGitIgnored_WhenGitIgnoreDisabled_ReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.log");
		temp.CreateFile("logs/app.log", "x");

		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		var rules = service.Build(temp.Path, [], selectedRootFolders: []);
		var path = Path.Combine(temp.Path, "logs", "app.log");

		Assert.False(rules.UseGitIgnore);
		Assert.False(rules.IsGitIgnored(path, isDirectory: false, "app.log"));
	}

	[Theory]
	[InlineData(".git", true)]
	[InlineData(".github", false)]
	[InlineData(".git-owned", false)]
	[InlineData(".gitignore", false)]
	public void IsGitIgnored_GitAdministrativeBoundary_RejectsOnlyExactMetadataName(
		string name,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/\n");
		var rules = BuildGitIgnoreRules(temp.Path);
		var fullPath = Path.Combine(temp.Path, name);

		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory: true, name));
		Assert.Equal(expectedIgnored, rules.IsGitIgnored(fullPath, isDirectory: false, name));
	}

	[Fact]
	public void GitIgnoreScanContexts_NestedGitMetadata_IsHardPrunedOnlyByActiveOrCandidateController()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "logs/\n");
		var activeRules = BuildGitIgnoreRules(temp.Path);
		var disabledRules = new IgnoreRulesService(new SmartIgnoreService([]))
			.Build(temp.Path, [], selectedRootFolders: []);
		var fullPath = Path.Combine(temp.Path, "module", ".git");
		var relativePath = Path.Combine("module", ".git").Replace('\\', '/');

		Assert.True(activeRules.CreateGitIgnoreScanContext(temp.Path)
			.Evaluate(fullPath, relativePath, isDirectory: true, ".git").IsIgnored);
		Assert.False(disabledRules.CreateGitIgnoreScanContext(temp.Path)
			.Evaluate(fullPath, relativePath, isDirectory: true, ".git").IsIgnored);
		Assert.True(disabledRules.CreateGitIgnoreCandidateScanContext(temp.Path)
			.Evaluate(fullPath, relativePath, isDirectory: true, ".git").IsIgnored);
	}

	[Fact]
	public void GitIgnoreScanContext_ActiveEmptyModePrunesAdministrativeDirectoriesAcrossTheScan()
	{
		using var temp = new TemporaryDirectory();
		var rules = new IgnoreRulesService(new SmartIgnoreService([]))
			.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], selectedRootFolders: []);
		var scopeRoot = Path.Combine(temp.Path, "deep", "repository");
		var scopedMatcher = new ScopedGitIgnoreMatcher(
			scopeRoot,
			GitIgnoreMatcher.Build(scopeRoot, ["*.log"]));
		var context = rules.CreateGitIgnoreScanContext(temp.Path)
			.WithScope(scopedMatcher, "deep/repository");
		var scopedGitPath = Path.Combine(scopeRoot, ".git");
		var unrelatedGitPath = Path.Combine(temp.Path, "other", ".git");

		Assert.True(rules.UseGitIgnore);
		Assert.True(rules.IsGitIgnoreTraversalEnabled);
		Assert.True(context
			.Evaluate(scopedGitPath, "deep/repository/.git", isDirectory: true, ".git")
			.IsIgnored);
		Assert.True(context
			.Evaluate(unrelatedGitPath, "other/.git", isDirectory: true, ".git")
			.IsIgnored);
	}

	private static IgnoreRules BuildGitIgnoreRules(string rootPath)
	{
		var service = new IgnoreRulesService(new SmartIgnoreService([]));
		return service.Build(
			rootPath,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: []);
	}

	private static void CreatePath(TemporaryDirectory temp, string relativePath, bool isDirectory)
	{
		if (isDirectory)
		{
			temp.CreateFolder(relativePath);
			return;
		}

		temp.CreateFile(relativePath, "content");
	}

	private static string NormalizeRelativePath(string relativePath)
	{
		return relativePath
			.Replace('/', Path.DirectorySeparatorChar)
			.Replace('\\', Path.DirectorySeparatorChar);
	}
}
