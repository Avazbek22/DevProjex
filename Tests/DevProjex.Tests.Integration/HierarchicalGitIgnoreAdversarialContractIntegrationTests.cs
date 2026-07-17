using System.Text;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class HierarchicalGitIgnoreAdversarialContractIntegrationTests
{
	[Fact]
	public void NativeGitDifferential_OfficialPatternsAndSevenScopePrecedenceMatchBothTreePipelines()
	{
		using var temp = new TemporaryDirectory();
		var relativeFiles = SeedNativeGitOracleWorkspace(temp);
		var repoPath = Path.Combine(temp.Path, "repo");
		RunGit(repoPath, "init", "--quiet");
		var nativeIgnored = QueryNativeGitIgnoredPaths(repoPath, relativeFiles);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var selectedRoots = RootSet("repo");
		var rules = services.IgnoreRulesService.Build(
			temp.Path,
			[IgnoreOptionId.UseGitIgnore],
			selectedRoots);
		var observation = ScanAndCompareTrees(
			temp.Path,
			selectedRoots,
			AllAdversarialExtensions(),
			rules);
		var applicationIgnored = relativeFiles
			.Where(path => !observation.Paths.Contains($"repo/{path}", StringComparer.OrdinalIgnoreCase))
			.ToHashSet(StringComparer.Ordinal);

		Assert.True(
			nativeIgnored.SetEquals(applicationIgnored),
			$"Native-only=[{string.Join(", ", nativeIgnored.Except(applicationIgnored))}]; " +
			$"Application-only=[{string.Join(", ", applicationIgnored.Except(nativeIgnored))}]");
		Assert.Equal(7, observation.ScopeCount);
	}

	[Fact]
	public void CorruptedFileMatrix_BadEncodingAndMalformedLinesCannotDisableValidRulesOrSiblingScopes()
	{
		using var temp = new TemporaryDirectory();
		SeedCorruptedFileWorkspace(temp);
		var selectedRoots = RootSet("repo");
		var observation = ScanAndCompareTrees(
			temp.Path,
			selectedRoots,
			AllAdversarialExtensions(),
			CreateTraversalRules());

		Assert.Equal(5, observation.ScopeCount);
		AssertVisible(observation, "repo/bom/keep.bom");
		AssertHidden(observation, "repo/bom/drop.bom");
		AssertHidden(observation, "repo/invalid-utf8/drop.tmp");
		AssertVisible(observation, "repo/invalid-utf8/keep.txt");
		AssertHidden(observation, "repo/malformed-pattern/drop.bad");
		AssertVisible(observation, "repo/malformed-pattern/invalid/keep.txt");
		AssertHidden(observation, "repo/nul-line/drop.nul");
		AssertHidden(observation, "repo/long-line/drop.long");
		AssertVisible(observation, "repo/bad-shape/keep.tmp");
		AssertVisible(observation, "repo/bad-shape/.gitignore/inner.txt");
	}

	[Fact]
	public void SelectedRootScopeMatrix_NeverLeaksMatchersAcrossSiblingRoots()
	{
		using var temp = new TemporaryDirectory();
		SeedSelectedRootWorkspace(temp);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var extensions = new HashSet<string>([".shared", ".txt"], StringComparer.OrdinalIgnoreCase);

		foreach (var selectedRoots in new[] { RootSet("alpha"), RootSet("beta"), RootSet("alpha", "beta") })
		foreach (var gitIgnoreEnabled in new[] { false, true })
		{
			var selectedOptions = gitIgnoreEnabled
				? new[] { IgnoreOptionId.UseGitIgnore }
				: Array.Empty<IgnoreOptionId>();
			var rules = services.IgnoreRulesService.Build(temp.Path, selectedOptions, selectedRoots);
			var observation = ScanAndCompareTrees(temp.Path, selectedRoots, extensions, rules);
			var scenario = $"Roots=[{string.Join(",", selectedRoots)}]; Git={gitIgnoreEnabled}";

			Assert.Equal(selectedRoots.Count, observation.Paths.Count(path => !path.Contains('/')));
			foreach (var root in selectedRoots)
			{
				AssertVisibility(observation, $"{root}/drop.shared", expected: !gitIgnoreEnabled, scenario);
				AssertVisibility(observation, $"{root}/{root}-keep.shared", expected: true, scenario);
				AssertVisibility(observation, $"{root}/nested/drop.shared", expected: !gitIgnoreEnabled, scenario);
			}

			foreach (var unselectedRoot in RootSet("alpha", "beta").Except(selectedRoots))
				Assert.DoesNotContain(observation.Paths, path => path.Equals(unselectedRoot, StringComparison.OrdinalIgnoreCase));
		}
	}

	[Fact]
	public void MalformedControllerToggleJourney_KeepsCheckboxExtensionsRootsAndTreeAtomic()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("repo/.gitignore", "[unterminated\ninvalid\\\n*.volatile\n");
		temp.CreateFile("repo/drop.volatile", "ignored by the valid rule");
		temp.CreateFile("repo/stable.cs", "visible");
		temp.CreateFile("repo/invalid/keep.cs", "an invalid trailing escape must not hide this directory");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var defaults = ComputeConvergedSnapshot(
			services,
			temp.Path,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			});

		AssertControllerJourneyStage(temp.Path, services, defaults, expectedChecked: true, expectVolatile: false);

		var gitOff = SetGitIgnoreState(temp.Path, services, defaults, isChecked: false);
		AssertControllerJourneyStage(temp.Path, services, gitOff, expectedChecked: false, expectVolatile: true);

		var gitOn = SetGitIgnoreState(temp.Path, services, gitOff, isChecked: true);
		AssertControllerJourneyStage(temp.Path, services, gitOn, expectedChecked: true, expectVolatile: false);
		Assert.Equal(
			defaults.RootOptions!.Select(static option => option.Name),
			gitOn.RootOptions!.Select(static option => option.Name));
	}

	private static IReadOnlyList<string> SeedNativeGitOracleWorkspace(TemporaryDirectory temp)
	{
		var rootRules = string.Join('\n',
			".git/",
			"# comment",
			"*.log",
			"!important.log",
			"/anchored.txt",
			"dir-only/",
			"docs/*.tmp",
			"scoped/path.txt",
			"scoped-dir/output/",
			"triple/***/leaf.txt",
			"a/**/deep.bin",
			@"\#literal.txt",
			@"\!literal.txt",
			@"escaped\ space.txt",
			"trimmed.txt   ",
			@"invalid\",
			"[broken",
			"blocked/",
			"*.state",
			"file[!0-9].dat",
			"*.юникод",
			string.Empty);
		temp.CreateFile("repo/.gitignore", rootRules);
		temp.CreateFile("repo/module/.gitignore", "*.cache\n!keep.cache\n/local.txt\n");
		temp.CreateFile("repo/module/child/.gitignore", "!rescue.cache\n*.secret\n");
		temp.CreateFile("repo/module-sibling/.gitignore", "*.sibling\n");
		temp.CreateFile("repo/blocked/.gitignore", "!visible.txt\n");
		temp.CreateFile("repo/l1/.gitignore", "!*.state\n");
		temp.CreateFile("repo/l1/l2/.gitignore", "*.state\n");
		temp.CreateFile("repo/l1/l2/l3/.gitignore", "!final.state\n");

		var files = new[]
		{
			"debug.log",
			"important.log",
			"nested/debug.log",
			"anchored.txt",
			"nested/anchored.txt",
			"dir-only/hidden.txt",
			"plain/dir-only",
			"docs/direct.tmp",
			"docs/deep/nested.tmp",
			"scoped/path.txt",
			"nested/scoped/path.txt",
			"scoped-dir/output/hidden.txt",
			"nested/scoped-dir/output/visible.txt",
			"triple/one/leaf.txt",
			"triple/one/two/leaf.txt",
			"a/deep.bin",
			"a/x/y/deep.bin",
			"other/deep.bin",
			"#literal.txt",
			"!literal.txt",
			"escaped space.txt",
			"trimmed.txt",
			"invalid/keep.txt",
			"[broken",
			"unicode/файл.юникод",
			"module/drop.cache",
			"module/keep.cache",
			"module/local.txt",
			"module/child/local.txt",
			"module/child/rescue.cache",
			"module/child/drop.cache",
			"module/child/drop.secret",
			"module-sibling/drop.sibling",
			"module-sibling/keep.cache",
			"outside/drop.sibling",
			"blocked/visible.txt",
			"root.state",
			"fileA.dat",
			"file5.dat",
			"l1/visible.state",
			"l1/sibling/visible.state",
			"l1/l2/hidden.state",
			"l1/l2/l3/final.state",
			"l1/l2/l3/other.state",
			"ordinary.txt"
		};

		foreach (var file in files)
			temp.CreateFile(Path.Combine("repo", file), file);
		return files.Select(static path => path.Replace('\\', '/')).ToArray();
	}

	private static void SeedCorruptedFileWorkspace(TemporaryDirectory temp)
	{
		var bomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
		var bomRules = bomEncoding.GetPreamble()
			.Concat(bomEncoding.GetBytes("*.bom\r\n!keep.bom\r\n"))
			.ToArray();
		WriteBytes(temp, "repo/bom/.gitignore", bomRules);
		temp.CreateFile("repo/bom/drop.bom", "ignored");
		temp.CreateFile("repo/bom/keep.bom", "visible");

		var malformedUtf8 = new byte[] { 0xC3, 0x28, (byte)'\r', (byte)'\n' }
			.Concat(Encoding.ASCII.GetBytes("*.tmp\r\n"))
			.ToArray();
		WriteBytes(temp, "repo/invalid-utf8/.gitignore", malformedUtf8);
		temp.CreateFile("repo/invalid-utf8/drop.tmp", "ignored");
		temp.CreateFile("repo/invalid-utf8/keep.txt", "visible");

		temp.CreateFile("repo/malformed-pattern/.gitignore", "[unterminated\n[z-a]\ninvalid\\\n*.bad\n");
		temp.CreateFile("repo/malformed-pattern/drop.bad", "ignored");
		temp.CreateFile("repo/malformed-pattern/invalid/keep.txt", "visible");

		WriteBytes(temp, "repo/nul-line/.gitignore", Encoding.UTF8.GetBytes("bad\0rule\n*.nul\n"));
		temp.CreateFile("repo/nul-line/drop.nul", "ignored");

		temp.CreateFile("repo/long-line/.gitignore", $"{new string('x', 16_384)}\n*.long\n");
		temp.CreateFile("repo/long-line/drop.long", "ignored");

		temp.CreateDirectory("repo/bad-shape/.gitignore");
		temp.CreateFile("repo/bad-shape/.gitignore/inner.txt", "ordinary directory content");
		temp.CreateFile("repo/bad-shape/keep.tmp", "no matcher exists in this scope");
	}

	private static void SeedSelectedRootWorkspace(TemporaryDirectory temp)
	{
		foreach (var root in new[] { "alpha", "beta" })
		{
			temp.CreateFile($"{root}/.gitignore", $"*.shared\n!{root}-keep.shared\n");
			temp.CreateFile($"{root}/drop.shared", "ignored only by the owning scope");
			temp.CreateFile($"{root}/{root}-keep.shared", "re-included only by the owning scope");
			temp.CreateFile($"{root}/nested/drop.shared", "inherits only the owning scope");
			temp.CreateFile($"{root}/ordinary.txt", "visible");
		}
	}

	private static ScanObservation ScanAndCompareTrees(
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> extensions,
		IgnoreRules rules)
	{
		var scanner = new FileSystemScanner();
		var scan = scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				rootPath,
				selectedRoots,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(extensions),
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: true,
				IncludeControllerImpactProbeRoots: true),
			TestContext.Current.CancellationToken);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory);
		var options = new TreeFilterOptions(extensions, selectedRoots, rules);
		var builder = new TreeBuilder();
		var direct = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var projected = builder.Build(inventory, options, TestContext.Current.CancellationToken);
		var directPaths = FlattenRelativePaths(rootPath, direct.Root);
		var projectedPaths = FlattenRelativePaths(rootPath, projected.Root);

		Assert.Equal(directPaths, projectedPaths);
		return new ScanObservation(
			projectedPaths,
			inventory.DiscoveredGitIgnoreMatchers.Count,
			scan.Value.IgnoreSection.ControllerImpactCounts);
	}

	private static HashSet<string> QueryNativeGitIgnoredPaths(
		string repoPath,
		IReadOnlyList<string> relativePaths)
	{
		var startInfo = CreateGitStartInfo(repoPath);
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add("core.excludesFile=");
		startInfo.ArgumentList.Add("check-ignore");
		startInfo.ArgumentList.Add("--no-index");
		startInfo.ArgumentList.Add("--stdin");
		startInfo.ArgumentList.Add("-z");
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git check-ignore.");
		foreach (var path in relativePaths)
		{
			process.StandardInput.Write(path);
			process.StandardInput.Write('\0');
		}
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("git check-ignore did not complete within 10 seconds.");
		}
		Assert.True(process.ExitCode is 0 or 1, $"git check-ignore failed ({process.ExitCode}): {error}");
		return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Select(static path => path.Replace('\\', '/'))
			.ToHashSet(StringComparer.Ordinal);
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = CreateGitStartInfo(workingDirectory);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("git did not complete within 10 seconds.");
		}
		Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
	}

	private static ProcessStartInfo CreateGitStartInfo(string workingDirectory) =>
		new("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

	private static void WriteBytes(TemporaryDirectory temp, string relativePath, byte[] content)
	{
		var fullPath = Path.Combine(temp.Path, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllBytes(fullPath, content);
	}

	private static SelectionRefreshSnapshot SetGitIgnoreState(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot previous,
		bool isChecked)
	{
		var states = previous.IgnoreOptionStateCache.ToDictionary(static pair => pair.Key, static pair => pair.Value);
		states[IgnoreOptionId.UseGitIgnore] = isChecked;
		var selected = previous.IgnoreOptions
			.Where(option => option.IsChecked && option.Id != IgnoreOptionId.UseGitIgnore)
			.Select(static option => option.Id)
			.ToHashSet();
		if (isChecked)
			selected.Add(IgnoreOptionId.UseGitIgnore);

		return ComputeConvergedSnapshot(
			services,
			rootPath,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
			{
				IgnoreSelectionInitialized = true,
				IgnoreSelectionCache = selected,
				IgnoreOptionStateCache = states,
				IgnoreOptionStateCacheIsComplete = true,
				IgnoreAllPreference = null,
				CaptureTreeInventory = true
			});
	}

	private static void AssertControllerJourneyStage(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool expectedChecked,
		bool expectVolatile)
	{
		var gitIgnore = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.Equal(expectedChecked, gitIgnore.IsChecked);
		var paths = BuildAndCompareSnapshotTrees(rootPath, services, snapshot);
		AssertVisibility(paths, "repo/drop.volatile", expectVolatile, $"Git={expectedChecked}");
		Assert.Contains("repo/stable.cs", paths);
		Assert.Contains("repo/invalid/keep.cs", paths);
		Assert.Equal(expectVolatile, snapshot.EffectiveExtensionOptions.Any(
			static option => option.Name.Equals(".volatile", StringComparison.OrdinalIgnoreCase)));
	}

	private static List<string> BuildAndCompareSnapshotTrees(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot)
	{
		var roots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var extensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = snapshot.IgnoreOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToArray();
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, roots);
		var options = new TreeFilterOptions(extensions, roots, rules);
		var builder = new TreeBuilder();
		var direct = FlattenRelativePaths(
			rootPath,
			builder.Build(rootPath, options, TestContext.Current.CancellationToken).Root);
		var projected = FlattenRelativePaths(
			rootPath,
			builder.Build(
				Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
				options,
				TestContext.Current.CancellationToken).Root);
		Assert.Equal(direct, projected);
		return projected;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var previous = services.Engine.ComputeFullRefreshSnapshot(context, TestContext.Current.CancellationToken);
		for (var pass = 0; pass < 6; pass++)
		{
			var next = services.Engine.ComputeFullRefreshSnapshot(
				ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
				{
					CaptureTreeInventory = context.CaptureTreeInventory
				},
				TestContext.Current.CancellationToken);
			try
			{
				ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(previous, next);
				return next;
			}
			catch (Xunit.Sdk.XunitException)
			{
				previous = next;
			}
		}

		var final = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
			{
				CaptureTreeInventory = context.CaptureTreeInventory
			},
			TestContext.Current.CancellationToken);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(previous, final);
		return final;
	}

	private static IgnoreRules CreateTraversalRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			EnableGitIgnoreTraversal = true,
			GitIgnoreCandidateMatchesActiveRules = true
		};

	private static HashSet<string> AllAdversarialExtensions() =>
		new(
			[".bad", ".bin", ".bom", ".cache", ".cs", ".dat", ".gitignore", ".log", ".long", ".nul", ".secret", ".shared", ".sibling", ".state", ".tmp", ".txt", ".volatile", ".юникод"],
			StringComparer.OrdinalIgnoreCase);

	private static HashSet<string> RootSet(params string[] roots) =>
		new(roots, PathComparer.Default);

	private static void AssertVisible(ScanObservation observation, string path) =>
		Assert.Contains(path, observation.Paths);

	private static void AssertHidden(ScanObservation observation, string path) =>
		Assert.DoesNotContain(path, observation.Paths);

	private static void AssertVisibility(
		ScanObservation observation,
		string path,
		bool expected,
		string scenario) =>
		AssertVisibility(observation.Paths, path, expected, scenario);

	private static void AssertVisibility(
		IReadOnlyCollection<string> paths,
		string path,
		bool expected,
		string scenario) =>
		Assert.True(
			paths.Contains(path, StringComparer.OrdinalIgnoreCase) == expected,
			$"{scenario}: '{path}' visibility expected {expected}");

	private static List<string> FlattenRelativePaths(string rootPath, FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		for (var index = root.Children.Count - 1; index >= 0; index--)
			pending.Push(root.Children[index]);

		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/'));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		paths.Sort(StringComparer.OrdinalIgnoreCase);
		return paths;
	}

	private sealed record ScanObservation(
		IReadOnlyList<string> Paths,
		int ScopeCount,
		IgnoreControllerImpactCounts ControllerImpactCounts);
}
