namespace DevProjex.Tests.Integration;

public sealed class EffectiveIgnoreOptionCountsContractIntegrationTests
{
	[Theory]
	[MemberData(nameof(RawInventoryButZeroEffectiveCases))]
	public void GetEffectiveIgnoreOptionCountsForRootFolders_WhenRuleAddsNoVisibleTreeDelta_ReturnsZeroEffectiveCount(
		string _,
		Action<string> seedWorkspace,
		HashSet<string> allowedExtensions,
		int expectedTargetRawCount,
		Func<IgnoreRules, IgnoreRules> enableTargetRule,
		Func<IgnoreOptionCounts, int> getTargetCount)
	{
		using var temp = new TemporaryDirectory();
		seedWorkspace(temp.Path);

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var enabledRules = enableTargetRule(CreateGitIgnoreRules(temp.Path));

		var rawScan = scanOptions.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			[],
			enabledRules);
		var effectiveScan = scanOptions.GetEffectiveIgnoreOptionCountsForRootFolders(
			temp.Path,
			[],
			allowedExtensions,
			enabledRules,
			rawScan.Value.IgnoreOptionCounts);
		var disabledRules = enabledRules with
		{
			IgnoreDotFiles = false,
			IgnoreEmptyFiles = false,
			IgnoreExtensionlessFiles = false
		};

		var treeWithRule = BuildTreeDescriptor(temp.Path, allowedExtensions, enabledRules);
		var treeWithoutRule = BuildTreeDescriptor(temp.Path, allowedExtensions, disabledRules);

		// Hidden-vs-dot semantics differ across platforms: on Unix-like systems a dot-file
		// can also surface through the HiddenFiles inventory path, while on Windows it stays
		// purely a dot-file unless the native hidden attribute is set. The contract under test
		// is narrower: the target rule must have raw inventory > 0, but still yield zero
		// effective tree delta because another active rule already removes the same nodes.
		Assert.Equal(expectedTargetRawCount, getTargetCount(rawScan.Value.IgnoreOptionCounts));
		Assert.Equal(0, getTargetCount(effectiveScan.Value));
		Assert.Equal(
			0,
			CountAsciiTreeLines(temp.Path, treeWithoutRule) - CountAsciiTreeLines(temp.Path, treeWithRule));
	}

	public static IEnumerable<object[]> RawInventoryButZeroEffectiveCases()
	{
		yield return
		[
			"dot-files-hidden-by-gitignore",
			new Action<string>(rootPath =>
			{
				WriteFile(rootPath, ".gitignore", ".gitignore\n.config.json\n");
				WriteFile(rootPath, ".config.json", "{ }\n");
				WriteFile(rootPath, "README.md", "# visible\n");
			}),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", ".md" },
			2,
			new Func<IgnoreRules, IgnoreRules>(rules => rules with { IgnoreDotFiles = true }),
			new Func<IgnoreOptionCounts, int>(counts => counts.DotFiles)
		];

		yield return
		[
			"empty-files-hidden-by-gitignore",
			new Action<string>(rootPath =>
			{
				WriteFile(rootPath, ".gitignore", ".gitignore\nempty.txt\n");
				WriteFile(rootPath, "empty.txt", string.Empty);
				WriteFile(rootPath, "keep.txt", "keep");
			}),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt" },
			1,
			new Func<IgnoreRules, IgnoreRules>(rules => rules with { IgnoreEmptyFiles = true }),
			new Func<IgnoreOptionCounts, int>(counts => counts.EmptyFiles)
		];

		yield return
		[
			"extensionless-files-hidden-by-gitignore",
			new Action<string>(rootPath =>
			{
				WriteFile(rootPath, ".gitignore", ".gitignore\nREADME\n");
				WriteFile(rootPath, "README", "keep hidden by gitignore");
				WriteFile(rootPath, "keep.cs", "class Keep {}");
			}),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
			1,
			new Func<IgnoreRules, IgnoreRules>(rules => rules with { IgnoreExtensionlessFiles = true }),
			new Func<IgnoreOptionCounts, int>(counts => counts.ExtensionlessFiles)
		];
	}

	private static IgnoreRules CreateGitIgnoreRules(string rootPath)
	{
		var patterns = File.ReadAllLines(Path.Combine(rootPath, ".gitignore"));
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			UseGitIgnore = true,
			GitIgnoreMatcher = GitIgnoreMatcher.Build(rootPath, patterns)
		};
	}

	private static void WriteFile(string rootPath, string relativePath, string content)
	{
		var fullPath = Path.Combine(rootPath, relativePath);
		var directoryPath = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directoryPath))
			Directory.CreateDirectory(directoryPath);

		File.WriteAllText(fullPath, content);
	}

	private static TreeNodeDescriptor BuildTreeDescriptor(
		string rootPath,
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
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
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
}
