using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class GitCloneIgnoreLifecycleIntegrationTests
{
	[Theory]
	[InlineData(CloneFixtureKind.Python, 2)]
	[InlineData(CloneFixtureKind.DotNet, 1)]
	public void ManagedCloneMetadata_AllAndFileToggleCycles_KeepIgnoreRootsAndExtensionsCoherent(
		CloneFixtureKind fixtureKind,
		int expectedDotFileCount)
	{
		using var workspace = CreateWorkspace(fixtureKind);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices(
			transformRules: static rules => rules with { ExcludedRootFolderName = ".git" });
		var baseline = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		AssertStableCloneBoundary(baseline, expectedDotFileCount);
		var baselineRootNames = SnapshotRootNames(baseline);
		var baselineIgnoreOptions = baseline.IgnoreOptions.ToArray();

		var current = baseline;
		for (var cycle = 0; cycle < 3; cycle++)
		{
			current = ApplyAll(workspace.Path, services, current, isChecked: false);
			AssertStableCloneBoundary(
				current,
				expectedDotFileCount,
				expectedDotFilesChecked: false,
				expectedExtensionlessChecked: false);
			Assert.Equal(baselineRootNames, SnapshotRootNames(current));
			Assert.Contains(current.EffectiveExtensionOptions, option => option.Name == ".gitignore");
			Assert.Contains(current.TreeInventory!.Entries, entry =>
				!entry.IsDirectory && entry.RelativePath == ".gitignore");

			current = ApplyAll(workspace.Path, services, current, isChecked: true);
			AssertStableCloneBoundary(current, expectedDotFileCount);
			Assert.Equal(baselineRootNames, SnapshotRootNames(current));
			Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);

			current = ToggleFileOption(
				workspace.Path,
				services,
				current,
				IgnoreOptionId.DotFiles,
				isChecked: false);
			AssertStableCloneBoundary(current, expectedDotFileCount, expectedDotFilesChecked: false);
			Assert.Contains(current.EffectiveExtensionOptions, option => option.Name == ".gitignore");

			current = ToggleFileOption(
				workspace.Path,
				services,
				current,
				IgnoreOptionId.DotFiles,
				isChecked: true);
			AssertStableCloneBoundary(current, expectedDotFileCount);
			Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);
		}

		current = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(workspace.Path, current));

		AssertStableCloneBoundary(current, expectedDotFileCount);
		Assert.Equal(baselineRootNames, SnapshotRootNames(current));
		Assert.Equal(baselineIgnoreOptions, current.IgnoreOptions);
	}

	[Fact]
	public void OrdinaryFolderScan_WithoutManagedCloneBoundary_KeepsDotGitUnderNormalIgnoreSemantics()
	{
		using var workspace = CreateWorkspace(CloneFixtureKind.DotNet);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var snapshot = RefreshFull(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));

		Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.DotFolders);
	}

	private static SelectionRefreshSnapshot RefreshFull(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshContext context)
	{
		return services.Engine.ComputeFullRefreshSnapshot(
			context with { CaptureTreeInventory = true },
			TestContext.Current.CancellationToken);
	}

	private static SelectionRefreshSnapshot ApplyAll(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool isChecked)
	{
		var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot);
		var states = snapshot.IgnoreOptionStateCache.Keys.ToDictionary(static id => id, _ => isChecked);
		return RefreshFull(
			rootPath,
			services,
			context with
			{
				IgnoreSelectionCache = isChecked ? states.Keys.ToHashSet() : [],
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = isChecked
			});
	}

	private static SelectionRefreshSnapshot ToggleFileOption(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId optionId,
		bool isChecked)
	{
		var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot);
		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[optionId] = isChecked
		};
		var selectedRoots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var liveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
			context with
			{
				IgnoreSelectionCache = states
					.Where(static pair => pair.Value)
					.Select(static pair => pair.Key)
					.ToHashSet(),
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = null,
				CaptureTreeInventory = true
			},
			selectedRoots,
			TestContext.Current.CancellationToken);

		return liveSnapshot with { RootOptions = snapshot.RootOptions };
	}

	private static void AssertStableCloneBoundary(
		SelectionRefreshSnapshot snapshot,
		int expectedDotFileCount,
		bool expectedDotFilesChecked = true,
		bool expectedExtensionlessChecked = true)
	{
		Assert.NotNull(snapshot.RootOptions);
		Assert.NotNull(snapshot.TreeInventory);
		Assert.DoesNotContain(snapshot.RootOptions!, option => option.Name == ".git");
		Assert.DoesNotContain(snapshot.TreeInventory!.Entries, entry =>
			entry.RelativePath == ".git" ||
			entry.RelativePath.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id is
			IgnoreOptionId.HiddenFolders or
			IgnoreOptionId.DotFolders or
			IgnoreOptionId.EmptyFolders or
			IgnoreOptionId.UseGitIgnore or
			IgnoreOptionId.SmartIgnore);

		var dotFiles = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles);
		var extensionless = Assert.Single(
			snapshot.IgnoreOptions,
			option => option.Id == IgnoreOptionId.ExtensionlessFiles);
		Assert.Equal(expectedDotFilesChecked, dotFiles.IsChecked);
		Assert.Equal(expectedExtensionlessChecked, extensionless.IsChecked);
		Assert.Equal(expectedDotFileCount, snapshot.IgnoreOptionCounts.DotFiles);
		Assert.Equal(1, snapshot.IgnoreOptionCounts.ExtensionlessFiles);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.HiddenFolders);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.DotFolders);
		Assert.Equal(0, snapshot.IgnoreOptionCounts.EmptyFolders);
	}

	private static string[] SnapshotRootNames(SelectionRefreshSnapshot snapshot) =>
		snapshot.RootOptions!.Select(static option => option.Name).ToArray();

	private static TemporaryDirectory CreateWorkspace(CloneFixtureKind fixtureKind)
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		workspace.CreateFile(Path.Combine(".git", "objects", "pack", "pack-a.pack"), "metadata\n");
		workspace.CreateDirectory(Path.Combine(".git", "refs", "tags"));
		MarkHidden(Path.Combine(workspace.Path, ".git"));

		if (fixtureKind == CloneFixtureKind.Python)
		{
			workspace.CreateFile(Path.Combine("app", "bot.py"), "print('ok')\n");
			workspace.CreateFile(".env-example", "TOKEN=\n");
			workspace.CreateFile(".gitignore", "__pycache__/\n*.pyc\n");
			workspace.CreateFile("LICENSE", "MIT\n");
			workspace.CreateFile("README.md", "# Bot\n");
			workspace.CreateFile("requirements.txt", "requests\n");
			workspace.CreateFile("install.sh", "#!/bin/sh\n");
			return workspace;
		}

		workspace.CreateFile(Path.Combine("OmenGamingHubUnlocker", "App.csproj"), "<Project />\n");
		workspace.CreateFile(Path.Combine("OmenGamingHubUnlocker", "Program.cs"), "class Program {}\n");
		workspace.CreateFile(Path.Combine("Tests", "AppTests.cs"), "class AppTests {}\n");
		workspace.CreateFile(".gitignore", "[Bb]in/\n[Oo]bj/\nartifacts/\n");
		workspace.CreateFile("OmenGamingHubUnlocker.sln", "Microsoft Visual Studio Solution File\n");
		workspace.CreateFile("README", "Omen Gaming Hub Unlocker\n");
		return workspace;
	}

	private static void MarkHidden(string path)
	{
		try
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
		}
		catch when (!OperatingSystem.IsWindows())
		{
			// Dot-name semantics still make this a valid clone-metadata fixture on Unix.
		}
	}

	public enum CloneFixtureKind
	{
		Python,
		DotNet
	}
}
