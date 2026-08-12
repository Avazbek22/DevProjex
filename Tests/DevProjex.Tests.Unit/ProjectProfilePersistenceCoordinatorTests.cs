using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfilePersistenceCoordinatorTests
{
	[Fact]
	public void Persist_UsesAppliedSelectionsWhileSavingCurrentMarkedSecrets()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HiddenFiles,
				"hidden files",
				true));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			viewModel.IgnoreOptions[0].IsChecked = false;
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			Assert.True(secretSession.AddMarkedSecret(new MarkedSecretProfileEntry(
				"00112233445566778899aabbccddeeff",
				"TOKEN",
				16)));
			var store = new RetryProfileStore(projectPath, failures: 0);
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			persistence.PersistIfNeeded(projectPath);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HiddenFiles, saved.SelectedIgnoreOptions);
			Assert.Single(saved.MarkedSecrets!);
		}
	}

	[Fact]
	public void Persist_AfterEnsuringHideSecrets_MergesImmediateAppliedOptionWithoutDrafts()
	{
		const string projectPath = @"C:\Project";
		var (viewModel, selectionCoordinator) = CreateSelectionCoordinator(projectPath);
		using (selectionCoordinator)
		{
			viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
			viewModel.Extensions.Add(new SelectionOptionViewModel(".md", false));
			viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				IgnoreOptionId.HideSecrets,
				"hide secrets",
				false));
			selectionCoordinator.AcceptCurrentSelectionsAsApplied(projectPath);

			viewModel.Extensions[0].IsChecked = false;
			viewModel.Extensions[1].IsChecked = true;
			Assert.True(selectionCoordinator.ApplyHideSecretsOverride(true));
			Assert.False(selectionCoordinator.ApplyHideSecretsOverride(true));
			selectionCoordinator.AcceptHideSecretsOverrideAsApplied(projectPath);
			var store = new RetryProfileStore(projectPath, failures: 0);
			using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
			var persistence = new ProjectProfilePersistenceCoordinator(
				viewModel,
				selectionCoordinator,
				store,
				secretSession);

			persistence.PersistIfNeeded(projectPath);

			var saved = store.SavedProfiles[Path.GetFullPath(projectPath)];
			Assert.Equal([".cs"], saved.SelectedExtensions.ToArray());
			Assert.Contains(IgnoreOptionId.HideSecrets, saved.SelectedIgnoreOptions);
		}
	}

	[Fact]
	public void PendingWrites_AreRetriedPerProjectWithoutCrossProjectReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var firstProject = workspace.CreateFolder("first");
		var secondProject = workspace.CreateFolder("second");
		var store = new RetryProfileStore(firstProject, failures: 2);
		var queue = new PendingProjectProfileWriteQueue(store);
		var firstProfile = CreateProfile(".cs");
		var secondProfile = CreateProfile(".json");

		queue.Persist(firstProject, firstProfile, DateTimeOffset.UtcNow.AddMinutes(-1));
		Assert.Equal(1, queue.Count);

		queue.Persist(secondProject, secondProfile, DateTimeOffset.UtcNow);

		Assert.Equal(1, queue.Count);
		Assert.DoesNotContain(Path.GetFullPath(firstProject), store.SavedProfiles.Keys);
		Assert.Equal([".json"], store.SavedProfiles[Path.GetFullPath(secondProject)].SelectedExtensions.ToArray());

		queue.Flush();

		Assert.Equal(0, queue.Count);
		Assert.Equal([".cs"], store.SavedProfiles[Path.GetFullPath(firstProject)].SelectedExtensions.ToArray());
	}

	private static ProjectSelectionProfile CreateProfile(string extension) => new(
		SelectedRootFolders: [],
		SelectedExtensions: [extension],
		SelectedIgnoreOptions: []);

	private static (MainWindowViewModel ViewModel, SelectionSyncCoordinator Coordinator)
		CreateSelectionCoordinator(string projectPath)
	{
		var catalog = new StubLocalizationCatalog(
			new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = new Dictionary<string, string>()
			});
		var localization = new LocalizationService(catalog, AppLanguage.En);
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider());
		var coordinator = new SelectionSyncCoordinator(
			viewModel,
			new ScanOptionsUseCase(LegacyWorkspaceScannerTestAdapter.Adapt(new StubFileSystemScanner())),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			_ => new IgnoreRules(
				false,
				false,
				false,
				false,
				new HashSet<string>(),
				new HashSet<string>()),
			_ => false,
			() => projectPath);
		return (viewModel, coordinator);
	}

	private sealed class RetryProfileStore(string failingPath, int failures) : IProjectProfileStore
	{
		private readonly string _failingPath = Path.GetFullPath(failingPath);
		private int _remainingFailures = failures;

		public Dictionary<string, ProjectSelectionProfile> SavedProfiles { get; } =
			new(PathComparer.Default);

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc)
		{
			var path = Path.GetFullPath(localProjectPath);
			if (PathComparer.Default.Equals(path, _failingPath) && _remainingFailures-- > 0)
				return false;

			SavedProfiles[path] = ProjectSelectionProfileBuilder.Clone(profile);
			return true;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public bool EnsureStorageExists() => true;

		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile) =>
			SavedProfiles.TryGetValue(Path.GetFullPath(localProjectPath), out profile!);

		public void ClearAllProfiles() => SavedProfiles.Clear();
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
