namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileStoreTests
{
	[Fact]
	public void GetPath_IncludesExpectedSegments()
	{
		var store = new ProjectProfileStore();
		var path = store.GetPath();

		Assert.EndsWith(Path.Combine("DevProjex", "project-profiles.json"), path);
	}

	[Fact]
	public void SaveProfile_ThenTryLoadProfile_ReturnsRoundTripData()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: ["src", "tests", "src"],
				SelectedExtensions: [".cs", ".md", ".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFiles, IgnoreOptionId.DotFiles, IgnoreOptionId.UseGitIgnore]);

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
	public void SaveProfile_ConflictingGitModesRoundTripAsTrackedOnly()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [],
					SelectedIgnoreOptions:
					[
						IgnoreOptionId.UseGitIgnore,
						IgnoreOptionId.TrackedGitFilesOnly
					],
					IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
					{
						[IgnoreOptionId.UseGitIgnore] = true,
						[IgnoreOptionId.TrackedGitFilesOnly] = true
					}));

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.DoesNotContain(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
			Assert.Contains(IgnoreOptionId.TrackedGitFilesOnly, loaded.SelectedIgnoreOptions);
			Assert.NotNull(loaded.IgnoreOptionStates);
			Assert.False(loaded.IgnoreOptionStates![IgnoreOptionId.UseGitIgnore]);
			Assert.True(loaded.IgnoreOptionStates[IgnoreOptionId.TrackedGitFilesOnly]);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void SaveProfile_ValidGitModeMatrixRoundTripsWithoutChangingUserIntent(
		bool useGitIgnore,
		bool trackedOnly)
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var selected = new List<IgnoreOptionId> { IgnoreOptionId.SmartIgnore };
			if (useGitIgnore)
				selected.Add(IgnoreOptionId.UseGitIgnore);
			if (trackedOnly)
				selected.Add(IgnoreOptionId.TrackedGitFilesOnly);

			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [],
					SelectedIgnoreOptions: selected,
					IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
					{
						[IgnoreOptionId.UseGitIgnore] = useGitIgnore,
						[IgnoreOptionId.TrackedGitFilesOnly] = trackedOnly,
						[IgnoreOptionId.SmartIgnore] = true
					}));

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Equal(useGitIgnore, loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore));
			Assert.Equal(trackedOnly, loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly));
			Assert.Contains(IgnoreOptionId.SmartIgnore, loaded.SelectedIgnoreOptions);
			Assert.NotNull(loaded.IgnoreOptionStates);
			Assert.Equal(useGitIgnore, loaded.IgnoreOptionStates![IgnoreOptionId.UseGitIgnore]);
			Assert.Equal(trackedOnly, loaded.IgnoreOptionStates[IgnoreOptionId.TrackedGitFilesOnly]);
			Assert.True(loaded.IgnoreOptionStates[IgnoreOptionId.SmartIgnore]);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_MissingFile_ReturnsFalse_AndDoesNotCreateStorageFile()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");

			Assert.False(File.Exists(store.GetPath()));
			Assert.False(store.TryLoadProfile(projectPath, out _));
			Assert.False(File.Exists(store.GetPath()));
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
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.HiddenFiles]);
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
	public void TryLoadProfile_InvalidPrimaryFile_RecoversFromBackup()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);

			store.SaveProfile(projectPath, profile);
			File.WriteAllText(store.GetPath(), "{ invalid");

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Contains("src", loaded.SelectedRootFolders);
			Assert.Contains(".cs", loaded.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
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
				SelectedRootFolders: ["src"],
				SelectedExtensions: [],
				SelectedIgnoreOptions: []);
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
				SelectedRootFolders: ["src"],
				SelectedExtensions: [],
				SelectedIgnoreOptions: []);
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
				SelectedRootFolders: ["src"],
				SelectedExtensions: [],
				SelectedIgnoreOptions: []);

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
					SelectedRootFolders: ["src"],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]));

			store.SaveProfile(
				projectB,
				new ProjectSelectionProfile(
					SelectedRootFolders: ["docs"],
					SelectedExtensions: [".md"],
					SelectedIgnoreOptions: [IgnoreOptionId.HiddenFiles]));

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
	public void SaveProfile_CaseDistinctRootFolders_BehaviorMatchesPlatform()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoCase");
			var profile = new ProjectSelectionProfile(
				SelectedRootFolders: ["src", "Src", "src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: []);

			store.SaveProfile(projectPath, profile);

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			var expectedCount = OperatingSystem.IsWindows() ? 1 : 2;
			Assert.Equal(expectedCount, loaded.SelectedRootFolders.Count);
			Assert.Contains("src", loaded.SelectedRootFolders);
			if (!OperatingSystem.IsWindows())
				Assert.Contains("Src", loaded.SelectedRootFolders);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_CaseDistinctProjectPaths_BehaviorMatchesPlatform()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectUpper = Path.Combine(tempRoot, "RepoCase");
			var projectLower = Path.Combine(tempRoot, "rePOcAse");

			store.SaveProfile(
				projectUpper,
				new ProjectSelectionProfile(
					SelectedRootFolders: ["src"],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: []));

			store.SaveProfile(
				projectLower,
				new ProjectSelectionProfile(
					SelectedRootFolders: ["docs"],
					SelectedExtensions: [".md"],
					SelectedIgnoreOptions: []));

			Assert.True(store.TryLoadProfile(projectUpper, out var upperProfile));
			Assert.True(store.TryLoadProfile(projectLower, out var lowerProfile));

			if (OperatingSystem.IsWindows())
			{
				Assert.Contains("docs", upperProfile.SelectedRootFolders);
				Assert.Contains(".md", upperProfile.SelectedExtensions);
				Assert.Contains("docs", lowerProfile.SelectedRootFolders);
			}
			else
			{
				Assert.Contains("src", upperProfile.SelectedRootFolders);
				Assert.DoesNotContain("docs", upperProfile.SelectedRootFolders);
				Assert.Contains("docs", lowerProfile.SelectedRootFolders);
				Assert.DoesNotContain("src", lowerProfile.SelectedRootFolders);
			}
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
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);

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
					SelectedRootFolders: ["src"],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]));

			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: ["docs"],
					SelectedExtensions: [".md"],
					SelectedIgnoreOptions: [IgnoreOptionId.HiddenFiles]));

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
