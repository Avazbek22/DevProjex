using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class TreeSearchEngineTests
{
	[Theory]
	// Verifies case-insensitive search returns expected match counts across queries.
	[InlineData("root", 1)]
	[InlineData("ROOT", 1)]
	[InlineData("alpha", 1)]
	[InlineData("ALPHA", 1)]
	[InlineData("beta", 1)]
	[InlineData("ta", 2)]
	[InlineData("DEL", 1)]
	[InlineData("epsilon", 1)]
	[InlineData("EPS", 1)]
	[InlineData("gamma", 1)]
	[InlineData("missing", 0)]
	[InlineData("a", 4)]
	public void CollectMatches_CaseInsensitive_ReturnsExpectedCounts(string query, int expectedCount)
	{
		var roots = BuildTree(out _);

		var matches = TreeSearchEngine.CollectMatches(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			StringComparison.OrdinalIgnoreCase);

		Assert.Equal(expectedCount, matches.Count);
	}

	[Theory]
	// Verifies case-sensitive search respects exact casing.
	[InlineData("root", 0)]
	[InlineData("Root", 1)]
	[InlineData("alpha", 0)]
	[InlineData("Alpha", 1)]
	[InlineData("BETA", 0)]
	[InlineData("Beta", 1)]
	[InlineData("DELTA", 0)]
	[InlineData("Delta", 1)]
	public void CollectMatches_CaseSensitive_ReturnsExpectedCounts(string query, int expectedCount)
	{
		var roots = BuildTree(out _);

		var matches = TreeSearchEngine.CollectMatches(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			StringComparison.Ordinal);

		Assert.Equal(expectedCount, matches.Count);
	}

	[Theory]
	// Verifies smart expand for search expands ancestors with matching descendants.
	[InlineData("Delta", true, true)]
	[InlineData("Epsilon", true, true)]
	[InlineData("Alpha", true, false)]
	[InlineData("Gamma", true, false)]
	[InlineData("Root", false, false)]
	[InlineData("Missing", false, false)]
	public void ApplySmartExpandForSearch_ExpandsAncestors(string query, bool expectedRoot, bool expectedBeta)
	{
		var roots = BuildTree(out var nodes);

		TreeSearchEngine.ApplySmartExpandForSearch(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			node => node.Children.Count > 0,
			(node, expanded) => node.Expanded = expanded);

		Assert.Equal(expectedRoot, nodes["Root"].Expanded);
		Assert.Equal(expectedBeta, nodes["Beta"].Expanded);
	}

	[Theory]
	// Verifies smart expand for search collapses non-matching branches with children.
	[InlineData("Delta", true)]
	[InlineData("Epsilon", true)]
	[InlineData("Alpha", false)]
	[InlineData("Gamma", false)]
	[InlineData("Root", false)]
	[InlineData("Missing", false)]
	public void ApplySmartExpandForSearch_CollapsesNonMatching(string query, bool expectedBetaExpanded)
	{
		var roots = BuildTree(out var nodes);
		nodes["Beta"].Expanded = true;

		TreeSearchEngine.ApplySmartExpandForSearch(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			node => node.Children.Count > 0,
			(node, expanded) => node.Expanded = expanded);

		Assert.Equal(expectedBetaExpanded, nodes["Beta"].Expanded);
	}

	[Theory]
	// Verifies smart expand for filter expands ancestors that contain matches.
	[InlineData("Delta", true, true)]
	[InlineData("Epsilon", true, true)]
	[InlineData("Alpha", true, false)]
	[InlineData("Gamma", true, false)]
	[InlineData("Root", false, false)]
	[InlineData("Missing", false, false)]
	public void ApplySmartExpandForFilter_ExpandsAncestors(string query, bool expectedRoot, bool expectedBeta)
	{
		var roots = BuildTree(out var nodes);

		TreeSearchEngine.ApplySmartExpandForFilter(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			(node, expanded) => node.Expanded = expanded);

		Assert.Equal(expectedRoot, nodes["Root"].Expanded);
		Assert.Equal(expectedBeta, nodes["Beta"].Expanded);
	}

	[Theory]
	// Verifies smart expand for filter collapses branches without matches.
	[InlineData("Delta", true)]
	[InlineData("Epsilon", true)]
	[InlineData("Alpha", false)]
	[InlineData("Gamma", false)]
	[InlineData("Root", false)]
	[InlineData("Missing", false)]
	public void ApplySmartExpandForFilter_CollapsesNonMatching(string query, bool expectedBetaExpanded)
	{
		var roots = BuildTree(out var nodes);
		nodes["Beta"].Expanded = true;

		TreeSearchEngine.ApplySmartExpandForFilter(
			roots,
			query,
			node => node.Name,
			node => node.Children,
			(node, expanded) => node.Expanded = expanded);

		Assert.Equal(expectedBetaExpanded, nodes["Beta"].Expanded);
	}

	private static IReadOnlyList<TestNode> BuildTree(out Dictionary<string, TestNode> nodes)
	{
		var root = new TestNode("Root");
		var alpha = new TestNode("Alpha");
		var beta = new TestNode("Beta");
		var gamma = new TestNode("Gamma");
		var delta = new TestNode("Delta");
		var epsilon = new TestNode("Epsilon");

		beta.Children.Add(delta);
		beta.Children.Add(epsilon);
		root.Children.Add(alpha);
		root.Children.Add(beta);
		root.Children.Add(gamma);

		nodes = new Dictionary<string, TestNode>
		{
			[root.Name] = root,
			[alpha.Name] = alpha,
			[beta.Name] = beta,
			[gamma.Name] = gamma,
			[delta.Name] = delta,
			[epsilon.Name] = epsilon
		};

		return new[] { root };
	}

	private sealed class TestNode
	{
		public TestNode(string name)
		{
			Name = name;
			Children = new List<TestNode>();
		}

		public string Name { get; }
		public List<TestNode> Children { get; }
		public bool Expanded { get; set; }
	}
}
