namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileStoreTests
{
	[Fact]
	public void SaveProfile_ThenTryLoadProfile_ReturnsRoundTripData()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src", "tests", "src" },
				SelectedExtensions: new[] { ".cs", ".md", ".cs" },
				SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles, IgnoreOptionId.DotFiles, IgnoreOptionId.UseGitIgnore });

			store.SaveProfile(projectPath, profile);

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Equal(2, loaded.SelectedRootFolders.Count);
			Assert.Equal(2, loaded.SelectedExtensions.Count);
			Assert.Equal(2, loaded.SelectedIgnoreOptions.Count);
			Assert.Contains("src", loaded.SelectedRootFolders);
			Assert.Contains(".cs", loaded.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_CorruptedJson_ReturnsFalseAndRecoversOnNextSave()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var path = store.GetPath();
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "{ this is not valid json");

			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));

			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src" },
				SelectedExtensions: new[] { ".cs" },
				SelectedIgnoreOptions: new[] { IgnoreOptionId.HiddenFiles });
			store.SaveProfile(Path.Combine(tempRoot, "RepoA"), profile);

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			Assert.Single(loaded.SelectedRootFolders);
			Assert.Single(loaded.SelectedExtensions);
			Assert.Single(loaded.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ClearAllProfiles_RemovesPersistedProfiles()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src" },
				SelectedExtensions: Array.Empty<string>(),
				SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());
			store.SaveProfile(projectPath, profile);
			Assert.True(store.TryLoadProfile(projectPath, out _));

			store.ClearAllProfiles();

			Assert.False(File.Exists(store.GetPath()));
			Assert.False(store.TryLoadProfile(projectPath, out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_PathComparerBehavior_MatchesPlatform()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoCase");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src" },
				SelectedExtensions: Array.Empty<string>(),
				SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());
			store.SaveProfile(projectPath, profile);

			var alteredCasePath = projectPath.Replace("RepoCase", "rePOcAse", StringComparison.Ordinal);
			var found = store.TryLoadProfile(alteredCasePath, out _);
			Assert.Equal(OperatingSystem.IsWindows(), found);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_NormalizesTrailingDirectorySeparators()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var withSeparator = $"{projectPath}{Path.DirectorySeparatorChar}";
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src" },
				SelectedExtensions: Array.Empty<string>(),
				SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

			store.SaveProfile(withSeparator, profile);

			Assert.True(store.TryLoadProfile(projectPath, out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_TwoDifferentProjects_DoNotMixSelections()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectA = Path.Combine(tempRoot, "RepoA");
			var projectB = Path.Combine(tempRoot, "RepoB");

			store.SaveProfile(
				projectA,
				new ProjectSelectionProfile(
					SelectedRootFolders: new[] { "src" },
					SelectedExtensions: new[] { ".cs" },
					SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles }));

			store.SaveProfile(
				projectB,
				new ProjectSelectionProfile(
					SelectedRootFolders: new[] { "docs" },
					SelectedExtensions: new[] { ".md" },
					SelectedIgnoreOptions: new[] { IgnoreOptionId.HiddenFiles }));

			Assert.True(store.TryLoadProfile(projectA, out var profileA));
			Assert.True(store.TryLoadProfile(projectB, out var profileB));

			Assert.Contains("src", profileA.SelectedRootFolders);
			Assert.DoesNotContain("docs", profileA.SelectedRootFolders);
			Assert.Contains(".cs", profileA.SelectedExtensions);
			Assert.DoesNotContain(".md", profileA.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.DotFiles, profileA.SelectedIgnoreOptions);
			Assert.DoesNotContain(IgnoreOptionId.HiddenFiles, profileA.SelectedIgnoreOptions);

			Assert.Contains("docs", profileB.SelectedRootFolders);
			Assert.DoesNotContain("src", profileB.SelectedRootFolders);
			Assert.Contains(".md", profileB.SelectedExtensions);
			Assert.DoesNotContain(".cs", profileB.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.HiddenFiles, profileB.SelectedIgnoreOptions);
			Assert.DoesNotContain(IgnoreOptionId.DotFiles, profileB.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_InvalidPath_IsIgnoredWithoutThrowing()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: new[] { "src" },
				SelectedExtensions: new[] { ".cs" },
				SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles });

			store.SaveProfile("\0invalid", profile);

			Assert.False(File.Exists(store.GetPath()));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_InvalidPath_ReturnsFalse()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			Assert.False(store.TryLoadProfile("\0invalid", out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_SameProject_OverwritesPreviousSelections()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");

			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: new[] { "src" },
					SelectedExtensions: new[] { ".cs" },
					SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles }));

			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: new[] { "docs" },
					SelectedExtensions: new[] { ".md" },
					SelectedIgnoreOptions: new[] { IgnoreOptionId.HiddenFiles }));

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Contains("docs", loaded.SelectedRootFolders);
			Assert.DoesNotContain("src", loaded.SelectedRootFolders);
			Assert.Contains(".md", loaded.SelectedExtensions);
			Assert.DoesNotContain(".cs", loaded.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.HiddenFiles, loaded.SelectedIgnoreOptions);
			Assert.DoesNotContain(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	private static ProjectProfileStore CreateStore(string tempRoot)
	{
		var appDataRoot = Path.Combine(tempRoot, "appdata");
		return new ProjectProfileStore(() => appDataRoot);
	}

	private static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), $"devprojex-profile-store-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
	}
}
