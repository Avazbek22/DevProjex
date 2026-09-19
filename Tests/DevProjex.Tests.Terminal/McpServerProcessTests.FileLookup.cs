using ModelContextProtocol.Client;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	/// <summary>
	/// The two things a caller does when they know a name and not a path: list one directory, and
	/// look the name up. Both are driven through a real server process, because the value of the
	/// second is the trusted line a caller reads when the lookup returns nothing.
	/// </summary>
	[Fact]
	public async Task RealProcessListsOneSubdirectoryAndAnswersAFileNameLookup()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/models/Target.cs", "internal sealed class Target;\n");
		workspace.WriteFile("project/src/models/Neighbour.cs", "internal sealed class Neighbour;\n");
		workspace.WriteFile("project/src/Elsewhere.cs", "internal sealed class Elsewhere;\n");
		workspace.WriteFile("project/RootLevel.cs", "internal sealed class RootLevel;\n");
		var (process, client, errorTask, output) = await StartPublishedMcpAsync(workspace, project, []);
		try
		{
			// One call lists exactly the requested subdirectory.
			var subdirectory = await CallTreeAsync(client, "paths", ["src/models"]);
			Assert.Contains("Target.cs", subdirectory, StringComparison.Ordinal);
			Assert.Contains("Neighbour.cs", subdirectory, StringComparison.Ordinal);
			Assert.DoesNotContain("Elsewhere.cs", subdirectory, StringComparison.Ordinal);
			Assert.DoesNotContain("RootLevel.cs", subdirectory, StringComparison.Ordinal);
			Assert.DoesNotContain("[Empty selection]", subdirectory, StringComparison.Ordinal);

			// The shape a caller reaches for with only a name returns nothing, and says what to
			// send instead rather than restating the glob rule.
			var bareName = await CallTreeAsync(client, "include_patterns", ["Target.cs"]);
			Assert.DoesNotContain("Target.cs\n", bareName, StringComparison.Ordinal);
			Assert.Contains("[Empty selection] stage=patterns.", bareName, StringComparison.Ordinal);
			Assert.Contains(
				"prefix it with '**/' to match that name at any depth",
				bareName,
				StringComparison.Ordinal);

			// The rewrite the line names finds the file.
			var rewritten = await CallTreeAsync(client, "include_patterns", ["**/Target.cs"]);
			Assert.Contains("Target.cs", rewritten, StringComparison.Ordinal);
			Assert.DoesNotContain("Neighbour.cs", rewritten, StringComparison.Ordinal);
			Assert.DoesNotContain("[Empty selection]", rewritten, StringComparison.Ordinal);
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}

		await CompletePublishedMcpAsync(process, errorTask, output);
	}

	private static async Task<string> CallTreeAsync(
		McpClient client,
		string argument,
		string[] values)
	{
		var result = await client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { [argument] = values },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(true, result.IsError);
		return AllProcessText(result);
	}
}
