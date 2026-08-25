namespace DevProjex.Tests.Integration;

public sealed class ProjectProfilePersistenceMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(RoundTripCases))]
	public void ProfilePersistence_Matrix_RoundTripIsStable(
		int pathMode,
		string[] roots,
		string[] extensions,
		IgnoreOptionId[] ignoreOptions)
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => temp.Path);
		var canonicalPath = Path.Combine(temp.Path, "workspace", "RepoA");
		Directory.CreateDirectory(canonicalPath);
		var savePath = BuildPathByMode(canonicalPath, pathMode);

		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: roots,
			SelectedExtensions: extensions,
			SelectedIgnoreOptions: ignoreOptions);

		store.SaveProfile(savePath, profile);

		Assert.True(store.TryLoadProfile(canonicalPath, out var loaded));
		AssertSetEqual(
			SanitizeRootValues(roots),
			new HashSet<string>(loaded.SelectedRootFolders, PathComparer.Default));
		AssertSetEqual(
			SanitizeStringValues(extensions),
			new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			SanitizeIgnoreValues(ignoreOptions),
			[..loaded.SelectedIgnoreOptions]);
	}

	[Theory]
	[MemberData(nameof(OverwriteCases))]
	public void ProfilePersistence_Matrix_OverwriteUsesLatestSnapshot(
		int pathMode,
		string[] firstRoots,
		string[] firstExtensions,
		IgnoreOptionId[] firstIgnore,
		string[] secondRoots,
		string[] secondExtensions,
		IgnoreOptionId[] secondIgnore)
	{
		using var temp = new TemporaryDirectory();
		var store = new ProjectProfileStore(() => temp.Path);
		var canonicalPath = Path.Combine(temp.Path, "workspace", "RepoA");
		Directory.CreateDirectory(canonicalPath);
		var savePath = BuildPathByMode(canonicalPath, pathMode);

		var first = new ProjectSelectionProfile(firstRoots, firstExtensions, firstIgnore);
		var second = new ProjectSelectionProfile(secondRoots, secondExtensions, secondIgnore);

		store.SaveProfile(savePath, first);
		store.SaveProfile(savePath, second);

		Assert.True(store.TryLoadProfile(canonicalPath, out var loaded));
		AssertSetEqual(
			SanitizeRootValues(secondRoots),
			new HashSet<string>(loaded.SelectedRootFolders, PathComparer.Default));
		AssertSetEqual(
			SanitizeStringValues(secondExtensions),
			new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			SanitizeIgnoreValues(secondIgnore),
			[..loaded.SelectedIgnoreOptions]);
	}

	[Fact]
	public void ProfilePersistence_RecoversFromCorruptedPrimaryFile_UsingPersistedBackupAcrossInstances()
	{
		using var temp = new TemporaryDirectory();
		var firstStore = new ProjectProfileStore(() => temp.Path);
		var canonicalPath = Path.Combine(temp.Path, "workspace", "RepoA");
		Directory.CreateDirectory(canonicalPath);
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: ["src", "tests"],
			SelectedExtensions: [".cs", ".json"],
			SelectedIgnoreOptions: [IgnoreOptionId.DotFiles, IgnoreOptionId.UseGitIgnore]);

		firstStore.SaveProfile(canonicalPath, profile);
		File.WriteAllText(firstStore.GetPath(), "{ invalid");

		var secondStore = new ProjectProfileStore(() => temp.Path);

		Assert.True(secondStore.TryLoadProfile(canonicalPath, out var loaded));
		Assert.Contains("src", loaded.SelectedRootFolders);
		Assert.Contains("tests", loaded.SelectedRootFolders);
		Assert.Contains(".cs", loaded.SelectedExtensions);
		Assert.Contains(".json", loaded.SelectedExtensions);
		Assert.Contains(IgnoreOptionId.DotFiles, loaded.SelectedIgnoreOptions);
		Assert.Contains(IgnoreOptionId.UseGitIgnore, loaded.SelectedIgnoreOptions);
	}

	public static IEnumerable<object[]> RoundTripCases()
	{
		var pathModes = new[] { 0, 1, 2, 3 };
		var variants = ProfileVariants().ToArray();

		// 4 path modes * 18 variants = 72 integration test cases.
		foreach (var pathMode in pathModes)
		{
			foreach (var variant in variants)
			{
				yield return
				[
					pathMode,
					variant.Roots,
					variant.Extensions,
					variant.IgnoreOptions
				];
			}
		}
	}

	public static IEnumerable<object[]> OverwriteCases()
	{
		var pathModes = new[] { 0, 1, 2, 3 };
		var variants = ProfileVariants().ToArray();
		var pairs = new[]
		{
			(0, 1),
			(2, 3),
			(4, 5),
			(6, 7),
			(8, 9),
			(10, 11)
		};

		// 4 path modes * 6 pairs = 24 integration test cases.
		foreach (var pathMode in pathModes)
		{
			foreach (var (firstIndex, secondIndex) in pairs)
			{
				var first = variants[firstIndex];
				var second = variants[secondIndex];
				yield return
				[
					pathMode,
					first.Roots,
					first.Extensions,
					first.IgnoreOptions,
					second.Roots,
					second.Extensions,
					second.IgnoreOptions
				];
			}
		}
	}

	private static IEnumerable<(string[] Roots, string[] Extensions, IgnoreOptionId[] IgnoreOptions)> ProfileVariants()
	{
		yield return (
			["src", "tests"],
			[".cs", ".json"],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles]);
		yield return (
			["SRC", "src", "  ", ""],
			[".CS", ".cs", " ", ""],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFolders]);
		yield return (
			[],
			[],
			[]);
		yield return (
			["api", "domain", "infrastructure"],
			[".cs", ".md", ".yml"],
			[IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders]);
		yield return (
			["Client", "Server", "Shared"],
			[".tsx", ".ts", ".css"],
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore]);
		yield return (
			["scripts", "tools", "build"],
			[".ps1", ".sh", ".cmd"],
			[IgnoreOptionId.ExtensionlessFiles]);
		yield return (
			["config"],
			[".json", ".yaml", ".toml"],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles]);
		yield return (
			["images", "assets"],
			[".png", ".jpg", ".svg"],
			[IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles]);
		yield return (
			["docs", "examples"],
			[".md", ".txt"],
			[IgnoreOptionId.UseGitIgnore]);
		yield return (
			["Desktop", "desktop", "  Desktop  "],
			[".xaml", ".XAML", ".axaml"],
			[IgnoreOptionId.SmartIgnore, IgnoreOptionId.SmartIgnore]);
		yield return (
			["A", "B", "C", "D"],
			[".a", ".b", ".c", ".d"],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles
			]);
		yield return (
			["ModuleA", "ModuleB"],
			[".proto", ".graphql", ".sql"],
			[IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.UseGitIgnore]);
		yield return (
			["src", "tests", "benchmarks"],
			[".go", ".mod", ".sum"],
			[IgnoreOptionId.HiddenFiles]);
		yield return (
			["frontend", "backend", "ops"],
			[".js", ".ts", ".json", ".yaml"],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.ExtensionlessFiles]);
		yield return (
			["mobile", "web", "desktop"],
			[".kt", ".swift", ".cs"],
			[IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders]);
		yield return (
			["samples", "templates", "snippets"],
			[".txt", ".md", ".json"],
			[IgnoreOptionId.DotFiles]);
		yield return (
			["infra", "pipelines", "deploy"],
			[".yml", ".yaml", ".tf"],
			[IgnoreOptionId.HiddenFolders, IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore]);
		yield return (
			["raw", "processed", "reports"],
			[".csv", ".parquet", ".json"],
			[IgnoreOptionId.HiddenFiles, IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles]);
	}

	private static string BuildPathByMode(string canonicalPath, int mode)
	{
		return mode switch
		{
			0 => canonicalPath,
			1 => $"{canonicalPath}{Path.DirectorySeparatorChar}",
			2 => canonicalPath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
			3 => Path.GetRelativePath(Environment.CurrentDirectory, canonicalPath),
			_ => canonicalPath
		};
	}

	private static HashSet<string> SanitizeStringValues(IEnumerable<string> values)
	{
		return values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static HashSet<string> SanitizeRootValues(IEnumerable<string> values)
	{
		return values
			.Where(value =>
				!string.IsNullOrEmpty(value) &&
				(!OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(value)))
			.ToHashSet(PathComparer.Default);
	}

	private static HashSet<IgnoreOptionId> SanitizeIgnoreValues(IEnumerable<IgnoreOptionId> values)
	{
		return values.ToHashSet();
	}

	private static void AssertSetEqual(HashSet<string> expected, HashSet<string> actual)
	{
		Assert.Equal(expected.Count, actual.Count);
		foreach (var value in expected)
			Assert.Contains(value, actual);
	}
}
