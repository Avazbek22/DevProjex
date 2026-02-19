using System.Text.Json;
using DevProjex.Infrastructure.ProjectProfiles;

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
					SelectedRootFolders: new[] { "src", "SRC", "", "   ", "tests" },
					SelectedExtensions: Array.Empty<string>(),
					SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>()));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
			Assert.Equal(2, loaded.SelectedRootFolders.Count);
			Assert.Contains("src", loaded.SelectedRootFolders);
			Assert.Contains("tests", loaded.SelectedRootFolders);
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
					SelectedRootFolders: Array.Empty<string>(),
					SelectedExtensions: new[] { ".cs", ".CS", "", "  ", ".md" },
					SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>()));

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
					SelectedRootFolders: Array.Empty<string>(),
					SelectedExtensions: Array.Empty<string>(),
					SelectedIgnoreOptions: new[]
					{
						IgnoreOptionId.DotFiles,
						IgnoreOptionId.DotFiles,
						IgnoreOptionId.HiddenFiles
					}));

			Assert.True(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out var loaded));
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
					SelectedRootFolders: Array.Empty<string>(),
					SelectedExtensions: Array.Empty<string>(),
					SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>()));

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
	public void TryLoadProfile_CorruptedJson_RewritesToValidStorageDocument()
	{
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = CreateStore(tempRoot);
			var storagePath = store.GetPath();
			Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
			File.WriteAllText(storagePath, "{ invalid");

			Assert.False(store.TryLoadProfile(Path.Combine(tempRoot, "RepoA"), out _));

			var persisted = File.ReadAllText(storagePath);
			using var doc = JsonDocument.Parse(persisted);
			Assert.True(doc.RootElement.TryGetProperty("profiles", out _));
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
						SelectedRootFolders: new[] { $"src{i}" },
						SelectedExtensions: new[] { ".cs" },
						SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>()));
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
			SelectedRootFolders: new[] { "src" },
			SelectedExtensions: new[] { ".cs" },
			SelectedIgnoreOptions: new[] { IgnoreOptionId.DotFiles });
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
