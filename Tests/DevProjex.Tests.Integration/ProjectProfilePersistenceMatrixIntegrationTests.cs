using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Tests.Integration.Helpers;

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
			SanitizeStringValues(roots),
			new HashSet<string>(loaded.SelectedRootFolders, StringComparer.OrdinalIgnoreCase));
		AssertSetEqual(
			SanitizeStringValues(extensions),
			new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			SanitizeIgnoreValues(ignoreOptions),
			new HashSet<IgnoreOptionId>(loaded.SelectedIgnoreOptions));
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
			SanitizeStringValues(secondRoots),
			new HashSet<string>(loaded.SelectedRootFolders, StringComparer.OrdinalIgnoreCase));
		AssertSetEqual(
			SanitizeStringValues(secondExtensions),
			new HashSet<string>(loaded.SelectedExtensions, StringComparer.OrdinalIgnoreCase));
		Assert.Equal(
			SanitizeIgnoreValues(secondIgnore),
			new HashSet<IgnoreOptionId>(loaded.SelectedIgnoreOptions));
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
				yield return new object[]
				{
					pathMode,
					variant.Roots,
					variant.Extensions,
					variant.IgnoreOptions
				};
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
				yield return new object[]
				{
					pathMode,
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
		yield return (
			new[] { "src", "tests" },
			new[] { ".cs", ".json" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.DotFiles });
		yield return (
			new[] { "SRC", "src", "  ", "" },
			new[] { ".CS", ".cs", " ", "" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFolders });
		yield return (
			Array.Empty<string>(),
			Array.Empty<string>(),
			Array.Empty<IgnoreOptionId>());
		yield return (
			new[] { "api", "domain", "infrastructure" },
			new[] { ".cs", ".md", ".yml" },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders });
		yield return (
			new[] { "Client", "Server", "Shared" },
			new[] { ".tsx", ".ts", ".css" },
			new[] { IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore });
		yield return (
			new[] { "scripts", "tools", "build" },
			new[] { ".ps1", ".sh", ".cmd" },
			new[] { IgnoreOptionId.ExtensionlessFiles });
		yield return (
			new[] { "config" },
			new[] { ".json", ".yaml", ".toml" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFiles });
		yield return (
			new[] { "images", "assets" },
			new[] { ".png", ".jpg", ".svg" },
			new[] { IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles });
		yield return (
			new[] { "docs", "examples" },
			new[] { ".md", ".txt" },
			new[] { IgnoreOptionId.UseGitIgnore });
		yield return (
			new[] { "Desktop", "desktop", "  Desktop  " },
			new[] { ".xaml", ".XAML", ".axaml" },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.SmartIgnore });
		yield return (
			new[] { "A", "B", "C", "D" },
			new[] { ".a", ".b", ".c", ".d" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles, IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles });
		yield return (
			new[] { "ModuleA", "ModuleB" },
			new[] { ".proto", ".graphql", ".sql" },
			new[] { IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.UseGitIgnore });
		yield return (
			new[] { "src", "tests", "benchmarks" },
			new[] { ".go", ".mod", ".sum" },
			new[] { IgnoreOptionId.HiddenFiles });
		yield return (
			new[] { "frontend", "backend", "ops" },
			new[] { ".js", ".ts", ".json", ".yaml" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.ExtensionlessFiles });
		yield return (
			new[] { "mobile", "web", "desktop" },
			new[] { ".kt", ".swift", ".cs" },
			new[] { IgnoreOptionId.SmartIgnore, IgnoreOptionId.DotFolders });
		yield return (
			new[] { "samples", "templates", "snippets" },
			new[] { ".txt", ".md", ".json" },
			new[] { IgnoreOptionId.DotFiles });
		yield return (
			new[] { "infra", "pipelines", "deploy" },
			new[] { ".yml", ".yaml", ".tf" },
			new[] { IgnoreOptionId.HiddenFolders, IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore });
		yield return (
			new[] { "raw", "processed", "reports" },
			new[] { ".csv", ".parquet", ".json" },
			new[] { IgnoreOptionId.HiddenFiles, IgnoreOptionId.ExtensionlessFiles, IgnoreOptionId.DotFiles });
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
