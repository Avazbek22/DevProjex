namespace DevProjex.Tests.Integration;

public sealed class IgnorePipelinePolyglotWorkspaceMatrixIntegrationTests
{
	private static readonly HashSet<string> AllowedExtensions =
	[
		".rs",
		".ts",
		".js",
		".a"
	];

	public enum RootSelectionMode
	{
		All = 0,
		MonoOnly = 1,
		WebOnly = 2,
		DeepOnly = 3,
		MonoWeb = 4,
		WebDeep = 5,
		MonoDeep = 6,
		AllWithDuplicates = 7
	}

	public static IEnumerable<object[]> MatrixCases()
	{
		foreach (var useGitIgnore in new[] { false, true })
		foreach (var useSmartIgnore in new[] { false, true })
		foreach (RootSelectionMode selectionMode in Enum.GetValues(typeof(RootSelectionMode)))
			yield return [useGitIgnore, useSmartIgnore, selectionMode];
	}

	[Theory]
	[MemberData(nameof(MatrixCases))]
	public void IgnorePipeline_PolyglotWorkspace_Matrix(
		bool useGitIgnore,
		bool useSmartIgnore,
		RootSelectionMode selectionMode)
	{
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp);

		var selectedRoots = ResolveSelectedRoots(selectionMode);
		var includesMono = selectedRoots.Contains("mono", StringComparer.OrdinalIgnoreCase);
		var includesWeb = selectedRoots.Contains("web", StringComparer.OrdinalIgnoreCase);
		var includesDeep = selectedRoots.Contains("deep", StringComparer.OrdinalIgnoreCase);

		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new FixedSmartIgnoreRule(["node_modules", "dist"], [])
		]));

		var selectedIgnoreOptions = BuildSelectedIgnoreOptions(useGitIgnore, useSmartIgnore);
		var rules = rulesService.Build(temp.Path, selectedIgnoreOptions, selectedRoots);

		var tree = new TreeBuilder();
		var treeResult = tree.Build(temp.Path, new TreeFilterOptions(
			AllowedExtensions: AllowedExtensions,
			AllowedRootFolders: new HashSet<string>(selectedRoots, StringComparer.OrdinalIgnoreCase),
			IgnoreRules: rules), cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(includesMono, ContainsRootFolder(treeResult, "mono"));
		Assert.Equal(includesWeb, ContainsRootFolder(treeResult, "web"));
		Assert.Equal(includesDeep, ContainsRootFolder(treeResult, "deep"));

		if (includesMono)
		{
			Assert.Equal(!(rules.UseGitIgnore || rules.UseSmartIgnore), ContainsPath(treeResult, "mono/target"));
		}

		if (includesWeb)
		{
			Assert.Equal(!rules.UseSmartIgnore, ContainsPath(treeResult, "web/node_modules"));
		}

		if (includesDeep)
		{
			Assert.Equal(!rules.UseSmartIgnore, ContainsPath(treeResult, "deep/dist"));
		}

		var scanUseCase = new ScanOptionsUseCase(new FileSystemScanner());
		var scan = scanUseCase.GetExtensionsAndIgnoreCountsForRootFolders(
			temp.Path,
			selectedRoots,
			rules, cancellationToken: TestContext.Current.CancellationToken);

		var extensions = scan.Value.Extensions;
		Assert.Equal(includesMono, extensions.Contains(".rs"));
		Assert.Equal(includesWeb || includesDeep, extensions.Contains(".ts"));
		Assert.Equal(includesMono && !(rules.UseGitIgnore || rules.UseSmartIgnore), extensions.Contains(".a"));
		Assert.Equal((includesWeb || includesDeep) && !rules.UseSmartIgnore, extensions.Contains(".js"));
	}

	private static bool ContainsRootFolder(TreeBuildResult result, string rootFolderName)
	{
		return result.Root.Children.Any(x => x.Name.Equals(rootFolderName, StringComparison.OrdinalIgnoreCase));
	}

	private static bool ContainsPath(TreeBuildResult result, string relativePath)
	{
		var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
			return false;

		IReadOnlyList<FileSystemNode> current = result.Root.Children;
		FileSystemNode? found = null;

		foreach (var segment in segments)
		{
			found = current.FirstOrDefault(x => x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
			if (found is null)
				return false;

			current = found.Children;
		}

		return true;
	}

	private static IReadOnlyCollection<string> ResolveSelectedRoots(RootSelectionMode mode)
	{
		return mode switch
		{
			RootSelectionMode.All => ["mono", "web", "deep"],
			RootSelectionMode.MonoOnly => ["mono"],
			RootSelectionMode.WebOnly => ["web"],
			RootSelectionMode.DeepOnly => ["deep"],
			RootSelectionMode.MonoWeb => ["mono", "web"],
			RootSelectionMode.WebDeep => ["web", "deep"],
			RootSelectionMode.MonoDeep => ["mono", "deep"],
			RootSelectionMode.AllWithDuplicates => ["web", "mono", "deep", "mono", "web"],
			_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported mode")
		};
	}

	private static IReadOnlyCollection<IgnoreOptionId> BuildSelectedIgnoreOptions(bool useGitIgnore, bool useSmartIgnore)
	{
		var selected = new List<IgnoreOptionId>(2);
		if (useGitIgnore)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if (useSmartIgnore)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static void SeedWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("mono/.gitignore", "target/\n");
		temp.CreateFile("mono/Cargo.toml", "[package]\nname = \"mono\"\n");
		temp.CreateFile("mono/src/main.rs", "fn main() {}\n");
		temp.CreateFile("mono/target/debug/lib.a", "binary");

		temp.CreateFile("web/package.json", "{}\n");
		temp.CreateFile("web/src/app.ts", "export const app = 1;\n");
		temp.CreateFile("web/node_modules/pkg/index.js", "module.exports = 1;\n");

		temp.CreateFile("deep/package.json", "{}\n");
		temp.CreateFile("deep/src/feature.ts", "export const feature = 1;\n");
		temp.CreateFile("deep/dist/bundle.js", "console.log('bundle');\n");
	}

	private sealed class FixedSmartIgnoreRule(IReadOnlyCollection<string> folders, IReadOnlyCollection<string> files)
		: ISmartIgnoreRule
	{
		public SmartIgnoreResult Evaluate(string rootPath)
		{
			return new SmartIgnoreResult(
				new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(files, StringComparer.OrdinalIgnoreCase));
		}
	}
}
