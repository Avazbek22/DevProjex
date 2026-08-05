namespace DevProjex.Tests.Integration;

public sealed class GitPathComparisonSemanticsIntegrationTests
{
	[Fact]
	public void GitEnvironmentSanitizerRemovesRepositoryAndConfigInjectionWithoutDroppingGlobalPolicy()
	{
		var startInfo = new ProcessStartInfo("git");
		startInfo.Environment["GIT_DIR"] = "foreign-metadata";
		startInfo.Environment["GIT_CONFIG"] = "foreign-config";
		startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
		startInfo.Environment["GIT_CONFIG_KEY_0"] = "core.ignoreCase";
		startInfo.Environment["GIT_CONFIG_VALUE_0"] = "false";
		startInfo.Environment["GIT_CONFIG_GLOBAL"] = "user-global-config";

		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);

		Assert.False(startInfo.Environment.ContainsKey("GIT_DIR"));
		Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG"));
		Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG_COUNT"));
		Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG_KEY_0"));
		Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG_VALUE_0"));
		Assert.Equal("user-global-config", startInfo.Environment["GIT_CONFIG_GLOBAL"]);
	}

	[Fact]
	public void GitIgnoreAndTrackedIndexFollowCoreIgnoreCaseAndInvalidateWithoutContentChanges()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo with кириллица");
		temp.CreateFile("repo with кириллица/.gitignore", "CASE*.TMP\n");
		var ignoredCandidate = temp.CreateFile(
			"repo with кириллица/src/case-file.tmp",
			"candidate");
		var trackedFile = temp.CreateFile(
			"repo with кириллица/src/TrackedCase.cs",
			"tracked");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "add", "--", ".gitignore", "src/TrackedCase.cs");

		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		var smartIgnore = new SmartIgnoreService([]);
		var rulesService = new IgnoreRulesService(
			smartIgnore,
			pathComparisonSemanticsResolver: resolver);
		var treeBuilder = new TreeBuilder();

		SetIgnoreCase(repositoryRoot, resolver, rulesService, value: false);
		Assert.False(resolver.Resolve(repositoryRoot).IgnoreCase);
		var caseSensitiveRules = rulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			["src"]);
		Assert.False(caseSensitiveRules.IsGitIgnored(ignoredCandidate, isDirectory: false, "case-file.tmp"));
		var caseSensitiveTree = BuildTree(treeBuilder, repositoryRoot, caseSensitiveRules);
		var caseSensitiveFiles = FlattenFiles(caseSensitiveTree.Root);
		Assert.Contains(caseSensitiveFiles, static path => Path.GetFileName(path) == "case-file.tmp");
		Assert.Equal(1, RunGitExitCode(
			repositoryRoot,
			"check-ignore",
			"--no-index",
			"--quiet",
			"--",
			"src/case-file.tmp"));
		Assert.True(GitTrackedPathIndexCache.TryLoad(
			repositoryRoot,
			Path.Combine(repositoryRoot, ".git"),
			TestContext.Current.CancellationToken,
			out var caseSensitiveIndex));
		Assert.False(caseSensitiveIndex.Contains(Path.Combine(repositoryRoot, "src", "trackedcase.cs")));

		SetIgnoreCase(repositoryRoot, resolver, rulesService, value: true);
		var caseInsensitiveRules = rulesService.Build(
			repositoryRoot,
			[IgnoreOptionId.UseGitIgnore],
			["src"]);
		var caseInsensitiveTree = BuildTree(treeBuilder, repositoryRoot, caseInsensitiveRules);
		Assert.DoesNotContain(
			FlattenFiles(caseInsensitiveTree.Root),
			static path => Path.GetFileName(path) == "case-file.tmp");
		Assert.Equal(0, RunGitExitCode(
			repositoryRoot,
			"check-ignore",
			"--no-index",
			"--quiet",
			"--",
			"src/case-file.tmp"));
		Assert.True(GitTrackedPathIndexCache.TryLoad(
			repositoryRoot,
			Path.Combine(repositoryRoot, ".git"),
			TestContext.Current.CancellationToken,
			out var caseInsensitiveIndex));
		Assert.True(caseInsensitiveIndex.Contains(Path.Combine(repositoryRoot, "src", "trackedcase.cs")));
		Assert.True(caseInsensitiveIndex.Contains(trackedFile));
	}

	[Fact]
	public void InvalidatingRepositorySemanticsKeepsUnrelatedRepositoryCacheWarm()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var firstRepository = temp.CreateDirectory("first-repository");
		var secondRepository = temp.CreateDirectory("second-repository");
		RunGit(firstRepository, "init", "--quiet");
		RunGit(secondRepository, "init", "--quiet");
		RunGit(firstRepository, "config", "core.ignoreCase", "false");
		RunGit(secondRepository, "config", "core.ignoreCase", "false");

		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		resolver.Invalidate(firstRepository);
		resolver.Invalidate(secondRepository);
		Assert.False(resolver.Resolve(firstRepository).IgnoreCase);
		Assert.False(resolver.Resolve(secondRepository).IgnoreCase);

		RunGit(secondRepository, "config", "core.ignoreCase", "true");
		resolver.Invalidate(firstRepository);
		Assert.False(resolver.Resolve(secondRepository).IgnoreCase);

		resolver.Invalidate(secondRepository);
		Assert.True(resolver.Resolve(secondRepository).IgnoreCase);
	}

	[Fact]
	public void CoreIgnoreCaseMatchesNativeGitAsciiOnlyCaseFolding()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("unicode-case-repo");
		var gitIgnorePath = temp.CreateFile(
			"unicode-case-repo/.gitignore",
			"ASCII*.TXT\nÄ*.TXT\n");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "true");
		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		resolver.Invalidate(repositoryRoot);

		var loaded = GitIgnoreMatcherFileCache.Load(repositoryRoot, gitIgnorePath, resolver);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, loaded.Status);
		Assert.Equal(
			0,
			RunGitExitCode(repositoryRoot, "check-ignore", "--no-index", "--quiet", "--", "ascii-file.txt"));
		Assert.True(loaded.Matcher!.Matcher.IsIgnored(
			Path.Combine(repositoryRoot, "ascii-file.txt"),
			isDirectory: false,
			"ascii-file.txt"));
		Assert.Equal(
			1,
			RunGitExitCode(repositoryRoot, "check-ignore", "--no-index", "--quiet", "--", "ä-file.txt"));
		Assert.False(loaded.Matcher.Matcher.IsIgnored(
			Path.Combine(repositoryRoot, "ä-file.txt"),
			isDirectory: false,
			"ä-file.txt"));
	}

	[Fact]
	public void TrackedIndexCoreIgnoreCaseDoesNotMergeDistinctUnicodeCaseVariants()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("tracked-unicode-case-repo");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "true");
		var trackedAsciiPath = temp.CreateFile(
			"tracked-unicode-case-repo/AsciiCase.txt",
			"tracked-ascii");
		if (!File.Exists(Path.Combine(repositoryRoot, "asciicase.txt")))
			Assert.Skip("This case-only rename contract requires a case-insensitive filesystem.");
		var trackedUnicodePath = temp.CreateFile(
			"tracked-unicode-case-repo/Ä.txt",
			"tracked-unicode");
		RunGit(repositoryRoot, "add", "--", "AsciiCase.txt", "Ä.txt");
		RunGit(
			repositoryRoot,
			"-c", "user.name=DevProjex Tests",
			"-c", "user.email=tests@devprojex.invalid",
			"commit", "--quiet", "-m", "tracked case fixtures");

		var renamedAsciiPath = Path.Combine(repositoryRoot, "asciicase.txt");
		var asciiIntermediatePath = Path.Combine(repositoryRoot, "ascii-case-intermediate.tmp");
		File.Move(trackedAsciiPath, asciiIntermediatePath);
		File.Move(asciiIntermediatePath, renamedAsciiPath);
		Assert.Empty(RunGit(repositoryRoot, "status", "--porcelain=v1", "-z"));

		var renamedUnicodePath = Path.Combine(repositoryRoot, "ä.txt");
		var unicodeIntermediatePath = Path.Combine(repositoryRoot, "unicode-case-intermediate.tmp");
		File.Move(trackedUnicodePath, unicodeIntermediatePath);
		File.Move(unicodeIntermediatePath, renamedUnicodePath);
		Assert.NotEmpty(RunGit(repositoryRoot, "status", "--porcelain=v1", "-z"));

		Assert.True(GitTrackedPathIndexCache.TryLoad(
			repositoryRoot,
			Path.Combine(repositoryRoot, ".git"),
			TestContext.Current.CancellationToken,
			out var index));

		Assert.True(index.Contains(renamedAsciiPath));
		Assert.False(index.Contains(renamedUnicodePath));
	}

	[Fact]
	public void InvalidRepositoryComparisonConfigRejectsGitIgnoreAndTrackedIndex()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("non-authoritative-semantics-repo");
		var gitIgnorePath = temp.CreateFile(
			"non-authoritative-semantics-repo/.gitignore",
			"CASE*.TMP\n");
		var trackedPath = temp.CreateFile(
			"non-authoritative-semantics-repo/tracked.txt",
			"tracked");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "add", "--", ".gitignore", "tracked.txt");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "not-a-boolean");
		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		resolver.Invalidate(repositoryRoot);

		var semantics = resolver.Resolve(repositoryRoot);
		var loadedMatcher = GitIgnoreMatcherFileCache.Load(
			repositoryRoot,
			gitIgnorePath,
			resolver);
		var indexLoaded = GitTrackedPathIndexCache.TryLoad(
			repositoryRoot,
			Path.Combine(repositoryRoot, ".git"),
			TestContext.Current.CancellationToken,
			out _);

		Assert.False(
			semantics.IsAuthoritative,
			$"Fallback semantics unexpectedly became authoritative: {semantics}.");
		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, loadedMatcher.Status);
		Assert.Null(loadedMatcher.Matcher);
		Assert.False(
			indexLoaded,
			"Tracked-only selection must reject an index whose comparison semantics are uncertain.");

		var analysisService = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		var analysis = analysisService.Load(
			new ProjectAnalysisRequest(
				repositoryRoot,
				SelectedRootFolders: [],
				SelectedExtensions: [".txt", ".tmp"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]),
			TestContext.Current.CancellationToken);

		Assert.True(analysis.HadAccessDenied);
		Assert.NotNull(analysis.Tree.OrderedFilePaths);
		Assert.Empty(analysis.Tree.OrderedFilePaths);
		Assert.True(File.Exists(trackedPath));
	}

	[Fact]
	public void CorePrecomposeUnicodeIsAppliedOnlyOnMacOS()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("unicode-repo");
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "false");
		RunGit(repositoryRoot, "config", "core.precomposeUnicode", "true");
		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		resolver.Invalidate(repositoryRoot);

		var semantics = resolver.Resolve(repositoryRoot);

		Assert.False(semantics.IgnoreCase);
		Assert.Equal(OperatingSystem.IsMacOS(), semantics.NormalizeUnicode);
	}

	[Fact]
	public void CorePrecomposeUnicodeNormalizesObservedMacPathsWithoutRewritingPatternBytes()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("Git precomposition affects working-tree path conversion on macOS only.");

		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("precompose-pattern-repo");
		const string composed = "caf\u00e9.tmp";
		const string decomposed = "cafe\u0301.tmp";
		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "false");
		RunGit(repositoryRoot, "config", "core.precomposeUnicode", "true");
		var gitIgnorePath = temp.CreateFile(
			"precompose-pattern-repo/.gitignore",
			decomposed + "\n");
		var resolver = GitConfigPathComparisonSemanticsResolver.Instance;
		resolver.Invalidate(repositoryRoot);

		var loaded = GitIgnoreMatcherFileCache.Load(repositoryRoot, gitIgnorePath, resolver);
		var nativeExitCode = RunGitExitCode(
			repositoryRoot,
			"check-ignore",
			"--no-index",
			"--quiet",
			"--",
			composed);

		Assert.Equal(1, nativeExitCode);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, loaded.Status);
		Assert.False(loaded.Matcher!.Matcher.IsIgnored(
			Path.Combine(repositoryRoot, composed),
			isDirectory: false,
			composed));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, true)]
	public void MatcherFileCacheKeysEntriesByExplicitComparisonSemantics(
		bool ignoreCase,
		bool expectedIgnored)
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "EXACT.TXT\n");
		var resolver = new FixedSemanticsResolver(new GitPathComparisonSemantics(
			ignoreCase,
			NormalizeUnicode: false));

		var loaded = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath, resolver);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, loaded.Status);
		Assert.Equal(
			expectedIgnored,
			loaded.Matcher!.Matcher.IsIgnored(
				Path.Combine(temp.Path, "exact.txt"),
				isDirectory: false,
				"exact.txt"));
	}

	private static TreeBuildResult BuildTree(
		TreeBuilder treeBuilder,
		string repositoryRoot,
		IgnoreRules rules) =>
		treeBuilder.Build(
			repositoryRoot,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				{
					".tmp",
					".cs"
				},
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "src" },
				IgnoreRules: rules),
			TestContext.Current.CancellationToken);

	private static IReadOnlyList<string> FlattenFiles(FileSystemNode root)
	{
		var files = new List<string>();
		var pending = new Stack<FileSystemNode>(root.Children);
		while (pending.TryPop(out var node))
		{
			if (!node.IsDirectory)
				files.Add(node.FullPath);
			foreach (var child in node.Children)
				pending.Push(child);
		}

		return files;
	}

	private static void SetIgnoreCase(
		string repositoryRoot,
		GitConfigPathComparisonSemanticsResolver resolver,
		IgnoreRulesService rulesService,
		bool value)
	{
		RunGit(repositoryRoot, "config", "core.ignoreCase", value ? "true" : "false");
		resolver.Invalidate(repositoryRoot);
		rulesService.InvalidateCaches(repositoryRoot);
	}

	private static void EnsureGitAvailable()
	{
		try
		{
			if (RunGitExitCode(Environment.CurrentDirectory, "--version") != 0)
				Assert.Skip("Git is not available in this test environment.");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is not available in this test environment.");
		}
	}

	private static string RunGit(string workingDirectory, params string[] arguments)
	{
		var result = RunGitCore(workingDirectory, arguments);
		Assert.True(
			result.ExitCode == 0,
			$"git failed ({result.ExitCode}): {result.Error}{result.Output}");
		return result.Output;
	}

	private static int RunGitExitCode(string workingDirectory, params string[] arguments) =>
		RunGitCore(workingDirectory, arguments).ExitCode;

	private static GitProcessResult RunGitCore(string workingDirectory, IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start git.");
		var outputTask = process.StandardOutput.ReadToEndAsync();
		var errorTask = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git command did not complete within 20 seconds.");
		}

		return new GitProcessResult(
			process.ExitCode,
			outputTask.GetAwaiter().GetResult(),
			errorTask.GetAwaiter().GetResult());
	}

	private sealed class FixedSemanticsResolver(GitPathComparisonSemantics semantics)
		: IGitPathComparisonSemanticsResolver
	{
		public GitPathComparisonSemantics Resolve(string scopeRootPath) => semantics;

		public void Invalidate(string rootPath)
		{
		}
	}

	private sealed record GitProcessResult(int ExitCode, string Output, string Error);
}
