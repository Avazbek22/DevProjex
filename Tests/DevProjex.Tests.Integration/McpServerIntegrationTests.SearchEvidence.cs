using System.Text;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task SearchKeepsAUsefulDeclarationAfterFiveThousandEarlyRepetitions()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "a-repetitions.txt"),
			string.Concat(Enumerable.Repeat("Needle repeated mention\n", 5_000)));
		Directory.CreateDirectory(Path.Combine(project, "zz"));
		File.WriteAllText(
			Path.Combine(project, "zz", "PublicApi.cs"),
			"namespace Contracts;\npublic sealed class Needle { }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "Needle",
			["context_lines"] = 0,
			["max_results"] = 1
		});
		var text = Text(result);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("zz/PublicApi.cs", text, StringComparison.Ordinal);
		Assert.Contains("2:public sealed class Needle { }", text, StringComparison.Ordinal);
		Assert.Contains("[Search boundary] partial", text, StringComparison.Ordinal);
		Assert.Contains("matches retained=5000/5001", text, StringComparison.Ordinal);
		Assert.Contains("limits=retained-matches,max-results", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchNamesADeclarationInTheLastOfManyMatchingFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 80; index++)
			File.WriteAllText(Path.Combine(project, $"mention-{index:D2}.txt"), "PublicContract mention\n");
		File.WriteAllText(
			Path.Combine(project, "zz-PublicContract.cs"),
			"public sealed class PublicContract { }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "PublicContract",
			["context_lines"] = 0,
			["max_results"] = 1
		});
		var text = Text(result);

		Assert.Contains("zz-PublicContract.cs", text, StringComparison.Ordinal);
		Assert.Contains("1:public sealed class PublicContract { }", text, StringComparison.Ordinal);
		Assert.Contains("in PublicContract", text, StringComparison.Ordinal);
		Assert.Contains("declaration files named=1", text, StringComparison.Ordinal);
		Assert.Contains("limits=annotation-files,max-results", text, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("tests/PublicApiTests.cs")]
	[InlineData("generated/PublicApi.snapshot")]
	public async Task ExplicitlySelectedEvidenceIsNotDemotedByItsPath(string selectedPath)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var fullPath = Path.Combine(project, selectedPath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllText(fullPath, "PublicApiCompatibility expected-signature\n");
		File.WriteAllText(Path.Combine(project, "ordinary.txt"), "PublicApiCompatibility mention\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "PublicApiCompatibility",
			["paths"] = new[] { selectedPath },
			["context_lines"] = 0,
			["max_results"] = 1
		});

		McpSearchOutputAssertions.ContainsMatch(
			Text(result),
			selectedPath,
			1,
			"PublicApiCompatibility expected-signature");
	}

	[Fact]
	public async Task ApprovedSnapshotCanSupplyThePublicApiCompatibilityEvidence()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "a-repeated.txt"),
			string.Concat(Enumerable.Repeat("PublicContract mention\n", 100)));
		Directory.CreateDirectory(Path.Combine(project, "snapshots"));
		File.WriteAllText(
			Path.Combine(project, "snapshots", "PublicApi.approved.txt"),
			"interface PublicContract { Execute(value: string): Result }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "PublicContract",
			["context_lines"] = 0,
			["max_results"] = 1
		});

		McpSearchOutputAssertions.ContainsMatch(
			Text(result),
			"snapshots/PublicApi.approved.txt",
			1,
			"interface PublicContract { Execute(value: string): Result }");
	}

	[Fact]
	public async Task UnsupportedLanguageMatchRemainsVisibleBesideSupportedDeclarations()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "contract.lua"), "PublicSurface = expected\n");
		File.WriteAllText(Path.Combine(project, "Contract.cs"), "public sealed class PublicSurface { }\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "PublicSurface",
			["context_lines"] = 0,
			["max_results"] = 2
		});
		var text = Text(result);

		McpSearchOutputAssertions.ContainsMatch(text, "contract.lua", 1, "PublicSurface = expected");
		Assert.Contains("Contract.cs", text, StringComparison.Ordinal);
		Assert.Contains("1:public sealed class PublicSurface { }", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchWithoutReachedLimitsDeclaresTheObservedSelectionComplete()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "one.txt"), "needle\n");
		File.WriteAllText(Path.Combine(project, "two.txt"), "nothing\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["context_lines"] = 0
		});

		Assert.Contains("[Search boundary] complete · sources inspected=2/2 · matches retained=1/1 · " +
						"matches written=1 · declaration files named=0.", Text(result), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchReportsTheAnnotationFileLimitSeparately()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 65; index++)
			File.WriteAllText(Path.Combine(project, $"file-{index:D2}.txt"), "needle\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["context_lines"] = 0,
			["max_results"] = 100
		});

		Assert.Contains("[Search boundary] partial", Text(result), StringComparison.Ordinal);
		Assert.Contains("limits=annotation-files", Text(result), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchReportsTheResponseCharacterLimitSeparately()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "large.txt"), "needle " + new string('x', 20_000));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["context_lines"] = 0
		});

		Assert.Contains("[Search boundary] partial", Text(result), StringComparison.Ordinal);
		Assert.Contains("limits=response-characters", Text(result), StringComparison.Ordinal);
		Assert.Contains("narrow pattern, paths, or include_patterns", Text(result), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SearchKeepsCoordinatesAndTextFromOneSnapshotWhenSourceChangesAfterScanning()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var path = Path.Combine(project, "Mutable.txt");
		File.WriteAllText(path, "alpha\nneedle old\nomega\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var changed = false;
		McpSearchExecutionHooks.AfterScan = scannedPath =>
		{
			if (changed || !string.Equals(scannedPath, "Mutable.txt", StringComparison.Ordinal))
				return;
			changed = true;
			File.WriteAllText(path, "inserted\nalpha\nneedle new\nomega\n");
		};
		try
		{
			var result = await server.CallAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = "needle",
				["context_lines"] = 0,
				["ignore_case"] = false
			});
			var text = Text(result);

			Assert.True(changed);
			McpSearchOutputAssertions.ContainsMatch(text, "Mutable.txt", 2, "needle old");
			Assert.DoesNotContain("needle new", text, StringComparison.Ordinal);
		}
		finally
		{
			McpSearchExecutionHooks.AfterScan = null;
		}
	}

	[Fact]
	public async Task ProtectedUnicodePrefixDoesNotShiftReturnedMatchOrBoundaryCounts()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(
			Path.Combine(project, "Unicode.txt"),
			$"πππ {Secret}\nλ needle evidence\n");
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);

		var result = await server.CallAsync("search_project", new Dictionary<string, object?>
		{
			["pattern"] = "needle",
			["context_lines"] = 0,
			["ignore_case"] = false
		});
		var text = Text(result);

		McpSearchOutputAssertions.ContainsMatch(text, "Unicode.txt", 2, "λ needle evidence");
		Assert.Contains("sources inspected=1/1 · matches retained=1/1 · matches written=1", text, StringComparison.Ordinal);
	}
}
