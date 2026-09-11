using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpStoredSearchRegistryTests
{
	[Fact]
	public async Task AnExpiredSearchResultTellsTheCallerToSearchAgain()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var stored = await WriteAsync(registry, McpStoredResultKind.Search, "src/App.cs\n5:hit\n");

		registry.Remove(stored);

		var failure = Assert.Throws<McpToolException>(() => registry.OpenReadDocument(stored));
		Assert.Contains("search result expired", failure.Message, StringComparison.Ordinal);
		Assert.Contains("call search_project again", failure.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("pack_context", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnExpiredPackStillTellsTheCallerToPackAgain()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var stored = await WriteAsync(registry, McpStoredResultKind.Pack, "packed context");

		registry.Remove(stored);

		var failure = Assert.Throws<McpToolException>(() => registry.OpenReadDocument(stored));
		Assert.Contains("pack expired", failure.Message, StringComparison.Ordinal);
		Assert.Contains("call pack_context again", failure.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("search_project", failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AnIdThisSessionNeverIssuedKeepsTheGeneralWording()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);

		// Another session's id carries no kind here, and the message already says so.
		var failure = Assert.Throws<McpToolException>(
			() => registry.OpenReadDocument(new string('a', 48)));
		Assert.Contains("belongs to another server session", failure.Message, StringComparison.Ordinal);
	}

	private static async Task<string> WriteAsync(
		McpPackRegistry registry,
		McpStoredResultKind kind,
		string content)
	{
		var document = await registry.CreateAsync(
			async (stream, token) =>
			{
				await using var writer = new StreamWriter(
					stream,
					new UTF8Encoding(false),
					bufferSize: 1024,
					leaveOpen: true);
				await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
			},
			kind,
			TestContext.Current.CancellationToken);
		return document.Id;
	}
}
