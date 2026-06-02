using Avalonia.Controls;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

public sealed class MainWindowCoordinatorRefactorTests
{
    [Theory]
    [InlineData("Receiving objects: 42%", 42)]
    [InlineData("99%", 99)]
    [InlineData("Resolving deltas: 12.5%", 12.5)]
    public void GitProgressStatusParser_ParsesTrailingPercent(string status, double expected)
    {
        Assert.True(GitProgressStatusParser.TryParseTrailingPercent(status, out var percent));
        Assert.Equal(expected, percent);
    }

    [Fact]
    public void TaskbarProgressCoordinator_SyncsStatusAndGitCloneProgress()
    {
        var viewModel = CreateViewModel();
        var taskbar = new RecordingTaskbarProgressService();
        var coordinator = new TaskbarProgressCoordinator(viewModel, taskbar);

        viewModel.StatusBusy = true;
        viewModel.StatusProgressIsIndeterminate = true;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Indeterminate, taskbar.LastState);

        viewModel.StatusProgressIsIndeterminate = false;
        viewModel.StatusProgressValue = 64;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(64, taskbar.LastPercent);

        coordinator.BeginGitClone();
        coordinator.UpdateGitClone("Receiving objects: 77%");

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(77, taskbar.LastPercent);

        coordinator.MarkGitCloneError();
        Assert.Equal(TaskbarProgressRecordingState.Error, taskbar.LastState);

