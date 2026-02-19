using DevProjex.Kernel.Models;

namespace DevProjex.Kernel.Abstractions;

public interface IProjectProfileStore
{
	bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile);
	void SaveProfile(string localProjectPath, ProjectSelectionProfile profile);
	void ClearAllProfiles();
}
