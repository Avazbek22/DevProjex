using ModelContextProtocol.Client;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	// Every client pays this on connect, before it asks the server anything about a project, and it
	// grows with each added parameter and description. The ceilings are deliberate headroom over a
	// recorded measurement, not the measurement itself: a ceiling with a few characters to spare is
	// a tripwire for the next honest addition rather than a budget.
	//
	// Recorded on 2026-11-12 from the wire JSON with one local root: 27,141 characters.
	// Three additions are pinned as exact differences, so a growing parameter is reported by
	// name: a second local root publishes project, --allow-remote publishes project and branch,
	// and --allow-agent-exclusions publishes exclusions. The ceiling leaves 250 characters.
	private const int ToolsListResultCeiling = 27_391;
	private const int ToolsListResultFloor = 26_641;
	private const int ExclusionsParameterCost = 2_961;
	private const int ProjectParameterCost = 1_407;
	private const int RemoteParameterCost = 2_030;
	private const int InstructionsCeiling = 1_200;
	private const int InstructionsFloor = 900;

	[Fact]
	public async Task RealProcessKeepsTheConnectionPayloadInsideItsBudget()
	{
		var (defaultPayload, instructions) = await MeasureConnectionPayloadAsync([]);
		Assert.InRange(defaultPayload, ToolsListResultFloor, ToolsListResultCeiling);
		Assert.InRange(instructions.Length, InstructionsFloor, InstructionsCeiling);

		// A delegation server publishes the exclusion parameter on seven tools and pays for nothing
		// else, so the whole difference is that parameter. Pinning it exactly reports a growing
		// parameter by name instead of waiting for a total to cross a line.
		var (delegationPayload, delegationInstructions) =
			await MeasureConnectionPayloadAsync(["--allow-agent-exclusions"]);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"tools/list result: default={defaultPayload} characters; delegated={delegationPayload} characters; " +
			$"instructions={instructions.Length} characters.");
		Assert.Equal(ExclusionsParameterCost, delegationPayload - defaultPayload);
		Assert.InRange(delegationPayload, ToolsListResultFloor + ExclusionsParameterCost,
			ToolsListResultCeiling + ExclusionsParameterCost);
		Assert.Equal(instructions, delegationInstructions);

		var (multiplePayload, multipleInstructions) = await MeasureConnectionPayloadAsync([], secondRoot: true);
		var (remotePayload, remoteInstructions) = await MeasureConnectionPayloadAsync(["--allow-remote"]);
		Assert.Equal(ProjectParameterCost, multiplePayload - defaultPayload);
		Assert.Equal(RemoteParameterCost, remotePayload - defaultPayload);
		Assert.InRange(multipleInstructions.Length, InstructionsFloor, InstructionsCeiling);
		Assert.Equal(instructions, remoteInstructions);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"tools/list result: two roots={multiplePayload}; remote={remotePayload}.");
	}

	private static async Task<(int ToolsListResultCharacters, string Instructions)>
		MeasureConnectionPayloadAsync(IReadOnlyList<string> additionalArguments, bool secondRoot = false)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		if (secondRoot)
			additionalArguments = additionalArguments.Concat(["--root", workspace.CreateDirectory("second")]).ToArray();
		var (process, client, errorTask, output) =
			await StartPublishedMcpAsync(workspace, project, additionalArguments);
		string instructions;
		int payload;
		try
		{
			instructions = Assert.IsType<string>(client.ServerInstructions);
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(ExpectedTools, tools.Select(static tool => tool.Name));
			var delivered = ReadToolsListResult(output.GetRecordedText());
			payload = delivered.Length;
			using var document = JsonDocument.Parse(delivered);
			var pack = document.RootElement.GetProperty("tools").EnumerateArray()
				.Single(static tool => tool.GetProperty("name").GetString() == "pack_context");
			TestContext.Current.TestOutputHelper?.WriteLine(
				$"PACK_TOTAL={pack.GetRawText().Length}; PACK_SCHEMA={pack.GetProperty("inputSchema").GetRawText().Length}.");
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
