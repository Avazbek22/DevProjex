namespace DevProjex.Tests.Unit;

public sealed class TreeExportServiceAdditionalTests
{
	[Theory]
	// Verifies selection detection covers root and nested descendants.
	[InlineData("", false)]
	[InlineData("/root", true)]
	[InlineData("/root/alpha", true)]
	[InlineData("/root/beta", true)]
	[InlineData("/root/beta/delta", true)]
	[InlineData("/root/beta/epsilon", true)]
	[InlineData("/root/gamma", true)]
	[InlineData("/root/unknown", false)]
	[InlineData("/other", false)]
	[InlineData("/root/alpha;/root/beta", true)]
	[InlineData("/root/alpha;/other", true)]
	[InlineData("/other;/another", false)]
	[InlineData("/root/beta/delta;/other", true)]
	[InlineData("/root/beta/epsilon;/root/unknown", true)]
	[InlineData("/root/gamma;/root/unknown", true)]
	[InlineData("/root/alpha;/root/beta/delta", true)]
	[InlineData("/root/beta/delta;/root/beta/epsilon", true)]
	[InlineData("/root/beta/delta;/root/gamma", true)]
	[InlineData("/root/unknown;/root/missing", false)]
	[InlineData(";/root", true)]
	public void HasSelectedDescendantOrSelf_DetectsSelection(string selectedPaths, bool expected)
	{
		var root = BuildTree();
		var selected = ParseSelections(selectedPaths);

		var result = TreeExportService.HasSelectedDescendantOrSelf(root, selected);

		Assert.Equal(expected, result);
	}

	[Fact]
	// Verifies full tree output includes root path and top-level display name.
	public void BuildFullTree_IncludesRootHeader()
	{
		var service = new TreeExportService();
		var root = BuildTree();

		var output = service.BuildFullTree("/root", root);

		Assert.Contains("/root:", output);
		Assert.Contains("├── Root", output);
	}

	[Fact]
	// Verifies full tree output includes all children display names.
	public void BuildFullTree_IncludesAllChildren()
	{
		var service = new TreeExportService();
		var root = BuildTree();

		var output = service.BuildFullTree("/root", root);

		Assert.Contains("Alpha", output);
		Assert.Contains("Beta", output);
		Assert.Contains("Gamma", output);
		Assert.Contains("Delta", output);
		Assert.Contains("Epsilon", output);
	}

	[Fact]
	// Verifies selected tree output is empty when nothing is selected.
	public void BuildSelectedTree_NoSelection_ReturnsEmpty()
	{
		var service = new TreeExportService();
		var root = BuildTree();

		var output = service.BuildSelectedTree("/root", root, new HashSet<string>());

		Assert.Equal(string.Empty, output);
	}

	[Fact]
	// Verifies selected tree output includes only selected branches.
	public void BuildSelectedTree_IncludesSelectedBranchOnly()
	{
		var service = new TreeExportService();
		var root = BuildTree();
		var selected = new HashSet<string> { "/root/beta/delta" };

		var output = service.BuildSelectedTree("/root", root, selected);

		Assert.Contains("Root", output);
		Assert.Contains("Beta", output);
		Assert.Contains("Delta", output);
		Assert.DoesNotContain("Alpha", output);
		Assert.DoesNotContain("Gamma", output);
		Assert.DoesNotContain("Epsilon", output);
	}

	private static TreeNodeDescriptor BuildTree()
	{
		var delta = new TreeNodeDescriptor("Delta", "/root/beta/delta", false, false, "file", new List<TreeNodeDescriptor>());
		var epsilon = new TreeNodeDescriptor("Epsilon", "/root/beta/epsilon", false, false, "file", new List<TreeNodeDescriptor>());
		var beta = new TreeNodeDescriptor("Beta", "/root/beta", true, false, "folder", new List<TreeNodeDescriptor> { delta, epsilon });
		var alpha = new TreeNodeDescriptor("Alpha", "/root/alpha", false, false, "file", new List<TreeNodeDescriptor>());
		var gamma = new TreeNodeDescriptor("Gamma", "/root/gamma", false, false, "file", new List<TreeNodeDescriptor>());

		return new TreeNodeDescriptor("Root", "/root", true, false, "folder", new List<TreeNodeDescriptor> { alpha, beta, gamma });
	}

	private static IReadOnlySet<string> ParseSelections(string selections)
	{
		return new HashSet<string>(
			selections.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
			StringComparer.OrdinalIgnoreCase);
	}
}
