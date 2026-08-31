namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesScopeResolutionMatrixTests
{
	private enum ExpectedScope
	{
		Empty = 0,
		Root = 1,
		Project = 2,
		Module = 3
	}

	private sealed record PathCase(
		string Key,
		string RelativePath,
		bool IsOutsideRoot,
		bool IsDirectory,
		ExpectedScope ExpectedScope,
		bool ExpectedSmart);

	private static readonly PathCase[] Cases =
	[
		new("root-dir", "src", false, true, ExpectedScope.Root, false),
		new("root-file", "README.md", false, false, ExpectedScope.Root, false),
		new("project-dir", Path.Combine("project"), false, true, ExpectedScope.Project, true),
		new("project-file", Path.Combine("project", "app.cs"), false, false, ExpectedScope.Project, true),
		new("module-dir", Path.Combine("project", "module"), false, true, ExpectedScope.Module, true),
		new("module-file", Path.Combine("project", "module", "lib.dll"), false, false, ExpectedScope.Module, true),
		new("project-similar-dir", "projector", false, true, ExpectedScope.Root, false),
		new("project-x-file", Path.Combine("project-x", "log.txt"), false, false, ExpectedScope.Root, false),
		new("tools-dir", "tools", false, true, ExpectedScope.Root, true),
		new("tools-file", Path.Combine("tools", "build.sh"), false, false, ExpectedScope.Root, true),
		new("outside-dir", "foreign", true, true, ExpectedScope.Empty, false),
		new("outside-file", Path.Combine("foreign", "x.txt"), true, false, ExpectedScope.Empty, false)
	];

	public static IEnumerable<object[]> ResolveGitIgnoreCases()
	{
		foreach (var pathCase in Cases)
		{
			foreach (var useGitIgnore in new[] { false, true })
			{
				foreach (var useUpperCase in new[] { false, true })
				{
					foreach (var appendDirectorySeparator in new[] { false, true })
					{
						yield return
						[
							pathCase.Key,
							pathCase.RelativePath,
							pathCase.IsOutsideRoot,
							pathCase.IsDirectory,
							(int)pathCase.ExpectedScope,
							useGitIgnore,
							useUpperCase,
							appendDirectorySeparator
						];
					}
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(ResolveGitIgnoreCases))]
	public void ResolveGitIgnoreMatcher_Matrix_SelectsExpectedScope(
		string _,
		string relativePath,
		bool isOutsideRoot,
		bool isDirectory,
		int expectedScopeRaw,
		bool useGitIgnore,
		bool useUpperCase,
		bool appendDirectorySeparator)
	{
		var rootPath = BuildAbsolute("repo");
		var outsideRootPath = BuildAbsolute("outside-repo");
		var projectPath = Path.Combine(rootPath, "project");
		var modulePath = Path.Combine(projectPath, "module");

		var rootMatcher = GitIgnoreMatcher.Build(rootPath, ["root-ignore/"]);
		var projectMatcher = GitIgnoreMatcher.Build(projectPath, ["project-ignore/"]);
		var moduleMatcher = GitIgnoreMatcher.Build(modulePath, ["module-ignore/"]);

		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = useGitIgnore,
			ScopedGitIgnoreMatchers =
			[
				new ScopedGitIgnoreMatcher(rootPath, rootMatcher),
				new ScopedGitIgnoreMatcher(projectPath, projectMatcher),
				new ScopedGitIgnoreMatcher(modulePath, moduleMatcher)
			]
		};

		var basePath = isOutsideRoot
			? Path.Combine(outsideRootPath, relativePath)
			: Path.Combine(rootPath, relativePath);
		var candidatePath = PreparePath(basePath, isDirectory, useUpperCase, appendDirectorySeparator);

		var expectedScope = (ExpectedScope)expectedScopeRaw;
		if (!useGitIgnore)
			expectedScope = ExpectedScope.Empty;
		else if (useUpperCase && !OperatingSystem.IsWindows() && expectedScope != ExpectedScope.Empty)
			expectedScope = ExpectedScope.Empty;

		var expectedMatcher = expectedScope switch
		{
			ExpectedScope.Root => rootMatcher,
			ExpectedScope.Project => projectMatcher,
			ExpectedScope.Module => moduleMatcher,
			_ => GitIgnoreMatcher.Empty
		};

		var actualMatcher = rules.ResolveGitIgnoreMatcher(candidatePath);
		Assert.Same(expectedMatcher, actualMatcher);
	}

	public static IEnumerable<object[]> SmartScopeCases()
	{
		foreach (var pathCase in Cases)
		{
			foreach (var useSmartIgnore in new[] { false, true })
			{
				foreach (var useUpperCase in new[] { false, true })
				{
					foreach (var appendDirectorySeparator in new[] { false, true })
					{
						yield return
						[
							pathCase.Key,
							pathCase.RelativePath,
							pathCase.IsOutsideRoot,
							pathCase.IsDirectory,
							pathCase.ExpectedSmart,
							useSmartIgnore,
							useUpperCase,
							appendDirectorySeparator
						];
					}
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(SmartScopeCases))]
	public void ShouldApplySmartIgnore_Matrix_UsesScopeRootsAndToggles(
		string _,
		string relativePath,
		bool isOutsideRoot,
		bool isDirectory,
		bool expectedSmart,
		bool useSmartIgnore,
		bool useUpperCase,
		bool appendDirectorySeparator)
	{
		var rootPath = BuildAbsolute("repo");
		var outsideRootPath = BuildAbsolute("outside-repo");
		var projectPath = Path.Combine(rootPath, "project");
		var toolsPath = Path.Combine(rootPath, "tools");

		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" })
		{
			UseSmartIgnore = useSmartIgnore,
			SmartIgnoreScopeRoots = [projectPath, toolsPath]
		};

		var basePath = isOutsideRoot
			? Path.Combine(outsideRootPath, relativePath)
			: Path.Combine(rootPath, relativePath);
		var candidatePath = PreparePath(basePath, isDirectory, useUpperCase, appendDirectorySeparator);

		var expected = useSmartIgnore && expectedSmart;
		if (useUpperCase && !OperatingSystem.IsWindows())
			expected = false;

		Assert.Equal(expected, rules.ShouldApplySmartIgnore(candidatePath));
	}

	[Fact]
	public void ResolveGitIgnoreMatcher_WindowsCaseDistinctScopesPreferExactIdentityAndRejectAmbiguousAlias()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows compatibility aliases are platform-specific.");

		var rootPath = BuildAbsolute("case-distinct");
		var titleCaseScope = Path.Combine(rootPath, "Repo");
		var lowerCaseScope = Path.Combine(rootPath, "repo");
		var titleCaseMatcher = GitIgnoreMatcher.Build(titleCaseScope, ["title-ignore/"]);
		var lowerCaseMatcher = GitIgnoreMatcher.Build(lowerCaseScope, ["lower-ignore/"]);
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = true,
			ScopedGitIgnoreMatchers =
			[
				new ScopedGitIgnoreMatcher(titleCaseScope, titleCaseMatcher),
				new ScopedGitIgnoreMatcher(lowerCaseScope, lowerCaseMatcher)
			]
		};

		Assert.Same(
			titleCaseMatcher,
			rules.ResolveGitIgnoreMatcher(Path.Combine(titleCaseScope, "value.cs")));
		Assert.Same(
			lowerCaseMatcher,
			rules.ResolveGitIgnoreMatcher(Path.Combine(lowerCaseScope, "value.cs")));
		Assert.Same(
			GitIgnoreMatcher.Empty,
			rules.ResolveGitIgnoreMatcher(Path.Combine(rootPath, "REPO", "value.cs")));
	}

	[Fact]
	public void ShouldApplySmartIgnore_WindowsCaseDistinctScopesRejectAmbiguousAlias()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows compatibility aliases are platform-specific.");

		var rootPath = BuildAbsolute("case-distinct-smart");
		var titleCaseScope = Path.Combine(rootPath, "Repo");
		var lowerCaseScope = Path.Combine(rootPath, "repo");
		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseSmartIgnore = true,
			SmartIgnoreScopeRoots = [titleCaseScope, lowerCaseScope]
		};

		Assert.True(rules.ShouldApplySmartIgnore(Path.Combine(titleCaseScope, "value.cs"), isDirectory: false));
		Assert.True(rules.ShouldApplySmartIgnore(Path.Combine(lowerCaseScope, "value.cs"), isDirectory: false));
		Assert.False(rules.ShouldApplySmartIgnore(
			Path.Combine(rootPath, "REPO", "value.cs"),
			isDirectory: false));
	}

	private static string BuildAbsolute(string folderName)
	{
		return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjex", "MatrixPaths", folderName));
	}

	private static string PreparePath(string fullPath, bool isDirectory, bool useUpperCase, bool appendDirectorySeparator)
	{
		var prepared = fullPath;
		if (appendDirectorySeparator && isDirectory && !prepared.EndsWith(Path.DirectorySeparatorChar))
			prepared += Path.DirectorySeparatorChar;

		if (useUpperCase)
			prepared = prepared.ToUpperInvariant();

		return prepared;
	}
}
