using DevProjex.Application.Models;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ProjectProfilePersistenceCoordinator(
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selectionCoordinator,
	IProjectProfileStore profileStore,
	SecretRedactionSession secretRedactionSession)
{
    private string? _lastPersistedPath;
    private ProjectSelectionProfile? _lastPersistedProfile;
    private DateTimeOffset _lastPersistedUpdatedUtc;
    private bool _persistencePending;

    public bool EnsureStorageExists() => profileStore.EnsureStorageExists();

    public void ClearAllProfiles() => profileStore.ClearAllProfiles();

    public void PersistIfNeeded(string? currentPath)
    {
        if (!IsApplicable(currentPath))
            return;

        var profile = CaptureCurrentProfile();
        var persistedAtUtc = DateTimeOffset.UtcNow;
        _lastPersistedPath = currentPath;
        _lastPersistedProfile = ProjectSelectionProfileBuilder.Clone(profile);
        _lastPersistedUpdatedUtc = persistedAtUtc;
        _persistencePending = !profileStore.TrySaveProfile(currentPath!, profile, persistedAtUtc);
    }

    public ProjectProfileLoadSnapshot LoadSnapshot(string? currentPath)
    {
        if (!TryLoadProfile(currentPath, out var profile))
            return new ProjectProfileLoadSnapshot(false, null);

        return new ProjectProfileLoadSnapshot(true, profile);
    }

    public void FlushPending()
    {
        if (!_persistencePending ||
            string.IsNullOrWhiteSpace(_lastPersistedPath) ||
            _lastPersistedProfile is null)
        {
            return;
        }

        if (profileStore.TrySaveProfile(
                _lastPersistedPath,
                ProjectSelectionProfileBuilder.Clone(_lastPersistedProfile),
                _lastPersistedUpdatedUtc))
        {
            _persistencePending = false;
        }
    }

    private bool TryLoadProfile(string? currentPath, out ProjectSelectionProfile profile)
    {
        profile = new ProjectSelectionProfile(
            SelectedRootFolders: [],
            SelectedExtensions: [],
            SelectedIgnoreOptions: []);

        if (!IsApplicable(currentPath))
            return false;

        return profileStore.TryLoadProfile(currentPath!, out profile);
    }

    private bool IsApplicable(string? currentPath)
    {
		return !string.IsNullOrWhiteSpace(currentPath);
    }

    private ProjectSelectionProfile CaptureCurrentProfile()
    {
        return ProjectSelectionProfileBuilder.Create(
            visibleExtensions: viewModel.Extensions.Select(static option => new SelectionOption(option.Name, option.IsChecked)),
            visibleIgnoreOptions: viewModel.IgnoreOptions.Select(static option => new IgnoreSelectionOption(option.Id, option.IsChecked)),
            cachedExtensionStates: selectionCoordinator.SnapshotExtensionOptionStatesForPersistence(),
            cachedIgnoreOptionStates: selectionCoordinator.SnapshotIgnoreOptionStatesForPersistence(),
            selectedIgnoreOptions: selectionCoordinator.GetSelectedIgnoreOptionIds(),
			extensionComparer: StringComparer.OrdinalIgnoreCase,
			markedSecrets: secretRedactionSession.GetMarkedSecrets());
    }
}
