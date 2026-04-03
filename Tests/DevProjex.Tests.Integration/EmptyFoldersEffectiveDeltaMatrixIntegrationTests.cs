namespace DevProjex.Tests.Integration;

public sealed class EmptyFoldersEffectiveDeltaMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(ContractCases))]
	public void EffectiveEmptyFoldersCount_MatchesExactTreeDeltaAcrossFilterComposition(
		SelectedRootsCase selectedRootsCase,
		AllowedExtensionsMode allowedExtensionsMode,
		FilterProfile filterProfile)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var selectedRoots = GetSelectedRoots(selectedRootsCase);
		var allowedExtensions = CreateAllowedExtensions(allowedExtensionsMode);
		var rulesWithoutEmptyFolders = CreateRules(
			temp.Path,
			filterProfile,
			ignoreEmptyFolders: false);

		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var effectiveCount = scanOptions.GetEffectiveEmptyFolderCountForRootFolders(
			temp.Path,
			selectedRoots,
			allowedExtensions,
			rulesWithoutEmptyFolders)
			.Value;

		var expected = GetExpectedEffectiveCount(selectedRootsCase, allowedExtensionsMode, filterProfile);
		var treeWithoutEmptyFolderPruning = BuildTreeDescriptor(
			temp.Path,
			selectedRoots,
			allowedExtensions,
			rulesWithoutEmptyFolders);
		var treeWithEmptyFolderPruning = BuildTreeDescriptor(
			temp.Path,
			selectedRoots,
			allowedExtensions,
			rulesWithoutEmptyFolders with { IgnoreEmptyFolders = true });

		Assert.Equal(expected, effectiveCount);
		Assert.Equal(
			expected,
			CountDirectoryNodes(treeWithoutEmptyFolderPruning) - CountDirectoryNodes(treeWithEmptyFolderPruning));
		Assert.Equal(
			expected,
			CountAsciiTreeLines(temp.Path, treeWithoutEmptyFolderPruning) -
			CountAsciiTreeLines(temp.Path, treeWithEmptyFolderPruning));
	}

	public static IEnumerable<object[]> ContractCases()
	{
		foreach (var selectedRootsCase in Enum.GetValues<SelectedRootsCase>())
		{
			foreach (var allowedExtensionsMode in Enum.GetValues<AllowedExtensionsMode>())
			{
				foreach (var filterProfile in FilterProfiles())
					yield return [selectedRootsCase, allowedExtensionsMode, filterProfile];
			}
		}
	}

	private static IEnumerable<FilterProfile> FilterProfiles()
	{
		yield return new FilterProfile("None", UseGitIgnore: false, UseSmartIgnore: false, IgnoreEmptyFiles: false, IgnoreExtensionlessFiles: false);
		yield return new FilterProfile("GitOnly", UseGitIgnore: true, UseSmartIgnore: false, IgnoreEmptyFiles: false, IgnoreExtensionlessFiles: false);
		yield return new FilterProfile("SmartOnly", UseGitIgnore: false, UseSmartIgnore: true, IgnoreEmptyFiles: false, IgnoreExtensionlessFiles: false);
		yield return new FilterProfile("EmptyFilesOnly", UseGitIgnore: false, UseSmartIgnore: false, IgnoreEmptyFiles: true, IgnoreExtensionlessFiles: false);
		yield return new FilterProfile("ExtensionlessOnly", UseGitIgnore: false, UseSmartIgnore: false, IgnoreEmptyFiles: false, IgnoreExtensionlessFiles: true);
		yield return new FilterProfile("Everything", UseGitIgnore: true, UseSmartIgnore: true, IgnoreEmptyFiles: true, IgnoreExtensionlessFiles: true);
	}

	private static IReadOnlyCollection<string> GetSelectedRoots(SelectedRootsCase selectedRootsCase)
	{
		return selectedRootsCase switch
		{
			SelectedRootsCase.GitOnly => ["git-root"],
			SelectedRootsCase.SmartOnly => ["smart-root"],
			SelectedRootsCase.MarkdownOnly => ["markdown-root"],
			SelectedRootsCase.EmptyFileOnly => ["empty-file-root"],
			SelectedRootsCase.ExtensionlessOnly => ["extensionless-root"],
			SelectedRootsCase.AllRoots => ["git-root", "smart-root", "markdown-root", "empty-file-root", "extensionless-root"],
			_ => throw new ArgumentOutOfRangeException(nameof(selectedRootsCase), selectedRootsCase, null)
		};
	}

	private static HashSet<string> CreateAllowedExtensions(AllowedExtensionsMode allowedExtensionsMode)
	{
		return allowedExtensionsMode switch
		{
			AllowedExtensionsMode.AllRelevant => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs",
				".md"
			},
			AllowedExtensionsMode.CSharpOnly => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				".cs"
			},
			_ => throw new ArgumentOutOfRangeException(nameof(allowedExtensionsMode), allowedExtensionsMode, null)
		};
	}

	private static IgnoreRules CreateRules(
		string workspaceRoot,
		FilterProfile filterProfile,
		bool ignoreEmptyFolders)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: filterProfile.UseSmartIgnore
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" }
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			UseGitIgnore = filterProfile.UseGitIgnore,
			UseSmartIgnore = filterProfile.UseSmartIgnore,
			IgnoreEmptyFolders = ignoreEmptyFolders,
			IgnoreEmptyFiles = filterProfile.IgnoreEmptyFiles,
			IgnoreExtensionlessFiles = filterProfile.IgnoreExtensionlessFiles,
			GitIgnoreMatcher = filterProfile.UseGitIgnore
				? GitIgnoreMatcher.Build(workspaceRoot, ["git-root/**/generated/"])
				: GitIgnoreMatcher.Empty
		};
	}

	private static TreeNodeDescriptor BuildTreeDescriptor(
		string rootPath,
		IReadOnlyCollection<string> selectedRoots,
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
				new HashSet<string>(selectedRoots, StringComparer.OrdinalIgnoreCase),
				rules)))
			.Root;
	}

	private static int CountDirectoryNodes(TreeNodeDescriptor root)
	{
		var count = 0;
		foreach (var child in root.Children)
			count += CountDirectoryNodesCore(child);

		return count;
	}

	private static int CountDirectoryNodesCore(TreeNodeDescriptor node)
	{
		var count = node.IsDirectory ? 1 : 0;
		foreach (var child in node.Children)
			count += CountDirectoryNodesCore(child);

		return count;
	}

	private static int CountAsciiTreeLines(string rootPath, TreeNodeDescriptor root)
	{
		var treeText = new TreeExportService().BuildFullTree(rootPath, root, TreeTextFormat.Ascii);
		return ExportOutputMetricsCalculator.FromText(treeText).Lines;
	}

	private static int GetExpectedEffectiveCount(
		SelectedRootsCase selectedRootsCase,
		AllowedExtensionsMode allowedExtensionsMode,
		FilterProfile filterProfile)
	{
		var rootNames = GetSelectedRoots(selectedRootsCase);
		var expected = 0;

		foreach (var rootName in rootNames)
		{
			expected += rootName switch
			{
				"git-root" => filterProfile.UseGitIgnore ? 1 : 0,
				"smart-root" => filterProfile.UseSmartIgnore ? 1 : 0,
				"markdown-root" => allowedExtensionsMode == AllowedExtensionsMode.CSharpOnly ? 2 : 0,
				"empty-file-root" => filterProfile.IgnoreEmptyFiles ? 2 : 0,
				"extensionless-root" => filterProfile.IgnoreExtensionlessFiles ? 2 : 0,
				_ => throw new InvalidOperationException($"Unexpected root name '{rootName}'.")
			};
		}

		return expected;
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("git-root/keep.cs", "class Keep {}");
		temp.CreateFile("git-root/mixed-parent/generated/child.cs", "class Generated {}");

		temp.CreateFile("smart-root/keep.cs", "class Keep {}");
		temp.CreateFile("smart-root/mixed-parent/node_modules/pkg/index.cs", "class Package {}");

		temp.CreateFile("markdown-root/keep.cs", "class Keep {}");
		temp.CreateFile("markdown-root/mixed-parent/docs/readme.md", "# doc");

		temp.CreateFile("empty-file-root/keep.cs", "class Keep {}");
		temp.CreateFile("empty-file-root/mixed-parent/leaf/empty.cs", string.Empty);

		temp.CreateFile("extensionless-root/keep.cs", "class Keep {}");
		temp.CreateFile("extensionless-root/mixed-parent/leaf/README", "note");
	}

	public enum SelectedRootsCase
	{
		GitOnly,
		SmartOnly,
		MarkdownOnly,
		EmptyFileOnly,
		ExtensionlessOnly,
		AllRoots
	}

	public enum AllowedExtensionsMode
	{
		AllRelevant,
		CSharpOnly
	}

	public sealed record FilterProfile(
		string Name,
		bool UseGitIgnore,
		bool UseSmartIgnore,
		bool IgnoreEmptyFiles,
		bool IgnoreExtensionlessFiles)
	{
		public override string ToString() => Name;
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
