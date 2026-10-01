using DevProjex.Application.Context;
using DevProjex.Infrastructure.AgentJournal;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task BatchReadRetainsPerRangeJournalProtectionCounts()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "Ranges.txt"),
			$"first {Secret}\nplain\nlast {Secret}\n");
		await using (var server = await McpTestServer.StartAsync(project, workspace.Path))
		{
			var result = await server.CallAsync("get_file", new Dictionary<string, object?>
			{
				["requests"] = new object[]
				{
					new
					{
						path = "Ranges.txt",
						ranges = new[]
						{
							new { start_line = 1, end_line = 1 },
							new { start_line = 3, end_line = 3 }
						}
					}
				}
			});
			Assert.NotEqual(true, result.IsError);
			Assert.Equal(2, Regex.Matches(Text(result), "DEVPROJEX_REDACTED\\[").Count);
		}

		using var journal = new AgentJournalStore(
			() => Path.Combine(workspace.Path, "app-data"),
			activeSessionProvider: static () => []);
		var session = Assert.Single(await journal.ListSessionsAsync(cancellationToken: TestContext.Current.CancellationToken));
		var call = Assert.Single(await journal.ReadCallsAsync(session.Id, TestContext.Current.CancellationToken));
		Assert.Equal(2, call.SecretsMasked);
	}

	[Fact]
	public async Task BatchReadReportsEachMergedRangeFromItsReturnedCoverage()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "Ranges.txt"),
			string.Concat(Enumerable.Range(1, 1_600).Select(static line => $"line-{line:D4}\n")));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new
				{
					path = "Ranges.txt",
					ranges = new[]
					{
						new { start_line = 1, end_line = 100 },
						new { start_line = 50, end_line = 1_500 },
						new { start_line = 1_450, end_line = 1_600 }
					}
				}
			}
		});
		var text = Text(result).Replace("\r\n", "\n", StringComparison.Ordinal);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("Lines: 1-992 of 1601", text, StringComparison.Ordinal);
		Assert.Contains("1.1 — ok", text, StringComparison.Ordinal);
		Assert.Contains("1.2 — partial", text, StringComparison.Ordinal);
		Assert.Contains("1.3 — not-returned", text, StringComparison.Ordinal);
		Assert.Contains("[Batch read] ok=1 · partial=1 · not-returned=1 · unavailable=0.", text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task BatchReadUsesScalarGetFileContinuationInsideALongLine()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Long.txt"), new string('x', 60_000) + "\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new
				{
					path = "Long.txt",
					ranges = new[] { new { start_line = 1, end_line = 1 } }
				}
			}
		});
		var text = Text(result);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains(
			"[Batch continuation] Continue with scalar get_file using the arguments below; " +
			"start_column is not supported in requests.",
			text,
			StringComparison.Ordinal);
		var arguments = ReadContinuationArguments(text, "Long.txt");
		Assert.True(arguments.TryGetProperty("project", out _));
		Assert.False(arguments.TryGetProperty("branch", out _));
		Assert.Equal("Long.txt", arguments.GetProperty("path").GetString());
		Assert.Equal(1, arguments.GetProperty("start_line").GetInt32());
		Assert.Equal(1, arguments.GetProperty("end_line").GetInt32());
		Assert.True(arguments.GetProperty("start_column").GetInt32() > 1);
		Assert.False(arguments.TryGetProperty("requests", out _));
	}

	[Fact]
	public async Task BatchReadUsesValidRequestsForLineContinuation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "Lines.txt"),
			string.Concat(Enumerable.Range(1, 1_600).Select(static line => $"line-{line:D4}\n")));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["requests"] = new object[]
			{
				new
				{
					path = "Lines.txt",
					ranges = new[] { new { start_line = 1, end_line = 1_600 } }
				}
			}
		});
		var text = Text(result);

		Assert.NotEqual(true, result.IsError);
		var arguments = ReadContinuationArguments(text, "Lines.txt");
		Assert.True(arguments.TryGetProperty("project", out _));
		Assert.False(arguments.TryGetProperty("branch", out _));
		Assert.False(arguments.TryGetProperty("start_column", out _));
		var request = Assert.Single(arguments.GetProperty("requests").EnumerateArray().ToArray());
		Assert.Equal("Lines.txt", request.GetProperty("path").GetString());
		var range = Assert.Single(request.GetProperty("ranges").EnumerateArray().ToArray());
		Assert.True(range.GetProperty("start_line").GetInt32() > 1);
		Assert.Equal(1_600, range.GetProperty("end_line").GetInt32());
	}

	[Fact]
	public async Task BatchReadScalarContinuationRetainsExplicitProfile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, ".gitignore"), "Ignored.txt\n");
		File.WriteAllText(Path.Combine(project, "Ignored.txt"), new string('x', 60_000) + "tail-marker\n");
		var profile = WriteUnfilteredPortableProfile(project);
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["profile"] = profile,
			["requests"] = new object[]
			{
				new
				{
					path = "Ignored.txt",
					ranges = new[] { new { start_line = 1, end_line = 1 } }
				}
			}
		});
		Assert.NotEqual(true, result.IsError);

		var arguments = ReadContinuationArguments(Text(result), "Ignored.txt");
		Assert.Equal(profile, arguments.GetProperty("profile").GetString());
		Assert.True(arguments.GetProperty("start_column").GetInt32() > 1);
		var continued = await server.CallAsync("get_file",
			JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText()));
		Assert.NotEqual(true, continued.IsError);
		Assert.Contains("tail-marker", Text(continued), StringComparison.Ordinal);
	}

	[Fact]
	public async Task BatchReadLineContinuationRetainsDelegatedExclusions()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, ".Hidden.txt"),
			string.Concat(Enumerable.Range(1, 1_600).Select(static line => $"line-{line:D4}\n")));
		await using var server = await McpTestServer.StartAsync(
			project,
			workspace.Path,
			exclusions: [ProjectExclusion.DotFiles],
			agentExclusions: true);

		var result = await server.CallAsync("get_file", new Dictionary<string, object?>
		{
			["exclusions"] = Array.Empty<string>(),
			["requests"] = new object[]
			{
				new
				{
					path = ".Hidden.txt",
					ranges = new[] { new { start_line = 1, end_line = 1_600 } }
				}
			}
		});
		Assert.NotEqual(true, result.IsError);

		var arguments = ReadContinuationArguments(Text(result), ".Hidden.txt");
		Assert.Empty(arguments.GetProperty("exclusions").EnumerateArray());
		Assert.True(arguments.TryGetProperty("requests", out _));
		var continued = await server.CallAsync("get_file",
			JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText()));
		Assert.NotEqual(true, continued.IsError);
		Assert.Contains("line-1600", Text(continued), StringComparison.Ordinal);
	}

	private static JsonElement ReadContinuationArguments(string text, string expectedPath)
	{
		const string invocationPrefix = "get_file ";
		var blocks = Regex.Matches(
			text,
			@"<untrusted-data-(?<nonce>[a-f0-9]+)>\s*(?<body>.*?)\s*</untrusted-data-\k<nonce>>",
			RegexOptions.Singleline | RegexOptions.CultureInvariant);
		foreach (Match block in blocks)
		{
			var body = block.Groups["body"].Value;
			if (!body.Contains($"\"{expectedPath}\"", StringComparison.Ordinal))
				continue;
			var invocation = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.FirstOrDefault(static line => line.StartsWith(invocationPrefix, StringComparison.Ordinal));
			if (invocation is null)
				continue;
			try
			{
				using var document = JsonDocument.Parse(invocation[invocationPrefix.Length..]);
				return document.RootElement.Clone();
			}
			catch (JsonException)
			{
				// The main content block can contain the path without being continuation JSON.
			}
		}
		throw new Xunit.Sdk.XunitException($"No continuation JSON for '{expectedPath}' was returned.\n{text}");
	}
}
