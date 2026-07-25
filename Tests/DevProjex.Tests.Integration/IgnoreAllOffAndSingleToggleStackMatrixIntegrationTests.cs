using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class IgnoreAllOffAndSingleToggleStackMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(StackCases))]
	public void FullRefresh_AllIgnoreOptionsOff_RevealsEveryStackArtifactAndGeneralIgnoreCandidate(
		StackCase stackCase)
	{
		using var temp = CreateStackWorkspace(stackCase, includeGitIgnore: true);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var defaults = ComputeConvergedSnapshot(
			services,
			temp.Path,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path));
		var allOff = ComputeConvergedSnapshot(
			services,
			temp.Path,
			CreateAllIgnoreOptionsOffContext(temp.Path, defaults));

		Assert.DoesNotContain(allOff.IgnoreOptions, option => option.IsChecked);
		AssertRootOptionsVisibleAndChecked(temp.Path, allOff, ExpectedAllRootFolders(stackCase));
		AssertExtensionOptionsVisibleAndChecked(allOff, ExpectedAllExtensions(stackCase));
		Assert.True(allOff.ExtensionlessEntriesCount > 0);

		var tree = BuildTreeFromSnapshot(temp.Path, allOff);
		AssertPathVisible(tree, stackCase.ArtifactPath);
		AssertPathVisible(tree, ".idea/workspace.xml");
		AssertPathVisible(tree, "git-ignored/ignored.log");
		AssertPathVisible(tree, "empty-dir");
		AssertPathVisible(tree, ".env");
		AssertPathVisible(tree, "README");
		AssertPathVisible(tree, "empty.txt");
		AssertPathVisible(tree, "normal/visible.txt");
		AssertPlatformHiddenEntriesVisibleWhenAllIgnoreOptionsAreOff(tree);
	}

	[Theory]
	[MemberData(nameof(StackCases))]
	public void TreeBuilder_SmartIgnoreOnly_HidesOnlyStackArtifactAcrossSupportedStacks(StackCase stackCase)
	{
		using var temp = CreateStackWorkspace(stackCase, includeGitIgnore: false);
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(temp.Path, [IgnoreOptionId.SmartIgnore], ExpectedAllRootFolders(stackCase));

		Assert.True(rules.UseSmartIgnore);
		Assert.False(rules.UseGitIgnore);

		var tree = BuildTree(temp.Path, rules, stackCase);
		AssertPathHidden(tree, stackCase.ArtifactPath);
		AssertPathVisible(tree, ".idea/workspace.xml");
		AssertPathVisible(tree, "git-ignored/ignored.log");
		AssertPathVisible(tree, "empty-dir");
		AssertPathVisible(tree, ".env");
		AssertPathVisible(tree, "README");
		AssertPathVisible(tree, "empty.txt");
		AssertPathVisible(tree, "normal/visible.txt");
	}

	[Theory]
	[MemberData(nameof(StackCases))]
	public void TreeBuilder_GitIgnoreOnly_HidesGitCandidatesWithoutActivatingSmartIgnore(
		StackCase stackCase)
	{
		using var temp = CreateStackWorkspace(stackCase, includeGitIgnore: true);
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(temp.Path, [IgnoreOptionId.UseGitIgnore], ExpectedAllRootFolders(stackCase));

		Assert.True(rules.UseGitIgnore);
		Assert.False(rules.UseSmartIgnore);

		var tree = BuildTree(temp.Path, rules, stackCase);
		AssertPathHidden(tree, "git-ignored/ignored.log");
		AssertPathVisible(tree, stackCase.ArtifactPath);
		AssertPathVisible(tree, ".idea/workspace.xml");
		AssertPathVisible(tree, "empty-dir");
		AssertPathVisible(tree, ".env");
		AssertPathVisible(tree, "README");
		AssertPathVisible(tree, "empty.txt");
	}

	[Theory]
	[MemberData(nameof(GeneralSingleToggleStackCases))]
	public void TreeBuilder_SingleGeneralIgnoreOption_HidesOnlyItsOwnCandidateAcrossSupportedStacks(
		StackCase stackCase,
		IgnoreOptionId optionId,
		string hiddenPath)
	{
		if (RequiresWindowsHiddenAttribute(optionId) && !OperatingSystem.IsWindows())
			return;

		using var temp = CreateStackWorkspace(stackCase, includeGitIgnore: false);
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(temp.Path, [optionId], ExpectedAllRootFolders(stackCase));

		var tree = BuildTree(temp.Path, rules, stackCase);
		AssertPathHidden(tree, hiddenPath);
		AssertPathVisible(tree, stackCase.ArtifactPath);
		AssertPathVisible(tree, "git-ignored/ignored.log");

		foreach (var candidatePath in GeneralCandidatePaths().Where(path => !PathMatches(path, hiddenPath)))
			AssertPathVisible(tree, candidatePath);
	}

	[Theory]
	[InlineData(IgnoreOptionId.HiddenFolders)]
	[InlineData(IgnoreOptionId.HiddenFiles)]
	public void TreeBuilder_UnixHiddenTogglesDoNotHideDotEntriesWhenDotTogglesAreOff(IgnoreOptionId optionId)
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = CreateStackWorkspace(StackCasesCore[0], includeGitIgnore: false);
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(temp.Path, [optionId], ExpectedAllRootFolders(StackCasesCore[0]));

		var tree = BuildTree(temp.Path, rules, StackCasesCore[0]);
		AssertPathVisible(tree, ".idea/workspace.xml");
		AssertPathVisible(tree, ".env");
	}

	public static IEnumerable<object[]> StackCases()
	{
		foreach (var stackCase in StackCasesCore)
			yield return [stackCase];
	}

	public static IEnumerable<object[]> GeneralSingleToggleCases()
	{
		yield return [IgnoreOptionId.DotFolders, ".idea/workspace.xml"];
		yield return [IgnoreOptionId.DotFiles, ".env"];
		yield return [IgnoreOptionId.EmptyFolders, "empty-dir"];
		yield return [IgnoreOptionId.EmptyFiles, "empty.txt"];
		yield return [IgnoreOptionId.ExtensionlessFiles, "README"];
		yield return [IgnoreOptionId.HiddenFolders, "hidden-root/hidden.txt"];
		yield return [IgnoreOptionId.HiddenFiles, "hidden-file.secret"];
	}

	public static IEnumerable<object[]> GeneralSingleToggleStackCases()
	{
		foreach (var stackCase in StackCasesCore)
		foreach (var optionCase in GeneralSingleToggleCases())
			yield return [stackCase, optionCase[0], optionCase[1]];
	}

	private static readonly StackCase[] StackCasesCore =
	[
		new("frontend", "package.json", "src/app.ts", "node_modules/pkg/index.js"),
		new("dotnet", "App.csproj", "src/Program.cs", "bin/Debug/app.dll"),
		new("python", "requirements.txt", "src/app.py", "__pycache__/app.pyc"),
		new("jvm", "settings.gradle", "src/main/java/App.java", "build/classes/App.class"),
		new("rust", "Cargo.toml", "src/main.rs", "target/debug/app.bin"),
		new("go", "go.work", "main.go", "vendor/module.go"),
		new("php", "composer.json", "src/App.php", "vendor/autoload.php"),
		new("ruby", "Gemfile.lock", "lib/app.rb", "tmp/cache.txt")
	];

	private static TemporaryDirectory CreateStackWorkspace(StackCase stackCase, bool includeGitIgnore)
	{
		var temp = new TemporaryDirectory();
		if (includeGitIgnore)
			temp.CreateFile(".gitignore", "git-ignored/\n");

		temp.CreateFile(stackCase.MarkerPath, MarkerContent(stackCase.MarkerPath));
		temp.CreateFile(stackCase.SourcePath, "source");
		temp.CreateFile(stackCase.ArtifactPath, "artifact");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("git-ignored/ignored.log", "git ignored");
		temp.CreateDirectory("empty-dir");
		temp.CreateFile(".env", "APP_ENV=dev");
		temp.CreateFile("README", "docs");
		temp.CreateFile("empty.txt", string.Empty);
		temp.CreateFile("normal/visible.txt", "visible");

		if (OperatingSystem.IsWindows())
		{
			var hiddenRoot = temp.CreateDirectory("hidden-root");
			temp.CreateFile("hidden-root/hidden.txt", "hidden folder content");
			MarkHidden(hiddenRoot);

			var hiddenFile = temp.CreateFile("hidden-file.secret", "hidden file content");
			MarkHidden(hiddenFile);
		}

		return temp;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var first = services.Engine.ComputeFullRefreshSnapshot(context, CancellationToken.None);
		var second = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, first),
			CancellationToken.None);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(first, second);
		return second;
	}

	private static SelectionRefreshContext CreateAllIgnoreOptionsOffContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache);
		foreach (var option in snapshot.IgnoreOptions)
			stateCache[option.Id] = false;
		foreach (var optionId in stateCache.Keys.ToArray())
			stateCache[optionId] = false;

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
		{
			AllRootFoldersChecked = true,
			AllExtensionsChecked = true,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = false
		};
	}

	private static TreeBuildResult BuildTreeFromSnapshot(string rootPath, SelectionRefreshSnapshot snapshot)
	{
		var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService()
			.Build(rootPath, ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot), ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot));

		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: ProjectLoadWorkflowRefreshHarness.CollectCheckedExtensionNames(snapshot),
			AllowedRootFolders: ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot),
			IgnoreRules: rules));
	}

	private static TreeBuildResult BuildTree(string rootPath, IgnoreRules rules, StackCase stackCase)
	{
		return new TreeBuilder().Build(rootPath, new TreeFilterOptions(
			AllowedExtensions: ExpectedAllExtensions(stackCase),
			AllowedRootFolders: ExpectedAllRootFolders(stackCase),
			IgnoreRules: rules));
	}

	private static HashSet<string> ExpectedAllRootFolders(StackCase stackCase)
	{
		var roots = new HashSet<string>(PathComparer.Default)
		{
			".idea",
			"git-ignored",
			"empty-dir",
			"normal",
			FirstPathSegment(stackCase.SourcePath),
			FirstPathSegment(stackCase.ArtifactPath)
		};
		roots.Remove(string.Empty);

		if (OperatingSystem.IsWindows())
			roots.Add("hidden-root");

		return roots;
	}

	private static HashSet<string> ExpectedAllExtensions(StackCase stackCase)
	{
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			Path.GetExtension(stackCase.SourcePath),
			Path.GetExtension(stackCase.ArtifactPath),
			".env",
			".log",
			".txt",
			".xml"
		};

		if (OperatingSystem.IsWindows())
			extensions.Add(".secret");

		extensions.Remove(string.Empty);
		return extensions;
	}

	private static string[] GeneralCandidatePaths()
	{
		var paths = new List<string>
		{
			".idea/workspace.xml",
			"empty-dir",
			".env",
			"README",
			"empty.txt",
			"normal/visible.txt"
		};

		if (OperatingSystem.IsWindows())
		{
			paths.Add("hidden-root/hidden.txt");
			paths.Add("hidden-file.secret");
		}

		return [.. paths];
	}

	private static void AssertRootOptionsVisibleAndChecked(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> expectedRoots)
	{
		Assert.NotNull(snapshot.RootOptions);
		foreach (var expectedRoot in expectedRoots.Where(root => Directory.Exists(Path.Combine(rootPath, root))))
		{
			Assert.Contains(snapshot.RootOptions!, option =>
				string.Equals(option.Name, expectedRoot, StringComparison.Ordinal) && option.IsChecked);
		}
	}

	private static void AssertExtensionOptionsVisibleAndChecked(
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> expectedExtensions)
	{
		foreach (var extension in expectedExtensions)
		{
			Assert.Contains(snapshot.ExtensionOptions, option =>
				string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase) && option.IsChecked);
		}
	}

	private static void AssertPlatformHiddenEntriesVisibleWhenAllIgnoreOptionsAreOff(TreeBuildResult tree)
	{
		if (!OperatingSystem.IsWindows())
			return;

		AssertPathVisible(tree, "hidden-root/hidden.txt");
		AssertPathVisible(tree, "hidden-file.secret");
	}

	private static void AssertPathVisible(TreeBuildResult tree, string relativePath)
	{
		Assert.True(ContainsPath(tree, relativePath), $"Expected path '{relativePath}' to be visible.");
	}

	private static void AssertPathHidden(TreeBuildResult tree, string relativePath)
	{
		Assert.False(ContainsPath(tree, relativePath), $"Expected path '{relativePath}' to be hidden.");
	}

	private static bool ContainsPath(TreeBuildResult tree, string relativePath)
	{
		var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		IReadOnlyList<FileSystemNode> current = tree.Root.Children;

		foreach (var segment in segments)
		{
			var match = current.FirstOrDefault(node => string.Equals(node.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (match is null)
				return false;

			current = match.Children;
		}

		return true;
	}

	private static bool PathMatches(string left, string right)
	{
		return string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
	}

	private static bool RequiresWindowsHiddenAttribute(IgnoreOptionId optionId)
	{
		return optionId is IgnoreOptionId.HiddenFolders or IgnoreOptionId.HiddenFiles;
	}

	private static string FirstPathSegment(string relativePath)
	{
		return relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
	}

	private static string MarkerContent(string markerPath)
	{
		return Path.GetExtension(markerPath).ToLowerInvariant() switch
		{
			".json" => "{}",
			".toml" => "[package]\nname = \"matrix\"\n",
			".xml" => "<Project />",
			_ => string.Empty
		};
	}

	private static void MarkHidden(string path)
	{
		File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
	}

	public sealed record StackCase(
		string Name,
		string MarkerPath,
		string SourcePath,
		string ArtifactPath);
}
