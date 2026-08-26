using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class ProjectAnalysisCancellationTests
{
	#pragma warning disable xUnit1051 // This test cancels analysis from the controlled facts provider.
	[Fact]
	public void ExplicitSelectionCancellationStopsDuringDefaultIgnoreDiscovery()
	{
		const int candidateCount = 256;
		using var temp = new TemporaryDirectory();
		var directories = Enumerable.Range(0, candidateCount)
			.Select(index =>
			{
				var name = $"scope-{index:D3}";
				return new ProjectRootDirectoryFact(
					name,
					Path.Combine(temp.Path, name),
					IsReparsePoint: false);
			})
			.ToArray();
		using var cancellation = new CancellationTokenSource();
		var inspectedCandidates = 0;
		var factsProvider = new ProjectRootFactsProvider(
			cacheTtl: TimeSpan.Zero,
			cacheLimit: 0,
			utcNowProvider: null,
			factsBuilder: path =>
			{
				if (PathComparer.Default.Equals(path, temp.Path))
				{
					return new ProjectRootFacts(
						temp.Path,
						exists: true,
						isAccessible: true,
						files: [],
						directories,
						gitIgnoreSignature: null);
				}

				Interlocked.Increment(ref inspectedCandidates);
				cancellation.Cancel();
				return new ProjectRootFacts(
					path,
					exists: true,
					isAccessible: true,
					files: [new ProjectRootFileFact("package.json", ".json", IsReparsePoint: false)],
					directories: [],
					gitIgnoreSignature: null);
			});
		var smartIgnore = new SmartIgnoreService([], factsProvider);
		var ignoreRules = new IgnoreRulesService(
			smartIgnore,
			new ProjectScopeDiscoveryService(smartIgnore, factsProvider));

		Assert.ThrowsAny<OperationCanceledException>(() =>
			CreateService(ignoreRules).Load(
				new ProjectAnalysisRequest(
					temp.Path,
					SelectedRootFolders: ["scope-000"]),
				cancellation.Token));
		Assert.InRange(Volatile.Read(ref inspectedCandidates), 1, candidateCount - 1);
	}
	#pragma warning restore xUnit1051

	private static ProjectAnalysisService CreateService(IgnoreRulesService ignoreRules)
	{
		var localization = new LocalizationService(new TestLocalizationCatalog(), AppLanguage.En);
		return new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			new BuildTreeUseCase(
				new TreeBuilder(),
				new TreeNodePresentationService(localization, new TestIconMapper())),
			new FilterOptionSelectionService(),
			new IgnoreOptionsService(localization),
			ignoreRules,
			new TreeExportService(),
			new FileContentAnalyzer());
	}

	private sealed class TestLocalizationCatalog : ILocalizationCatalog
	{
		public IReadOnlyDictionary<string, string> Get(AppLanguage language) =>
			new Dictionary<string, string>
			{
				["Tree.AccessDenied"] = "Access denied",
				["Settings.Ignore.SmartIgnore"] = "Smart ignore",
				["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
				["Settings.Ignore.HiddenFolders"] = "Hidden folders",
				["Settings.Ignore.HiddenFiles"] = "Hidden files",
				["Settings.Ignore.DotFolders"] = "Dot folders",
				["Settings.Ignore.DotFiles"] = "Dot files",
				["Settings.Ignore.EmptyFolders"] = "Empty folders",
				["Settings.Ignore.EmptyFiles"] = "Empty files",
				["Settings.Ignore.ExtensionlessFiles"] = "Files without extension"
			};
	}

	private sealed class TestIconMapper : IIconMapper
	{
		public string GetIconKey(FileSystemNode node) => node.IsDirectory ? "folder" : "file";
	}
}
