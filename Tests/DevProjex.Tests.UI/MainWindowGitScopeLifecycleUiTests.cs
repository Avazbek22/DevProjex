using DevProjex.Application.Context;
using DevProjex.Application.Services;
using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowGitScopeLifecycleUiTests
{
	[AvaloniaFact]
	public async Task CleanStagedScope_HidesIrrelevantSettingsAndClearsAggregates()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Empty(Assert.Single(viewModel.TreeNodes).Children);
			Assert.Empty(viewModel.PathIgnoreOptions);
			Assert.Empty(viewModel.Extensions);
			Assert.False(viewModel.AllIgnoreChecked);
			Assert.False(viewModel.AllExtensionsChecked);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task StartupCleanStagedScope_PublishesItsEmptySettingsPresentation()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			startupSelection: new ProjectSelectionSpec(
				GitMode: GitFilteringMode.Staged,
				Exclusions: []));

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(
				GitFilteringMode.Staged,
				viewModel.SelectedGitFilteringModeOption?.Mode);
			Assert.Empty(Assert.Single(viewModel.TreeNodes).Children);
			Assert.Empty(viewModel.PathIgnoreOptions);
			Assert.Empty(viewModel.Extensions);
			Assert.False(viewModel.AllIgnoreChecked);
			Assert.False(viewModel.AllExtensionsChecked);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitModeMatrix_RepositoryAndLocalAvailabilityAndAllPathToggleStayConsistent()
	{
		EnsureGitAvailable();
		using var repository = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(repository.RootPath);
		Directory.CreateDirectory(Path.Combine(repository.RootPath, ".scope"));
		await File.WriteAllTextAsync(
			Path.Combine(repository.RootPath, ".scope", "Scoped.cs"),
			"class Scoped {}\n",
			TestContext.Current.CancellationToken);
		var repositoryWindow = await UiTestDriver.CreateLoadedMainWindowAsync(repository);

		try
		{
			GitFilteringMode[] expectedModes =
			[
				GitFilteringMode.None,
				GitFilteringMode.RespectGitIgnore,
				GitFilteringMode.TrackedFilesOnly,
				GitFilteringMode.Staged,
				GitFilteringMode.Changes
			];
			Assert.Equal(
				expectedModes,
				UiTestDriver.GetViewModel(repositoryWindow).GitFilteringModes
					.Select(static option => option.Mode));

			GitFilteringMode[] transitionOrder =
			[
				GitFilteringMode.None,
				GitFilteringMode.TrackedFilesOnly,
				GitFilteringMode.Staged,
				GitFilteringMode.Changes,
				GitFilteringMode.RespectGitIgnore
			];
			foreach (var mode in transitionOrder)
			{
				await SelectAndApplyGitModeAsync(repositoryWindow, mode);
				Assert.Equal(
					mode,
					UiTestDriver.GetViewModel(repositoryWindow).SelectedGitFilteringModeOption?.Mode);
			}

			await SelectAndApplyGitModeAsync(repositoryWindow, GitFilteringMode.Changes);
			await UiTestDriver.WaitForConditionAsync(
				repositoryWindow,
				() => UiTestDriver.GetViewModel(repositoryWindow).PathIgnoreOptions.Count > 0,
				"the changes scope to expose at least one path filter");
			var all = UiTestDriver.GetRequiredControl<CheckBox>(repositoryWindow, "IgnoreAllCheckBox");
			await UiTestDriver.ClickAsync(repositoryWindow, all);
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(repositoryWindow);
			await UiTestDriver.ClickApplySettingsAsync(repositoryWindow);
			Assert.Equal(
				GitFilteringMode.Changes,
				UiTestDriver.GetViewModel(repositoryWindow).SelectedGitFilteringModeOption?.Mode);
			Assert.DoesNotContain(
				UiTestDriver.GetViewModel(repositoryWindow).PathIgnoreOptions,
				static option => option.IsChecked);

			await UiTestDriver.ClickAsync(repositoryWindow, all);
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(repositoryWindow);
			await UiTestDriver.ClickApplySettingsAsync(repositoryWindow);
			Assert.Equal(
				GitFilteringMode.Changes,
				UiTestDriver.GetViewModel(repositoryWindow).SelectedGitFilteringModeOption?.Mode);
			Assert.All(
				UiTestDriver.GetViewModel(repositoryWindow).PathIgnoreOptions,
				static option => Assert.True(option.IsChecked));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(repositoryWindow);
		}

		using var localFolder = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var localWindow = await UiTestDriver.CreateLoadedMainWindowAsync(localFolder);
		try
		{
			Assert.Equal(
				[GitFilteringMode.None, GitFilteringMode.RespectGitIgnore],
				UiTestDriver.GetViewModel(localWindow).GitFilteringModes
					.Select(static option => option.Mode));
			Assert.DoesNotContain(
				UiTestDriver.GetViewModel(localWindow).GitFilteringModes,
				static option => option.Mode is GitFilteringMode.Staged or GitFilteringMode.Changes);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(localWindow);
		}
	}

	[AvaloniaFact]
	public async Task NestedRepositoryScopesRemainAvailableAndProjectTheirGitState()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateDefault();
		var nestedRoot = Path.Combine(project.RootPath, "nested-repository");
		Directory.CreateDirectory(nestedRoot);
		await File.WriteAllTextAsync(
			Path.Combine(nestedRoot, "Baseline.cs"),
			"class Baseline {}\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(nestedRoot);
		await File.WriteAllTextAsync(
			Path.Combine(nestedRoot, "Staged.cs"),
			"class Staged {}\n",
			TestContext.Current.CancellationToken);
		RunGit(nestedRoot, "add", "--", "Staged.cs");
		await File.WriteAllTextAsync(
			Path.Combine(nestedRoot, "Untracked.txt"),
			"untracked\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			Assert.Contains(
				UiTestDriver.GetViewModel(window).GitFilteringModes,
				static option => option.Mode == GitFilteringMode.Staged);
			Assert.Contains(
				UiTestDriver.GetViewModel(window).GitFilteringModes,
				static option => option.Mode == GitFilteringMode.Changes);

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			Assert.Equal(
				GitFilteringMode.Staged,
				UiTestDriver.GetViewModel(window).SelectedGitFilteringModeOption?.Mode);
			await WaitForProjectTreePathStateAsync(
				window,
				exists: true,
				"nested-repository",
				"Staged.cs");
			await WaitForProjectTreePathStateAsync(
				window,
				exists: false,
				"nested-repository",
				"Untracked.txt");

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Changes);
			Assert.Equal(
				GitFilteringMode.Changes,
				UiTestDriver.GetViewModel(window).SelectedGitFilteringModeOption?.Mode);
			await WaitForProjectTreePathStateAsync(
				window,
				exists: true,
				"nested-repository",
				"Staged.cs");
			await WaitForProjectTreePathStateAsync(
				window,
				exists: true,
				"nested-repository",
				"Untracked.txt");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ManualNestedSelectionDoesNotQueryAnUnselectedBrokenOuterRepository()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateDefault();
		Directory.CreateDirectory(Path.Combine(project.RootPath, ".git"));
		var nestedRoot = Path.Combine(project.RootPath, "nested-repository");
		Directory.CreateDirectory(nestedRoot);
		await File.WriteAllTextAsync(
			Path.Combine(nestedRoot, "Baseline.cs"),
			"class Baseline {}\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(nestedRoot);
		await File.WriteAllTextAsync(
			Path.Combine(nestedRoot, "Staged.cs"),
			"class Staged {}\n",
			TestContext.Current.CancellationToken);
		RunGit(nestedRoot, "add", "--", "Staged.cs");
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await SetSingleTopLevelSelectionAsync(window, "nested-repository");
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);

			Assert.Equal(
				GitFilteringMode.Staged,
				UiTestDriver.GetViewModel(window).SelectedGitFilteringModeOption?.Mode);
			await WaitForProjectTreePathStateAsync(
				window,
				exists: true,
				"nested-repository",
				"Staged.cs");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ManualNestedSelectionRestrictsStagedExtensionsAndIgnoreCounts()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var selectedDirectory = Path.Combine(project.RootPath, "container", "selected");
		var siblingDirectory = Path.Combine(project.RootPath, "container", "sibling");
		Directory.CreateDirectory(selectedDirectory);
		Directory.CreateDirectory(siblingDirectory);
		var selectedPath = Path.Combine(selectedDirectory, "Selected.cs");
		var siblingExtensionPath = Path.Combine(siblingDirectory, "Sibling.xyz");
		var siblingDotFilePath = Path.Combine(siblingDirectory, ".scope-noise");
		await File.WriteAllTextAsync(
			selectedPath,
			"class Selected {}\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			siblingExtensionPath,
			"sibling\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			siblingDotFilePath,
			"dot file\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(project.RootPath);
		await File.AppendAllTextAsync(
			selectedPath,
			"// staged\n",
			TestContext.Current.CancellationToken);
		await File.AppendAllTextAsync(
			siblingExtensionPath,
			"staged\n",
			TestContext.Current.CancellationToken);
		await File.AppendAllTextAsync(
			siblingDotFilePath,
			"staged\n",
			TestContext.Current.CancellationToken);
		RunGit(project.RootPath, "add", "--all");
		var provider = RecordingGitScopePathProvider.Available(
			[selectedPath, siblingExtensionPath, siblingDotFilePath]);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });

		try
		{
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForExtensionStateAsync(window, ".xyz", visible: true);
			Assert.Contains(
				UiTestDriver.GetViewModel(window).PathIgnoreOptions,
				static option => option.Id == IgnoreOptionId.DotFiles);
			var providerCallCount = provider.CallCount;

			await SetSingleTreePathSelectionAsync(window, "container", "selected");

			await WaitForExtensionStateAsync(window, ".cs", visible: true);
			await WaitForExtensionStateAsync(window, ".xyz", visible: false);
			Assert.DoesNotContain(
				UiTestDriver.GetViewModel(window).PathIgnoreOptions,
				static option => option.Id == IgnoreOptionId.DotFiles);
			Assert.Equal(providerCallCount, provider.CallCount);

			await window.Dispatcher.InvokeAsync(() =>
				Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes).IsChecked = true);
			await WaitForExtensionStateAsync(window, ".xyz", visible: true);
			Assert.Contains(
				UiTestDriver.GetViewModel(window).PathIgnoreOptions,
				static option => option.Id == IgnoreOptionId.DotFiles);
			Assert.Equal(providerCallCount, provider.CallCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ScopedPresentationExcludesFilesAndImpactCountsFromUncheckedTopLevelRoot()
	{
		using var project = UiTestProject.CreateDefault();
		var selectedRoot = Path.Combine(project.RootPath, "selected-root");
		var excludedRoot = Path.Combine(project.RootPath, "excluded-root");
		Directory.CreateDirectory(selectedRoot);
		Directory.CreateDirectory(excludedRoot);
		var includedPath = Path.Combine(selectedRoot, "Included.cs");
		var excludedExtensionPath = Path.Combine(excludedRoot, "Only.xyz");
		var excludedDotFilePath = Path.Combine(excludedRoot, ".scoped-noise");
		await File.WriteAllTextAsync(
			includedPath,
			"class Included {}\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			excludedExtensionPath,
			"excluded\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			excludedDotFilePath,
			"excluded dot file\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			var host = (IRefreshTreePipelineHost)window;
			var broadInput = Assert.IsType<TreeRefreshInput>(host.CaptureTreeRefreshInput(true));
			var initialResult = host.BuildTree(broadInput, TestContext.Current.CancellationToken);
			var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(initialResult.Inventory);
			Assert.Contains(
				inventory.Entries,
				entry => PathComparer.Default.Equals(entry.FullPath, excludedExtensionPath));

			var selectedRoots = new HashSet<string>(["selected-root"], PathComparer.Default);
			var availableRoots = new HashSet<string>(
				["selected-root", "excluded-root"],
				PathComparer.Default);
			var scopedPaths = new HashSet<string>(
				[includedPath, excludedExtensionPath, excludedDotFilePath],
				PathComparer.Default);
			var scopedInput = broadInput with
			{
				Options = broadInput.Options with { AllowedRootFolders = availableRoots },
				GitMode = GitFilteringMode.Staged,
				GitScope = new GitScopePathResult(
					true,
					scopedPaths,
					DeletedPathCount: 0),
				GitScopePresentation = null,
				AvailableRootFolders = availableRoots,
				TreeInventory = inventory
			};

			var broadResult = host.BuildTree(scopedInput, TestContext.Current.CancellationToken);
			var broadProjection = Assert.IsType<GitScopePresentationProjection>(broadResult.GitScopePresentation);
			Assert.Contains(
				broadProjection.AvailableExtensions,
				static extension => string.Equals(extension, ".xyz", StringComparison.OrdinalIgnoreCase));
			Assert.True(broadProjection.IgnoreOptionCounts.DotFiles > 0);

			var restrictedResult = host.BuildTree(
				scopedInput with
				{
					Options = scopedInput.Options with { AllowedRootFolders = selectedRoots }
				},
				TestContext.Current.CancellationToken);
			var restrictedProjection = Assert.IsType<GitScopePresentationProjection>(
				restrictedResult.GitScopePresentation);
			Assert.Contains(
				restrictedProjection.AvailableExtensions,
				static extension => string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain(
				restrictedProjection.AvailableExtensions,
				static extension => string.Equals(extension, ".xyz", StringComparison.OrdinalIgnoreCase));
			Assert.Equal(0, restrictedProjection.IgnoreOptionCounts.DotFiles);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task RepositoryCreatedAfterOpenEnablesMomentaryModesAfterRefresh()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			Assert.DoesNotContain(
				UiTestDriver.GetViewModel(window).GitFilteringModes,
				static option => option.Mode is GitFilteringMode.Staged or GitFilteringMode.Changes);

			InitializeRepository(project.RootPath);
			var stagedPath = Path.Combine(project.RootPath, "CreatedAfterOpen.cs");
			await File.WriteAllTextAsync(
				stagedPath,
				"class CreatedAfterOpen {}\n",
				TestContext.Current.CancellationToken);
			RunGit(project.RootPath, "add", "--", "CreatedAfterOpen.cs");
			await UiTestDriver.RefreshProjectAsync(window);

			var modes = UiTestDriver.GetViewModel(window).GitFilteringModes;
			Assert.Contains(modes, static option => option.Mode == GitFilteringMode.Staged);
			Assert.Contains(modes, static option => option.Mode == GitFilteringMode.Changes);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForProjectTreePathStateAsync(window, exists: true, "CreatedAfterOpen.cs");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task GitModeRefreshMatrix_ReconcilesExternalStateAndRemembersUncheckedExtension()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "Known.md"),
			"# Known\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: true);
			await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".md");
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: false);
			await UiTestDriver.ClickApplySettingsAsync(window);

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Known.md");

			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "Staged.cs"),
				"class Staged {}\n",
				TestContext.Current.CancellationToken);
			RunGit(project.RootPath, "add", "--", "Staged.cs");
			await UiTestDriver.RefreshProjectAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: true, "Staged.cs");

			RunGit(project.RootPath, "reset", "--", "Staged.cs");
			await UiTestDriver.RefreshProjectAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Staged.cs");

			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "Program.cs"),
				"Console.WriteLine(\"modified\");\n",
				TestContext.Current.CancellationToken);
			await UiTestDriver.RefreshProjectAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Program.cs");

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Changes);
			await WaitForProjectTreePathStateAsync(window, exists: true, "Program.cs");
			await WaitForProjectTreePathStateAsync(window, exists: true, "Staged.cs");

			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "Local.cs"),
				"class Local {}\n",
				TestContext.Current.CancellationToken);
			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "private.user"),
				"ignored\n",
				TestContext.Current.CancellationToken);
			await UiTestDriver.RefreshProjectAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: true, "Local.cs");
			await WaitForProjectTreePathStateAsync(window, exists: false, "private.user");

			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "Known.md"),
				"# Known modified\n",
				TestContext.Current.CancellationToken);
			await UiTestDriver.RefreshProjectAsync(window);
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: false);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Known.md");

			RunGit(project.RootPath, "add", "--", "Known.md");
			await UiTestDriver.RefreshProjectAsync(window);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: false);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Known.md");

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.None);
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: false);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Changes);
			await WaitForExtensionStateAsync(window, ".md", visible: true, isChecked: false);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ExplicitExtensionSet_GitScopeRefreshKeepsNewTypeUncheckedAndOutOfTree()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			var explicitExtensions = UiTestDriver.GetViewModel(window).Extensions
				.Select(static option => option.Name)
				.ToArray();
			Assert.NotEmpty(explicitExtensions);
			Assert.True(UiTestDriver.GetViewModel(window).AllExtensionsChecked);
			await ApplySelectionOverridesAsync(
				window,
				project.RootPath,
				explicitExtensions,
				GitFilteringMode.Changes);

			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, "Unknown.scope"),
				"new type\n",
				TestContext.Current.CancellationToken);
			await RefreshProjectSelectionAsync(window, project.RootPath);

			await WaitForExtensionStateAsync(window, ".scope", visible: true, isChecked: false);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: false, "Unknown.scope");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task BroadTreeSelection_SurvivesStagedScopeRoundTripWithConsistentMetrics()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var firstPath = Path.Combine(project.RootPath, "A.scope");
		var secondPath = Path.Combine(project.RootPath, "B.scope");
		await File.WriteAllTextAsync(
			firstPath,
			"A_SCOPE_CONTENT\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			secondPath,
			"B_SCOPE_CONTENT\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(project.RootPath);
		await File.AppendAllTextAsync(
			firstPath,
			"staged change\n",
			TestContext.Current.CancellationToken);
		RunGit(project.RootPath, "add", "--", "A.scope");
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await SetBroadTreeSelectionAsync(window, "B.scope");

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForProjectTreePathStateAsync(window, exists: true, "A.scope");
			await WaitForProjectTreePathStateAsync(window, exists: false, "B.scope");

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.None);
			await WaitForProjectTreePathStateAsync(window, exists: true, "A.scope");
			await WaitForProjectTreePathStateAsync(window, exists: true, "B.scope");
			Assert.True(FindProjectTreeNode(window, "A.scope")?.IsChecked is true);
			Assert.True(FindProjectTreeNode(window, "B.scope")?.IsChecked is false);

			var content = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
				window,
				PreviewContentMode.Content,
				TestContext.Current.CancellationToken);
			Assert.Contains("A_SCOPE_CONTENT", content, StringComparison.Ordinal);
			Assert.DoesNotContain("B_SCOPE_CONTENT", content, StringComparison.Ordinal);

			var expectedMetrics = await UiTestDriver.ComputeAppliedExportMetricsAsync(
				window,
				TestContext.Current.CancellationToken);
			await UiTestDriver.WaitForStatusMetricsAsync(
				window,
				expectedMetrics.TreeMetrics,
				expectedMetrics.ContentMetrics);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task StagedTreeOverride_MergesBackIntoBroadSelectionIntent()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var firstPath = Path.Combine(project.RootPath, "A.scope");
		var secondPath = Path.Combine(project.RootPath, "B.scope");
		await File.WriteAllTextAsync(
			firstPath,
			"A_SCOPE_CONTENT\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			secondPath,
			"B_SCOPE_CONTENT\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(project.RootPath);
		await File.AppendAllTextAsync(
			firstPath,
			"staged change\n",
			TestContext.Current.CancellationToken);
		RunGit(project.RootPath, "add", "--", "A.scope");
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await SetBroadTreeSelectionAsync(window, "B.scope");
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await SetTreeNodeCheckedAsync(window, "A.scope", isChecked: false);

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.None);
			Assert.True(FindProjectTreeNode(window, "A.scope")?.IsChecked is false);
			Assert.True(FindProjectTreeNode(window, "B.scope")?.IsChecked is false);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task StagedHiddenFile_FilterThenExtensionPriorityRecoversWithoutRevivingExplicitType()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		Directory.CreateDirectory(Path.Combine(project.RootPath, ".hidden"));
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, ".hidden", "Scoped.cs"),
			"class Scoped {}\n",
			TestContext.Current.CancellationToken);
		RunGit(project.RootPath, "add", "--", ".hidden/Scoped.cs");
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await ApplySelectionOverridesAsync(
				window,
				project.RootPath,
				[],
				GitFilteringMode.Staged);
			var pathOption = Assert.Single(UiTestDriver.GetViewModel(window).PathIgnoreOptions);
			Assert.Equal(IgnoreOptionId.DotFolders, pathOption.Id);
			Assert.True(pathOption.IsChecked);
			Assert.Empty(UiTestDriver.GetViewModel(window).Extensions);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden", "Scoped.cs");

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			await WaitForExtensionStateAsync(window, ".cs", visible: true, isChecked: false);
			await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden", "Scoped.cs");

			await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".cs");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: true, ".hidden", "Scoped.cs");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task UnavailableGitScope_RestoresStableModeTreeAndSelectionPresentation()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var provider = RecordingGitScopePathProvider.Unavailable();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var stableMode = viewModel.SelectedGitFilteringModeOption?.Mode;
			var stableTree = UiTestDriver.GetCurrentTreeIdentity(window);
			var stableExtensions = SnapshotExtensions(viewModel);
			var stablePathOptions = SnapshotPathOptions(viewModel);
			var stableAllExtensions = viewModel.AllExtensionsChecked;
			var stableAllIgnore = viewModel.AllIgnoreChecked;

			await RequestGitFilteringModeAsync(window, GitFilteringMode.Staged);
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			Assert.Equal(1, provider.CallCount);
			Assert.Equal(stableMode, viewModel.SelectedGitFilteringModeOption?.Mode);
			Assert.Same(stableTree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Equal(stableExtensions, SnapshotExtensions(viewModel));
			Assert.Equal(stablePathOptions, SnapshotPathOptions(viewModel));
			Assert.Equal(stableAllExtensions, viewModel.AllExtensionsChecked);
			Assert.Equal(stableAllIgnore, viewModel.AllIgnoreChecked);
			Assert.Contains(
				viewModel.ToastItems,
				static toast => toast.Message.Contains("Git", StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task UnavailableGitScopeDuringLiveRefresh_RestoresScopedSelectionPresentation()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "Live.cs"),
			"class Live {}\n",
			TestContext.Current.CancellationToken);
		var provider = RecordingGitScopePathProvider.Available(
			Directory.EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories)
				.Where(path => !path.Contains(
					$"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase)));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });

		try
		{
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Changes);
			var viewModel = UiTestDriver.GetViewModel(window);
			var stableTree = UiTestDriver.GetCurrentTreeIdentity(window);
			var stableExtensions = SnapshotExtensions(viewModel);
			var stablePathOptions = SnapshotPathOptions(viewModel);
			var stableProviderCallCount = provider.CallCount;
			provider.SetAvailable(false);

			await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".cs");
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

			Assert.Equal(stableProviderCallCount + 1, provider.CallCount);
			Assert.Equal(
				GitFilteringMode.Changes,
				viewModel.SelectedGitFilteringModeOption?.Mode);
			Assert.Same(stableTree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Equal(stableExtensions, SnapshotExtensions(viewModel));
			Assert.Equal(stablePathOptions, SnapshotPathOptions(viewModel));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, GitFilteringMode.None)]
	public async Task RemovedRepositoryDuringMomentaryScope_FallsBackToAvailablePersistentMode(
		GitFilteringMode preferredMode,
		GitFilteringMode expectedFallback)
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var stagedPath = Path.Combine(project.RootPath, "Staged.cs");
		var fallbackPath = Path.Combine(project.RootPath, "Fallback.cs");
		await File.WriteAllTextAsync(
			stagedPath,
			"class Staged {}\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			fallbackPath,
			"class Fallback {}\n",
			TestContext.Current.CancellationToken);
		RunGit(project.RootPath, "add", "--", "Staged.cs");
		var provider = RecordingGitScopePathProvider.Available([stagedPath]);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });
		var detachedGitPath = Path.Combine(
			Path.GetTempPath(),
			$"devprojex-git-metadata-{Guid.NewGuid():N}");

		try
		{
			if (UiTestDriver.GetViewModel(window).SelectedGitFilteringModeOption?.Mode != preferredMode)
				await SelectAndApplyGitModeAsync(window, preferredMode);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);
			await WaitForProjectTreePathStateAsync(window, exists: true, "Staged.cs");
			await WaitForProjectTreePathStateAsync(window, exists: false, "Fallback.cs");
			var providerCallCount = provider.CallCount;

			Directory.Move(Path.Combine(project.RootPath, ".git"), detachedGitPath);
			provider.SetAvailable(false);
			await RefreshProjectSelectionAsync(window, project.RootPath);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await WaitForProjectTreePathStateAsync(window, exists: true, "Fallback.cs");

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(providerCallCount + 1, provider.CallCount);
			Assert.Equal(expectedFallback, viewModel.SelectedGitFilteringModeOption?.Mode);
			Assert.Collection(
				viewModel.GitFilteringModes,
				static option => Assert.Equal(GitFilteringMode.None, option.Mode),
				static option => Assert.Equal(GitFilteringMode.RespectGitIgnore, option.Mode));
			var persistedStates = GetSelectionCoordinator(window)
				.SnapshotIgnoreOptionStatesForPersistence();
			Assert.NotNull(persistedStates);
			Assert.Equal(
				preferredMode == GitFilteringMode.RespectGitIgnore,
				persistedStates![IgnoreOptionId.UseGitIgnore]);
			Assert.Equal(
				preferredMode == GitFilteringMode.TrackedFilesOnly,
				persistedStates[IgnoreOptionId.TrackedGitFilesOnly]);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
			var projectGitPath = Path.Combine(project.RootPath, ".git");
			if (Directory.Exists(detachedGitPath) && !Directory.Exists(projectGitPath))
				Directory.Move(detachedGitPath, projectGitPath);
		}
	}

	[AvaloniaFact]
	public async Task PendingGitScope_ReusedUntilSuccessfulTreePublication()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var provider = RecordingGitScopePathProvider.Available(
			Directory.EnumerateFiles(project.RootPath, "*", SearchOption.AllDirectories)
				.Where(path => !path.Contains(
					$"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase)));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });

		try
		{
			await UiTestDriver.SelectGitFilteringModeAsync(window, GitFilteringMode.Staged);
			await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
			Assert.Equal(1, provider.CallCount);

			var firstInput = CaptureTreeRefreshInput(window);
			var secondInput = CaptureTreeRefreshInput(window);
			Assert.NotNull(GetGitScope(firstInput));
			Assert.NotNull(GetGitScope(secondInput));
			BuildTree(window, secondInput);
			Assert.Equal(1, provider.CallCount);

			await UiTestDriver.ClickApplySettingsAsync(window);
			Assert.Null(GetGitScope(CaptureTreeRefreshInput(window)));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task SelectAndApplyGitModeAsync(
		MainWindow window,
		GitFilteringMode mode)
	{
		await UiTestDriver.SelectGitFilteringModeAsync(window, mode);
		await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
		await UiTestDriver.ClickApplySettingsAsync(window);
	}

	private static async Task RequestGitFilteringModeAsync(
		MainWindow window,
		GitFilteringMode mode)
	{
		var viewModel = UiTestDriver.GetViewModel(window);
		var option = Assert.Single(viewModel.GitFilteringModes, candidate => candidate.Mode == mode);
		var comboBox = UiTestDriver.GetRequiredControl<ComboBox>(window, "GitFilteringModeComboBox");
		await window.Dispatcher.InvokeAsync(() => comboBox.SelectedItem = option);
	}

	private static async Task ApplySelectionOverridesAsync(
		MainWindow window,
		string projectPath,
		IReadOnlyCollection<string> selectedExtensions,
		GitFilteringMode gitMode)
	{
		var coordinator = GetSelectionCoordinator(window);
		var method = typeof(SelectionSyncCoordinator).GetMethod(
			"ApplySelectionOverrides",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		Assert.NotNull(method);

		await window.Dispatcher.InvokeAsync(() =>
		{
			var changed = method!.Invoke(
				coordinator,
				[projectPath, selectedExtensions, null, gitMode, false, false]);
			Assert.True(Assert.IsType<bool>(changed));
		});
		await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
	}

	private static async Task RefreshProjectSelectionAsync(MainWindow window, string projectPath)
	{
		var coordinator = GetSelectionCoordinator(window);
		coordinator.InvalidateFileSystemCaches();
		await coordinator.RefreshProjectSelectionAsync(
			projectPath,
			TestContext.Current.CancellationToken);
		await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
	}

	private static SelectionSyncCoordinator GetSelectionCoordinator(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_selectionCoordinator",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		return Assert.IsType<SelectionSyncCoordinator>(field?.GetValue(window));
	}

	private static string[] SnapshotExtensions(MainWindowViewModel viewModel) =>
		viewModel.Extensions
			.Select(static option => $"{option.Name}|{option.IsChecked}")
			.ToArray();

	private static string[] SnapshotPathOptions(MainWindowViewModel viewModel) =>
		viewModel.PathIgnoreOptions
			.Select(static option => $"{option.Id}|{option.IsChecked}|{option.Label}")
			.ToArray();

	private static object CaptureTreeRefreshInput(MainWindow window)
	{
		var hostInterface = typeof(MainWindow).GetInterfaces().Single(type =>
			type.Name == "IRefreshTreePipelineHost");
		var capture = hostInterface.GetMethod("CaptureTreeRefreshInput");
		Assert.NotNull(capture);
		var input = capture!.Invoke(window, [true]);
		Assert.NotNull(input);
		return input;
	}

	private static object? GetGitScope(object input) =>
		input.GetType().GetProperty("GitScope")?.GetValue(input);

	private static void BuildTree(MainWindow window, object input)
	{
		var hostInterface = typeof(MainWindow).GetInterfaces().Single(type =>
			type.Name == "IRefreshTreePipelineHost");
		var build = hostInterface.GetMethod("BuildTree");
		Assert.NotNull(build);
		Assert.NotNull(build!.Invoke(window, [input, TestContext.Current.CancellationToken]));
	}

	private static async Task WaitForExtensionStateAsync(
		MainWindow window,
		string extension,
		bool visible,
		bool? isChecked = null)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() =>
			{
				var option = UiTestDriver.GetViewModel(window).Extensions.FirstOrDefault(candidate =>
					string.Equals(candidate.Name, extension, StringComparison.OrdinalIgnoreCase));
				return visible
					? option is not null && (isChecked is null || option.IsChecked == isChecked)
					: option is null;
			},
			$"extension '{extension}' to become visible={visible} checked={isChecked?.ToString() ?? "<any>"}");
	}

	private static async Task WaitForProjectTreePathStateAsync(
		MainWindow window,
		bool exists,
		params string[] relativeDisplayPath)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() => ProjectTreeContainsPath(window, relativeDisplayPath) == exists,
			$"project tree path '{string.Join("/", relativeDisplayPath)}' to exist={exists}");
	}

	private static async Task SetBroadTreeSelectionAsync(
		MainWindow window,
		string uncheckedFile)
	{
		await window.Dispatcher.InvokeAsync(() =>
		{
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsChecked = true;
			var uncheckedNode = FindProjectTreeNode(window, uncheckedFile);
			Assert.NotNull(uncheckedNode);
			uncheckedNode!.IsChecked = false;
		});
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static async Task SetTreeNodeCheckedAsync(
		MainWindow window,
		string displayName,
		bool isChecked)
	{
		await window.Dispatcher.InvokeAsync(() =>
		{
			var node = FindProjectTreeNode(window, displayName);
			Assert.NotNull(node);
			node!.IsChecked = isChecked;
		});
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	[AvaloniaFact]
	public async Task ZeroCheckedPathsUseTheWholeTreeGitScope()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		var includedPath = Path.Combine(project.RootPath, "Whole.scope");
		await File.WriteAllTextAsync(
			includedPath,
			"whole tree scope\n",
			TestContext.Current.CancellationToken);
		InitializeRepository(project.RootPath);
		var provider = RecordingGitScopePathProvider.Available([includedPath]);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { GitScopePathProvider = provider });

		try
		{
			await window.Dispatcher.InvokeAsync(() =>
			{
				var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
				root.IsChecked = true;
				root.IsChecked = false;
			});
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);

			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(GitFilteringMode.Staged, viewModel.SelectedGitFilteringModeOption?.Mode);
			Assert.Contains(
				Assert.Single(viewModel.TreeNodes).Children,
				static node => string.Equals(node.DisplayName, "Whole.scope", StringComparison.Ordinal));
			Assert.Contains(
				viewModel.Extensions,
				static option => string.Equals(option.Name, ".scope", StringComparison.OrdinalIgnoreCase));
			Assert.Equal(1, provider.CallCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task BuildTreeScopedPresentationHonorsSelectedPathFrontier()
	{
		using var project = UiTestProject.CreateDefault();
		var selectedDirectory = Path.Combine(project.RootPath, "container", "selected");
		var siblingDirectory = Path.Combine(project.RootPath, "container", "sibling");
		Directory.CreateDirectory(selectedDirectory);
		Directory.CreateDirectory(siblingDirectory);
		var selectedPath = Path.Combine(selectedDirectory, "Selected.cs");
		var siblingExtensionPath = Path.Combine(siblingDirectory, "Sibling.xyz");
		var siblingDotFilePath = Path.Combine(siblingDirectory, ".scope-noise");
		await File.WriteAllTextAsync(
			selectedPath,
			"class Selected {}\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			siblingExtensionPath,
			"sibling\n",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			siblingDotFilePath,
			"dot file\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			var host = (IRefreshTreePipelineHost)window;
			var input = Assert.IsType<TreeRefreshInput>(host.CaptureTreeRefreshInput(true));
			var initialResult = host.BuildTree(input, TestContext.Current.CancellationToken);
			var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(initialResult.Inventory);
			var scopePaths = new HashSet<string>(
				[selectedPath, siblingExtensionPath, siblingDotFilePath],
				PathComparer.Default);
			var frontier = new HashSet<string>([selectedDirectory], StringComparer.Ordinal);

			var result = host.BuildTree(
				input with
				{
					GitMode = GitFilteringMode.Staged,
					GitScope = new GitScopePathResult(true, scopePaths, DeletedPathCount: 0),
					GitScopePresentation = null,
					TreeInventory = inventory,
					GitRepositoryScopePaths = frontier
				},
				TestContext.Current.CancellationToken);
			var projection = Assert.IsType<GitScopePresentationProjection>(result.GitScopePresentation);

			Assert.Contains(
				projection.AvailableExtensions,
				static extension => string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain(
				projection.AvailableExtensions,
				static extension => string.Equals(extension, ".xyz", StringComparison.OrdinalIgnoreCase));
			Assert.Equal(0, projection.IgnoreOptionCounts.DotFiles);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ExplicitNonePersistsBehindASubsequentMomentaryMode()
	{
		EnsureGitAvailable();
		using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
		InitializeRepository(project.RootPath);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

		try
		{
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.TrackedFilesOnly);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.None);
			await SelectAndApplyGitModeAsync(window, GitFilteringMode.Staged);

			var persisted = GetSelectionCoordinator(window)
				.SnapshotIgnoreOptionStatesForPersistence();
			Assert.NotNull(persisted);
			Assert.False(persisted![IgnoreOptionId.UseGitIgnore]);
			Assert.False(persisted[IgnoreOptionId.TrackedGitFilesOnly]);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task SetSingleTopLevelSelectionAsync(
		MainWindow window,
		string selectedDisplayName)
	{
		await window.Dispatcher.InvokeAsync(() =>
		{
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsChecked = false;
			var selected = Assert.Single(root.Children, node =>
				string.Equals(node.DisplayName, selectedDisplayName, StringComparison.Ordinal));
			selected.IsChecked = true;
		});
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static async Task SetSingleTreePathSelectionAsync(
		MainWindow window,
		params string[] relativeDisplayPath)
	{
		Assert.NotEmpty(relativeDisplayPath);
		await window.Dispatcher.InvokeAsync(() =>
		{
			var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
			root.IsChecked = false;
			var current = root;
			foreach (var segment in relativeDisplayPath)
			{
				current = Assert.Single(current.Children, node =>
					string.Equals(node.DisplayName, segment, StringComparison.Ordinal));
			}
			current.IsChecked = true;
		});
		await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
	}

	private static TreeNodeViewModel? FindProjectTreeNode(
		MainWindow window,
		string displayName)
	{
		var roots = UiTestDriver.GetViewModel(window).TreeNodes;
		if (roots.Count != 1)
			return null;

		return roots[0].Children.FirstOrDefault(node =>
			string.Equals(node.DisplayName, displayName, StringComparison.Ordinal));
	}

	private static bool ProjectTreeContainsPath(
		MainWindow window,
		IReadOnlyList<string> relativeDisplayPath)
	{
		var roots = UiTestDriver.GetViewModel(window).TreeNodes;
		if (roots.Count != 1)
			return false;

		IEnumerable<TreeNodeViewModel> current = roots[0].Children;
		foreach (var segment in relativeDisplayPath)
		{
			var match = current.FirstOrDefault(node =>
				string.Equals(node.DisplayName, segment, StringComparison.Ordinal));
			if (match is null)
				return false;
			current = match.Children;
		}
		return true;
	}

	private static void InitializeRepository(string rootPath)
	{
		RunGit(rootPath, "init", "--quiet");
		RunGit(rootPath, "add", "--all");
		RunGit(
			rootPath,
			"-c", "user.name=DevProjex Tests",
			"-c", "user.email=tests@devprojex.invalid",
			"commit", "--quiet", "-m", "baseline");
	}

	private static void EnsureGitAvailable()
	{
		var startInfo = CreateGitStartInfo(Environment.CurrentDirectory);
		startInfo.ArgumentList.Add("--version");
		try
		{
			using var process = Process.Start(startInfo);
			if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
				Assert.Skip("Git is not available in this test environment.");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is not available in this test environment.");
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = CreateGitStartInfo(workingDirectory);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ??
			throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(20_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("Git command did not complete within 20 seconds.");
		}
		Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
	}

	private static ProcessStartInfo CreateGitStartInfo(string workingDirectory) =>
		new("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

	private sealed class RecordingGitScopePathProvider(
		bool isAvailable,
		IReadOnlySet<string> includedPaths) : IGitScopePathProvider
	{
		private int _callCount;
		private bool _isAvailable = isAvailable;

		public int CallCount => Volatile.Read(ref _callCount);

		public static RecordingGitScopePathProvider Unavailable() =>
			new(false, new HashSet<string>(PathComparer.Default));

		public static RecordingGitScopePathProvider Available(IEnumerable<string> paths) =>
			new(true, paths.ToHashSet(PathComparer.Default));

		public void SetAvailable(bool value) => Volatile.Write(ref _isAvailable, value);

		public Task<GitScopePathResult> ResolveAsync(
			string projectRoot,
			GitFilteringMode mode,
			string? diffRange,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _callCount);
			return Task.FromResult(Volatile.Read(ref _isAvailable)
				? new GitScopePathResult(true, includedPaths, DeletedPathCount: 0)
				: GitScopePathResult.Unavailable("test provider unavailable"));
		}
	}
}
