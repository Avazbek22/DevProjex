using System.Globalization;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task BudgetedPackWithholdsUnscannableContentFromItsDeliveredPaths()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Safe.txt"), "safe-content\n");
		WriteAsciiFileWithLength(
			Path.Combine(project, "Oversized.txt"),
			SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1,
			"withheld-content\n");
		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var result = await server.CallAsync("pack_context", new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["max_tokens"] = 20_000_000.ToString(CultureInfo.InvariantCulture)
			});
			Assert.False(result.IsError == true, Text(result));
			Assert.Contains("safe-content", Text(result), StringComparison.Ordinal);
			Assert.DoesNotContain("withheld-content", Text(result), StringComparison.Ordinal);
			Assert.Contains("Uninspected content was withheld from the pack.", Text(result), StringComparison.Ordinal);
		}

		using var journal = new AgentJournalStore(
			() => Path.Combine(workspace.Path, "app-data"),
			activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken));
		var call = Assert.Single(await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken));
		Assert.Equal(["Safe.txt"], call.DeliveredPaths);
	}
}
