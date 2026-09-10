using System.Text.RegularExpressions;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	private const string BodyMarker = "unique-body-marker-of-the-reduced-file";

	/// <summary>
	/// Ten C# files: three overridden to signatures, one to full, the rest on the call level, under a
	/// call that compresses. Mirrors the shape a caller reaches for when a few files matter verbatim.
	/// </summary>
	private static string CreateFixture(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		Directory.CreateDirectory(Path.Combine(project, "docs"));
		for (var index = 0; index < 8; index++)
		{
			File.WriteAllText(
				Path.Combine(project, "src", $"file{index}.cs"),
				$$"""
				// leading comment for file {{index}}
				namespace Fixture;

				public static class Sample{{index}}
				{
					public static int Compute()
					{
						var accumulated = {{index}};
						// body comment {{index}}
						for (var step = 0; step < 10; step++)
							accumulated += step;
						return accumulated;
					}
				}
				""");
		}
		File.WriteAllText(
			Path.Combine(project, "src", "reduced.cs"),
			$$"""
			// leading comment for the reduced file
			namespace Fixture;

			public static class Reduced
			{
				public static string Describe()
				{
					var text = "{{BodyMarker}}";
					return text;
				}
			}
			""");
		File.WriteAllText(
			Path.Combine(project, "docs", "notes.md"),
			"# Notes\n\nPlain prose that no language pack transforms.\n");
		return project;
	}

	private static Dictionary<string, object?> PackArguments(params object[] overrides) =>
		new()
		{
			["view"] = "content",
			["format"] = "text",
			["detail"] = "compact",
			["detail_by_pattern"] = overrides
		};

	[Fact]
	public async Task AnOverrideToSignaturesCollapsesOnlyTheFilesItClaims()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/reduced.cs" },
					["detail"] = "signatures"
				}));
		var text = Text(response);

		// The claimed file loses its body; a file the pattern does not claim keeps its own.
		Assert.DoesNotContain(BodyMarker, text, StringComparison.Ordinal);
		Assert.Contains("accumulated", text, StringComparison.Ordinal);
		Assert.Contains("Reduced", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheTrailerStatesTheMixAndNamesAMaskThatClaimedNothing()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/file0.cs", "src/file1.cs", "src/file2.cs" },
					["detail"] = "signatures"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "docs/notes.md" },
					["detail"] = "full"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/absent/**" },
					["detail"] = "signatures"
				}));
		var text = Text(response);

		var mix = DetailMixPattern().Match(text);
		Assert.True(mix.Success, $"no detail mix line in:\n{text}");
		Assert.Equal("1", mix.Groups["full"].Value);
		Assert.Equal("6", mix.Groups["compact"].Value);
		Assert.Equal("3", mix.Groups["signatures"].Value);
		Assert.Equal("4", mix.Groups["matched"].Value);
		Assert.Equal("5", mix.Groups["total"].Value);
		// Selection is never widened, so a mask that claimed nothing has to be named rather than
		// looking like a level that simply had no files.
		Assert.Contains("[Detail] unmatched: src/absent/**", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheLastMatchingEntryWins()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var response = await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/**" },
					["detail"] = "signatures"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/reduced.cs" },
					["detail"] = "full"
				}));
		var text = Text(response);

		// Both entries claim src/reduced.cs; the later one decides, so its body survives.
		Assert.Contains(BodyMarker, text, StringComparison.Ordinal);
		var mix = DetailMixPattern().Match(text);
		Assert.True(mix.Success);
		Assert.Equal("1", mix.Groups["full"].Value);
		Assert.Equal("8", mix.Groups["signatures"].Value);
	}

	[Fact]
	public async Task AFullOverrideDoesNotUndoAProfileThatCompresses()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		var profile = Path.Combine(project, "compressing.json");
		File.WriteAllText(
			profile,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": { "compressCode": true, "stripComments": true, "stripBlankLines": true }
			}
			""");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = PackArguments(
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "src/reduced.cs" },
				["detail"] = "full"
			});
		arguments["profile"] = "compressing.json";
		arguments["detail"] = "full";
		var text = Text(await server.CallAsync("pack_context", arguments));

		// full adds no reduction of its own and must never remove one the profile mandates.
		Assert.DoesNotContain(BodyMarker, text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ContentDoesNotDependOnHowFilesAreGroupedAcrossEntries()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var oneEntry = Text(await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/file0.cs", "src/file1.cs", "src/file2.cs" },
					["detail"] = "signatures"
				})));
		var threeEntries = Text(await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/file2.cs" },
					["detail"] = "signatures"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/file0.cs" },
					["detail"] = "signatures"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/file1.cs" },
					["detail"] = "signatures"
				})));

		// Same files at the same effective level: the document may not depend on which entry claimed
		// them or in what order the entries were written.
		Assert.Equal(ExtractSpotlightBody(oneEntry), ExtractSpotlightBody(threeEntries));
	}

	[Fact]
	public async Task StructuredEntriesCarryTheEffectiveDetailOfEachFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = PackArguments(
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "src/reduced.cs" },
				["detail"] = "signatures"
			},
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "docs/**" },
				["detail"] = "full"
			});
		arguments["format"] = "json";
		var text = Text(await server.CallAsync("pack_context", arguments));

		using var document = JsonDocument.Parse(ExtractSpotlightBody(text));
		// JSON keeps its machine file-path representation, so entries are matched by suffix.
		var byPath = document.RootElement.GetProperty("files").EnumerateArray()
			.ToDictionary(
				file => file.GetProperty("path").GetString()!.Replace('\\', '/'),
				file => file.GetProperty("detail").GetString());

		Assert.Equal("signatures", DetailOf(byPath, "src/reduced.cs"));
		Assert.Equal("full", DetailOf(byPath, "docs/notes.md"));
		Assert.Equal("compact", DetailOf(byPath, "src/file0.cs"));
	}

	[Fact]
	public async Task AUniformCallKeepsItsExactBytesAndAddsNoDetailField()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var uniform = new Dictionary<string, object?>
		{
			["view"] = "content",
			["format"] = "json",
			["detail"] = "compact"
		};
		var withEmptyOverrides = new Dictionary<string, object?>(uniform)
		{
			["detail_by_pattern"] = Array.Empty<object>()
		};

		var first = Text(await server.CallAsync("pack_context", uniform));
		var second = Text(await server.CallAsync("pack_context", withEmptyOverrides));

		// An empty list means no mix, so the response may not gain a per-file field or change shape.
		Assert.Equal(ExtractSpotlightBody(first), ExtractSpotlightBody(second));
		Assert.DoesNotContain("\"detail\"", ExtractSpotlightBody(first), StringComparison.Ordinal);
		Assert.DoesNotContain("[Detail]", first, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BudgetAdmissionUsesTheEffectiveCostAndSkippedEntriesShowTheirLevel()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		Directory.CreateDirectory(Path.Combine(project, "src"));
		var body = string.Join("\n", Enumerable.Range(0, 60).Select(step => $"\t\tvalue += {step};"));
		foreach (var name in new[] { "wide-a", "wide-b" })
		{
			File.WriteAllText(
				Path.Combine(project, "src", $"{name}.cs"),
				$$"""
				namespace Fixture;

				public static class {{name.Replace("-", "")}}
				{
					public static int Run()
					{
						var value = 0;
				{{body}}
						return value;
					}
				}
				""");
		}
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = PackArguments(
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "src/wide-a.cs" },
				["detail"] = "signatures"
			});
		arguments["detail"] = "full";
		arguments["max_tokens"] = "40";
		var text = Text(await server.CallAsync("pack_context", arguments));

		// wide-a is costed at signatures and fits; wide-b is costed at full and does not, and its
		// skipped line names the level its estimate belongs to.
		Assert.Contains("Included: 1 file", text, StringComparison.Ordinal);
		Assert.Contains("Skipped: 1 file", text, StringComparison.Ordinal);
		Assert.Contains("estimated tokens at full)", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task InvalidEntriesFailWithTheirIndexAndProduceNoContent()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var response = await server.CallAsync(
			"pack_context",
			PackArguments(
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "src/**" },
					["detail"] = "signatures"
				},
				new Dictionary<string, object?>
				{
					["patterns"] = new[] { "docs/**" },
					["detail"] = "verbose"
				}));

		Assert.True(response.IsError);
		var text = Text(response);
		Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", text, StringComparison.Ordinal);
		Assert.Contains("detail_by_pattern[1]", text, StringComparison.Ordinal);
		Assert.Equal(0, measurement.Capture().PreparedFilesMaterialized);
	}

	[Fact]
	public async Task PerFileDetailIsRejectedForATreeOnlyPack()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateFixture(workspace);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var arguments = PackArguments(
			new Dictionary<string, object?>
			{
				["patterns"] = new[] { "src/**" },
				["detail"] = "signatures"
			});
		arguments["view"] = "tree";
		var response = await server.CallAsync("pack_context", arguments);

		Assert.True(response.IsError);
		Assert.Contains("DPX-MCP-INVALID-ARGUMENTS", Text(response), StringComparison.Ordinal);
		Assert.Contains("detail_by_pattern", Text(response), StringComparison.Ordinal);
	}

	private static string? DetailOf(IReadOnlyDictionary<string, string?> byPath, string suffix)
	{
		var match = byPath.Keys.SingleOrDefault(path => path.EndsWith(suffix, StringComparison.Ordinal));
		Assert.NotNull(match);
		return byPath[match];
	}

	[GeneratedRegex(
		@"\[Detail\] full (?<full>\d+) · compact (?<compact>\d+) · signatures (?<signatures>\d+) · overrides (?<matched>\d+) of (?<total>\d+) patterns matched")]
	private static partial Regex DetailMixPattern();
}
