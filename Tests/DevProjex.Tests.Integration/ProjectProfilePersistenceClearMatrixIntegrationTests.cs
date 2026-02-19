namespace DevProjex.Tests.Integration;

public sealed class ProjectProfilePersistenceClearMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(ClearMatrixCases))]
	public void ProjectProfileStore_ClearAllProfiles_Matrix_RemovesPersistedEntries(
		int caseId,
		int pathMode,
		int projectCount)
	{
		_ = caseId;
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => temp.Path);
		var projectPaths = new List<string>(projectCount);
		for (var i = 0; i < projectCount; i++)
		{
			var canonicalPath = Path.Combine(temp.Path, "workspace", $"Repo{i + 1}");
			Directory.CreateDirectory(canonicalPath);
			projectPaths.Add(canonicalPath);
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { $"src{i}", $"tests{i}" },
				SelectedExtensions: new[] { ".cs", ".json", $".x{i}" },
				SelectedIgnoreOptions: new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles });
			store.SaveProfile(BuildPathByMode(canonicalPath, pathMode), profile);
		}

		foreach (var projectPath in projectPaths)
			Assert.True(store.TryLoadProfile(projectPath, out _));

		store.ClearAllProfiles();

		foreach (var projectPath in projectPaths)
			Assert.False(store.TryLoadProfile(projectPath, out _));
	}

	public static IEnumerable<object[]> ClearMatrixCases()
	{
		var caseId = 0;
		var pathModes = new[] { 0, 1, 2, 3, 4, 5 };
		var projectCounts = new[] { 1, 2, 3, 5, 8 };

		// 6 path modes * 5 project counts = 30 integration cases.
		foreach (var mode in pathModes)
		{
			foreach (var count in projectCounts)
				yield return new object[] { caseId++, mode, count };
		}
	}

	private static string BuildPathByMode(string canonicalPath, int mode)
	{
		return mode switch
		{
			0 => canonicalPath,
			1 => $"{canonicalPath}{Path.DirectorySeparatorChar}",
			2 => canonicalPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			3 => Path.Combine(canonicalPath, "."),
			4 => Path.Combine(canonicalPath, "..", Path.GetFileName(canonicalPath)),
			5 => Path.GetRelativePath(Environment.CurrentDirectory, canonicalPath),
			_ => canonicalPath
		};
	}
}
