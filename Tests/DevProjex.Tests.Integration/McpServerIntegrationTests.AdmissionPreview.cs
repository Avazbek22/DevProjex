using DevProjex.Application.Diagnostics;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private static string CreateAdmissionFixture(TemporaryDirectory workspace, int fileCount = 6)
	{
		var project = workspace.CreateDirectory("admission");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		for (var index = 0; index < fileCount; index++)
		{
			// Sizes rise with the index, so a small budget admits a prefix and skips the rest.
			File.WriteAllText(
				Path.Combine(project, "src", $"f{index:D3}.txt"),
				new string('x', 40 + (index * 120)));
		}
		return project;
	}

	private static JsonElement Structured(CallToolResult result)
	{
		Assert.NotNull(result.StructuredContent);
		return result.StructuredContent!.Value;
	}

	[Fact]
	public async Task AnalyzeWithoutABudgetKeepsItsExactResult()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync("analyze", new Dictionary<string, object?>());

		// The preview is opt-in: no budget means no admission section and no new bytes at all.
		Assert.False(Structured(response).TryGetProperty("admission", out _));
		Assert.DoesNotContain("[Budget accounting]", AllText(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeReportsTheAdmissionPlanUnderABudget()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"analyze",
			new Dictionary<string, object?> { ["max_tokens"] = "60" });
		var admission = Structured(response).GetProperty("admission");

		Assert.Equal(60, admission.GetProperty("budget").GetInt64());
		Assert.True(admission.GetProperty("includedFileCount").GetInt32() > 0);
		Assert.True(admission.GetProperty("skippedFileCount").GetInt32() > 0);
		Assert.False(admission.GetProperty("includedFilesTruncated").GetBoolean());
		Assert.Equal(0, admission.GetProperty("additionalIncludedFileCount").GetInt32());
		Assert.False(string.IsNullOrEmpty(admission.GetProperty("includedOrderDigest").GetString()));
		Assert.Equal("full", admission.GetProperty("detail").GetString());
		var included = admission.GetProperty("includedFiles").EnumerateArray().ToArray();
		Assert.Equal(admission.GetProperty("includedFileCount").GetInt32(), included.Length);
		Assert.All(included, file => Assert.True(file.GetProperty("tokens").GetInt64() >= 0));
		var skipped = admission.GetProperty("skippedFiles").EnumerateArray().ToArray();
		Assert.NotEmpty(skipped);
		Assert.All(skipped, file => Assert.True(file.GetProperty("remainingTokens").GetInt64() >= 0));
	}

	[Fact]
	public async Task TheAdmittedSetMatchesWhatAPackAdmits()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var preview = Structured(await server.CallAsync(
				"analyze",
				new Dictionary<string, object?> { ["max_tokens"] = "60" }))
			.GetProperty("admission");
		var previewPaths = preview.GetProperty("includedFiles").EnumerateArray()
			.Select(file => file.GetProperty("path").GetString()!.Replace('\\', '/'))
			.ToArray();

		foreach (var format in new[] { "markdown", "text", "json", "xml" })
		{
			var packText = Text(await server.CallAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "content",
					["format"] = format,
					["max_tokens"] = "60"
				}));

			Assert.Contains(
				$"Included: {preview.GetProperty("includedFileCount").GetInt32()} file",
				packText,
				StringComparison.Ordinal);
			Assert.Contains(
				$"Skipped: {preview.GetProperty("skippedFileCount").GetInt32()} file",
				packText,
				StringComparison.Ordinal);
			// Every file the preview admitted is present in the pack, in every format.
			foreach (var path in previewPaths)
				Assert.Contains(Path.GetFileName(path), packText, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task TheAdmittedSetMatchesAPackWhenDetailIsMixed()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var overrides = new object[]
		{
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "src/file0.cs", "src/file1.cs" },
				["detail"] = "signatures"
			}
		};
		var preview = Structured(await server.CallAsync(
				"analyze",
				new Dictionary<string, object?>
				{
					["max_tokens"] = "120",
					["detail"] = "full",
					["detail_by_pattern"] = overrides
				}))
			.GetProperty("admission");
		var packText = Text(await server.CallAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["max_tokens"] = "120",
				["detail"] = "full",
				["detail_by_pattern"] = overrides
			}));

		Assert.Contains(
			$"Included: {preview.GetProperty("includedFileCount").GetInt32()} file",
			packText,
			StringComparison.Ordinal);
		Assert.Contains(
			$"Skipped: {preview.GetProperty("skippedFileCount").GetInt32()} file",
			packText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheAdmittedSetMatchesWithRankingAndFocus()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = new Dictionary<string, object?>
		{
			["max_tokens"] = "60",
			["rank"] = "importance"
		};
		var preview = Structured(await server.CallAsync("analyze", arguments)).GetProperty("admission");
		var packText = Text(await server.CallAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["view"] = "content",
				["format"] = "text",
				["max_tokens"] = "60",
				["rank"] = "importance"
			}));

		Assert.Contains(
			$"Included: {preview.GetProperty("includedFileCount").GetInt32()} file",
			packText,
			StringComparison.Ordinal);
		// With ranking every admitted entry carries the position it was considered at.
		Assert.All(
			preview.GetProperty("includedFiles").EnumerateArray(),
			file => Assert.True(file.GetProperty("priority").GetInt32() > 0));
	}

	[Fact]
	public async Task ALargeSelectionReportsAPrefixWithItsFlagCountAndDigest()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("wide");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		for (var index = 0; index < 1_100; index++)
			File.WriteAllText(Path.Combine(project, "src", $"f{index:D5}.txt"), "tiny");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var admission = Structured(await server.CallAsync(
				"analyze",
				new Dictionary<string, object?> { ["max_tokens"] = "5000" }))
			.GetProperty("admission");

		Assert.Equal(1_100, admission.GetProperty("includedFileCount").GetInt32());
		Assert.True(admission.GetProperty("includedFilesTruncated").GetBoolean());
		var listed = admission.GetProperty("includedFiles").GetArrayLength();
		Assert.True(listed is > 0 and <= 1_000, $"listed {listed}");
		Assert.Equal(
			1_100 - listed,
			admission.GetProperty("additionalIncludedFileCount").GetInt32());
		// The digest covers the complete order even though the list was cut.
		Assert.Equal(64, admission.GetProperty("includedOrderDigest").GetString()!.Length);
	}

	[Fact]
	public async Task ThePreviewProducesNoPreparedFilesNoDocumentAndNoStoredPack()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);

		// The diagnostics scope flows with the async context, so each server is started inside the
		// scope that is meant to observe it.
		ContentPipelineDiagnosticSnapshot previewDiagnostics;
		CallToolResult response;
		using (var measurement = ContentPipelineDiagnostics.BeginMeasurement())
		{
			await using var previewServer = await McpTestServer.StartAsync(project, workspace.Path);
			response = await previewServer.CallAsync(
				"analyze",
				new Dictionary<string, object?> { ["max_tokens"] = "60" });
			previewDiagnostics = measurement.Capture();
		}

		// The same selection and budget through a pack, so the zero counters below are shown to be
		// real rather than a scope that recorded nothing at all.
		ContentPipelineDiagnosticSnapshot packDiagnostics;
		using (var measurement = ContentPipelineDiagnostics.BeginMeasurement())
		{
			await using var packServer = await McpTestServer.StartAsync(project, workspace.Path);
			await packServer.CallAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "content",
					["format"] = "text",
					["max_tokens"] = "60"
				});
			packDiagnostics = measurement.Capture();
		}

		Assert.NotEqual(true, response.IsError);
		// A pack materialises the admitted content and serialises a document; those non-zero counters
		// are what show the scope is recording, so the preview's zeros mean something.
		Assert.True(packDiagnostics.PreparedFilesMaterialized > 0);
		Assert.True(packDiagnostics.DocumentWriteBytes > 0);
		Assert.Equal(0, previewDiagnostics.PreparedFilesMaterialized);
		Assert.Equal(0, previewDiagnostics.PreparedWriteBytes);
		Assert.Equal(0, previewDiagnostics.DocumentWriteBytes);
		Assert.True(
			Structured(response).GetProperty("admission").GetProperty("includedFileCount").GetInt32() > 0);
		Assert.DoesNotContain("Pack stored as", AllText(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheTextFormMirrorsThePackBudgetLines()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var text = AllText(await server.CallAsync(
			"analyze",
			new Dictionary<string, object?> { ["max_tokens"] = "60" }));

		Assert.Contains("Token budget: 60 estimated tokens.", text, StringComparison.Ordinal);
		Assert.Contains("Included: ", text, StringComparison.Ordinal);
		Assert.Contains("Skipped: ", text, StringComparison.Ordinal);
		Assert.Contains("Skipped files:", text, StringComparison.Ordinal);
		Assert.Matches(
			@"\[Budget accounting\] content ≈ \d+ of 60 tokens · budget report ≈ \d+ · reply ≈ \d+",
			text);
	}

	[Theory]
	[InlineData("rank")]
	[InlineData("focus")]
	public async Task OrderingInputsRequireABudget(string parameter)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"analyze",
			new Dictionary<string, object?>
			{
				[parameter] = parameter == "rank" ? "importance" : "src/f000.txt"
			});

		// An order that is neither returned nor used must not be computed.
		Assert.True(response.IsError);
		Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", AllText(response), StringComparison.Ordinal);
		Assert.Contains("max_tokens", AllText(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task FocusStillRequiresRanking()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateAdmissionFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"analyze",
			new Dictionary<string, object?>
			{
				["max_tokens"] = "60",
				["focus"] = "src/f000.txt"
			});

		Assert.True(response.IsError);
		Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", AllText(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SkippedEntriesShowTheEffectiveDetailTheyWereCostedAt()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var admission = Structured(await server.CallAsync(
				"analyze",
				new Dictionary<string, object?>
				{
					["max_tokens"] = "30",
					["detail"] = "full",
					["detail_by_pattern"] = new object[]
					{
						new Dictionary<string, object?>
						{
							["patterns"] = new[] { "src/file0.cs" },
							["detail"] = "signatures"
						}
					}
				}))
			.GetProperty("admission");

		var skipped = admission.GetProperty("skippedFiles").EnumerateArray().ToArray();
		Assert.NotEmpty(skipped);
		Assert.All(
			skipped,
			file => Assert.Contains(
				file.GetProperty("detail").GetString(),
				new[] { "full", "compact", "signatures" }));
	}
}
