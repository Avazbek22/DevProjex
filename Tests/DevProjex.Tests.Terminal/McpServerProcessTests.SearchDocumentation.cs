using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessListsDocumentationAfterCodeUnderItsOwnHeading()
	{
		const string marker = "Zq9DocMarkerName";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("documentation-order");
		workspace.WriteFile("documentation-order/z.log", "documentationOrderNeedle\n");
		var documentation = new[] { marker + ".md", "a.MDX", "b.rst", "c.txt", "d.adoc", "nested/DOCS/Example.cs", "nested/doc/plain.log" };
		foreach (var path in documentation)
			workspace.WriteFile("documentation-order/" + path,
				path.EndsWith(".cs", StringComparison.Ordinal) ? "class documentationOrderNeedle { }\n" : "documentationOrderNeedle\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "documentationOrderNeedle",
			["context_lines"] = 0
		});
		Assert.NotEqual(true, response.IsError);
		var text = Normalize(AllProcessText(response));
		var body = SpotlightBody(text);
		var heading = body.IndexOf("Matches in documentation files:\n", StringComparison.Ordinal);
		Assert.True(heading > body.IndexOf("z.log\n", StringComparison.Ordinal), body);
		foreach (var path in documentation)
			Assert.True(body.IndexOf(path + "\n", StringComparison.Ordinal) > heading, body);
		var trusted = OutsideSelectionTrustedText(text);
		Assert.Contains("[Search] 7 match(es) in documentation files are listed after code.", trusted, StringComparison.Ordinal);
		Assert.DoesNotContain(marker, trusted, StringComparison.Ordinal);
		Assert.Contains("matches retained=8/8 · matches written=8", trusted, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessWithholdsDocumentationBeforeCodeAndKeepsItReadableFromTheStoredPack()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("documentation-budget");
		workspace.WriteFile("documentation-budget/z.log", string.Concat(Enumerable.Range(0, 70)
			.Select(index => $"documentationBudgetNeedle {index:D3} " + new string('c', 170) + "\n")));
		workspace.WriteFile("documentation-budget/a.md", string.Concat(Enumerable.Range(0, 30)
			.Select(index => $"documentationBudgetNeedle {index:D3} " + new string('d', 170) + "\n")));
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "documentationBudgetNeedle",
			["context_lines"] = 0,
			["max_results"] = 200
		});
		Assert.NotEqual(true, response.IsError);
		var text = Normalize(AllProcessText(response));
		var body = SpotlightBody(text);
		Assert.InRange(body.Length, 1, 16_000);
		var codeStart = body.IndexOf("z.log\n", StringComparison.Ordinal);
		var docsStart = body.IndexOf("Matches in documentation files:\n", StringComparison.Ordinal);
		Assert.True(codeStart >= 0 && docsStart > codeStart, body);
		Assert.Equal(70, Regex.Matches(body[codeStart..docsStart], @"(?m)^\d+:").Count);
		var written = Regex.Matches(body, @"(?m)^\d+:").Count;
		Assert.InRange(written, 71, 99);
		Assert.Contains($"matches retained=100/100 · matches written={written}", text, StringComparison.Ordinal);
		var stored = Regex.Match(text, @"\[Search stored\] pack_id=([0-9a-f]+)");
		Assert.True(stored.Success, text);
		var read = await CallAsync(server, "read_pack", new Dictionary<string, object?> { ["pack_id"] = stored.Groups[1].Value });
		Assert.NotEqual(true, read.IsError);
		var page = Normalize(AllProcessText(read));
		Assert.Contains("a.md\n", page, StringComparison.Ordinal);
		Assert.Contains("30:documentationBudgetNeedle 029", page, StringComparison.Ordinal);
		Assert.DoesNotContain("z.log\n", page, StringComparison.Ordinal);
	}
}
