using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Integration;

public sealed class GitIgnoreSourceIoIntegrationTests
{
	public static TheoryData<string, string, string[]> LineEndingCases => new()
	{
		{ "lf", "alpha\nbeta\n", ["alpha", "beta"] },
		{ "crlf", "alpha\r\nbeta\r\n", ["alpha", "beta"] },
		{ "cr-only", "alpha\rbeta\r", [] },
		{ "mixed", "alpha\r\nbeta\rgamma\nlast", ["alpha", "last"] },
		{ "final-no-lf", "alpha\nlast", ["alpha", "last"] }
	};

	[Theory]
	[InlineData("utf8", true, true)]
	[InlineData("utf8-bom", true, true)]
	[InlineData("utf16-le", false, false)]
	[InlineData("utf16-be", false, false)]
	[InlineData("utf32-le", false, false)]
	[InlineData("utf32-be", false, false)]
	public async Task OnlyUtf8GitIgnoreSourcesAreAcceptedAcrossLoadersAndPublicConsumers(
		string encodingName,
		bool expectedNativeIgnored,
		bool expectedSourceAccepted)
	{
		using var temp = new TemporaryDirectory();
		EnsureGitAvailable(temp.Path);
		Assert.Equal(0, RunGit(temp.Path, "init", "--quiet"));
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		File.WriteAllBytes(gitIgnorePath, EncodeGitIgnore(encodingName, "*.secret\n"));
		var ignoredPath = temp.CreateFile("ignored.secret", "payload");
		temp.CreateFile("visible.cs", "class Visible {}");

		var nativeIgnored = RunGit(temp.Path, "check-ignore", "--no-index", "-q", "--", "ignored.secret") == 0;
		Assert.Equal(expectedNativeIgnored, nativeIgnored);

		var dynamicLoad = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		Assert.Equal(
			expectedSourceAccepted
				? GitIgnoreMatcherLoadStatus.Loaded
				: GitIgnoreMatcherLoadStatus.ReadFailure,
			dynamicLoad.Status);
		if (expectedSourceAccepted)
		{
			Assert.True(
				dynamicLoad.Matcher!.Matcher.Evaluate(
					ignoredPath,
					isDirectory: false,
					"ignored.secret").IsIgnored);
		}
		else
		{
			Assert.Null(dynamicLoad.Matcher);
		}

		var analysisService = CreateProjectAnalysisService();
		var loaded = analysisService.Load(
			new ProjectAnalysisRequest(
				temp.Path,
				SelectedRootFolders: [],
				SelectedExtensions: [".secret", ".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]),
			TestContext.Current.CancellationToken);
		var loadedPaths = FlattenPaths(loaded.Tree.Root);
		Assert.DoesNotContain("ignored.secret", loadedPaths, StringComparer.Ordinal);
		Assert.DoesNotContain(".secret", loaded.AvailableExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.Equal(expectedSourceAccepted, loadedPaths.Contains("visible.cs", StringComparer.Ordinal));
		Assert.Equal(!expectedSourceAccepted, loaded.Tree.Root.IsAccessDenied);

		var plan = await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				temp.Path,
				new ProjectSelectionSpec(
					Roots: [],
					Extensions: [".secret", ".cs"],
					GitMode: GitFilteringMode.RespectGitIgnore,
					Exclusions: [])),
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(
			plan.IncludedFiles,
			static path => Path.GetFileName(path) == "ignored.secret");
		Assert.Equal(
			expectedSourceAccepted,
			plan.IncludedFiles.Any(static path => Path.GetFileName(path) == "visible.cs"));
		Assert.Equal(
			!expectedSourceAccepted,
			plan.Diagnostics.Any(static diagnostic =>
				diagnostic.Code == "DPX-PROJECT-PARTIAL-ACCESS" &&
				diagnostic.Severity == ContextDiagnosticSeverity.Warning));
	}

	[Theory]
	[MemberData(nameof(LineEndingCases))]
	public void LineEndingSemanticsMatchNativeGit(
		string _,
		string sourceContent,
		string[] expectedIgnoredNames)
	{
		using var temp = new TemporaryDirectory();
		EnsureGitAvailable(temp.Path);
		Assert.Equal(0, RunGit(temp.Path, "init", "--quiet"));
		var gitIgnorePath = temp.CreateFile(".gitignore", sourceContent);
		var candidateNames = new[] { "alpha", "beta", "gamma", "last" };
		foreach (var candidateName in candidateNames)
			temp.CreateFile(candidateName, candidateName);

		var loaded = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, loaded.Status);
		foreach (var candidateName in candidateNames)
		{
			var expectedIgnored = expectedIgnoredNames.Contains(candidateName, StringComparer.Ordinal);
			var nativeIgnored = RunGit(
				temp.Path,
				"check-ignore",
				"--no-index",
				"-q",
				"--",
				candidateName) == 0;
			var productIgnored = loaded.Matcher!.Matcher.Evaluate(
				Path.Combine(temp.Path, candidateName),
				isDirectory: false,
				candidateName).IsIgnored;

			Assert.Equal(expectedIgnored, nativeIgnored);
			Assert.Equal(nativeIgnored, productIgnored);
		}
	}

	[Fact]
	public void InvalidUtf8CannotCollideWithAValidReplacementCharacterPattern()
	{
		using var temp = new TemporaryDirectory();
		EnsureGitAvailable(temp.Path);
		Assert.Equal(0, RunGit(temp.Path, "init", "--quiet"));
		var candidateName = "bad\uFFFD.tmp";
		var candidatePath = temp.CreateFile(candidateName, "payload");
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		File.WriteAllBytes(
			gitIgnorePath,
			[0x62, 0x61, 0x64, 0xFF, 0x2E, 0x74, 0x6D, 0x70, 0x0A]);

		Assert.NotEqual(
			0,
			RunGit(temp.Path, "check-ignore", "--no-index", "-q", "--", candidateName));
		var invalidLoad = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, invalidLoad.Status);
		Assert.Null(invalidLoad.Matcher);
		var invalidRules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore]);
		var invalidTree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>([".tmp"], StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(PathComparer.Default),
				invalidRules),
			TestContext.Current.CancellationToken);
		Assert.True(invalidTree.HadAccessDenied);
		Assert.True(invalidTree.Root.IsAccessDenied);
		Assert.Empty(invalidTree.Root.Children);

		File.WriteAllText(
			gitIgnorePath,
			candidateName + "\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
		Assert.Equal(
			0,
			RunGit(temp.Path, "check-ignore", "--no-index", "-q", "--", candidateName));
		var validLoad = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, validLoad.Status);
		Assert.True(
			validLoad.Matcher!.Matcher.Evaluate(
				candidatePath,
				isDirectory: false,
				candidateName).IsIgnored);
	}

	[Fact]
	public async Task UnreadableWorkingTreeGitIgnoreFailsClosedAndProducesExistingPartialAccessDiagnostic()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "ignored/\n");
		temp.CreateFile("src/app.cs", "class App {}");
		temp.CreateFile("ignored/secret.cs", "class Secret {}");
		using var exclusiveLease = new FileStream(
			gitIgnorePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);
		var probe = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		if (probe.Status != GitIgnoreMatcherLoadStatus.ReadFailure)
			Assert.Skip("This filesystem does not enforce an exclusive lease against same-process readers.");

		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src", "ignored"]);
		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(["src", "ignored"], PathComparer.Default),
				rules),
			TestContext.Current.CancellationToken);

		Assert.False(tree.RootAccessDenied);
		Assert.True(tree.HadAccessDenied);
		Assert.True(tree.Root.IsAccessDenied);
		Assert.Empty(tree.Root.Children);

		var analysis = CreateProjectAnalysisService().Load(
			new ProjectAnalysisRequest(
				temp.Path,
				SelectedRootFolders: ["src", "ignored"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]),
			TestContext.Current.CancellationToken);
		var diagnostics = ProjectAnalysisService.BuildDiagnostics(analysis);

		Assert.False(diagnostics.RootAccessDenied);
		Assert.True(diagnostics.HadAccessDenied);

		var plan = await new ProjectContextPlanner(CreateProjectAnalysisService()).BuildAsync(
			new ProjectContextRequest(
				temp.Path,
				new ProjectSelectionSpec(
					Roots: ["src", "ignored"],
					Extensions: [".cs"],
					GitMode: GitFilteringMode.RespectGitIgnore,
					Exclusions: [])),
			TestContext.Current.CancellationToken);

		Assert.Empty(plan.IncludedFiles);
		Assert.Contains(
			plan.Diagnostics,
			static diagnostic =>
				diagnostic.Code == "DPX-PROJECT-PARTIAL-ACCESS" &&
				diagnostic.Severity == ContextDiagnosticSeverity.Warning);
	}

	[Fact]
	public void MissingGitIgnoreHasTypedNotFoundOutcome()
	{
		using var temp = new TemporaryDirectory();
		var missingPath = Path.Combine(temp.Path, ".gitignore");
		var nonAuthoritativeResolver = new FixedSemanticsResolver(
			GitPathComparisonSemantics.PlatformDefault with { IsAuthoritative = false });

		Assert.Equal(
			GitIgnoreMatcherLoadStatus.NotFound,
			GitIgnoreMatcherFileCache.Load(temp.Path, missingPath).Status);
		Assert.Equal(
			GitIgnoreMatcherLoadStatus.NotFound,
			GitIgnoreMatcherFileCache.Load(
				temp.Path,
				missingPath,
				nonAuthoritativeResolver).Status);
	}

	[Fact]
	public void TreeBuild_ReusesPreparsedRootMatcherAsOneOperationSnapshot()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "*.generated\n");
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("src/leak.generated", "generated");
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["src"]);

		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>([".cs", ".generated"], StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(["src"], PathComparer.Default),
				rules),
			TestContext.Current.CancellationToken);
		var diagnostics = measurement.Capture();

		var sourceRoot = Assert.Single(tree.Root.Children, static node => node.Name == "src");
		Assert.Contains(sourceRoot.Children, static node => node.Name == "App.cs");
		Assert.DoesNotContain(sourceRoot.Children, static node => node.Name == "leak.generated");
		Assert.Equal(0, diagnostics.GitIgnoreLoadExecutions);
		Assert.Equal(0, diagnostics.GitIgnoreSourceReadRequests);
		Assert.True(diagnostics.GitIgnoreLoadReuses >= 1);
	}

	[Fact]
	public void DirectoryAtGitIgnorePathHasTypedReadFailureOutcome()
	{
		using var temp = new TemporaryDirectory();
		var directoryPath = temp.CreateDirectory(".gitignore");

		var result = GitIgnoreMatcherFileCache.Load(temp.Path, directoryPath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, result.Status);
		Assert.Null(result.Matcher);
	}

	[Fact]
	public void SourceAboveMaximumSizeHasTypedReadFailureOutcomeWithoutReadingPayload()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		using (var stream = new FileStream(
		       gitIgnorePath,
		       FileMode.CreateNew,
		       FileAccess.Write,
		       FileShare.None))
		{
			stream.SetLength(GitIgnoreFileReader.MaximumFileSizeBytes + 1);
		}

		var result = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, result.Status);
		Assert.Null(result.Matcher);
	}

	[Fact]
	public void ParsedMatcherAboveRetentionBudgetIsReturnedButNotRetained()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		var sourceLength = GitIgnoreMatcherFileCache.MaximumRetainedSourceBytes + 1;
		CreateSparseFile(gitIgnorePath, sourceLength);
		var syntheticSource = new GitIgnoreFileContent("*.tmp\n", sourceLength, "oversized-source");

		var first = GitIgnoreMatcherFileCache.Load(
			temp.Path,
			gitIgnorePath,
			_ => syntheticSource);
		var second = GitIgnoreMatcherFileCache.Load(
			temp.Path,
			gitIgnorePath,
			_ => syntheticSource);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, first.Status);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, second.Status);
		Assert.NotSame(first.Matcher, second.Matcher);
	}

	[Fact]
	public void NewlineDenseSourceDoesNotMaterializeEveryLineBeforeMatching()
	{
		using var temp = new TemporaryDirectory();
		var content = new string('\n', 1024 * 1024);
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		CreateSparseFile(gitIgnorePath, content.Length);
		var source = new GitIgnoreFileContent(content, content.Length, "newline-dense-source");
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var result = GitIgnoreMatcherFileCache.Load(
			temp.Path,
			gitIgnorePath,
			_ => source);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, result.Status);
		Assert.InRange(allocatedBytes, 0, 4 * 1024 * 1024);
	}

	[Fact]
	public void ExcessiveEffectiveRuleCountHasTypedReadFailureOutcome()
	{
		const int excessiveRuleCount = 8_193;
		using var temp = new TemporaryDirectory();
		var content = string.Join(
			'\n',
			Enumerable.Range(0, excessiveRuleCount)
				.Select(static index => $"literal-{index}"));
		var gitIgnorePath = Path.Combine(temp.Path, ".gitignore");
		CreateSparseFile(gitIgnorePath, content.Length);
		var source = new GitIgnoreFileContent(content, content.Length, "excessive-rules-source");

		var result = GitIgnoreMatcherFileCache.Load(
			temp.Path,
			gitIgnorePath,
			_ => source);

		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, result.Status);
		Assert.Null(result.Matcher);
	}

	[Fact]
	public void SmallUnchangedSourceReusesMatcherByLengthAndContentFingerprint()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "*.tmp\n");

		var first = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
		var second = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, first.Status);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, second.Status);
		Assert.Same(first.Matcher, second.Matcher);
	}

	[Fact]
	public void MatcherCacheEvictsLeastRecentlyUsedEntriesByCombinedSourceWeight()
	{
		using var temp = new TemporaryDirectory();
		var firstRoot = temp.CreateDirectory("first");
		var secondRoot = temp.CreateDirectory("second");
		var sourceLength = GitIgnoreMatcherFileCache.MaximumRetainedSourceBytes / 2 + 1;
		var firstPath = Path.Combine(firstRoot, ".gitignore");
		var secondPath = Path.Combine(secondRoot, ".gitignore");
		CreateSparseFile(firstPath, sourceLength);
		CreateSparseFile(secondPath, sourceLength);
		var firstSource = new GitIgnoreFileContent("first.tmp\n", sourceLength, "weighted-first");
		var secondSource = new GitIgnoreFileContent("second.tmp\n", sourceLength, "weighted-second");

		var firstLoad = GitIgnoreMatcherFileCache.Load(firstRoot, firstPath, _ => firstSource);
		var secondLoad = GitIgnoreMatcherFileCache.Load(secondRoot, secondPath, _ => secondSource);
		var firstReload = GitIgnoreMatcherFileCache.Load(firstRoot, firstPath, _ => firstSource);

		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, firstLoad.Status);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, secondLoad.Status);
		Assert.Equal(GitIgnoreMatcherLoadStatus.Loaded, firstReload.Status);
		Assert.NotSame(firstLoad.Matcher, firstReload.Matcher);
	}

	[Fact]
	public void SourceRemovedAfterSuccessfulReadIsReadFailureRatherThanNotFoundOrStaleSuccess()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile(".gitignore", "*.tmp\n");

		var result = GitIgnoreMatcherFileCache.Load(
			temp.Path,
			gitIgnorePath,
			path =>
			{
				var source = GitIgnoreFileReader.Read(path);
				File.Delete(path);
				return source;
			});

		Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, result.Status);
		Assert.Null(result.Matcher);
	}

	[Fact]
	public void UnixInaccessibleSourceMetadataIsReadFailureRatherThanNotFound()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix mode bits are not an access-control mechanism on Windows.");
			return;
		}

		using var temp = new TemporaryDirectory();
		var blockedDirectory = temp.CreateDirectory("blocked");
		var gitIgnorePath = temp.CreateFile("blocked/.gitignore", "*.tmp\n");
		var originalMode = File.GetUnixFileMode(blockedDirectory);
		try
		{
			File.SetUnixFileMode(blockedDirectory, UnixFileMode.None);
			var result = GitIgnoreMatcherFileCache.Load(temp.Path, gitIgnorePath);
			if (result.Status == GitIgnoreMatcherLoadStatus.Loaded)
			{
				Assert.Skip(
					"The test process can bypass Unix mode bits; metadata access cannot be denied reliably.");
			}

			Assert.Equal(GitIgnoreMatcherLoadStatus.ReadFailure, result.Status);
			Assert.Null(result.Matcher);
		}
		finally
		{
			File.SetUnixFileMode(blockedDirectory, originalMode);
		}
	}

	[Fact]
	public void SymlinkedGitIgnoreHasTypedNonErrorSkipOutcome()
	{
		using var temp = new TemporaryDirectory();
		var linkPath = Path.Combine(temp.Path, ".gitignore");
		var targetPath = temp.CreateFile("external-ignore-rules", "*.tmp\n");
		try
		{
			File.CreateSymbolicLink(linkPath, targetPath);
			if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
				Assert.Skip("The created file link is not reported as a reparse point.");
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       PlatformNotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}

		var result = GitIgnoreMatcherFileCache.Load(temp.Path, linkPath);

		Assert.Equal(GitIgnoreMatcherLoadStatus.SymbolicLinkSkipped, result.Status);
		Assert.Null(result.Matcher);
	}

	[Fact]
	public void UnreadableNestedGitIgnoreExcludesOnlyItsScopeAndKeepsSiblingScopeUsable()
	{
		using var temp = new TemporaryDirectory();
		var gitIgnorePath = temp.CreateFile("blocked/.gitignore", "*.secret\n");
		temp.CreateFile("blocked/private.secret", "must not leak");
		temp.CreateFile("sibling/visible.cs", "class Visible {}");
		using var exclusiveLease = new FileStream(
			gitIgnorePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);
		if (GitIgnoreMatcherFileCache.Load(Path.GetDirectoryName(gitIgnorePath)!, gitIgnorePath).Status !=
		    GitIgnoreMatcherLoadStatus.ReadFailure)
		{
			Assert.Skip("This filesystem does not enforce an exclusive lease against same-process readers.");
		}

		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRootFolders: ["blocked", "sibling"]);
		var tree = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(
				new HashSet<string>([".secret", ".cs"], StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(["blocked", "sibling"], PathComparer.Default),
				rules),
			TestContext.Current.CancellationToken);

		Assert.False(tree.RootAccessDenied);
		Assert.True(tree.HadAccessDenied);
		var blocked = Assert.Single(
			tree.Root.Children,
			static node => node.Name.Equals("blocked", StringComparison.Ordinal));
		Assert.True(blocked.IsAccessDenied);
		Assert.Empty(blocked.Children);
		var sibling = Assert.Single(
			tree.Root.Children,
			static node => node.Name.Equals("sibling", StringComparison.Ordinal));
		Assert.Contains(
			sibling.Children,
			static node => node.Name.Equals("visible.cs", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RepositoryInfoExcludeLockedSourceFailsClosedWithPartialAccessDiagnostic()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Exclusive sharing is a Windows filesystem contract.");
		using var temp = new TemporaryDirectory();
		EnsureGitAvailable(temp.Path);
		Assert.Equal(0, RunGit(temp.Path, "init", "--quiet"));
		temp.CreateFile("visible.cs", "visible");
		using var lockedSource = new FileStream(Path.Combine(temp.Path, ".git", "info", "exclude"),
			FileMode.Open, FileAccess.ReadWrite, FileShare.None);
		var plan = await new ProjectContextPlanner(CreateProjectAnalysisService()).BuildAsync(
			new ProjectContextRequest(temp.Path, new ProjectSelectionSpec(
				GitMode: GitFilteringMode.RespectGitIgnore, Exclusions: [])), TestContext.Current.CancellationToken);
		Assert.Empty(plan.IncludedFiles);
		Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "DPX-PROJECT-PARTIAL-ACCESS");
	}

	[Fact]
	public async Task RepositoryInfoExcludePermissionDeniedFailsClosedOnUnix()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix permissions require a Unix filesystem.");
			return;
		}
		using var temp = new TemporaryDirectory();
		EnsureGitAvailable(temp.Path);
		Assert.Equal(0, RunGit(temp.Path, "init", "--quiet"));
		temp.CreateFile("visible.cs", "visible");
		var source = Path.Combine(temp.Path, ".git", "info", "exclude");
		var originalMode = File.GetUnixFileMode(source);
		try
		{
			File.SetUnixFileMode(source, UnixFileMode.None);
			try
			{
				using var readable = File.OpenRead(source);
				Assert.Skip("This user can bypass Unix file permissions.");
			}
			catch (UnauthorizedAccessException)
			{
			}
			var plan = await new ProjectContextPlanner(CreateProjectAnalysisService()).BuildAsync(
				new ProjectContextRequest(temp.Path, new ProjectSelectionSpec(
					GitMode: GitFilteringMode.RespectGitIgnore, Exclusions: [])), TestContext.Current.CancellationToken);
			Assert.Empty(plan.IncludedFiles);
			Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "DPX-PROJECT-PARTIAL-ACCESS");
		}
		finally
		{
			File.SetUnixFileMode(source, originalMode);
		}
	}

	private static ProjectAnalysisService CreateProjectAnalysisService() =>
		new(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());

	private static void CreateSparseFile(string path, long length)
	{
		using var stream = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None);
		stream.SetLength(length);
	}

	private static IReadOnlyList<string> FlattenPaths(TreeNodeDescriptor root)
	{
		var paths = new List<string>();
		var pending = new Stack<TreeNodeDescriptor>(root.Children.Reverse());
		while (pending.TryPop(out var node))
		{
			paths.Add(Path.GetFileName(node.FullPath));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}

	private static byte[] EncodeGitIgnore(string encodingName, string content)
	{
		Encoding encoding = encodingName switch
		{
			"utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			"utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
			"utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
			"utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
			"utf32-le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true),
			"utf32-be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
			_ => throw new ArgumentOutOfRangeException(nameof(encodingName), encodingName, null)
		};
		return [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
	}

	private static void EnsureGitAvailable(string workingDirectory)
	{
		try
		{
			if (RunGit(workingDirectory, "--version") != 0)
				Assert.Skip("Git is not available in this test environment.");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is not available in this test environment.");
		}
	}

	private static int RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git.");
		_ = process.StandardOutput.ReadToEnd();
		_ = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git command did not complete within 10 seconds.");
		}

		return process.ExitCode;
	}

	private sealed class FixedSemanticsResolver(GitPathComparisonSemantics semantics)
		: IGitPathComparisonSemanticsResolver
	{
		public GitPathComparisonSemantics Resolve(string scopeRootPath) => semantics;

		public void Invalidate(string rootPath)
		{
		}
	}
}
