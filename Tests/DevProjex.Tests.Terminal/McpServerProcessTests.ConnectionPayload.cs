using ModelContextProtocol.Client;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	// Every client pays this on connect, before it asks the server anything about a project, and it
	// grows with each added parameter and description. The ceilings are deliberate headroom over a
	// recorded measurement, not the measurement itself: a ceiling with a few characters to spare is
	// a tripwire for the next honest addition rather than a budget.
	//
	// Recorded on 2026-09-12 from the wire JSON the client received: tools/list result 34,439 on a
	// default server, 38,357 when per-call exclusions are enabled, and 1,128 of instructions.
	// The base revision delivered 42,370 characters before the protected JSON tools stopped
	// publishing duplicate output schemas. The exact mode difference below isolates the optional
	// exclusion parameter; the total ceiling keeps deliberate room for protocol metadata.
	private const int ToolsListResultCeiling = 35_000;
	private const int ToolsListResultFloor = 33_000;
	private const int ExclusionsParameterCost = 3_918;
	private const int InstructionsCeiling = 1_200;
	private const int InstructionsFloor = 900;

	[Fact]
	public async Task RealProcessKeepsTheConnectionPayloadInsideItsBudget()
	{
		var (defaultPayload, instructions) = await MeasureConnectionPayloadAsync([]);
		Assert.InRange(defaultPayload, ToolsListResultFloor, ToolsListResultCeiling);
		Assert.InRange(instructions.Length, InstructionsFloor, InstructionsCeiling);

		// A delegation server publishes the exclusion parameter on six tools and pays for nothing
		// else, so the whole difference is that parameter. Pinning it exactly reports a growing
		// parameter by name instead of waiting for a total to cross a line.
		var (delegationPayload, delegationInstructions) =
			await MeasureConnectionPayloadAsync(["--allow-agent-exclusions"]);
		Assert.Equal(ExclusionsParameterCost, delegationPayload - defaultPayload);
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
