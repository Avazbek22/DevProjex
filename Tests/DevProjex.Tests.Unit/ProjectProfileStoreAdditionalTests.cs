namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileStoreAdditionalTests
{
	[Fact]
	public void TryLoadProfile_WhenStorageFileMissing_ReturnsFalse()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void EnsureStorageExists_CreatesPrimaryAndBackup_WhenFilesAreMissing()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);

			Assert.True(store.EnsureStorageExists());
			Assert.True(File.Exists(store.GetPath()));
			Assert.True(File.Exists(store.GetPath() + ".bak"));
			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void EnsureStorageExists_RecreatesMissingBackup_FromPrimarySnapshot()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(Path.Combine(tempRoot, "RepoA"), CreateProfile());
			File.Delete(store.GetPath() + ".bak");

			Assert.True(store.EnsureStorageExists());
			Assert.True(File.Exists(store.GetPath() + ".bak"));
			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ClearAllProfiles_WhenStorageFileMissing_DoesNotThrow()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.ClearAllProfiles();
			Assert.False(File.Exists(store.GetPath()));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_CreatesStorageDirectoryAndFile()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(Path.Combine(tempRoot, "RepoA"), CreateProfile());
			Assert.True(File.Exists(store.GetPath()));
			Assert.True(File.Exists(store.GetPath() + ".bak"));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_RelativePath_NormalizesAndLoadsByAbsolutePath()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var absolute = Path.Combine(tempRoot, "RepoRel");
			var relative = Path.GetRelativePath(Environment.CurrentDirectory, absolute);
			store.SaveProfile(relative, CreateProfile());

			Assert.True(store.TryLoadProfile(absolute, out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_AlternateDirectorySeparator_NormalizesPath()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var absolute = Path.Combine(tempRoot, "RepoSep");
			var alt = absolute.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

			store.SaveProfile(alt, CreateProfile());

			Assert.True(store.TryLoadProfile(absolute, out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_DropsWhitespaceAndDuplicateRootFolders()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(
				Path.Combine(tempRoot, "RepoA"),
				new ProjectSelectionProfile(
					SelectedRootFolders: ["src", "SRC", "", "   ", "tests"],
					SelectedExtensions: [],
					SelectedIgnoreOptions: []));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			var expectedRootFolders = new HashSet<string>(PathComparer.Default) { "src", "SRC", "tests" };
			Assert.Equal(expectedRootFolders.Count, loaded.SelectedRootFolders.Count);
			Assert.Contains("src", loaded.SelectedRootFolders);
			Assert.Contains("tests", loaded.SelectedRootFolders);
			if (!OperatingSystem.IsWindows())
				Assert.Contains("SRC", loaded.SelectedRootFolders);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_DropsWhitespaceAndDuplicateExtensions()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(
				Path.Combine(tempRoot, "RepoA"),
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [".cs", ".CS", "", "  ", ".md"],
					SelectedIgnoreOptions: []));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			Assert.Equal(2, loaded.SelectedExtensions.Count);
			Assert.Contains(".cs", loaded.SelectedExtensions);
			Assert.Contains(".md", loaded.SelectedExtensions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_DeduplicatesIgnoreOptions()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(
				Path.Combine(tempRoot, "RepoA"),
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [],
					SelectedIgnoreOptions:
					[
						IgnoreOptionId.DotFiles,
						IgnoreOptionId.DotFiles,
						IgnoreOptionId.HiddenFiles
					]));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			Assert.Equal(2, loaded.SelectedIgnoreOptions.Count);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_RoundTripsEmptyFilesIgnoreOption()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoEmptyFiles");
			store.SaveProfile(
				projectPath,
				new ProjectSelectionProfile(
					SelectedRootFolders: ["src"],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: [IgnoreOptionId.EmptyFiles, IgnoreOptionId.DotFiles]));

			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Contains(IgnoreOptionId.EmptyFiles, loaded.SelectedIgnoreOptions);
			Assert.Contains(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
			Assert.Equal(2, loaded.SelectedIgnoreOptions.Count);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_EmptyCollections_RoundTripAsEmpty()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(
				Path.Combine(tempRoot, "RepoA"),
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [],
					SelectedIgnoreOptions: []));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			Assert.Empty(loaded.SelectedRootFolders);
			Assert.Empty(loaded.SelectedExtensions);
			Assert.Empty(loaded.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_MultipleStoreInstances_SeeSamePersistedData()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var storeA = CreateStore(tempRoot);
			var storeB = CreateStore(tempRoot);
			var path = Path.Combine(tempRoot, "RepoA");

			storeA.SaveProfile(path, CreateProfile());

			Assert.True(storeB.TryLoadProfile(path, out var loaded));
			Assert.Contains("src", loaded.SelectedRootFolders);
			Assert.Contains(".cs", loaded.SelectedExtensions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_CorruptedJson_DoesNotOverwriteOriginalFileWithoutBackup()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var storagePath = store.GetPath();
			Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
			const string invalidJson = "{ invalid";
			File.WriteAllText(storagePath, invalidJson);

			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));
			Assert.Equal(invalidJson, File.ReadAllText(storagePath));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TryLoadProfile_NullProfilesInStorage_DoesNotThrow()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var storagePath = store.GetPath();
			Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
			File.WriteAllText(storagePath, "{\"schemaVersion\":1,\"profiles\":null}");

			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_PrunesStorageToMaxProfiles()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);

			for (var i = 0; i < 510; i++)
			{
				store.SaveProfile(
					Path.Combine(tempRoot, $"Repo{i}"),
					new ProjectSelectionProfile(
						SelectedRootFolders: [$"src{i}"],
						SelectedExtensions: [".cs"],
						SelectedIgnoreOptions: []));
			}

			var json = File.ReadAllText(store.GetPath());
			using var doc = JsonDocument.Parse(json);
			var profiles = doc.RootElement.GetProperty("profiles").EnumerateObject().ToList();
			Assert.Equal(500, profiles.Count);
			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "Repo509"), out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_DoesNotLeaveTemporaryFilesInStorageDirectory()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(Path.Combine(tempRoot, "RepoA"), CreateProfile());
			store.SaveProfile(Path.Combine(tempRoot, "RepoB"), CreateProfile());

			var storageDir = Path.GetDirectoryName(store.GetPath())!;
			var tempFiles = Directory.GetFiles(storageDir, "*.tmp");
			Assert.Empty(tempFiles);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void ClearAllProfiles_RemovesBackupFile()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			store.SaveProfile(Path.Combine(tempRoot, "RepoA"), CreateProfile());

			store.ClearAllProfiles();

			Assert.False(File.Exists(store.GetPath()));
			Assert.False(File.Exists(store.GetPath() + ".bak"));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void TrySaveProfile_InvalidAppDataPath_ReturnsFalse()
	{
		var invalidRoot = string.Concat("broken", '\0', "root");
		var store = new ProjectProfileStore(() => invalidRoot);
		var projectPath = Path.Combine(Path.GetTempPath(), "RepoA");

		Assert.False(store.TrySaveProfile(projectPath, CreateProfile()));
	}

	[Fact]
	public void TrySaveProfile_OlderTimestampRetry_DoesNotOverwriteNewerProfile()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var projectPath = Path.Combine(tempRoot, "RepoA");
			var olderProfile = new ProjectSelectionProfile(
				SelectedRootFolders: ["src"],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);
			var newerProfile = new ProjectSelectionProfile(
				SelectedRootFolders: ["docs"],
				SelectedExtensions: [".md"],
				SelectedIgnoreOptions: [IgnoreOptionId.UseGitIgnore]);
			var olderTimestamp = new DateTimeOffset(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);
			var newerTimestamp = olderTimestamp.AddMinutes(5);

			Assert.True(store.TrySaveProfile(projectPath, olderProfile, olderTimestamp));
			Assert.True(store.TrySaveProfile(projectPath, newerProfile, newerTimestamp));
			Assert.True(store.TrySaveProfile(projectPath, olderProfile, olderTimestamp));
			Assert.True(store.TryLoadProfile(projectPath, out var loaded));
			Assert.Contains("docs", loaded.SelectedRootFolders);
			Assert.DoesNotContain("src", loaded.SelectedRootFolders);
			Assert.Contains(".md", loaded.SelectedExtensions);
			Assert.DoesNotContain(".cs", loaded.SelectedExtensions);
			Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
			Assert.DoesNotContain(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Theory]
	[InlineData("RepoTrailingSlash")]
	[InlineData("RepoTrailingAltSlash")]
	public void TryLoadProfile_WithTrailingSeparator_Succeeds(string repoName)
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var path = Path.Combine(tempRoot, repoName);
			store.SaveProfile(path, CreateProfile());

			var withSeparator = repoName.Contains("Alt", StringComparison.Ordinal)
				? path + Path.AltDirectorySeparatorChar
				: path + Path.DirectorySeparatorChar;
			Assert.True(store.TryLoadProfile(withSeparator, out _));
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveProfile_GetPath_IsInsideProvidedAppDataRoot()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var path = store.GetPath();
			Assert.StartsWith(Path.Combine(tempRoot, "appdata"), path, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	private static ProjectSelectionProfile CreateProfile()
	{
		return new ProjectSelectionProfile(
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles]);
	}

	private static ProjectProfileStore CreateStore(string tempRoot)
	{
		var appDataRoot = Path.Combine(tempRoot, "appdata");
		return new ProjectProfileStore(() => appDataRoot);
	}

	private static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), $"devprojex-profile-store-additional-{Guid.NewGuid():N}");
		Directory.CreateDirectory(path);
		return path;
	}
}
