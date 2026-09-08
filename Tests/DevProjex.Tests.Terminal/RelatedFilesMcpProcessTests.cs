using System.Diagnostics;
using DevProjex.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessRelatedFilesReportsBothDirectionsAmbiguityCoverageAndProgress()
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available in this test environment.");
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/src/Contracts/IContract.cs", "namespace Contracts; public interface IContract {}\n");
		workspace.WriteFile(
			"project/src/Services/Service.cs",
			"using Contracts; using Alpha; using Beta; namespace Services; public sealed class Service { public IContract Contract { get; } public Widget Current { get; } }\n");
		workspace.WriteFile("project/src/Consumers/Consumer.cs", "using Services; public sealed class Consumer { public Service Value { get; } }\n");
		workspace.WriteFile("project/src/Alpha/Widget.cs", "namespace Alpha; public sealed class Widget {}\n");
		workspace.WriteFile("project/src/Beta/Widget.cs", "namespace Beta; public sealed class Widget {}\n");
		workspace.WriteFile("project/src/Relations/A.cs", """
			namespace Targets { public sealed class ResolvedType { } }
			namespace One { public sealed class SharedAB { } public sealed class SharedAC { } }
			""");
		workspace.WriteFile("project/src/Relations/B.cs", "namespace Two; public sealed class SharedAB { }\n");
		workspace.WriteFile("project/src/Relations/C.cs", "namespace Three; public sealed class SharedAC { }\n");
		workspace.WriteFile("project/src/Relations/Seed.cs", """
			using Targets;
			using One;
			using Two;
			using Three;
			public sealed class Seed
			{
				public ResolvedType Resolved { get; }
				public SharedAB FirstAmbiguous { get; }
				public SharedAC SecondAmbiguous { get; }
			}
			""");
		workspace.WriteFile("project/outside/Hidden.cs", "using Services; public sealed class Hidden { public Service Value { get; } }\n");
		workspace.WriteFile("project/README.md", "# Unsupported seed\n");
		InitializeIsolatedRepository(project);
		RunGit(project, "add", ".");
		RunGit(project, "commit", "--quiet", "-m", "fixture");
		File.AppendAllText(Path.Combine(project, "src", "Contracts", "IContract.cs"), "// staged\n");
		File.AppendAllText(Path.Combine(project, "src", "Services", "Service.cs"), "// staged\n");
		File.AppendAllText(Path.Combine(project, "src", "Consumers", "Consumer.cs"), "// staged\n");
		File.AppendAllText(Path.Combine(project, "src", "Fixture.csproj"), "<!-- staged -->\n");
		RunGit(project, "add", "src/Contracts/IContract.cs", "src/Services/Service.cs", "src/Consumers/Consumer.cs", "src/Fixture.csproj");

		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = project
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		startInfo.ArgumentList.Add("--git-mode");
		startInfo.ArgumentList.Add("none");
		startInfo.ArgumentList.Add("--exclude");
		startInfo.ArgumentList.Add("none");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MCP process did not start.");
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			TestContext.Current.CancellationToken))
		{
			var progress = new InlineProgress<ProgressNotificationValue>();
			var result = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["path"] = "src/Services/Service.cs",
					["direction"] = "both",
					["include_patterns"] = new[] { "src/**" }
				},
				progress,
				options: null,
				TestContext.Current.CancellationToken);
			var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
			Assert.NotEqual(true, result.IsError);
			Assert.Contains("src/Contracts/IContract.cs", text, StringComparison.Ordinal);
			Assert.Contains("src/Consumers/Consumer.cs", text, StringComparison.Ordinal);
			Assert.Contains("candidates: src/Alpha/Widget.cs, src/Beta/Widget.cs", text, StringComparison.Ordinal);
			Assert.DoesNotContain("outside/Hidden.cs", text, StringComparison.Ordinal);
			Assert.Contains("[Facts coverage] files=10, supported=9, unsupported=1, extraction-failed=0", text, StringComparison.Ordinal);
			Assert.Contains("[Search scope] files=10", text, StringComparison.Ordinal);
			Assert.Contains("[Effective filters]", text, StringComparison.Ordinal);
			Assert.NotEmpty(progress.Values);

			var overlappingDependencies = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["path"] = "src/Relations/Seed.cs",
					["direction"] = "dependencies",
					["include_patterns"] = new[] { "src/**" }
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var dependenciesText = Assert.IsType<TextContentBlock>(Assert.Single(overlappingDependencies.Content)).Text;
			Assert.Equal(3, CountLinesStartingWith(dependenciesText, "src/Relations/A.cs —"));
			Assert.Equal(1, CountLinesContaining(dependenciesText, "src/Relations/A.cs —", " — resolved — "));
			Assert.Equal(2, CountLinesContaining(dependenciesText, "src/Relations/A.cs —", " — ambiguous — "));

			var overlappingDependents = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["path"] = "src/Relations/A.cs",
					["direction"] = "dependents",
					["include_patterns"] = new[] { "src/**" }
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var dependentsText = Assert.IsType<TextContentBlock>(Assert.Single(overlappingDependents.Content)).Text;
			Assert.Equal(3, CountLinesStartingWith(dependentsText, "src/Relations/Seed.cs —"));
			Assert.Equal(1, CountLinesContaining(dependentsText, "src/Relations/Seed.cs —", " — resolved — "));
			Assert.Equal(2, CountLinesContaining(dependentsText, "src/Relations/Seed.cs —", " — ambiguous — "));

			var excluded = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["path"] = "src/Services/Service.cs",
					["include_patterns"] = new[] { "src/**" },
					["exclude_patterns"] = new[] { "src/Beta/**" }
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var excludedText = Assert.IsType<TextContentBlock>(Assert.Single(excluded.Content)).Text;
			Assert.Contains("src/Alpha/Widget.cs", excludedText, StringComparison.Ordinal);
			Assert.DoesNotContain("src/Beta/Widget.cs", excludedText, StringComparison.Ordinal);

			var staged = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?>
				{
					["path"] = "src/Services/Service.cs",
					["git_scope"] = "staged"
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var stagedText = Assert.IsType<TextContentBlock>(Assert.Single(staged.Content)).Text;
			Assert.Contains("[Search scope] files=4", stagedText, StringComparison.Ordinal);
			Assert.DoesNotContain("outside/Hidden.cs", stagedText, StringComparison.Ordinal);

			var unsupported = await client.CallToolAsync(
				"related_files",
				new Dictionary<string, object?> { ["path"] = "README.md" },
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var unsupportedText = Assert.IsType<TextContentBlock>(Assert.Single(unsupported.Content)).Text;
			var closingBoundary = unsupportedText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
			var noFacts = unsupportedText.IndexOf("[No facts] md is not supported", StringComparison.Ordinal);
			Assert.True(closingBoundary >= 0 && noFacts > closingBoundary, unsupportedText);
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(await errorTask), await errorTask);
	}

	private static int CountLinesStartingWith(string value, string prefix) =>
		value.Split('\n').Count(line => line.TrimEnd('\r').StartsWith(prefix, StringComparison.Ordinal));

	private static int CountLinesContaining(string value, string prefix, string fragment) =>
		value.Split('\n').Count(line =>
		{
			var normalized = line.TrimEnd('\r');
			return normalized.StartsWith(prefix, StringComparison.Ordinal) &&
			       normalized.Contains(fragment, StringComparison.Ordinal);
		});
}
