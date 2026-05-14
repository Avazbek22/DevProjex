namespace DevProjex.Tests.Integration;

public sealed class FileSystemScannerIgnoreSectionSnapshotMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(SnapshotCases))]
	public void GetIgnoreSectionSnapshotForRootFolders_MatchesLegacyTwoStepPipeline(
		SnapshotCase testCase)
	{
		if (testCase.RequiresWindows && !OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		testCase.Seed(temp.Path);
		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var effectiveRules = testCase.BuildEffectiveRules(temp.Path);
		var discoveryRules = BuildExtensionDiscoveryRules(effectiveRules);

		var snapshot = useCase.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			[testCase.RootRelativePath],
			discoveryRules,
			effectiveRules,
			testCase.AllowedExtensions);

		var legacyRawScan = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			[testCase.RootRelativePath],
			discoveryRules);
		var resolvedAllowedExtensions = testCase.AllowedExtensions ??
		                                BuildAllDiscoveredExtensionsSet(legacyRawScan.Value.Extensions);
		var legacyEffectiveScan = useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			[testCase.RootRelativePath],
			resolvedAllowedExtensions,
			effectiveRules,
			legacyRawScan.Value.IgnoreOptionCounts);

		AssertSetEquals(legacyRawScan.Value.Extensions, snapshot.Value.Extensions);
		Assert.Equal(legacyRawScan.Value.IgnoreOptionCounts, snapshot.Value.RawIgnoreOptionCounts);
		Assert.Equal(legacyEffectiveScan.Value, snapshot.Value.EffectiveIgnoreOptionCounts);
		Assert.Equal(
			legacyRawScan.RootAccessDenied || legacyEffectiveScan.RootAccessDenied,
			snapshot.RootAccessDenied);
		Assert.Equal(
			legacyRawScan.HadAccessDenied || legacyEffectiveScan.HadAccessDenied,
			snapshot.HadAccessDenied);
	}

	[Fact]
	public void GetIgnoreSectionSnapshotForRootFolders_PolicyOverload_MatchesLegacyPipelineOnDeepMixedWorkspace()
	{
		using var temp = new TemporaryDirectory();
		SeedPolicyWorkspace(temp.Path);

		var useCase = new ScanOptionsUseCase(new FileSystemScanner());
		var effectiveRules = BuildPolicyEffectiveRules(temp.Path);
		var discoveryRules = BuildExtensionDiscoveryRules(effectiveRules);
		var selectedRoots = new[] { "src", "docs", "logs", ".idea", "node_modules", "ignored-root" };
		var policy = new ExtensionSelectionInclusionPolicy(
			new SelectionStateResolver(
				previousSelections: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				previousStateCache: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
				{
					[".cs"] = true,
					[".md"] = false
				}),
			defaultForNewExtension: true);

		var snapshot = useCase.GetIgnoreSectionSnapshotForRootFolders(
			temp.Path,
			selectedRoots,
			discoveryRules,
			effectiveRules,
			policy);

		var legacyRawScan = useCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			selectedRoots,
			discoveryRules);
		var legacyAllowedExtensions = BuildPolicyAllowedExtensionsSet(legacyRawScan.Value.Extensions, policy);
		var legacyEffectiveScan = useCase.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			selectedRoots,
			legacyAllowedExtensions,
			effectiveRules,
			legacyRawScan.Value.IgnoreOptionCounts);

		AssertSetEquals(legacyRawScan.Value.Extensions, snapshot.Value.Extensions);
		Assert.Equal(legacyRawScan.Value.IgnoreOptionCounts, snapshot.Value.RawIgnoreOptionCounts);
		Assert.Equal(legacyEffectiveScan.Value, snapshot.Value.EffectiveIgnoreOptionCounts);
		Assert.Contains(".md", snapshot.Value.Extensions);
		Assert.DoesNotContain(".md", legacyAllowedExtensions);
		Assert.Contains(".log", legacyAllowedExtensions);
		Assert.Equal(1, snapshot.Value.EffectiveIgnoreOptionCounts.EmptyFiles);
		Assert.Equal(
			legacyRawScan.RootAccessDenied || legacyEffectiveScan.RootAccessDenied,
			snapshot.RootAccessDenied);
		Assert.Equal(
			legacyRawScan.HadAccessDenied || legacyEffectiveScan.HadAccessDenied,
			snapshot.HadAccessDenied);
	}

	public static IEnumerable<object[]> SnapshotCases()
	{
		var allowedExtensionModes = new IReadOnlySet<string>?[]
		{
			null,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".txt" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		};

		foreach (var allowedExtensions in allowedExtensionModes)
		{
			yield return
			[
				new SnapshotCase(
					$"content-root::{DescribeAllowedExtensions(allowedExtensions)}",
					"content-root",
					SeedContentWorkspace,
					_ => CreateBaseRules() with
					{
						IgnoreDotFiles = true,
						IgnoreEmptyFiles = true,
						IgnoreExtensionlessFiles = true,
						IgnoreEmptyFolders = true
					},
					allowedExtensions)
			];

			yield return
			[
				new SnapshotCase(
					$"git-root::{DescribeAllowedExtensions(allowedExtensions)}",
					"git-root",
					SeedGitWorkspace,
					BuildGitIgnoreRules,
					allowedExtensions)
			];

			yield return
			[
				new SnapshotCase(
					$"selected-dot-root::{DescribeAllowedExtensions(allowedExtensions)}",
					".cache",
					SeedDotRootWorkspace,
					_ => CreateBaseRules() with
					{
						IgnoreDotFolders = true,
						IgnoreExtensionlessFiles = true,
						IgnoreEmptyFolders = true
					},
					allowedExtensions)
			];

			yield return
			[
				new SnapshotCase(
					$"smart-root::{DescribeAllowedExtensions(allowedExtensions)}",
					"smart-root",
					SeedSmartIgnoreWorkspace,
					_ => CreateBaseRules() with
					{
						IgnoreEmptyFiles = true,
						IgnoreExtensionlessFiles = true,
						IgnoreEmptyFolders = true,
						SmartIgnoredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
						SmartIgnoredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Thumbs.db" }
					},
					allowedExtensions)
			];

			yield return
			[
				new SnapshotCase(
					$"deep-root::{DescribeAllowedExtensions(allowedExtensions)}",
					"deep-root",
					SeedDeepWorkspace,
					_ => CreateBaseRules() with
					{
						IgnoreDotFiles = true,
						IgnoreEmptyFiles = true,
						IgnoreExtensionlessFiles = true,
						IgnoreEmptyFolders = true
					},
					allowedExtensions)
			];

			yield return
			[
				new SnapshotCase(
					$"hidden-root::{DescribeAllowedExtensions(allowedExtensions)}",
					"hidden-root",
					SeedHiddenWorkspace,
					_ => CreateBaseRules() with
					{
						IgnoreHiddenFolders = true,
						IgnoreHiddenFiles = true,
						IgnoreEmptyFolders = true
					},
					allowedExtensions,
					RequiresWindows: true)
			];
		}
	}

	private static void AssertSetEquals(
		IReadOnlySet<string> expected,
		IReadOnlySet<string> actual)
	{
		Assert.True(
			expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
				.SequenceEqual(actual.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
	}

	private static IgnoreRules CreateBaseRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>());

	private static IgnoreRules BuildGitIgnoreRules(string rootPath)
	{
		var gitRootPath = Path.Combine(rootPath, "git-root");
		var patterns = File.ReadAllLines(Path.Combine(gitRootPath, ".gitignore"));
		return CreateBaseRules() with
		{
			UseGitIgnore = true,
			IgnoreDotFiles = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true,
			IgnoreEmptyFolders = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(gitRootPath, patterns)
		};
	}

	private static IgnoreRules BuildExtensionDiscoveryRules(IgnoreRules effectiveRules)
	{
		return effectiveRules with
		{
			IgnoreHiddenFiles = false,
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};
	}

	private static HashSet<string> BuildAllDiscoveredExtensionsSet(IReadOnlyCollection<string> discoveredEntries)
	{
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in discoveredEntries)
		{
			var extension = Path.GetExtension(entry);
			if (!string.IsNullOrWhiteSpace(extension))
				allowedExtensions.Add(extension);
		}

		return allowedExtensions;
	}

	private static HashSet<string> BuildPolicyAllowedExtensionsSet(
		IReadOnlyCollection<string> discoveredEntries,
		IExtensionInclusionPolicy policy)
	{
		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in discoveredEntries)
		{
			var extension = Path.GetExtension(entry);
			if (!string.IsNullOrWhiteSpace(extension) && policy.AllowsExtension(extension))
				allowedExtensions.Add(extension);
		}

		return allowedExtensions;
	}

	private static string DescribeAllowedExtensions(IReadOnlySet<string>? allowedExtensions)
	{
		if (allowedExtensions is null)
			return "all-discovered";
		if (allowedExtensions.Count == 0)
			return "extensionless-only";

		return string.Join("-", allowedExtensions.OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase));
	}

	private static void SeedContentWorkspace(string rootPath)
	{
		WriteFile(rootPath, "content-root/visible.cs", "class Visible {}");
		WriteFile(rootPath, "content-root/notes.txt", "notes");
		WriteFile(rootPath, "content-root/.env", "dot");
		WriteFile(rootPath, "content-root/README", "extensionless");
		WriteFile(rootPath, "content-root/empty.txt", string.Empty);
		Directory.CreateDirectory(Path.Combine(rootPath, "content-root", "empty-dir"));
	}

	private static void SeedGitWorkspace(string rootPath)
	{
		WriteFile(rootPath, "git-root/.gitignore", ".env\nREADME\nempty.txt\nignored-dir/\n");
		WriteFile(rootPath, "git-root/.env", "hidden");
		WriteFile(rootPath, "git-root/README", "extensionless");
		WriteFile(rootPath, "git-root/empty.txt", string.Empty);
		WriteFile(rootPath, "git-root/visible.cs", "class Visible {}");
		WriteFile(rootPath, "git-root/ignored-dir/deep.txt", "ignored");
	}

	private static void SeedDotRootWorkspace(string rootPath)
	{
		WriteFile(rootPath, ".cache/README", "extensionless");
		WriteFile(rootPath, ".cache/nested/visible.cs", "class Visible {}");
		WriteFile(rootPath, ".cache/nested/.env", "dot");
	}

	private static void SeedSmartIgnoreWorkspace(string rootPath)
	{
		WriteFile(rootPath, "smart-root/node_modules/pkg/index.js", "console.log('x');");
		WriteFile(rootPath, "smart-root/Thumbs.db", "thumbs");
		WriteFile(rootPath, "smart-root/src/app.cs", "class App {}");
		WriteFile(rootPath, "smart-root/src/README", "extensionless");
		WriteFile(rootPath, "smart-root/src/empty.txt", string.Empty);
	}

	private static void SeedDeepWorkspace(string rootPath)
	{
		WriteFile(rootPath, "deep-root/src/main.cs", "class Main {}");
		WriteFile(rootPath, "deep-root/src/.config", "dot");
		WriteFile(rootPath, "deep-root/src/README", "extensionless");
		WriteFile(rootPath, "deep-root/src/empty.txt", string.Empty);
		Directory.CreateDirectory(Path.Combine(rootPath, "deep-root", "ghost", "nested", "leaf"));
	}

	private static void SeedHiddenWorkspace(string rootPath)
	{
		var hiddenFolderPath = Path.Combine(rootPath, "hidden-root", "target-hidden-folder");
		Directory.CreateDirectory(Path.Combine(hiddenFolderPath, "nested"));
		WriteFile(rootPath, "hidden-root/target-hidden-folder/nested/visible.cs", "class Visible {}");
		WriteFile(rootPath, "hidden-root/visible.txt", "visible");
		File.SetAttributes(hiddenFolderPath, File.GetAttributes(hiddenFolderPath) | FileAttributes.Hidden);

		var hiddenFilePath = WriteFile(rootPath, "hidden-root/target.txt", "hidden");
		File.SetAttributes(hiddenFilePath, File.GetAttributes(hiddenFilePath) | FileAttributes.Hidden);
	}

	private static void SeedPolicyWorkspace(string rootPath)
	{
		WriteFile(rootPath, ".gitignore", "ignored-root/\n*.tmp\n");
		WriteFile(rootPath, "src/App.cs", "class App {}");
		WriteFile(rootPath, "src/empty.log", string.Empty);
		WriteFile(rootPath, "src/empty.md", string.Empty);
		WriteFile(rootPath, "src/README", "extensionless");
		WriteFile(rootPath, "docs/manual.md", "# Manual");
		WriteFile(rootPath, "logs/current.log", "log");
		WriteFile(rootPath, ".idea/workspace.xml", "<project />");
		WriteFile(rootPath, "node_modules/pkg/index.js", "module.exports = {};");
		WriteFile(rootPath, "ignored-root/ignored.log", "ignored");
		WriteFile(rootPath, "scratch.tmp", "ignored by git");
	}

	private static IgnoreRules BuildPolicyEffectiveRules(string rootPath)
	{
		var patterns = File.ReadAllLines(Path.Combine(rootPath, ".gitignore"));
		return CreateBaseRules() with
		{
			UseGitIgnore = true,
			UseSmartIgnore = true,
			IgnoreDotFolders = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true,
			SmartIgnoredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
			GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, patterns)
		};
	}

	private static string WriteFile(string rootPath, string relativePath, string content)
	{
		var fullPath = Path.Combine(rootPath, relativePath);
		var directoryPath = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directoryPath))
			Directory.CreateDirectory(directoryPath);

		File.WriteAllText(fullPath, content);
		return fullPath;
	}

	public sealed record SnapshotCase(
		string Name,
		string RootRelativePath,
		Action<string> Seed,
		Func<string, IgnoreRules> BuildEffectiveRules,
		IReadOnlySet<string>? AllowedExtensions,
		bool RequiresWindows = false)
	{
		public override string ToString() => Name;
	}
}
