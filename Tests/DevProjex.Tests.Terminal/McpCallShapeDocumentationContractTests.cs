using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed class McpCallShapeDocumentationContractTests
{
	[Fact]
	public void NewCallFormsAndDeliveryLimitsStayExplicit()
	{
		var root = PublishedApplicationLocator.FindRepositoryRoot();
		var server = Normalize(File.ReadAllText(Path.Combine(root, "Docs", "McpServer.md")));
		Assert.Contains("One listed file uses the scalar form, including columns and symbols", server, StringComparison.Ordinal);
		Assert.Contains("`requests` come first, followed by whole-file reads from `path` and `paths` not already requested", server, StringComparison.Ordinal);
		Assert.Contains("a whole-file item can instead report `structure`", server, StringComparison.Ordinal);
		Assert.Contains("`structure — no room left in this response, N lines`", server, StringComparison.Ordinal);
		Assert.Contains("the 1,000-line limit applies only to scalar reads and `read_pack`", server, StringComparison.Ordinal);
		Assert.Contains("The distribution has its own reservation of at most 2,000 characters", server, StringComparison.Ordinal);
		Assert.Contains("the rest are counted per file under \"Withheld matches by file\"", server, StringComparison.Ordinal);
		Assert.Contains("The `[Search stored]` line is the only instruction to call `read_pack`", server, StringComparison.Ordinal);
		Assert.Contains("The expanded set may contain at most 16 files", server, StringComparison.Ordinal);
		Assert.DoesNotContain("one to eight records", server, StringComparison.Ordinal);
		Assert.DoesNotContain("batch reads are unchanged", server, StringComparison.Ordinal);
	}

	[Fact]
	public void TerminalSearchFallbackAndFolderSeedsMatchMcp()
	{
		var root = PublishedApplicationLocator.FindRepositoryRoot();
		var commandLine = Normalize(File.ReadAllText(Path.Combine(root, "Docs", "CommandLine.md")));
		var workspace = Normalize(File.ReadAllText(Path.Combine(root, "Docs", "TerminalWorkspace.md")));
		Assert.Contains("is searched as text with that regular expression instead", commandLine, StringComparison.Ordinal);
		Assert.Contains("with at most 16 seeds", commandLine, StringComparison.Ordinal);
		Assert.Contains("is searched as text instead, with the fixed `[Symbols]` notice", workspace, StringComparison.Ordinal);
		Assert.Contains("with at most 16 seeds and the fixed `[Read as]` notice", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("any other regular expression is still refused", commandLine, StringComparison.Ordinal);
		Assert.DoesNotContain("any other regular expression is still refused", workspace, StringComparison.Ordinal);
	}

	[Fact]
	public void PublicationHintsAndBenchmarkEvidenceKeepTheirOwnBoundaries()
	{
		var root = PublishedApplicationLocator.FindRepositoryRoot();
		var server = Normalize(File.ReadAllText(Path.Combine(root, "Docs", "McpServer.md")));
		var dependencies = Normalize(File.ReadAllText(Path.Combine(root, "Docs", "Dependencies.md")));
		var benchmark = Normalize(File.ReadAllText(Path.Combine(root, "tools", "AgentBenchmark", "README.md")));
		Assert.Contains("one level of its `references`, at most 16 links", server, StringComparison.Ordinal);
		Assert.Contains("at most four links and no cycle", server, StringComparison.Ordinal);
		Assert.Contains("Compilation membership comes only from the candidate's own `files`, `include`, and `exclude`", server, StringComparison.Ordinal);
		Assert.Contains("These publication hints do not add import edges to the ranking graph", server, StringComparison.Ordinal);
		Assert.Contains("These rules are separate from the import-resolution inheritance rules above", dependencies, StringComparison.Ordinal);
		Assert.Contains("`Status: structure` and numeric `Lines: A-B` counts as `content-shown`", benchmark, StringComparison.Ordinal);
		Assert.Contains("`Lines: none` counts as `listed`", benchmark, StringComparison.Ordinal);
		Assert.Contains("Project content cannot contribute trusted notice counts", benchmark, StringComparison.Ordinal);
	}

	private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant,
		TimeSpan.FromSeconds(2));
}
