namespace DevProjex.Tests.Integration;

public sealed class FileSystemScannerEffectiveIgnoreBatchMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(BatchCases))]
	public void GetEffectiveIgnoreOptionCounts_BatchScanner_ReturnsExpectedCounts(
		BatchCase testCase)
	{
		if (testCase.RequiresWindows && !OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		testCase.Seed(temp.Path);
		var scanner = new FileSystemScanner();
		var rules = testCase.BuildRules(temp.Path);

		var result = scanner.GetEffectiveIgnoreOptionCounts(
			Path.Combine(temp.Path, testCase.RootRelativePath),
			testCase.AllowedExtensions,
			rules);

		Assert.Equal(testCase.ExpectedCounts, result.Value);
	}

	public static IEnumerable<object[]> BatchCases()
	{
		yield return
		[
			new BatchCase(
				"dot-targets-direct",
				"dot-root",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".cs" },
				SeedDotTargetsWorkspace,
				_ => CreateBaseRules() with
				{
					IgnoreDotFolders = true,
					IgnoreDotFiles = true
				},
				new IgnoreOptionCounts(DotFolders: 1, DotFiles: 1))
		];

		yield return
		[
			new BatchCase(
				"empty-extensionless-emptyfolders-direct",
				"content-root",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".cs" },
				SeedEmptyExtensionlessWorkspace,
				_ => CreateBaseRules() with
				{
					IgnoreEmptyFolders = true,
					IgnoreEmptyFiles = true,
					IgnoreExtensionlessFiles = true
				},
				new IgnoreOptionCounts(EmptyFolders: 1, ExtensionlessFiles: 1, EmptyFiles: 1))
		];

		yield return
		[
			new BatchCase(
				"gitignore-overlap-zeroes-effective-file-counts",
				"git-root",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".cs" },
				SeedGitIgnoredFileTargetsWorkspace,
				BuildGitIgnoreRules,
				IgnoreOptionCounts.Empty)
		];

		yield return
		[
			new BatchCase(
				"selected-dot-root-counts-only-the-root-folder",
				".cache",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				SeedDotRootWorkspace,
				_ => CreateBaseRules() with
				{
					IgnoreDotFolders = true,
					IgnoreExtensionlessFiles = true
				},
				new IgnoreOptionCounts(DotFolders: 1))
		];

		yield return
		[
			new BatchCase(
				"empty-file-toggle-keeps-empty-folder-count-separate",
				"runtime-root",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
				SeedEmptyFileFolderWorkspace,
				_ => CreateBaseRules() with
				{
					IgnoreEmptyFolders = true,
					IgnoreEmptyFiles = true
				},
				new IgnoreOptionCounts(EmptyFolders: 1, EmptyFiles: 1))
		];

		yield return
		[
			new BatchCase(
				"hidden-targets-direct",
				"hidden-root",
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".cs" },
				SeedHiddenTargetsWorkspace,
				_ => CreateBaseRules() with
				{
					IgnoreHiddenFolders = true,
					IgnoreHiddenFiles = true
				},
				new IgnoreOptionCounts(HiddenFolders: 1, HiddenFiles: 1),
				RequiresWindows: true)
		];
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
			GitIgnoreMatcher = GitIgnoreMatcher.Build(gitRootPath, patterns)
		};
	}

	private static void SeedDotTargetsWorkspace(string rootPath)
	{
		WriteFile(rootPath, "dot-root/.target-folder/nested/visible.cs", "class Visible {}");
		WriteFile(rootPath, "dot-root/.target.txt", "dot file");
		WriteFile(rootPath, "dot-root/visible.txt", "visible");
	}

	private static void SeedEmptyExtensionlessWorkspace(string rootPath)
	{
		WriteFile(rootPath, "content-root/README", "extensionless");
		WriteFile(rootPath, "content-root/empty.txt", string.Empty);
		Directory.CreateDirectory(Path.Combine(rootPath, "content-root", "empty-folder"));
		WriteFile(rootPath, "content-root/visible.cs", "class Visible {}");
	}

	private static void SeedGitIgnoredFileTargetsWorkspace(string rootPath)
	{
		WriteFile(rootPath, "git-root/.gitignore", ".target.txt\nREADME\nempty.txt\n");
		WriteFile(rootPath, "git-root/.target.txt", "dot file");
		WriteFile(rootPath, "git-root/README", "extensionless");
		WriteFile(rootPath, "git-root/empty.txt", string.Empty);
		WriteFile(rootPath, "git-root/visible.cs", "class Visible {}");
	}

	private static void SeedDotRootWorkspace(string rootPath)
	{
		WriteFile(rootPath, ".cache/README", "hidden by dot-root");
		WriteFile(rootPath, ".cache/nested/another", "still hidden by dot-root");
	}

	private static void SeedEmptyFileFolderWorkspace(string rootPath)
	{
		WriteFile(rootPath, "runtime-root/folder/empty.txt", string.Empty);
		WriteFile(rootPath, "runtime-root/visible.txt", "visible");
	}

	private static void SeedHiddenTargetsWorkspace(string rootPath)
	{
		var hiddenFolderPath = Path.Combine(rootPath, "hidden-root", "target-hidden-folder");
		Directory.CreateDirectory(Path.Combine(hiddenFolderPath, "nested"));
		WriteFile(rootPath, "hidden-root/target-hidden-folder/nested/visible.cs", "class Visible {}");
		WriteFile(rootPath, "hidden-root/visible.txt", "visible");
		File.SetAttributes(hiddenFolderPath, File.GetAttributes(hiddenFolderPath) | FileAttributes.Hidden);

		var hiddenFilePath = WriteFile(rootPath, "hidden-root/target.txt", "hidden");
		File.SetAttributes(hiddenFilePath, File.GetAttributes(hiddenFilePath) | FileAttributes.Hidden);
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

	public sealed record BatchCase(
		string Name,
		string RootRelativePath,
		IReadOnlySet<string> AllowedExtensions,
		Action<string> Seed,
		Func<string, IgnoreRules> BuildRules,
		IgnoreOptionCounts ExpectedCounts,
		bool RequiresWindows = false)
	{
		public override string ToString() => Name;
	}
}
