using DevProjex.Application.Context;

namespace DevProjex.Tests.Integration;

public sealed class RootAliasFailClosedIntegrationTests
{
	[Fact]
	public async Task UnixDirectorySymlinkRoot_IsDeniedAcrossSelectionTreeAnalysisAndContext()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Unix directory symbolic-link behavior is covered on Unix hosts.");

		using var workspace = CreatePhysicalProject();
		var aliasPath = Path.Combine(workspace.Path, "linked-project");
		var physicalPath = Path.Combine(workspace.Path, "physical-project");
		if (!TryCreateDirectorySymlink(aliasPath, physicalPath))
			Assert.Skip("Directory symbolic links are unavailable in this test environment.");

		await AssertRootAliasIsDeniedAcrossSurfaces(aliasPath, physicalPath);
	}

	[Fact]
	public async Task WindowsJunctionRoot_IsDeniedAcrossSelectionTreeAnalysisAndContext()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows junction behavior is covered on Windows hosts.");

		using var workspace = CreatePhysicalProject();
		var aliasPath = Path.Combine(workspace.Path, "junction-project");
		var physicalPath = Path.Combine(workspace.Path, "physical-project");
		if (!TryCreateDirectoryJunction(aliasPath, physicalPath))
			Assert.Skip("The test environment did not allow creating a Windows junction.");

		await AssertRootAliasIsDeniedAcrossSurfaces(aliasPath, physicalPath);
	}

	[Fact]
	public async Task PhysicalRoot_RemainsVisibleAcrossSelectionTreeAnalysisAndContext()
	{
		using var workspace = CreatePhysicalProject();
		var physicalPath = Path.Combine(workspace.Path, "physical-project");
		var rules = CreateIgnoreRules();
		var scanner = new FileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);

		Assert.True(scanner.CanReadRoot(physicalPath));
		var roots = scanOptions.GetRootFolders(
			physicalPath,
			rules,
			TestContext.Current.CancellationToken);
		var extensions = scanOptions.GetExtensionsForRootFolders(
			physicalPath,
			["src"],
			rules,
			TestContext.Current.CancellationToken);
		var tree = BuildTree(physicalPath, rules);
		var analysisService = CreateProjectAnalysisService();
		var analysis = analysisService.Load(
			CreateAnalysisRequest(physicalPath),
			TestContext.Current.CancellationToken);
		var context = await new ProjectContextPlanner(analysisService).BuildAsync(
			CreateContextRequest(physicalPath),
			TestContext.Current.CancellationToken);

		Assert.False(roots.RootAccessDenied);
		Assert.False(roots.HadAccessDenied);
		Assert.Contains("src", roots.Value, PathComparer.Default);
		Assert.False(extensions.RootAccessDenied);
		Assert.Contains(".cs", extensions.Value);
		Assert.False(tree.RootAccessDenied);
		Assert.Contains(tree.Root.Children, static node => node.Name == "src");
		Assert.False(analysis.RootAccessDenied);
		Assert.NotNull(analysis.Tree.OrderedFilePaths);
		Assert.Single(analysis.Tree.OrderedFilePaths);
		Assert.Single(context.IncludedFiles);
		AssertSourceUnchanged(physicalPath);
	}

	private static async Task AssertRootAliasIsDeniedAcrossSurfaces(string aliasPath, string physicalPath)
	{
		var rules = CreateIgnoreRules();
		var scanner = new FileSystemScanner();
		var scanOptions = new ScanOptionsUseCase(scanner);

		Assert.False(scanner.CanReadRoot(aliasPath));
		var roots = scanOptions.GetRootFolders(
			aliasPath,
			rules,
			TestContext.Current.CancellationToken);
		var extensions = scanOptions.GetExtensionsForRootFolders(
			aliasPath,
			["src"],
			rules,
			TestContext.Current.CancellationToken);
		var tree = BuildTree(aliasPath, rules);
		var ownership = new IgnoreOwnershipAuditService().AuditRootDirectories(
			aliasPath,
			rules,
			TestContext.Current.CancellationToken);
		var analysisService = CreateProjectAnalysisService();
		var analysis = analysisService.Load(
			CreateAnalysisRequest(aliasPath),
			TestContext.Current.CancellationToken);
		var context = await new ProjectContextPlanner(analysisService).BuildAsync(
			CreateContextRequest(aliasPath),
			TestContext.Current.CancellationToken);

		Assert.True(roots.RootAccessDenied);
		Assert.True(roots.HadAccessDenied);
		Assert.Empty(roots.Value);
		Assert.True(extensions.RootAccessDenied);
		Assert.True(extensions.HadAccessDenied);
		Assert.Empty(extensions.Value);
		Assert.True(tree.RootAccessDenied);
		Assert.True(tree.HadAccessDenied);
		Assert.Empty(tree.Root.Children);
		Assert.True(ownership.RootAccessDenied);
		Assert.True(ownership.HadAccessDenied);
		Assert.True(analysis.RootAccessDenied);
		Assert.True(analysis.HadAccessDenied);
		Assert.NotNull(analysis.Tree.OrderedFilePaths);
		Assert.Empty(analysis.Tree.OrderedFilePaths);
		Assert.Empty(context.IncludedFiles);
		Assert.Empty(context.EffectiveTree.Children);
		AssertSourceUnchanged(physicalPath);
	}

	private static TreeBuildResult BuildTree(string rootPath, IgnoreRules rules) =>
		new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "src" },
				IgnoreRules: rules),
			TestContext.Current.CancellationToken);

	private static ProjectAnalysisRequest CreateAnalysisRequest(string rootPath) =>
		new(
			rootPath,
			SelectedRootFolders: ["src"],
			SelectedExtensions: [".cs"],
			SelectedIgnoreOptions: []);

	private static ProjectContextRequest CreateContextRequest(string rootPath) =>
		new(
			rootPath,
			new ProjectSelectionSpec(
				Roots: ["src"],
				Extensions: [".cs"],
				GitMode: GitFilteringMode.None,
				Exclusions: []));

	private static ProjectAnalysisService CreateProjectAnalysisService() =>
		new(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());

	private static IgnoreRules CreateIgnoreRules() => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	private static TemporaryDirectory CreatePhysicalProject()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("physical-project/src/app.cs", "public sealed class App {}\n");
		return workspace;
	}

	private static void AssertSourceUnchanged(string physicalPath)
	{
		var files = Directory
			.EnumerateFiles(physicalPath, "*", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(physicalPath, path).Replace('\\', '/'))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(["src/app.cs"], files);
		Assert.Equal(
			"public sealed class App {}\n",
			File.ReadAllText(Path.Combine(physicalPath, "src", "app.cs")));
	}

	private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
	{
		try
		{
			var startInfo = new ProcessStartInfo(
				"cmd.exe",
				$"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			using var process = Process.Start(startInfo);
			if (process is null || !process.WaitForExit(5000))
			{
				process?.Kill(entireProcessTree: true);
				return false;
			}

			return process.ExitCode == 0 &&
			       Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}
}
