using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Tests.Unit;

public sealed class ProjectProfilePersistenceCoordinatorTests
{
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
}
