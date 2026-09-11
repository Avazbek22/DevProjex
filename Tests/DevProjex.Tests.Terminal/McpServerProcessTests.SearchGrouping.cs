using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessWritesEachSearchedPathOnceAndLeadsEveryLineWithItsNumber()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("grouping-project");
		// Two matches far apart in one file become two groups under one heading, and a second
		// file gets a heading of its own. The paths are long on purpose: repeating them per
		// line is exactly what this contract forbids.
		workspace.WriteFile(
			"grouping-project/src/very/deeply/nested/component/first-of-two.ts",
			"const before = 0\nconst groupingNeedle = 1\nconst after = 2\n" +
			string.Concat(Enumerable.Repeat("const filler = 0\n", 20)) +
			"const trailingBefore = 0\nconst groupingNeedle2 = groupingNeedle\nconst trailingAfter = 0\n");
		workspace.WriteFile(
			"grouping-project/src/very/deeply/nested/component/second-of-two.ts",
			"const other = 0\nconst groupingNeedle = 3\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var search = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "groupingNeedle",
				["context_lines"] = "1"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		Assert.NotEqual(true, search.IsError);
		var text = AllProcessText(search);
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		const string First = "src/very/deeply/nested/component/first-of-two.ts";
		const string Second = "src/very/deeply/nested/component/second-of-two.ts";

		// Each path heads its own block exactly once, and never again on a result line.
		Assert.Equal(1, lines.Count(line => string.Equals(line, First, StringComparison.Ordinal)));
		Assert.Equal(1, lines.Count(line => string.Equals(line, Second, StringComparison.Ordinal)));
		Assert.DoesNotContain(First + ":", text, StringComparison.Ordinal);
		Assert.DoesNotContain(First + "-", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Second + ":", text, StringComparison.Ordinal);

		// A match leads with its number and a colon, context with its number and a hyphen.
		Assert.Contains(lines, line => line.StartsWith("2:const groupingNeedle = 1", StringComparison.Ordinal));
		Assert.Contains(lines, line => line.StartsWith("1-const before = 0", StringComparison.Ordinal));
		Assert.Contains(lines, line => line.StartsWith("3-const after = 2", StringComparison.Ordinal));
		Assert.Contains(lines, line => line.StartsWith("2:const groupingNeedle = 3", StringComparison.Ordinal));

		// Two groups inside one file are still separated, and the separator belongs to the file
		// whose heading precedes it rather than standing between two headings.
		var firstHeading = Array.IndexOf(lines, First);
		var secondHeading = Array.IndexOf(lines, Second);
		Assert.True(firstHeading >= 0 && secondHeading > firstHeading);
		var firstBlock = lines[(firstHeading + 1)..secondHeading];
		Assert.Contains(firstBlock, line => string.Equals(line, "--", StringComparison.Ordinal));
	}
}
