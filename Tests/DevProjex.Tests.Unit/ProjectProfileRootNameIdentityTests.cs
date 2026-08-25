namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileRootNameIdentityTests
{
	[Fact]
	public void SaveAndLoad_PreservesExactRootNamesAndStates()
	{
		using var appData = new TemporaryDirectory();
		using var project = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => appData.Path);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [" source ", "   "],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [],
			RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
			{
				[" source "] = true,
				["   "] = false
			});

		store.SaveProfile(project.Path, profile);

		Assert.True(store.TryLoadProfile(project.Path, out var loaded));
		var expectedRoots = new HashSet<string>([" source "], PathComparer.Default);
		Assert.Equal(expectedRoots, loaded.SelectedRootFolders.ToHashSet(PathComparer.Default));
		Assert.NotNull(loaded.RootFolderStates);
		Assert.True(loaded.RootFolderStates![" source "]);
		if (OperatingSystem.IsWindows())
			Assert.DoesNotContain("   ", loaded.RootFolderStates.Keys);
		else
			Assert.False(loaded.RootFolderStates["   "]);
	}
}
