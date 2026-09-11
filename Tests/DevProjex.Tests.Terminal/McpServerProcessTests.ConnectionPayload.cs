using ModelContextProtocol.Client;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	// Every client pays this on connect, before it asks the server anything about a project, and it
	// grows with each added parameter and description. The ceilings are deliberate headroom over a
	// recorded measurement, not the measurement itself: a ceiling with a few characters to spare is
	// a tripwire for the next honest addition rather than a budget.
	//
	// Recorded on 2026-09-11 against the built application: tools/list result 33,981 characters on
	// a default server and 37,869 on a delegation server, initialize result 1,223 of which
	// instructions are 1,044. The exclusion parameter costs a flat 3,888 characters.
	private const int ToolsListResultCeiling = 38_000;
	private const int ToolsListResultFloor = 30_000;
	private const int DelegationToolsListResultCeiling = 42_000;
	private const int DelegationToolsListResultFloor = 34_000;
	private const int InstructionsCeiling = 1_400;
	private const int InstructionsFloor = 800;

	[Fact]
	public async Task RealProcessKeepsTheConnectionPayloadInsideItsBudget()
	{
		var (defaultPayload, instructions) = await MeasureConnectionPayloadAsync([]);
		Assert.InRange(defaultPayload, ToolsListResultFloor, ToolsListResultCeiling);
		Assert.InRange(instructions.Length, InstructionsFloor, InstructionsCeiling);

		// A delegation server publishes the exclusion parameter on six tools and is the larger
		// payer, so it carries its own ceiling rather than riding on the default one.
		var (delegationPayload, delegationInstructions) =
			await MeasureConnectionPayloadAsync(["--allow-agent-exclusions"]);
		Assert.InRange(
			delegationPayload,
			DelegationToolsListResultFloor,
			DelegationToolsListResultCeiling);
		Assert.Equal(instructions, delegationInstructions);
	}

	private static async Task<(int ToolsListResultCharacters, string Instructions)>
		MeasureConnectionPayloadAsync(IReadOnlyList<string> additionalArguments)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		var (process, client, errorTask, output) =
			await StartPublishedMcpAsync(workspace, project, additionalArguments);
		string instructions;
		int payload;
		try
		{
			instructions = Assert.IsType<string>(client.ServerInstructions);
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(ExpectedTools, tools.Select(static tool => tool.Name));
			payload = ReadToolsListResult(output.GetRecordedText()).Length;
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}

		await CompletePublishedMcpAsync(process, errorTask, output);
		return (payload, instructions);
	}

	/// <summary>
	/// The serialized <c>result</c> of the tools/list response exactly as the client received it,
	/// so the budget measures delivered characters rather than a re-serialization of them.
	/// </summary>
	private static string ReadToolsListResult(string recordedSession)
	{
		foreach (var line in recordedSession
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			JsonDocument document;
			try
			{
				document = JsonDocument.Parse(line);
			}
			catch (JsonException)
			{
				continue;
			}

			using (document)
			{
				if (document.RootElement.TryGetProperty("result", out var result) &&
				    result.TryGetProperty("tools", out _))
				{
					return result.GetRawText();
				}
			}
		}

		Assert.Fail("The recorded session carries no tools/list result.");
		return string.Empty;
	}
}
