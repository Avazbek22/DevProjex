namespace DevProjex.Tests.Integration;

public sealed class HierarchicalGitIgnoreConsumerParityIntegrationTests
{
	[Fact]
	public async Task FourScopeWorkspace_TreeContentPreviewAndExportConsumeExactlyTheProjectedFiles()
	{
		using var temp = CreateWorkspace();
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var tree = BuildProjectedTree(temp.Path, services, snapshot);
		var filePaths = CollectOrderedFiles(tree.Root);
		var descriptor = ToDescriptor(tree.Root);
		var analyzer = new FileContentAnalyzer();
		var treeExport = new TreeExportService();
		var contentExport = new SelectedContentExportService(analyzer);
		var treeText = treeExport.BuildFullTree(temp.Path, descriptor, TreeTextFormat.Ascii);
		var exportedContent = await contentExport.BuildAsync(
			filePaths,
			TestContext.Current.CancellationToken);
		var combinedExport = await new TreeAndContentExportService(treeExport, contentExport).BuildAsync(
			temp.Path,
			descriptor,
			new HashSet<string>(PathComparer.Default),
			TreeTextFormat.Ascii,
			TestContext.Current.CancellationToken);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);
		using var contentPreview = await previewBuilder.BuildContentDocumentAsync(
			filePaths,
			TestContext.Current.CancellationToken,
			displayPathMapper: null);
		using var combinedPreview = await previewBuilder.BuildTreeAndContentDocumentAsync(
			treeText,
			filePaths,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path));

		var contentPreviewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(contentPreview);
		var combinedPreviewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(combinedPreview);
		Assert.Equal(Normalize(exportedContent), Normalize(contentPreviewPayload));
		Assert.Equal(Normalize(combinedExport), Normalize(combinedPreviewPayload));

		AssertVisibleTreeEntries(treeText);
		AssertIgnoredTreeEntriesAbsent(treeText);
		AssertVisibleSentinels(exportedContent, combinedExport);
		AssertIgnoredSentinelsAbsent(exportedContent, combinedExport);
		SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
			temp.Path,
			services.IgnoreRulesService,
			snapshot);
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
		var rules = services.IgnoreRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);

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

	private static TreeNodeDescriptor ToDescriptor(FileSystemNode node) =>
		new(
			DisplayName: node.Name,
			FullPath: node.FullPath,
			IsDirectory: node.IsDirectory,
			IsAccessDenied: node.IsAccessDenied,
			IconKey: node.IsDirectory ? "folder" : "text",
			Children: node.Children.Select(ToDescriptor).ToArray());

	private static void AssertVisibleTreeEntries(string treeText)
	{
		foreach (var fileName in new[]
		         {
			         "keep.rootdrop",
			         "module-keep.rootdrop",
			         "rescue.moddrop",
			         "visible.deepdrop",
			         "visible.txt",
			         "visible.siblingdrop"
		         })
		{
			Assert.Contains(fileName, treeText, StringComparison.Ordinal);
		}
	}

	private static void AssertIgnoredTreeEntriesAbsent(string treeText)
	{
		foreach (var fileName in new[]
		         {
			         "drop.rootdrop",
			         "drop.moddrop",
			         "drop.deepdrop",
			         "drop.lastdrop",
			         "drop.siblingdrop"
		         })
		{
			Assert.DoesNotContain(fileName, treeText, StringComparison.Ordinal);
		}
	}

	private static void AssertVisibleSentinels(params string[] payloads)
	{
		string[] expected =
		[
			"ROOT-KEEP-SENTINEL",
			"MODULE-KEEP-SENTINEL",
			"CHILD-RESCUE-SENTINEL",
			"GRAND-KEEP-SENTINEL",
			"MALFORMED-RULE-SENTINEL",
			"SIBLING-ISOLATION-SENTINEL"
		];

		foreach (var payload in payloads)
		foreach (var sentinel in expected)
			Assert.Contains(sentinel, payload, StringComparison.Ordinal);
	}

	private static void AssertIgnoredSentinelsAbsent(params string[] payloads)
	{
		string[] ignored =
		[
			"ROOT-DROP-SENTINEL",
			"MODULE-DROP-SENTINEL",
			"CHILD-DROP-SENTINEL",
			"GRAND-DROP-SENTINEL",
			"SIBLING-DROP-SENTINEL"
		];

		foreach (var payload in payloads)
		foreach (var sentinel in ignored)
			Assert.DoesNotContain(sentinel, payload, StringComparison.Ordinal);
	}

	private static string Normalize(string value) =>
		value.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static TemporaryDirectory CreateWorkspace()
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile("repo/.gitignore", "*.rootdrop\n!keep.rootdrop\n[unterminated\n");
		temp.CreateFile("repo/drop.rootdrop", "ROOT-DROP-SENTINEL\n");
		temp.CreateFile("repo/keep.rootdrop", "ROOT-KEEP-SENTINEL\n");
		temp.CreateFile("repo/module/.gitignore", "!module-keep.rootdrop\n*.moddrop\n");
		temp.CreateFile("repo/module/module-keep.rootdrop", "MODULE-KEEP-SENTINEL\n");
		temp.CreateFile("repo/module/drop.moddrop", "MODULE-DROP-SENTINEL\n");
		temp.CreateFile("repo/module/child/.gitignore", "!rescue.moddrop\n*.deepdrop\n");
		temp.CreateFile("repo/module/child/rescue.moddrop", "CHILD-RESCUE-SENTINEL\n");
		temp.CreateFile("repo/module/child/drop.deepdrop", "CHILD-DROP-SENTINEL\n");
		temp.CreateFile("repo/module/child/grand/.gitignore", "!visible.deepdrop\n*.lastdrop\ninvalid\\\n");
		temp.CreateFile("repo/module/child/grand/visible.deepdrop", "GRAND-KEEP-SENTINEL\n");
		temp.CreateFile("repo/module/child/grand/drop.lastdrop", "GRAND-DROP-SENTINEL\n");
		temp.CreateFile("repo/module/child/grand/invalid/visible.txt", "MALFORMED-RULE-SENTINEL\n");
		temp.CreateFile("repo/sibling/.gitignore", "*.siblingdrop\n");
		temp.CreateFile("repo/sibling/drop.siblingdrop", "SIBLING-DROP-SENTINEL\n");
		temp.CreateFile("repo/outside/visible.siblingdrop", "SIBLING-ISOLATION-SENTINEL\n");
		return temp;
	}
}
