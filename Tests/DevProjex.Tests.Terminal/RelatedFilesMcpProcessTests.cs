using System.Diagnostics;
using DevProjex.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessRelatedFilesReportsJavaManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("java-project");
		workspace.WriteFile("java-project/library/Remote.java", "package library; public class Remote { }");
		workspace.WriteFile(
			"java-project/app/Consumer.java",
			"package app; import library.Remote; public class Consumer { Remote value; }");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var result = await CallAsync(
			server,
			"related_files",
			new Dictionary<string, object?>
			{
				["path"] = "app/Consumer.java",
				["direction"] = "dependencies"
			});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

		Assert.Contains("library/Remote.java", text, StringComparison.Ordinal);
		Assert.DoesNotContain(" — unresolved — ", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesReportsRustManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("rust-project");
		workspace.WriteFile("rust-project/src/lib.rs", "mod model; mod service;");
		workspace.WriteFile("rust-project/src/model.rs", "pub struct User;");
		workspace.WriteFile("rust-project/src/service.rs", "use crate::model::User; pub struct Service(User);");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var result = await CallAsync(
			server,
			"related_files",
			new Dictionary<string, object?>
			{
				["path"] = "src/service.rs",
				["direction"] = "dependencies"
			});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

		Assert.Contains("src/model.rs", text, StringComparison.Ordinal);
		Assert.DoesNotContain(" — unresolved — ", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesReportsKotlinManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("kotlin-project");
		workspace.WriteFile("kotlin-project/library/Remote.kt", "package library\nclass Remote");
		workspace.WriteFile(
			"kotlin-project/app/Consumer.kt",
			"package app\nimport library.Remote\nclass Consumer(val value: Remote)");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var result = await CallAsync(
			server,
			"related_files",
			new Dictionary<string, object?>
			{
				["path"] = "app/Consumer.kt",
				["direction"] = "dependencies"
			});
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

		Assert.Contains("library/Remote.kt", text, StringComparison.Ordinal);
		Assert.DoesNotContain(" — unresolved — ", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessRelatedFilesPreservesParsedImportSemantics()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("project/package.json", "{\"imports\":{\"#dual\":{\"import\":\"./import.mts\",\"require\":\"./require.cts\"}}}\n");
		workspace.WriteFile("project/register.ts", "export const ready = true;\n");
		workspace.WriteFile("project/View.tsx", "export default function View() { return null; }\n");
		workspace.WriteFile("project/main.ts", "import \"./register.js\"; import View from './View.jsx';\n");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Holder.cs",
			"namespace Company; public static class Holder { public sealed class Nested { } }\n");
		workspace.WriteFile("project/StaticConsumer.cs",
			"using static Company.Holder; public sealed class StaticConsumer { Nested Value; }\n");
		workspace.WriteFile("project/import.mts", "export const value = 1;\n");
		workspace.WriteFile("project/require.cts", "export const value = 2;\n");
		workspace.WriteFile("project/dual.cts", "import('#dual'); require('#dual');\n");
		workspace.WriteFile("project/pyproject.toml", "[project]\nname = \"fixture\"\n");
		workspace.WriteFile("project/impl.py", "class Item: pass\n");
		workspace.WriteFile("project/model.py", "class Container:\n    def nested(self): pass\n\nclass Item: pass\n");
		workspace.WriteFile("project/consumer.py", "from model import (\n    Item,\n)\n");
		workspace.WriteFile("project/local_model.py", "def loader():\n    from impl import Item as LocalItem\n");
		workspace.WriteFile("project/local_consumer.py", "from local_model import LocalItem\n");
		workspace.WriteFile("project/pkg/__init__.py", string.Empty);
		workspace.WriteFile("project/pkg/sub.py", "class Item: pass\n");
		workspace.WriteFile("project/facade.py", "import pkg.sub\n");
		workspace.WriteFile("project/dotted_consumer.py", "from facade import pkg\n");
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
			clientOptions: null, loggerFactory: null, TestContext.Current.CancellationToken))
		{
			var typeScript = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "main.ts", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var staticUsing = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "StaticConsumer.cs", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var python = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "consumer.py", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var conditional = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "dual.cts", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var localPython = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "local_consumer.py", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var localModel = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "local_model.py", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var dottedPython = await client.CallToolAsync("related_files",
				new Dictionary<string, object?> { ["path"] = "dotted_consumer.py", ["direction"] = "dependencies" },
				progress: null, options: null, TestContext.Current.CancellationToken);
			var typeScriptText = Assert.IsType<TextContentBlock>(Assert.Single(typeScript.Content)).Text;
			Assert.Contains("register.ts", typeScriptText, StringComparison.Ordinal);
			Assert.Contains("View.tsx", typeScriptText, StringComparison.Ordinal);
			Assert.Contains("import ./register.js at line 1", typeScriptText, StringComparison.Ordinal);
			Assert.Contains("Holder.cs",
				Assert.IsType<TextContentBlock>(Assert.Single(staticUsing.Content)).Text,
				StringComparison.Ordinal);
			var conditionalText = Assert.IsType<TextContentBlock>(Assert.Single(conditional.Content)).Text;
			Assert.Contains("import.mts", conditionalText, StringComparison.Ordinal);
			Assert.Contains("require.cts", conditionalText, StringComparison.Ordinal);
			var pythonText = Assert.IsType<TextContentBlock>(Assert.Single(python.Content)).Text;
			Assert.Contains("model.py", pythonText, StringComparison.Ordinal);
			Assert.DoesNotContain("nested", pythonText, StringComparison.Ordinal);
			var localPythonText = Assert.IsType<TextContentBlock>(Assert.Single(localPython.Content)).Text;
			Assert.DoesNotContain("impl.py", localPythonText, StringComparison.Ordinal);
			Assert.Contains("[No related files]", localPythonText, StringComparison.Ordinal);
			Assert.Contains("impl.py", Assert.IsType<TextContentBlock>(Assert.Single(localModel.Content)).Text,
				StringComparison.Ordinal);
			var dottedPythonText = Assert.IsType<TextContentBlock>(Assert.Single(dottedPython.Content)).Text;
			Assert.Contains("pkg/sub.py", dottedPythonText, StringComparison.Ordinal);
			Assert.DoesNotContain(" — unresolved — ", dottedPythonText, StringComparison.Ordinal);
		}
		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(await errorTask), await errorTask);
	}

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
			await progress.WaitForValueAsync(TestContext.Current.CancellationToken);
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
			// Dependency diagnostics intentionally use constant trusted reasons so project-controlled
			// extensions cannot be echoed outside the untrusted-data boundary.
			var noFacts = unsupportedText.IndexOf(
				"[No facts] file language is not supported by the dependency engine yet",
				StringComparison.Ordinal);
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
