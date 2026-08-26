namespace DevProjex.Kernel.Abstractions;

public interface IProjectProfileStore
{
	bool EnsureStorageExists();
	bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile);
	bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile);
	bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc);
	ProjectProfileSaveResult TrySaveProfileWithResult(
		string localProjectPath,
		ProjectSelectionProfile profile) =>
		new(TrySaveProfile(localProjectPath, profile), WasTruncated: false);
	ProjectProfileBatchSaveResult TrySaveProfilesWithResult(
		IReadOnlyList<ProjectProfileSaveRequest> requests,
		TimeSpan lockTimeout)
	{
		ArgumentNullException.ThrowIfNull(requests);
		ArgumentOutOfRangeException.ThrowIfLessThan(lockTimeout, TimeSpan.Zero);
		var savedPaths = new List<string>(requests.Count);
		foreach (var request in requests)
		{
			if (TrySaveProfile(request.LocalProjectPath, request.Profile, request.UpdatedUtc))
				savedPaths.Add(request.LocalProjectPath);
		}

		return new ProjectProfileBatchSaveResult(savedPaths);
	}
	ProjectProfileLookupResult LookupProfile(string localProjectPath, TimeSpan lockTimeout)
	{
		return TryLoadProfile(localProjectPath, out var profile)
			? new ProjectProfileLookupResult(ProjectProfileLookupStatus.Found, profile)
			: new ProjectProfileLookupResult(ProjectProfileLookupStatus.Missing, null);
	}
	bool TryDeleteProfile(string localProjectPath) => false;
	void SaveProfile(string localProjectPath, ProjectSelectionProfile profile);
	ProjectProfileClearStatus ClearAllProfiles();
}
