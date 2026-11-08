namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessReportsFilesWithoutIndexedDeclarations()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/types.ts", "export type TypedResponse<T = unknown> = { _data: T }\n");
		workspace.WriteFile("project/Zq9UnindexedPath.txt", "Text that does not contain the requested declaration name.\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));

		var response = await CallAsync(server, "search_project", new Dictionary<string, object?>
		{
			["pattern"] = "TypedResponse",
			["symbols"] = true,
			["context_lines"] = 0
		});
		var text = AllProcessText(response);
		var body = SpotlightBody(Normalize(text));

		Assert.NotEqual(true, response.IsError);
		Assert.Contains("TypedResponse", body, StringComparison.Ordinal);
		Assert.Contains("[Declarations not indexed] Zq9UnindexedPath.txt", body, StringComparison.Ordinal);
		var outside = text.Replace(ExtractRawBody(text), string.Empty, StringComparison.Ordinal);
		Assert.DoesNotContain("Zq9UnindexedPath", outside, StringComparison.Ordinal);
		Assert.Contains("[Symbols coverage] declarations-not-indexed=1; search without symbols=true still reads these files.", outside, StringComparison.Ordinal);

		var read = await CallAsync(server, "get_file", new Dictionary<string, object?>
		{
			["path"] = "types.ts",
			["symbol"] = "TypedResponse"
		});
		Assert.NotEqual(true, read.IsError);
		Assert.Contains("_data: T", AllProcessText(read), StringComparison.Ordinal);
	}

	private static string ExtractRawBody(string text)
	{
		var opening = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var closing = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(opening >= 0 && closing > opening);
		return text[opening..closing];
	}
}
