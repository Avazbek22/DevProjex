namespace DevProjex.Tests.Unit;

public sealed class ProjectProfileStorePathMatrixUnitTests
{
	[Theory]
	[MemberData(nameof(PathRoundTripCases))]
	public void SaveLoad_PathNormalizationMatrix_RoundTripIsStable(
		int caseId,
		int pathMode,
		string[] roots,
		string[] extensions,
		IgnoreOptionId[] ignoreOptions)
	{
		_ = caseId;
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = new ProjectProfileStore(() => tempRoot);
			var canonicalPath = Path.Combine(tempRoot, "workspace", "RepoA");
			Directory.CreateDirectory(canonicalPath);
			var savePath = BuildPathByMode(canonicalPath, pathMode);

			store.SaveProfile(savePath, new ProjectSelectionProfile(roots, extensions, ignoreOptions));

			Assert.True(store.TryLoadProfile(canonicalPath, out var loaded));
			Assert.Equal(SanitizeRoots(roots), new HashSet<string>(loaded.SelectedRootFolders, PathComparer.Default));
			Assert.Equal(SanitizeStrings(extensions), new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(ignoreOptions), [..loaded.SelectedIgnoreOptions]);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Theory]
	[MemberData(nameof(OverwriteIsolationCases))]
	public void SaveLoad_OverwriteAndIsolationMatrix_UsesLatestSnapshotPerProject(
		int caseId,
		int pathModeA,
		int pathModeB,
		string[] firstRoots,
		string[] firstExtensions,
		IgnoreOptionId[] firstIgnore,
		string[] secondRoots,
		string[] secondExtensions,
		IgnoreOptionId[] secondIgnore)
	{
		_ = caseId;
		var tempRoot = CreateTempDirectory();
		try
		{
			var store = new ProjectProfileStore(() => tempRoot);
			var canonicalPathA = Path.Combine(tempRoot, "workspace", "RepoA");
			var canonicalPathB = Path.Combine(tempRoot, "workspace", "RepoB");
			Directory.CreateDirectory(canonicalPathA);
			Directory.CreateDirectory(canonicalPathB);

			store.SaveProfile(
				BuildPathByMode(canonicalPathA, pathModeA),
				new ProjectSelectionProfile(firstRoots, firstExtensions, firstIgnore));
			store.SaveProfile(
				BuildPathByMode(canonicalPathB, pathModeB),
				new ProjectSelectionProfile(secondRoots, secondExtensions, secondIgnore));
			store.SaveProfile(
				BuildPathByMode(canonicalPathA, pathModeB),
				new ProjectSelectionProfile(secondRoots, secondExtensions, secondIgnore));

			Assert.True(store.TryLoadProfile(canonicalPathA, out var loadedA));
			Assert.True(store.TryLoadProfile(canonicalPathB, out var loadedB));

			Assert.Equal(SanitizeRoots(secondRoots), new HashSet<string>(loadedA.SelectedRootFolders, PathComparer.Default));
			Assert.Equal(SanitizeStrings(secondExtensions), new HashSet<string>(loadedA.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(secondIgnore), [..loadedA.SelectedIgnoreOptions]);

			Assert.Equal(SanitizeRoots(secondRoots), new HashSet<string>(loadedB.SelectedRootFolders, PathComparer.Default));
			Assert.Equal(SanitizeStrings(secondExtensions), new HashSet<string>(loadedB.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(secondIgnore), [..loadedB.SelectedIgnoreOptions]);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	public static IEnumerable<object[]> PathRoundTripCases()
	{
		var caseId = 0;
		var pathModes = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
		var variants = ProfileVariants().ToArray();

		// 8 path modes * 15 profile variants = 120 unit cases.
		foreach (var pathMode in pathModes)
		{
			foreach (var variant in variants)
			{
				yield return [ caseId++, pathMode, variant.Roots, variant.Extensions, variant.IgnoreOptions ];
			}
		}
	}

	public static IEnumerable<object[]> OverwriteIsolationCases()
	{
		var caseId = 0;
		var modePairs = new (int A, int B)[]
		{
			(0, 1), (2, 3), (4, 5), (6, 7), (1, 6), (0, 4)
		};
		var variants = ProfileVariants().ToArray();
		var variantPairs = new (int First, int Second)[]
		{
			(0, 1), (2, 3), (4, 5), (6, 7), (8, 9), (10, 11), (12, 13), (14, 0)
		};

		// 6 mode-pairs * 8 variant-pairs = 48 unit cases.
		foreach (var (a, b) in modePairs)
		{
			foreach (var (firstIndex, secondIndex) in variantPairs)
			{
				var first = variants[firstIndex];
				var second = variants[secondIndex];
				yield return
				[
					caseId++,
					a,
					b,
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
		yield return (["src", "tests"], [".cs", ".json"], [IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles]);
		yield return (["SRC", "src", " ", ""], [".CS", ".cs", " ", ""], [IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFolders
		]);
		yield return ([], [], []);
		yield return (["Client", "Server", "Shared"], [".ts", ".tsx", ".json"], [IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore
		]);
		yield return (["docs", "examples"], [".md", ".txt"], [IgnoreOptionId.UseGitIgnore]);
		yield return (["scripts", "tools"], [".ps1", ".sh", ".cmd"], [IgnoreOptionId.ExtensionlessFiles]);
		yield return (["images", "assets"], [".png", ".jpg", ".svg"], [IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles
		]);
		yield return (["A", "B", "C"], [".a", ".b", ".c"], [IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles
		]);
		yield return (["module", "module", "MODULE"], [".xaml", ".XAML", ".axaml"], [IgnoreOptionId.SmartIgnore, IgnoreOptionId.SmartIgnore
		]);
		yield return (["infra", "deploy"], [".yml", ".yaml", ".tf"], [IgnoreOptionId.HiddenFolders, IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore
		]);
		yield return (["raw", "processed", "reports"], [".csv", ".json", ".txt"], [IgnoreOptionId.HiddenFiles, IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles
		]);
		yield return (["desktop"], [".axaml", ".xaml"], [IgnoreOptionId.HiddenFiles]);
		yield return (["api", "domain", "infra"], [".cs", ".props", ".sln"], [IgnoreOptionId.DotFolders]);
		yield return (["config"], [".json", ".yaml", ".toml"], [IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles
		]);
		yield return (["mobile", "web", "desktop"], [".kt", ".swift", ".cs"], [IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders
		]);
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
			6 => Path.GetFullPath(canonicalPath),
			7 => canonicalPath,
			_ => canonicalPath
		};
	}

	private static HashSet<string> SanitizeStrings(IEnumerable<string> values)
	{
		return values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static HashSet<string> SanitizeRoots(IEnumerable<string> values)
	{
		return values
			.Where(value => !string.IsNullOrEmpty(value) &&
			                (!OperatingSystem.IsWindows() || !string.IsNullOrWhiteSpace(value)))
			.ToHashSet(PathComparer.Default);
	}

	private static HashSet<IgnoreOptionId> SanitizeIgnore(IEnumerable<IgnoreOptionId> values)
	{
		return values.ToHashSet();
	}

	private static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "Unit", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}
