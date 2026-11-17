namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessesOnOneRootKeepUnicodeRangesAndObserveReplacedAndDeletedFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/mixed µ.txt", "\uFEFFalpha\r\nβ🙂\rthird\nlast");
		workspace.WriteFile("project/empty.txt", string.Empty);
		await using var compact = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("compact-data"),
			useDefaultToolSet: true);
		await using var full = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("full-data"));
		var servers = new[] { compact, full };
		var slices = await Task.WhenAll(servers.Select(server => CallAsync(server, "get_file", new()
		{
			["path"] = "mixed µ.txt", ["start_line"] = 2, ["end_line"] = 2, ["start_column"] = 2
		}).AsTask()));
		foreach (var result in slices)
		{
			Assert.NotEqual(true, result.IsError);
			Assert.Equal("File: mixed µ.txt\nLines: 2-2 of 4\n🙂", BatchData(result));
		}
		foreach (var server in servers)
		{
			var empty = await CallAsync(server, "get_file", new() { ["path"] = "empty.txt" });
			Assert.NotEqual(true, empty.IsError);
			Assert.Equal("File: empty.txt\nLines: 0-0 of 0\n", BatchData(empty));
			var batch = await CallAsync(server, "get_file", new() { ["paths"] = new[] { "empty.txt", "mixed µ.txt" } });
			Assert.NotEqual(true, batch.IsError);
			Assert.Contains("1.1 — ok", BatchData(batch), StringComparison.Ordinal);
			Assert.Contains("2.1 — ok", BatchData(batch), StringComparison.Ordinal);
			Assert.DoesNotContain('\r', BatchData(batch));
			Assert.DoesNotContain('\uFEFF', BatchData(batch));
		}
		workspace.WriteFile("project/mixed µ.txt", "replacement without a final newline");
		foreach (var server in servers)
		{
			var replaced = await CallAsync(server, "get_file", new() { ["path"] = "mixed µ.txt" });
			Assert.NotEqual(true, replaced.IsError);
			Assert.Equal("File: mixed µ.txt\nLines: 1-1 of 1\nreplacement without a final newline", BatchData(replaced));
		}
		File.Delete(Path.Combine(project, "mixed µ.txt"));
		foreach (var server in servers)
		{
			var missing = await CallAsync(server, "get_file", new() { ["path"] = "mixed µ.txt" });
			Assert.True(missing.IsError);
			Assert.StartsWith("DPX-MCP-PATH-NOT-FOUND:", AllProcessText(missing), StringComparison.Ordinal);
		}
	}
}
