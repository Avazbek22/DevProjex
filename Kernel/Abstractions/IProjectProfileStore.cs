namespace DevProjex.Kernel.Abstractions;

public interface IProjectProfileStore
{
	bool EnsureStorageExists();
	bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile);
	bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile);
	bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc);
	bool TryDeleteProfile(string localProjectPath) => false;
	void SaveProfile(string localProjectPath, ProjectSelectionProfile profile);
	void ClearAllProfiles();
}
