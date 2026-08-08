using DevProjex.Application.Models;

namespace DevProjex.Avalonia.Coordinators;

public sealed class ProjectProfilePersistenceCoordinator(
    MainWindowViewModel viewModel,
    SelectionSyncCoordinator selectionCoordinator,
    IProjectProfileStore profileStore,
    SecretRedactionSession secretRedactionSession)
{
    private readonly PendingProjectProfileWriteQueue _pendingWrites = new(profileStore);

    public bool EnsureStorageExists() => profileStore.EnsureStorageExists();

    public void ClearAllProfiles() => profileStore.ClearAllProfiles();

    public void PersistIfNeeded(string? currentPath)
    {
        if (!IsApplicable(currentPath))
            return;

        var profile = CaptureCurrentProfile();
        _pendingWrites.Persist(currentPath!, profile, DateTimeOffset.UtcNow);
    }

    public ProjectProfileLoadSnapshot LoadSnapshot(string? currentPath)
    {
        if (!TryLoadProfile(currentPath, out var profile))
            return new ProjectProfileLoadSnapshot(false, null);

        return new ProjectProfileLoadSnapshot(true, profile);
    }

    public void FlushPending() => _pendingWrites.Flush();

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

internal sealed class PendingProjectProfileWriteQueue(IProjectProfileStore profileStore)
{
    private readonly Dictionary<string, PendingProfileWrite> _pending =
        new(PathComparer.Default);

    internal int Count => _pending.Count;

    public void Persist(
        string projectPath,
        ProjectSelectionProfile profile,
        DateTimeOffset updatedUtc)
    {
        Flush();
        var normalizedPath = Path.GetFullPath(projectPath);
        if (profileStore.TrySaveProfile(normalizedPath, profile, updatedUtc))
        {
            _pending.Remove(normalizedPath);
            return;
        }

        _pending[normalizedPath] = new PendingProfileWrite(
            ProjectSelectionProfileBuilder.Clone(profile),
            updatedUtc);
    }

    public void Flush()
    {
        foreach (var (path, pending) in _pending.ToArray())
        {
            if (!profileStore.TrySaveProfile(
                    path,
                    ProjectSelectionProfileBuilder.Clone(pending.Profile),
                    pending.UpdatedUtc))
            {
                continue;
            }

            _pending.Remove(path);
        }
    }

    private sealed record PendingProfileWrite(
        ProjectSelectionProfile Profile,
        DateTimeOffset UpdatedUtc);
}
