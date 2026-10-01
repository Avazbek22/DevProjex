using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Theory]
	[InlineData("text")]
	[InlineData("json")]
	public void RealCliProcessRedactsAnAbsoluteFocusSeedEverywhere(string format)
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userProfile))
			Assert.Skip("The environment does not expose a user profile directory.");
		using var workspace = new TemporaryDirectory(userProfile);
		var project = CreateRankingFixture(workspace);
		if (OutputRootPathPresentation.MaskLocalUserSegment(project) == project)
			Assert.Skip("The user profile path does not use a supported local-user layout.");
		var result = RunFocusCliCore(
			workspace.CreateDirectory("cli-private-focus-data"),
			project,
			format,
			includeRank: true,
			[Path.Combine(project, "A.cs")],
			["--hide-private-data"]);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.DoesNotContain(new DirectoryInfo(userProfile).Name, result.StandardOutput, StringComparison.Ordinal);
		var placeholderCount = result.StandardOutput
			.Split(OutputRootPathPresentation.LocalUserPlaceholder, StringSplitOptions.None).Length - 1;
		Assert.True(placeholderCount >= (format == "json" ? 2 : 1), result.StandardOutput);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("json")]
	public async Task RealMcpProcessRedactsAnAbsoluteFocusSeedEverywhere(string format)
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userProfile))
			Assert.Skip("The environment does not expose a user profile directory.");
		using var workspace = new TemporaryDirectory(userProfile);
		var project = CreateRankingFixture(workspace);
		if (OutputRootPathPresentation.MaskLocalUserSegment(project) == project)
			Assert.Skip("The user profile path does not use a supported local-user layout.");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("mcp-private-focus-data"),
			arguments: ["--hide-private-data"]);

		var result = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = format,
				["rank"] = "importance",
				["focus"] = Path.Combine(project, "A.cs")
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(result);

		Assert.NotEqual(true, result.IsError);
		Assert.DoesNotContain(new DirectoryInfo(userProfile).Name, text, StringComparison.Ordinal);
		var placeholderCount = text
			.Split(OutputRootPathPresentation.LocalUserPlaceholder, StringSplitOptions.None).Length - 1;
		Assert.True(placeholderCount >= (format == "json" ? 2 : 1), text);
	}

	[Fact]
	public void RealCliFocusPreservesPayloadAcrossConsolidatedStorageBoundary()
	{
		using var workspace = new TemporaryDirectory();
		var separate = CreateStorageThresholdFixture(workspace, "separate", 255);
		var consolidated = CreateStorageThresholdFixture(workspace, "consolidated", 256);
		var dataRoot = workspace.CreateDirectory("cli-storage-data");

		foreach (var format in new[] { "markdown", "text", "json", "xml" })
		{
			var separateResult = RunFocusCliCore(dataRoot, separate, format, true, ["A.cs"], ["--hide-secrets"]);
			var consolidatedResult = RunFocusCliCore(dataRoot, consolidated, format, true, ["A.cs"], ["--hide-secrets"]);

			Assert.Equal(CommandLineExitCodes.Success, separateResult.ExitCode);
			Assert.Equal(CommandLineExitCodes.Success, consolidatedResult.ExitCode);
			Assert.Equal(StoragePayload, ExtractFileContent(separateResult.StandardOutput, format, "A.cs"));
			Assert.Equal(StoragePayload, ExtractFileContent(consolidatedResult.StandardOutput, format, "A.cs"));
		}
	}

	[Fact]
	public async Task RealMcpFocusPreservesPayloadAcrossConsolidatedStorageBoundary()
	{
		using var workspace = new TemporaryDirectory();
		var separate = CreateStorageThresholdFixture(workspace, "separate", 255);
		var consolidated = CreateStorageThresholdFixture(workspace, "consolidated", 256);
		await using var separateServer = await ActualMcpProcess.StartAsync(
			separate,
			workspace.CreateDirectory("mcp-storage-separate"));
		await using var consolidatedServer = await ActualMcpProcess.StartAsync(
			consolidated,
			workspace.CreateDirectory("mcp-storage-consolidated"));

		foreach (var format in new[] { "markdown", "text", "json", "xml" })
		{
			var separateResult = await CallFocusPackAsync(separateServer, format, ["A.cs"]);
			var consolidatedResult = await CallFocusPackAsync(consolidatedServer, format, ["A.cs"]);

			Assert.NotEqual(true, separateResult.IsError);
			Assert.NotEqual(true, consolidatedResult.IsError);
			var separateDocument = await ReadStoredPackIfNeededAsync(separateServer, separateResult);
			var consolidatedDocument = await ReadStoredPackIfNeededAsync(consolidatedServer, consolidatedResult);
			Assert.Equal(StoragePayload, ExtractFileContent(separateDocument, format, "A.cs"));
			Assert.Equal(StoragePayload, ExtractFileContent(consolidatedDocument, format, "A.cs"));
		}
	}

	[Fact]
	public async Task FocusRedactionPayloadIsIndependentOfSeedOrderAcrossCliAndMcp()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRedactionFixture(workspace);
		var dataRoot = workspace.CreateDirectory("seed-order-data");
		var cliFirst = RunFocusCliCore(dataRoot, project, "json", true, ["A.cs", "B.cs"], ["--hide-secrets"]);
		var cliSecond = RunFocusCliCore(dataRoot, project, "json", true, ["B.cs", "A.cs"], ["--hide-secrets"]);
		Assert.Equal(CommandLineExitCodes.Success, cliFirst.ExitCode);
		Assert.Equal(CommandLineExitCodes.Success, cliSecond.ExitCode);
		AssertPayloadsEqual(JsonPayloads(cliFirst.StandardOutput), JsonPayloads(cliSecond.StandardOutput));

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("seed-order-mcp-data"));
		var mcpFirst = await CallFocusPackAsync(server, "json", ["A.cs", "B.cs"]);
		var mcpSecond = await CallFocusPackAsync(server, "json", ["B.cs", "A.cs"]);
		Assert.NotEqual(true, mcpFirst.IsError);
		Assert.NotEqual(true, mcpSecond.IsError);
		AssertPayloadsEqual(
			JsonPayloads(ExtractJsonDocument(AllProcessText(mcpFirst))),
			JsonPayloads(ExtractJsonDocument(AllProcessText(mcpSecond))));
	}

	[Fact]
	public async Task FocusBudgetMetadataStaysBoundToSkippedFilesAcrossCliAndMcp()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateBudgetFixture(workspace);
		var cli = RunFocusCliCore(
			workspace.CreateDirectory("budget-cli-data"),
			project,
			"json",
			true,
			["A.cs"],
			["--max-tokens", "10"]);
		Assert.Equal(CommandLineExitCodes.Success, cli.ExitCode);
		AssertSkippedBudgetMetadata(cli.StandardOutput);

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("budget-mcp-data"));
		var mcp = await CallFocusPackAsync(server, "json", ["A.cs"], maximumTokens: 10);
		Assert.NotEqual(true, mcp.IsError);
		AssertSkippedBudgetMetadata(ExtractJsonDocument(AllProcessText(mcp)));
	}

	[Fact]
	public void RealCliProcessExportsFocusOrderAndValidatesTheRankPair()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		workspace.CreateDirectory("project/folder");
		var dataRoot = workspace.CreateDirectory("cli-focus-data");
		var focused = RunFocusCli(
			dataRoot,
			project,
			"text",
			includeRank: true,
			"A.cs",
			Path.Combine(project, "A.cs"));

		Assert.Equal(0, focused.ExitCode);
		Assert.True(
			focused.StandardOutput.IndexOf("A.cs:", StringComparison.Ordinal) <
			focused.StandardOutput.IndexOf("B.cs:", StringComparison.Ordinal),
			focused.StandardOutput);
		Assert.Contains("[Ranking] focus-v1 · 1 seed", focused.StandardError, StringComparison.Ordinal);
		Assert.Contains("[Ranking top] A.cs — seed", focused.StandardError, StringComparison.Ordinal);
		var json = RunFocusCli(dataRoot, project, "json", includeRank: true, "A.cs");
		Assert.Equal(0, json.ExitCode);
		using (var document = JsonDocument.Parse(json.StandardOutput))
		{
			var ranking = document.RootElement.GetProperty("ranking");
			Assert.Equal("focus-v1", ranking.GetProperty("algorithm").GetString());
			Assert.Equal("focus-v1", ranking.GetProperty("focus").GetProperty("algorithm").GetString());
			Assert.Equal(0, ranking.GetProperty("top")[0].GetProperty("hop").GetInt32());
			Assert.True(ranking.GetProperty("top")[0].GetProperty("baseImportancePriority").GetInt32() > 0);
		}

		var withoutRank = RunFocusCli(dataRoot, project, "text", includeRank: false, "A.cs");
		Assert.Equal(CommandLineExitCodes.UsageError, withoutRank.ExitCode);
		Assert.Contains("--focus", withoutRank.StandardError, StringComparison.Ordinal);
		Assert.Contains("--rank", withoutRank.StandardError, StringComparison.Ordinal);

		var tooMany = RunFocusCli(
			dataRoot,
			project,
			"text",
			includeRank: true,
			Enumerable.Repeat("A.cs", 17).ToArray());
		Assert.Equal(CommandLineExitCodes.UsageError, tooMany.ExitCode);
		Assert.Contains("16", tooMany.StandardError, StringComparison.Ordinal);

		var empty = RunFocusCli(dataRoot, project, "text", includeRank: true, "");
		Assert.Equal(CommandLineExitCodes.UsageError, empty.ExitCode);
		Assert.Contains("--focus", empty.StandardError, StringComparison.Ordinal);
		var directory = RunFocusCli(dataRoot, project, "text", includeRank: true, "folder");
		Assert.Equal(CommandLineExitCodes.PolicyFailure, directory.ExitCode);
		Assert.Contains("DPX-SELECTION-PATH-MISSING", directory.StandardError, StringComparison.Ordinal);
		var outside = RunFocusCli(
			dataRoot,
			project,
			"text",
			includeRank: true,
			Path.Combine(workspace.Path, "outside.cs"));
		Assert.Equal(CommandLineExitCodes.PolicyFailure, outside.ExitCode);
		Assert.Contains("DPX-SELECTION-PATH-MISSING", outside.StandardError, StringComparison.Ordinal);
		var filtered = RunFocusCliCore(
			dataRoot,
			project,
			"text",
			includeRank: true,
			["A.cs"],
			["--select", "B.cs"]);
		Assert.True(
			filtered.ExitCode == CommandLineExitCodes.PolicyFailure,
			$"Expected policy failure but received {filtered.ExitCode}:{Environment.NewLine}{filtered.StandardError}");
		Assert.Contains("DPX-SELECTION-PATH-MISSING", filtered.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealMcpProcessPublishesAndEnforcesFocusRankingContract()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateRankingFixture(workspace);
		workspace.WriteFile("project/D[one].cs", "namespace Fixture; public sealed class DOne { }");
		workspace.CreateDirectory("project/folder");
		workspace.WriteFile("project/.excluded.cs", "namespace Fixture; public sealed class Excluded { }");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("focus-data"));

		var tools = await server.Client.ListToolsAsync(
			options: null,
			TestContext.Current.CancellationToken);
		var packTool = tools.Single(static tool => tool.Name == "pack_context");
		var focusSchema = packTool.ProtocolTool.InputSchema
			.GetProperty("properties")
			.GetProperty("focus");
		Assert.Equal(2, focusSchema.GetProperty("oneOf").GetArrayLength());
		Assert.Equal(16, focusSchema.GetProperty("oneOf")[1].GetProperty("maxItems").GetInt32());

		var focused = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["rank"] = "importance",
				["focus"] = new[] { "A.cs", Path.Combine(project, "A.cs") }
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(focused);
		Assert.NotEqual(true, focused.IsError);
		Assert.Contains("[Ranking] focus-v1 · 1 seed", text, StringComparison.Ordinal);
		Assert.True(text.IndexOf("A.cs:", StringComparison.Ordinal) < text.IndexOf("B.cs:", StringComparison.Ordinal), text);
		Assert.Contains("[Ranking top] A.cs — seed", text, StringComparison.Ordinal);
		Assert.Contains("dependency of A.cs", text, StringComparison.Ordinal);
		var rankingIndex = text.IndexOf("[Ranking] focus-v1", StringComparison.Ordinal);
		var mainDataClose = text.IndexOf("</untrusted-data-", StringComparison.Ordinal);
		var rankingTopIndex = text.IndexOf("[Ranking top] A.cs", StringComparison.Ordinal);
		var rankingDataOpen = text.LastIndexOf("<untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		var rankingDataClose = text.IndexOf("</untrusted-data-", rankingTopIndex, StringComparison.Ordinal);
		Assert.True(rankingIndex > mainDataClose, text);
		Assert.True(rankingDataOpen > rankingIndex && rankingDataClose > rankingTopIndex, text);

		foreach (var validFocus in new object[]
		         {
			         "D\\[one\\].cs",
			         Path.Combine(project, "B.cs")
		         })
		{
			var result = await server.Client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "content",
					["format"] = "text",
					["rank"] = "importance",
					["focus"] = validFocus
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			Assert.Contains("[Ranking] focus-v1", AllProcessText(result), StringComparison.Ordinal);
		}

		var invalidArgumentCases = new Dictionary<string, object?>[]
		{
			new() { ["focus"] = "A.cs" },
			new() { ["rank"] = "importance", ["focus"] = Array.Empty<string>() },
			new() { ["rank"] = "importance", ["focus"] = "" },
			new() { ["rank"] = "importance", ["focus"] = null },
			new() { ["rank"] = "importance", ["focus"] = 42 },
			new() { ["rank"] = "importance", ["focus"] = new object[] { "A.cs", 42 } },
			new() { ["rank"] = "importance", ["focus"] = Enumerable.Repeat("A.cs", 17).ToArray() }
		};
		foreach (var invalidArguments in invalidArgumentCases)
		{
			var invalid = await server.Client.CallToolAsync(
				"pack_context",
				invalidArguments,
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.Equal(true, invalid.IsError);
			var error = AllProcessText(invalid);
			Assert.Contains("DPX-MCP-INVALID-ARGUMENTS:", error, StringComparison.Ordinal);
			if (invalidArguments.ContainsKey("focus") && !invalidArguments.ContainsKey("rank"))
				Assert.Contains("focus", error, StringComparison.Ordinal);
		}

		foreach (var (invalidArguments, reason) in new[]
		         {
			         (new Dictionary<string, object?>
			         {
				         ["rank"] = "importance",
				         ["focus"] = "folder"
			         }, "is a directory"),
			         (new Dictionary<string, object?>
			         {
				         ["rank"] = "importance",
				         ["focus"] = "a.cs"
			         }, "differs only in letter case"),
			         (new Dictionary<string, object?>
			         {
				         ["rank"] = "importance",
				         ["focus"] = "A.cs",
				         ["include_patterns"] = new[] { "B.cs" }
			         }, "effective filters"),
			         (new Dictionary<string, object?>
			         {
				         ["rank"] = "importance",
				         ["focus"] = "A.cs",
				         ["max_file_bytes"] = 1
			         }, "max_file_bytes")
		         })
		{
			var invalid = await server.Client.CallToolAsync(
				"pack_context",
				invalidArguments,
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.Equal(true, invalid.IsError);
			var error = AllProcessText(invalid);
			Assert.Contains("DPX-MCP-PATH-NOT-FOUND:", error, StringComparison.Ordinal);
			Assert.Contains(reason, error, StringComparison.Ordinal);
		}
	}

	private static TerminalTestProcessResult RunFocusCli(
		string dataRoot,
		string project,
		string format,
		bool includeRank,
		params string[] focus) =>
		RunFocusCliCore(dataRoot, project, format, includeRank, focus, []);

	private static TerminalTestProcessResult RunFocusCliCore(
		string dataRoot,
		string project,
		string format,
		bool includeRank,
		IReadOnlyList<string> focus,
		IReadOnlyList<string> extraArguments)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in new[]
		         {
			         "--language", "en", "export", "context", project,
			         "--view", "content", "--format", format,
			         "--git-mode", "none", "--exclude", "none", "-o", "-", "--progress", "never"
		         })
		{
			startInfo.ArgumentList.Add(argument);
		}
		if (includeRank)
		{
			startInfo.ArgumentList.Add("--rank");
			startInfo.ArgumentList.Add("importance");
		}
		foreach (var path in focus)
		{
			startInfo.ArgumentList.Add("--focus");
			startInfo.ArgumentList.Add(path);
		}
		foreach (var argument in extraArguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		return TerminalTestProcess.Run(startInfo);
	}

	private const string StoragePayload = "namespace Fixture; public sealed class A { B Value = new(); } // STORAGE_PAYLOAD";

	private static string CreateStorageThresholdFixture(
		TemporaryDirectory workspace,
		string name,
		int fileCount)
	{
		var project = workspace.CreateDirectory(name);
		workspace.WriteFile($"{name}/A.cs", StoragePayload);
		workspace.WriteFile($"{name}/B.cs", "namespace Fixture; public sealed class B { }");
		for (var index = 2; index < fileCount; index++)
			workspace.WriteFile($"{name}/dummy-{index:D3}.txt", $"payload-{index:D3}");
		return project;
	}

	private static string CreateRedactionFixture(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("redaction-project");
		workspace.WriteFile(
			"redaction-project/A.cs",
			"class A { const string Token = \"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\"; B Value = new(); }");
		workspace.WriteFile(
			"redaction-project/B.cs",
			"class B { const string Token = \"ghp_Q7wE9rT2yU4iO6pA8sD0fG1hJ3kL5zX7cV9b\"; }");
		return project;
	}

	private static string CreateBudgetFixture(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("budget-project");
		workspace.WriteFile(
			"budget-project/A.cs",
			"public sealed class A { B Value = new(); } // " + new string('a', 400));
		workspace.WriteFile(
			"budget-project/B.cs",
			"public sealed class B { } // " + new string('b', 400));
		workspace.WriteFile(
			"budget-project/Fixture.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
		return project;
	}

	private static ValueTask<CallToolResult> CallFocusPackAsync(
		ActualMcpProcess server,
		string format,
		IReadOnlyList<string> focus,
		long? maximumTokens = null)
	{
		var arguments = new Dictionary<string, object?>
		{
			["view"] = "content",
			["format"] = format,
			["rank"] = "importance",
			["focus"] = focus.ToArray()
		};
		if (maximumTokens is not null)
			arguments["max_tokens"] = maximumTokens.Value;
		return server.Client.CallToolAsync(
			"pack_context",
			arguments,
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
	}

	private static string ExtractFileContent(string document, string format, string path)
	{
		var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (format == "json")
		{
			using var json = JsonDocument.Parse(ExtractJsonDocument(normalized));
			return json.RootElement.GetProperty("files").EnumerateArray()
				.Single(file => Path.GetFileName(file.GetProperty("path").GetString()) == path)
				.GetProperty("content").GetString()!;
		}
		if (format == "xml")
		{
			var start = normalized.IndexOf("<?xml", StringComparison.Ordinal);
			var end = normalized.IndexOf("</devprojexContext>", start, StringComparison.Ordinal);
			Assert.True(start >= 0 && end >= start, normalized);
			var xml = XDocument.Parse(normalized[start..(end + "</devprojexContext>".Length)]);
			return xml.Root!.Element("files")!.Elements("file")
				.Single(file => Path.GetFileName(file.Attribute("path")?.Value) == path)
				.Element("content")!.Value;
		}
		if (format == "markdown")
		{
			var marker = $"## `{path}`\n\n```";
			var start = normalized.IndexOf(marker, StringComparison.Ordinal);
			Assert.True(start >= 0, normalized);
			start = normalized.IndexOf('\n', start + marker.Length);
			Assert.True(start >= 0, normalized);
			start++;
			var end = normalized.IndexOf("\n```", start, StringComparison.Ordinal);
			Assert.True(end >= start, normalized);
			return normalized[start..end];
		}

		var textMarker = $"{path}:\n\n";
		var textStart = normalized.IndexOf(textMarker, StringComparison.Ordinal);
		Assert.True(textStart >= 0, normalized);
		textStart += textMarker.Length;
		var textEnd = normalized.IndexOf('\n', textStart);
		return textEnd < 0 ? normalized[textStart..] : normalized[textStart..textEnd];
	}

	private static async Task<string> ReadStoredPackIfNeededAsync(
		ActualMcpProcess server,
		CallToolResult result)
	{
		var response = AllProcessText(result);
		if (!response.Contains("Pack stored as '", StringComparison.Ordinal))
			return response;

		var lineCountStart = response.IndexOf(" characters, ", StringComparison.Ordinal);
		var lineCountEnd = response.IndexOf(" lines).", lineCountStart, StringComparison.Ordinal);
		Assert.True(lineCountStart >= 0 && lineCountEnd > lineCountStart, response);
		lineCountStart += " characters, ".Length;
		Assert.True(int.TryParse(response[lineCountStart..lineCountEnd], out var lineCount), response);

		var packId = ExtractPackId(response);
		var document = new StringBuilder();
		const int pageSize = 200;
		for (var startLine = 1; startLine <= lineCount; startLine += pageSize)
		{
			var page = await server.Client.CallToolAsync(
				"read_pack",
				new Dictionary<string, object?>
				{
					["pack_id"] = packId,
					["start_line"] = startLine,
					["end_line"] = Math.Min(startLine + pageSize - 1, lineCount)
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, page.IsError);
			var pageText = AllProcessText(page).Replace("\r\n", "\n", StringComparison.Ordinal);
			var opening = pageText.IndexOf("<untrusted-data-", StringComparison.Ordinal);
			var payloadStart = pageText.IndexOf('\n', opening);
			var payloadEnd = pageText.IndexOf("</untrusted-data-", payloadStart, StringComparison.Ordinal);
			Assert.True(opening >= 0 && payloadStart >= 0 && payloadEnd >= payloadStart, pageText);
			if (document.Length > 0 && document[^1] != '\n')
				document.Append('\n');
			document.Append(pageText.AsSpan(payloadStart + 1, payloadEnd - payloadStart - 1).TrimEnd('\n'));
		}

		return document.ToString();
	}

	private static Dictionary<string, string> JsonPayloads(string document)
	{
		using var json = JsonDocument.Parse(ExtractJsonDocument(document));
		return json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
			static file => file.GetProperty("path").GetString()!,
			static file => file.GetProperty("content").ValueKind == JsonValueKind.String
				? file.GetProperty("content").GetString()!
				: string.Empty,
			StringComparer.Ordinal);
	}

	private static void AssertPayloadsEqual(
		IReadOnlyDictionary<string, string> expected,
		IReadOnlyDictionary<string, string> actual) =>
		Assert.Equal(
			expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
			actual.OrderBy(static pair => pair.Key, StringComparer.Ordinal));

	private static string ExtractJsonDocument(string text)
	{
		var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
		var schema = normalized.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
		Assert.True(schema >= 0, normalized);
		var start = normalized.LastIndexOf('{', schema);
		var end = normalized.IndexOf("\n}", schema, StringComparison.Ordinal);
		Assert.True(start >= 0 && end >= start, normalized);
		return normalized[start..(end + 2)];
	}

	private static void AssertSkippedBudgetMetadata(string document)
	{
		using var json = JsonDocument.Parse(ExtractJsonDocument(document));
		var skipped = json.RootElement.GetProperty("ranking").GetProperty("skipped").EnumerateArray()
			.ToDictionary(
				file => Path.GetFileName(file.GetProperty("path").GetString()!.Replace('/', Path.DirectorySeparatorChar)),
				StringComparer.Ordinal);
		var seed = skipped["A.cs"];
		Assert.Equal(1, seed.GetProperty("priority").GetInt32());
		Assert.Equal(0, seed.GetProperty("hop").GetInt32());
		Assert.True(seed.GetProperty("baseImportancePriority").GetInt32() > 0);
		Assert.Equal(10, seed.GetProperty("remainingEstimatedTokens").GetInt64());
		Assert.False(seed.TryGetProperty("via", out _));

		var dependency = skipped["B.cs"];
		Assert.Equal(2, dependency.GetProperty("priority").GetInt32());
		Assert.Equal(1, dependency.GetProperty("hop").GetInt32());
		Assert.True(dependency.GetProperty("baseImportancePriority").GetInt32() > 0);
		Assert.Equal(10, dependency.GetProperty("remainingEstimatedTokens").GetInt64());
		Assert.Equal("A.cs", dependency.GetProperty("via").GetProperty("path").GetString());
		Assert.Equal("dependency-of", dependency.GetProperty("via").GetProperty("relation").GetString());
	}
}