        viewModel.StatusBusy = false;
        coordinator.CompleteGitClone();
        Assert.Equal(TaskbarProgressRecordingState.Clear, taskbar.LastState);
    }

    [Fact]
    public void StatusOperationCoordinator_TracksActiveOperationAndIgnoresStaleCompletion()
    {
        var viewModel = CreateViewModel();
        var coordinator = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);

        var first = coordinator.Begin("Loading", operationType: StatusOperationType.LoadProject);
        var second = coordinator.Begin("Preview", operationType: StatusOperationType.PreviewBuild);

        coordinator.Complete(first);

        Assert.True(viewModel.StatusBusy);
        Assert.Equal("Preview", viewModel.StatusOperationText);
        Assert.True(coordinator.IsActive(second));

        coordinator.UpdateProgress(42, "Preview 42%", second);

        Assert.False(viewModel.StatusProgressIsIndeterminate);
        Assert.Equal(42, viewModel.StatusProgressValue);
        Assert.Equal("Preview 42%", viewModel.StatusOperationText);

        coordinator.Complete(second);

        Assert.False(viewModel.StatusBusy);
        Assert.Equal(string.Empty, viewModel.StatusOperationText);
    }

    [Fact]
    public void StatusOperationCoordinator_LeavesMetricsVisibleWhenMetricsOperationIsActive()
    {
        var viewModel = CreateViewModel();
        var metricsActive = true;
        var coordinator = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => metricsActive,
            metricsOperationTextProvider: () => "Calculating data");

        var operationId = coordinator.Begin(
            "Calculating data",
            operationType: StatusOperationType.MetricsCalculation);

        coordinator.Complete(operationId);

        Assert.True(viewModel.StatusBusy);
        Assert.Equal("Calculating data", viewModel.StatusOperationText);

        metricsActive = false;
        coordinator.Complete(operationId);

        Assert.False(viewModel.StatusBusy);
    }

    [Fact]
    public void ProjectLoadCancellationCoordinator_AppliesExpectedFallback()
    {
        var coordinator = new ProjectLoadCancellationCoordinator();
        var resetCalled = false;
        ProjectLoadCancellationSnapshot? restored = null;

        Assert.False(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));

        var noPreviousProject = CreateProjectLoadSnapshot(hadLoadedProjectBefore: false);
        coordinator.Capture(noPreviousProject);

        Assert.True(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));
        Assert.True(resetCalled);
        Assert.Null(restored);

        resetCalled = false;
        var previousProject = CreateProjectLoadSnapshot(hadLoadedProjectBefore: true);
        coordinator.Capture(previousProject);

        Assert.True(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));
        Assert.False(resetCalled);
        Assert.Same(previousProject, restored);
    }

    [Fact]
    public void ProjectProfilePersistenceCoordinator_PersistsOnlyLocalFoldersAndFlushesPendingSave()
    {
        var viewModel = CreateViewModel();
        var store = new FlakyProjectProfileStore();
        var selectionCoordinator = CreateSelectionCoordinator(viewModel);
        var coordinator = new ProjectProfilePersistenceCoordinator(viewModel, selectionCoordinator, store);

        viewModel.ProjectSourceType = ProjectSourceType.GitClone;
        coordinator.PersistIfNeeded(@"C:\Repo");

        Assert.Equal(0, store.SaveAttempts);

        viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("docs", false));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".csv", false));

        store.FailNextSave = true;
        coordinator.PersistIfNeeded(@"C:\Project");

        Assert.Equal(1, store.SaveAttempts);
        Assert.False(store.HasProfile(@"C:\Project"));

        coordinator.FlushPending();

        Assert.Equal(2, store.SaveAttempts);
        Assert.True(store.TryLoadProfile(@"C:\Project", out var persisted));
        Assert.Equal(["src"], persisted.SelectedRootFolders);
        Assert.Equal([".cs"], persisted.SelectedExtensions);
        Assert.False(persisted.RootFolderStates!["docs"]);
        Assert.False(persisted.ExtensionStates![".csv"]);
    }

    private static ProjectLoadCancellationSnapshot CreateProjectLoadSnapshot(bool hadLoadedProjectBefore)
    {
        return new ProjectLoadCancellationSnapshot(
            HadLoadedProjectBefore: hadLoadedProjectBefore,
            Path: @"C:\Project",
            ProjectDisplayName: "Project",
            RepositoryUrl: null,
            Tree: null,
            ProjectSourceType: ProjectSourceType.LocalFolder,
            CurrentBranch: string.Empty,
            GitBranches: [],
            SettingsVisible: true,
            SearchVisible: false,
            FilterVisible: false,
            PreviewWorkspaceMode: PreviewWorkspaceMode.Off,
            StatusMetricsVisible: false,
            StatusTreeStatsText: string.Empty,
            StatusContentStatsText: string.Empty,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: true,
            AllIgnoreChecked: true,
            HasCompleteMetricsBaseline: false,
            RootFolders: [],
            Extensions: [],
            IgnoreOptions: []);
    }

    private static SelectionSyncCoordinator CreateSelectionCoordinator(MainWindowViewModel viewModel)
    {
        var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
        var scanner = new StubFileSystemScanner();
        var scanOptions = new ScanOptionsUseCase(scanner);
        var filterService = new FilterOptionSelectionService();
        var ignoreService = new IgnoreOptionsService(localization);

        return new SelectionSyncCoordinator(
            viewModel,
            scanOptions,
            filterService,
            ignoreService,
            (_, _, _) => new IgnoreRules(
                IgnoreHiddenFolders: false,
                IgnoreHiddenFiles: false,
                IgnoreDotFolders: false,
                IgnoreDotFiles: false,
                SmartIgnoredFolders: new HashSet<string>(),
                SmartIgnoredFiles: new HashSet<string>()),
            (_, _) => new IgnoreOptionsAvailability(false, false),
            _ => false,
            () => @"C:\Project");
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
        return new MainWindowViewModel(localization, new HelpContentProvider());
    }

    private static StubLocalizationCatalog CreateCatalog()
    {
        var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.En] = new Dictionary<string, string>
            {
                ["Settings.Ignore.SmartIgnore"] = "Smart ignore",
                ["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
                ["Settings.Ignore.HiddenFolders"] = "Hidden folders",
                ["Settings.Ignore.HiddenFiles"] = "Hidden files",
                ["Settings.Ignore.DotFolders"] = "dot folders",
                ["Settings.Ignore.DotFiles"] = "dot files",
                ["Settings.Ignore.ExtensionlessFiles"] = "Files without extension",
                ["Status.Operation.CalculatingData"] = "Calculating data"
            }
        };

        return new StubLocalizationCatalog(data);
    }

    private sealed class FlakyProjectProfileStore : IProjectProfileStore
    {
        private readonly Dictionary<string, ProjectSelectionProfile> _profiles = new(PathComparer.Default);

        public bool FailNextSave { get; set; }

        public int SaveAttempts { get; private set; }

        public bool EnsureStorageExists() => true;

        public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
        {
            if (_profiles.TryGetValue(localProjectPath, out profile!))
                return true;

            profile = new ProjectSelectionProfile([], [], []);
            return false;
        }

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
            => TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
        {
            _ = updatedUtc;
            SaveAttempts++;
            if (FailNextSave)
            {
                FailNextSave = false;
                return false;
            }

            _profiles[localProjectPath] = ProjectSelectionProfileBuilder.Clone(profile);
            return true;
        }

        public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
        {
            TrySaveProfile(localProjectPath, profile);
        }

        public void ClearAllProfiles()
        {
            _profiles.Clear();
        }

        public bool HasProfile(string path) => _profiles.ContainsKey(path);
    }

    private sealed class RecordingTaskbarProgressService : ITaskbarProgressService
    {
        public bool IsSupported => true;

        public TaskbarProgressRecordingState LastState { get; private set; } = TaskbarProgressRecordingState.None;

        public double LastPercent { get; private set; }

        public void Attach(Window window)
        {
            _ = window;
            LastState = TaskbarProgressRecordingState.Attached;
        }

        public void SetIndeterminate()
        {
            LastState = TaskbarProgressRecordingState.Indeterminate;
        }

        public void SetProgress(double percent)
        {
            LastState = TaskbarProgressRecordingState.Progress;
            LastPercent = percent;
        }

        public void SetPaused()
        {
            LastState = TaskbarProgressRecordingState.Paused;
        }

        public void SetError()
        {
            LastState = TaskbarProgressRecordingState.Error;
        }

        public void Clear()
        {
            LastState = TaskbarProgressRecordingState.Clear;
        }

        public void Dispose()
        {
            LastState = TaskbarProgressRecordingState.Disposed;
        }
    }

    private enum TaskbarProgressRecordingState
    {
        None,
        Attached,
        Indeterminate,
        Progress,
        Paused,
        Error,
        Clear,
        Disposed
    }
}
