using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class GitCloneContentConsumerParityIntegrationTests
{
	[Fact]
	public async Task ShallowClone_OutputFlowsFromCloneResultThroughTreeMetricsPreviewAndExport()
	{
		var gitService = new GitRepositoryService();
		if (!await gitService.IsGitAvailableAsync(TestContext.Current.CancellationToken))
			return;

		await using var repository = await GitTestRepository.CreateAsync(
			repositoryName: "Content-Consumer-Repo",
			cancellationToken: TestContext.Current.CancellationToken);
		await repository.AddCommitToBranchAsync(
			repository.DefaultBranchName,
			Path.Combine("assets", "clone-image.bin"),
			new string('\0', 16),
			"Add binary content probe",
			TestContext.Current.CancellationToken);

		using var cloneParent = new TemporaryDirectory();
		var clonePath = Path.Combine(cloneParent.Path, "managed-clone");
		var cloneResult = await gitService.CloneAsync(
			repository.RepositoryUrl,
			clonePath,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(cloneResult.Success, cloneResult.ErrorMessage);
		Assert.Equal(ProjectSourceType.GitClone, cloneResult.SourceType);
		Assert.Equal(clonePath, cloneResult.LocalPath, PathComparer.Default);

		var services = ProjectLoadWorkflowRefreshHarness.CreateServices(
			transformRules: ExcludeManagedGitMetadata);
		var snapshot = Refresh(
			cloneResult.LocalPath,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(cloneResult.LocalPath));
		var tree = BuildProjectedTree(cloneResult.LocalPath, services, snapshot);
		var orderedFiles = CollectOrderedFiles(tree.Root);

		Assert.DoesNotContain(orderedFiles, path => IsRootGitMetadataPath(cloneResult.LocalPath, path));
		Assert.Contains(orderedFiles, path => path.EndsWith(Path.Combine("src", "app.txt"), StringComparison.Ordinal));
		Assert.Contains(orderedFiles, path => path.EndsWith(Path.Combine("docs", "guide.md"), StringComparison.Ordinal));
		Assert.Contains(orderedFiles, path => path.EndsWith("clone-image.bin", StringComparison.Ordinal));

		var exportedContent = await RenderAndAssertConsumerParityAsync(orderedFiles);
		Assert.Contains("master branch payload", exportedContent, StringComparison.Ordinal);
		Assert.Contains("# Hello-World", exportedContent, StringComparison.Ordinal);
		Assert.DoesNotContain("clone-image.bin:", exportedContent, StringComparison.OrdinalIgnoreCase);
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			cloneResult.LocalPath,
			services.IgnoreRulesService,
			snapshot);
	}

	[Fact]
	public async Task ManagedClone_AllIgnoreCycle_KeepsMetricsPreviewAndExportOnTheSameReadableFiles()
	{
		using var workspace = CreateWorkspace();
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices(
			transformRules: ExcludeManagedGitMetadata);
		var baseline = Refresh(
			workspace.Path,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(workspace.Path));
		var allOff = ApplyAll(workspace.Path, services, baseline, isChecked: false);
		var allOn = ApplyAll(workspace.Path, services, allOff, isChecked: true);

		await AssertContentConsumersStayAlignedAsync(
			workspace.Path,
			services,
			baseline,
			expectDotWorkspaceContent: false);
		await AssertContentConsumersStayAlignedAsync(
			workspace.Path,
			services,
			allOff,
			expectDotWorkspaceContent: true);
		await AssertContentConsumersStayAlignedAsync(
			workspace.Path,
			services,
			allOn,
			expectDotWorkspaceContent: false);
	}

	private static async Task AssertContentConsumersStayAlignedAsync(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool expectDotWorkspaceContent)
	{
		var tree = BuildProjectedTree(rootPath, services, snapshot);
		var orderedFiles = CollectOrderedFiles(tree.Root);

		Assert.NotEmpty(orderedFiles);
		Assert.DoesNotContain(orderedFiles, path => IsRootGitMetadataPath(rootPath, path));
		Assert.Contains(orderedFiles, path => path.EndsWith("CloneContentProbe.cs", StringComparison.Ordinal));
		Assert.Contains(orderedFiles, path => path.EndsWith("clone-guide.md", StringComparison.Ordinal));
		Assert.Equal(
			expectDotWorkspaceContent,
			orderedFiles.Any(path => path.EndsWith("empty.txt", StringComparison.Ordinal)));
		Assert.Contains(orderedFiles, path => path.EndsWith("clone-image.bin", StringComparison.Ordinal));

		var exportedContent = await RenderAndAssertConsumerParityAsync(orderedFiles);
		Assert.Contains("CLONE-CONTENT-SENTINEL", exportedContent, StringComparison.Ordinal);
		Assert.Contains("CLONE-DOCUMENTATION-SENTINEL", exportedContent, StringComparison.Ordinal);
		Assert.Equal(
			expectDotWorkspaceContent,
			exportedContent.Contains("[No Content, 0 bytes]", StringComparison.Ordinal));
		Assert.DoesNotContain("clone-image.bin:", exportedContent, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(
			expectDotWorkspaceContent,
			exportedContent.Contains("CLONE-DOT-WORKSPACE-SENTINEL", StringComparison.Ordinal));

		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			rootPath,
			services.IgnoreRulesService,
			snapshot);
	}

	private static async Task<string> RenderAndAssertConsumerParityAsync(IReadOnlyList<string> orderedFiles)
	{
		var analyzer = new FileContentAnalyzer();
		var renderedMetrics = new List<ContentFileMetrics>();
		foreach (var filePath in orderedFiles)
		{
			var metrics = await analyzer.GetTextFileMetricsAsync(
				filePath,
				TestContext.Current.CancellationToken);
			var content = await analyzer.TryReadAsTextAsync(
				filePath,
				TestContext.Current.CancellationToken);

			Assert.Equal(metrics is not null, content is not null);
			if (metrics is null)
				continue;

			renderedMetrics.Add(new ContentFileMetrics(
				Path: filePath,
				SizeBytes: metrics.SizeBytes,
				LineCount: metrics.LineCount,
				CharCount: metrics.CharCount,
				IsEmpty: metrics.IsEmpty,
				IsWhitespaceOnly: metrics.IsWhitespaceOnly,
				IsEstimated: metrics.IsEstimated,
				CrLfPairCount: metrics.CrLfPairCount,
				TrailingNewlineChars: metrics.TrailingNewlineChars,
				TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
		}

		var contentExport = new SelectedContentExportService(analyzer);
		var exportedContent = await contentExport.BuildAsync(
			orderedFiles,
			TestContext.Current.CancellationToken);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);
		using var previewDocument = await previewBuilder.BuildContentDocumentAsync(
			orderedFiles,
			TestContext.Current.CancellationToken,
			displayPathMapper: null);
		var previewContent = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(previewDocument);

		Assert.Equal(NormalizeLineEndings(exportedContent), NormalizeLineEndings(previewContent));
		Assert.Equal(
			ExportOutputMetricsCalculator.FromText(exportedContent),
			ExportOutputMetricsCalculator.FromOrderedContentFiles(renderedMetrics));
		return exportedContent;
	}

	private static SelectionRefreshSnapshot Refresh(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshContext context)
	{
		return services.Engine.ComputeFullRefreshSnapshot(
			context with { CaptureTreeInventory = true },
			TestContext.Current.CancellationToken);
	}

	private static SelectionRefreshSnapshot ApplyAll(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		bool isChecked)
	{
		var states = snapshot.IgnoreOptionStateCache.Keys.ToDictionary(static id => id, _ => isChecked);
		return Refresh(
			rootPath,
			services,
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot) with
			{
				IgnoreSelectionCache = isChecked ? states.Keys.ToHashSet() : [],
				IgnoreOptionStateCache = states,
				IgnoreAllPreference = isChecked
			});
	}

	private static TreeBuildResult BuildProjectedTree(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot)
	{
		var selectedRoots = ProjectLoadWorkflowRefreshHarness.CollectCheckedRootNames(snapshot);
		var selectedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = ProjectLoadWorkflowRefreshHarness.CollectCheckedIgnoreOptionIds(snapshot);
		var rules = ExcludeManagedGitMetadata(
			services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots));

		return new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
	}

	private static List<string> CollectOrderedFiles(FileSystemNode root)
	{
		var files = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			if (!node.IsDirectory)
			{
				files.Add(node.FullPath);
				continue;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		files.Sort(PathComparer.Default);
		return files;
	}

	private static bool IsRootGitMetadataPath(string rootPath, string candidatePath)
	{
		var relativePath = Path.GetRelativePath(rootPath, candidatePath);
		return string.Equals(relativePath, ".git", StringComparison.OrdinalIgnoreCase) ||
		       relativePath.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeLineEndings(string value) =>
		value.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static IgnoreRules ExcludeManagedGitMetadata(IgnoreRules rules) =>
		rules with { ExcludedRootFolderName = ".git" };

	private static TemporaryDirectory CreateWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile(Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		workspace.CreateFile(Path.Combine(".git", "objects", "pack", "pack-test.pack"), "git metadata\n");
		workspace.CreateFile(
			Path.Combine("src", "CloneContentProbe.cs"),
			"namespace CloneProbe;\npublic static class CloneContentProbe { public const string Value = \"CLONE-CONTENT-SENTINEL\"; }\n");
		workspace.CreateFile(
			Path.Combine("docs", "clone-guide.md"),
			"# Clone guide\n\nCLONE-DOCUMENTATION-SENTINEL\n");
		workspace.CreateFile(Path.Combine("src", "empty.txt"), string.Empty);
		workspace.CreateFile(
			Path.Combine(".workspace", "notes.md"),
			"CLONE-DOT-WORKSPACE-SENTINEL\n");
		workspace.CreateFile(
			Path.Combine("assets", "clone-image.bin"),
			new string('\0', 16));
		return workspace;
	}
}
