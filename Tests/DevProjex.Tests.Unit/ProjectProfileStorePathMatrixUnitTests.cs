using DevProjex.Infrastructure.ProjectProfiles;

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
			Assert.Equal(SanitizeStrings(roots), new HashSet<string>(loaded.SelectedRootFolders, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeStrings(extensions), new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(ignoreOptions), new HashSet<IgnoreOptionId>(loaded.SelectedIgnoreOptions));
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

			Assert.Equal(SanitizeStrings(secondRoots), new HashSet<string>(loadedA.SelectedRootFolders, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeStrings(secondExtensions), new HashSet<string>(loadedA.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(secondIgnore), new HashSet<IgnoreOptionId>(loadedA.SelectedIgnoreOptions));

			Assert.Equal(SanitizeStrings(secondRoots), new HashSet<string>(loadedB.SelectedRootFolders, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeStrings(secondExtensions), new HashSet<string>(loadedB.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
			Assert.Equal(SanitizeIgnore(secondIgnore), new HashSet<IgnoreOptionId>(loadedB.SelectedIgnoreOptions));
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
				yield return new object[] { caseId++, pathMode, variant.Roots, variant.Extensions, variant.IgnoreOptions };
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
				yield return new object[]
				{
					caseId++,
					a,
					b,
					first.Roots,
					first.Extensions,
					first.IgnoreOptions,
					second.Roots,
					second.Extensions,
					second.IgnoreOptions
				};
			}
		}
	}

	private static IEnumerable<(string[] Roots, string[] Extensions, IgnoreOptionId[] IgnoreOptions)> ProfileVariants()
	{
		yield return (new[] { "src", "tests" }, new[] { ".cs", ".json" }, new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles });
		yield return (new[] { "SRC", "src", " ", "" }, new[] { ".CS", ".cs", " ", "" }, new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFolders });
		yield return (Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IgnoreOptionId>());
		yield return (new[] { "Client", "Server", "Shared" }, new[] { ".ts", ".tsx", ".json" }, new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore });
		yield return (new[] { "docs", "examples" }, new[] { ".md", ".txt" }, new[] { IgnoreOptionId.UseGitIgnore });
		yield return (new[] { "scripts", "tools" }, new[] { ".ps1", ".sh", ".cmd" }, new[] { IgnoreOptionId.ExtensionlessFiles });
		yield return (new[] { "images", "assets" }, new[] { ".png", ".jpg", ".svg" }, new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles });
		yield return (new[] { "A", "B", "C" }, new[] { ".a", ".b", ".c" }, new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles });
		yield return (new[] { "module", "module", "MODULE" }, new[] { ".xaml", ".XAML", ".axaml" }, new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.SmartIgnore });
		yield return (new[] { "infra", "deploy" }, new[] { ".yml", ".yaml", ".tf" }, new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore });
		yield return (new[] { "raw", "processed", "reports" }, new[] { ".csv", ".json", ".txt" }, new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles });
		yield return (new[] { "desktop" }, new[] { ".axaml", ".xaml" }, new[] { IgnoreOptionId.HiddenFiles });
		yield return (new[] { "api", "domain", "infra" }, new[] { ".cs", ".props", ".sln" }, new[] { IgnoreOptionId.DotFolders });
		yield return (new[] { "config" }, new[] { ".json", ".yaml", ".toml" }, new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles });
		yield return (new[] { "mobile", "web", "desktop" }, new[] { ".kt", ".swift", ".cs" }, new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders });
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
