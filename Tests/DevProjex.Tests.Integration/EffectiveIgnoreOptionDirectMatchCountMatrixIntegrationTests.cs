namespace DevProjex.Tests.Integration;

public sealed class EffectiveIgnoreOptionDirectMatchCountMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(ContractCases))]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_CountsDirectMatchesWithoutInflatingDescendants(
		TargetCase target,
		bool ruleEnabled)
	{
		if (target.RequiresWindows && !OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		target.Seed(temp.Path);

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = target.BuildRules(temp.Path, ruleEnabled);
		var rawScan = scanOptions.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			[target.RootName],
			rules);
		var effectiveScan = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			[target.RootName],
			target.AllowedExtensions,
			rules,
			rawScan.Value.IgnoreOptionCounts);

		var toggledRules = target.BuildRules(temp.Path, !ruleEnabled);
		var currentTree = BuildTreeDescriptor(temp.Path, target.RootName, target.AllowedExtensions, rules);
		var toggledTree = BuildTreeDescriptor(temp.Path, target.RootName, target.AllowedExtensions, toggledRules);

		var effectiveCount = GetTargetCount(effectiveScan.Value, target.OptionId);
		var rawCount = GetTargetCount(rawScan.Value.IgnoreOptionCounts, target.OptionId);
		var lineDelta = Math.Abs(
			CountAsciiTreeLines(temp.Path, currentTree) -
			CountAsciiTreeLines(temp.Path, toggledTree));

		Assert.Equal(1, rawCount);
		Assert.Equal(1, effectiveCount);
		Assert.True(lineDelta > effectiveCount);
	}

	public static IEnumerable<object[]> ContractCases()
	{
		foreach (var target in BuildTargets())
		foreach (var ruleEnabled in new[] { false, true })
			yield return [target, ruleEnabled];
	}

	private static IEnumerable<TargetCase> BuildTargets()
	{
		yield return new TargetCase(
			IgnoreOptionId.HiddenFolders,
			"hidden-folder-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			SeedHiddenFolderWorkspace,
			BuildHiddenFolderRules,
			RequiresWindows: true);
		yield return new TargetCase(
			IgnoreOptionId.DotFolders,
			"dot-folder-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			SeedDotFolderWorkspace,
			BuildDotFolderRules);
		yield return new TargetCase(
			IgnoreOptionId.HiddenFiles,
			"hidden-file-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			SeedHiddenFileWorkspace,
			BuildHiddenFileRules,
			RequiresWindows: true);
		yield return new TargetCase(
			IgnoreOptionId.DotFiles,
			"dot-file-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			SeedDotFileWorkspace,
			BuildDotFileRules);
		yield return new TargetCase(
			IgnoreOptionId.EmptyFiles,
			"empty-file-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			SeedEmptyFileWorkspace,
			BuildEmptyFileRules);
		yield return new TargetCase(
			IgnoreOptionId.ExtensionlessFiles,
			"extensionless-root",
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			SeedExtensionlessWorkspace,
			BuildExtensionlessRules);
	}

	private static void SeedHiddenFolderWorkspace(string rootPath)
	{
		var hiddenFolder = CreateDirectory(rootPath, "hidden-folder-root/target-hidden-folder/nested");
		WriteFile(rootPath, "hidden-folder-root/target-hidden-folder/nested/one.cs", "class One {}");
		WriteFile(rootPath, "hidden-folder-root/target-hidden-folder/nested/two.cs", "class Two {}");
		WriteFile(rootPath, "hidden-folder-root/visible.cs", "class Visible {}");
		MarkHidden(Path.GetDirectoryName(hiddenFolder)!);
	}

	private static void SeedDotFolderWorkspace(string rootPath)
	{
		WriteFile(rootPath, "dot-folder-root/.target-dot-folder/nested/one.cs", "class One {}");
		WriteFile(rootPath, "dot-folder-root/.target-dot-folder/nested/two.cs", "class Two {}");
		WriteFile(rootPath, "dot-folder-root/visible.cs", "class Visible {}");
	}

	private static void SeedHiddenFileWorkspace(string rootPath)
	{
		var hiddenFile = WriteFile(rootPath, "hidden-file-root/a/b/target.txt", "secret");
		WriteFile(rootPath, "hidden-file-root/visible.txt", "visible");
		MarkHidden(hiddenFile);
	}

	private static void SeedDotFileWorkspace(string rootPath)
	{
		WriteFile(rootPath, "dot-file-root/a/b/.target.txt", "secret");
		WriteFile(rootPath, "dot-file-root/visible.txt", "visible");
	}

	private static void SeedEmptyFileWorkspace(string rootPath)
	{
		WriteFile(rootPath, "empty-file-root/a/b/target.txt", string.Empty);
		WriteFile(rootPath, "empty-file-root/visible.txt", "visible");
	}

	private static void SeedExtensionlessWorkspace(string rootPath)
	{
		WriteFile(rootPath, "extensionless-root/a/b/TARGETREADME", "secret");
		WriteFile(rootPath, "extensionless-root/visible.txt", "visible");
	}

	private static IgnoreRules BuildHiddenFolderRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: ruleEnabled,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

	private static IgnoreRules BuildDotFolderRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: ruleEnabled,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>());

	private static IgnoreRules BuildHiddenFileRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: ruleEnabled,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFolders = true
		};

	private static IgnoreRules BuildDotFileRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: ruleEnabled,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFolders = true
		};

	private static IgnoreRules BuildEmptyFileRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreEmptyFiles = ruleEnabled,
			IgnoreEmptyFolders = true
		};

	private static IgnoreRules BuildExtensionlessRules(string _, bool ruleEnabled) =>
		new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ruleEnabled,
			IgnoreEmptyFolders = true
		};

	private static void MarkHidden(string path)
	{
		var attributes = File.GetAttributes(path);
		File.SetAttributes(path, attributes | FileAttributes.Hidden);
	}

	private static string CreateDirectory(string rootPath, string relativePath)
	{
		var fullPath = Path.Combine(rootPath, relativePath);
		Directory.CreateDirectory(fullPath);
		return fullPath;
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

	private static int GetTargetCount(IgnoreOptionCounts counts, IgnoreOptionId optionId)
	{
		return optionId switch
		{
			IgnoreOptionId.HiddenFolders => counts.HiddenFolders,
			IgnoreOptionId.HiddenFiles => counts.HiddenFiles,
			IgnoreOptionId.DotFolders => counts.DotFolders,
			IgnoreOptionId.DotFiles => counts.DotFiles,
			IgnoreOptionId.EmptyFiles => counts.EmptyFiles,
			IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles,
			_ => throw new ArgumentOutOfRangeException(nameof(optionId), optionId, null)
		};
	}

	private static TreeNodeDescriptor BuildTreeDescriptor(
		string rootPath,
		string selectedRoot,
		IReadOnlySet<string> allowedExtensions,
		IgnoreRules rules)
	{
		var presenter = new TreeNodePresentationService(
			new LocalizationService(new TreeLocalizationCatalog(), AppLanguage.En),
			new TreeIconMapper());
		var buildTreeUseCase = new BuildTreeUseCase(new TreeBuilder(), presenter);

		return buildTreeUseCase.Execute(new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				allowedExtensions,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedRoot },
				rules)))
			.Root;
	}

	private static int CountAsciiTreeLines(string rootPath, TreeNodeDescriptor root)
	{
		var treeText = new TreeExportService().BuildFullTree(rootPath, root, TreeTextFormat.Ascii);
		return ExportOutputMetricsCalculator.FromText(treeText).Lines;
	}

	private sealed class TreeLocalizationCatalog : ILocalizationCatalog
	{
		public IReadOnlyDictionary<string, string> Get(AppLanguage language)
		{
			return new Dictionary<string, string>
			{
				["Tree.AccessDeniedRoot"] = "Access denied",
				["Tree.AccessDenied"] = "Access denied"
			};
		}
	}

	private sealed class TreeIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
	}

	public sealed record TargetCase(
		IgnoreOptionId OptionId,
		string RootName,
		IReadOnlySet<string> AllowedExtensions,
		Action<string> Seed,
		Func<string, bool, IgnoreRules> BuildRules,
		bool RequiresWindows = false);
}
