using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class RelatedCommandProcessTests
{
	[Fact]
	public void RealPublishedCommandReportsJavaManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/library/Remote.java", "package library; public class Remote { }");
		workspace.WriteFile(
			"project/app/Consumer.java",
			"package app; import library.Remote; public class Consumer { Remote value; }");

		var result = Run(
			workspace,
			"related", "app/Consumer.java",
			"--project", project,
			"--direction", "dependencies",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("library/Remote.java", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\"status\": \"unresolved\"", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandReportsRustManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/lib.rs", "mod model; mod service;");
		workspace.WriteFile("project/src/model.rs", "pub struct User;");
		workspace.WriteFile("project/src/service.rs", "use crate::model::User; pub struct Service(User);");

		var result = Run(
			workspace,
			"related", "src/service.rs",
			"--project", project,
			"--direction", "dependencies",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("src/model.rs", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\"status\": \"unresolved\"", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandUsesPythonDottedImportBindingAndPackageBoundaries()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/pyproject.toml", "[project]\nname = \"fixture\"\n");
		workspace.WriteFile("project/pkg/__init__.py", string.Empty);
		workspace.WriteFile("project/pkg/sub.py", "class Item: pass\n");
		workspace.WriteFile("project/facade.py", "import pkg.sub\n");
		workspace.WriteFile("project/consumer.py", "from facade import pkg\n");

		var result = Run(
			workspace,
			"related", "consumer.py",
			"--project", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
		Assert.Contains("pkg/sub.py", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("unresolved", result.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("json")]
	public void RealPublishedCommandReportsDependenciesAndDependents(string format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Contracts/IClock.cs", "namespace Contracts; public interface IClock {}\n");
		workspace.WriteFile("project/Services/ClockService.cs", "using Contracts; namespace Services; public sealed class ClockService { public IClock Clock { get; } }\n");
		workspace.WriteFile("project/Consumers/Worker.cs", "using Services; public sealed class Worker { public ClockService Service { get; } }\n");
		workspace.WriteFile("project/Outside.cs", "public sealed class Outside {}\n");
		WriteOverlappingRelationsFixture(workspace, "project/Relations");

		var result = Run(
			workspace,
			"related", "Services/ClockService.cs",
			"--project", project,
			"--direction", "both",
			"--format", format,
			"--select", "Contracts",
			"--select", "Services",
			"--select", "Consumers",
			"--select", "Fixture.csproj",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
		Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
		Assert.DoesNotContain("Outside.cs", result.StandardOutput, StringComparison.Ordinal);
		if (format == "text")
		{
			Assert.Contains("Dependencies", result.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("Contracts/IClock.cs", result.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("Dependents", result.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("Consumers/Worker.cs", result.StandardOutput, StringComparison.Ordinal);
			return;
		}

		using var document = JsonDocument.Parse(result.StandardOutput);
		var root = document.RootElement;
		Assert.Equal("devprojex-related-files", root.GetProperty("kind").GetString());
		var seed = Assert.Single(root.GetProperty("seeds").EnumerateArray());
		Assert.Equal("Contracts/IClock.cs", Assert.Single(seed.GetProperty("dependencies").EnumerateArray()).GetProperty("path").GetString());
		Assert.Equal("Consumers/Worker.cs", Assert.Single(seed.GetProperty("dependents").EnumerateArray()).GetProperty("path").GetString());

		var dependencies = RunRelatedJson(workspace, project, "Relations/Seed.cs", "dependencies");
		AssertRelatedOverlap(dependencies.GetProperty("dependencies"), "Relations/A.cs");
		var dependents = RunRelatedJson(workspace, project, "Relations/A.cs", "dependents");
		AssertRelatedOverlap(dependents.GetProperty("dependents"), "Relations/Seed.cs");
	}

	[Fact]
	public void UnsupportedSeedIsASuccessWithLocalizedDiagnosticAndEmptyRelations()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/README.md", "# Fixture\n");

		var result = Run(
			workspace,
			"related", "README.md",
			"--project", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("warning[DPX-DEPENDENCY-UNSUPPORTED]", result.StandardError, StringComparison.Ordinal);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var seed = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray());
		Assert.Empty(seed.GetProperty("dependencies").EnumerateArray());
		Assert.Empty(seed.GetProperty("dependents").EnumerateArray());
		Assert.NotEqual(JsonValueKind.Null, seed.GetProperty("noFactsReason").ValueKind);
	}

	[Fact]
	public void ConfigurationDiagnosticsAreSafeAndEquivalentInTextAndJson()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/tsconfig.json", "{\"compilerOptions\":{");
		workspace.WriteFile("project/main.ts", "import value from './target.js';");
		workspace.WriteFile("project/target.ts", "export default 1;");

		var text = Run(
			workspace,
			"related", "main.ts",
			"--project", project,
			"--format", "text",
			"--git-mode", "none",
			"--exclude", "none");
		var json = Run(
			workspace,
			"related", "main.ts",
			"--project", project,
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, text.ExitCode);
		Assert.Contains(
			"[Dependency configuration] affected-scopes=1 · problem=corrupt · path=tsconfig.json",
			text.StandardOutput,
			StringComparison.Ordinal);
		Assert.DoesNotContain("LineNumber", text.StandardOutput, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(0, json.ExitCode);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var diagnostic = Assert.Single(document.RootElement.GetProperty("coverage")
			.GetProperty("configurationDiagnostics").EnumerateArray());
		Assert.Equal("tsconfig.json", diagnostic.GetProperty("path").GetString());
		Assert.Equal("corrupt", diagnostic.GetProperty("problem").GetString());
		Assert.Equal(1, diagnostic.GetProperty("affectedScopes").GetInt32());
		Assert.False(diagnostic.TryGetProperty("reason", out _));
		Assert.False(diagnostic.TryGetProperty("scopeIds", out _));

		workspace.WriteFile(
			"project/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var valid = Run(
			workspace,
			"related", "main.ts",
			"--project", project,
			"--format", "text",
			"--git-mode", "none",
			"--exclude", "none");
		Assert.Equal(0, valid.ExitCode);
		Assert.DoesNotContain("[Dependency configuration]", valid.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandPreservesParsedImportAndCSharpOccurrenceSemantics()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Models.cs", "namespace Models; public sealed class User { }\n");
		workspace.WriteFile("project/Consumers.cs",
			"using Models; class Box<User> { User a; } class Consumer { User b; }\n");
		workspace.WriteFile("project/Holder.cs",
			"namespace Company; public static class Holder { public sealed class Nested { } }\n");
		workspace.WriteFile("project/StaticConsumer.cs",
			"using static Company.Holder; public sealed class StaticConsumer { Nested Value; }\n");
		workspace.WriteFile("project/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("project/package.json", "{\"imports\":{\"#dual\":{\"import\":\"./import.mts\",\"require\":\"./require.cts\"}}}\n");
		workspace.WriteFile("project/register.ts", "export const ready = true;\n");
		workspace.WriteFile("project/View.tsx", "export default function View() { return null; }\n");
		workspace.WriteFile("project/main.ts", "import \"./register.js\"; import View from './View.jsx';\n");
		workspace.WriteFile("project/import.mts", "export const value = 1;\n");
		workspace.WriteFile("project/require.cts", "export const value = 2;\n");
		workspace.WriteFile("project/dual.cts", "import('#dual'); require('#dual');\n");
		workspace.WriteFile("project/pyproject.toml", "[project]\nname = \"fixture\"\n");
		workspace.WriteFile("project/impl.py", "class Item: pass\n");
		workspace.WriteFile("project/model.py", "class Container:\n    def nested(self): pass\n\nclass Item: pass\n");
		workspace.WriteFile("project/python_consumer.py", "from model import (\n    Item,\n)\n");
		workspace.WriteFile("project/local_model.py", "def loader():\n    from impl import Item as LocalItem\n");
		workspace.WriteFile("project/local_consumer.py", "from local_model import LocalItem\n");

		var csharp = Run(workspace, "related", "Consumers.cs", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var staticUsing = Run(workspace, "related", "StaticConsumer.cs", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var typeScript = Run(workspace, "related", "main.ts", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var conditional = Run(workspace, "related", "dual.cts", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var python = Run(workspace, "related", "python_consumer.py", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var localPython = Run(workspace, "related", "local_consumer.py", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		var localModel = Run(workspace, "related", "local_model.py", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, csharp.ExitCode);
		using (var document = JsonDocument.Parse(csharp.StandardOutput))
		{
			var dependencies = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray())
				.GetProperty("dependencies").EnumerateArray().ToArray();
			Assert.Contains(dependencies, item => item.GetProperty("path").GetString() == "Models.cs");
		}
		Assert.Equal(0, staticUsing.ExitCode);
		Assert.Contains("Holder.cs", staticUsing.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, typeScript.ExitCode);
		Assert.Contains("register.ts", typeScript.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("View.tsx", typeScript.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("import ./register.js at line 1", typeScript.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, conditional.ExitCode);
		Assert.Contains("import.mts", conditional.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("require.cts", conditional.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, python.ExitCode);
		Assert.Contains("model.py", python.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("nested", python.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(0, localPython.ExitCode);
		using (var localDocument = JsonDocument.Parse(localPython.StandardOutput))
		{
			var localSeed = Assert.Single(localDocument.RootElement.GetProperty("seeds").EnumerateArray());
			Assert.Empty(localSeed.GetProperty("dependencies").EnumerateArray());
		}
		Assert.Equal(0, localModel.ExitCode);
		Assert.Contains("impl.py", localModel.StandardOutput, StringComparison.Ordinal);
	}

	private static TerminalTestProcessResult Run(TemporaryDirectory workspace, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.ArgumentList.Add("--language");
		startInfo.ArgumentList.Add("en");
		startInfo.ArgumentList.Add("--plain");
		startInfo.ArgumentList.Add("--progress");
		startInfo.ArgumentList.Add("never");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");
		return TerminalTestProcess.Run(startInfo, TimeSpan.FromMinutes(1));
	}

	private static JsonElement RunRelatedJson(
		TemporaryDirectory workspace,
		string project,
		string seed,
		string direction)
	{
		var result = Run(
			workspace,
			"related", seed,
			"--project", project,
			"--direction", direction,
			"--format", "json",
			"--select", "Relations",
			"--select", "Fixture.csproj",
			"--git-mode", "none",
			"--exclude", "none");
		Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
		using var document = JsonDocument.Parse(result.StandardOutput);
		return Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray()).Clone();
	}

	private static void AssertRelatedOverlap(JsonElement relatedElement, string expectedPath)
	{
		var related = relatedElement.EnumerateArray().ToArray();
		Assert.Equal(3, related.Length);
		Assert.All(related, item => Assert.Equal(expectedPath, item.GetProperty("path").GetString()));
		Assert.Single(related, item => item.GetProperty("status").GetString() == "resolved");
		var ambiguous = related.Where(item => item.GetProperty("status").GetString() == "ambiguous").ToArray();
		Assert.Equal(2, ambiguous.Length);
		Assert.Contains(ambiguous, item => item.GetProperty("candidates").EnumerateArray()
			.Select(static candidate => candidate.GetString()).SequenceEqual(["Relations/A.cs", "Relations/B.cs"]));
		Assert.Contains(ambiguous, item => item.GetProperty("candidates").EnumerateArray()
			.Select(static candidate => candidate.GetString()).SequenceEqual(["Relations/A.cs", "Relations/C.cs"]));
	}

	private static void WriteOverlappingRelationsFixture(TemporaryDirectory workspace, string directory)
	{
		workspace.WriteFile(directory + "/A.cs", """
			namespace Targets { public sealed class ResolvedType { } }
			namespace One { public sealed class SharedAB { } public sealed class SharedAC { } }
			""");
		workspace.WriteFile(directory + "/B.cs", "namespace Two; public sealed class SharedAB { }\n");
		workspace.WriteFile(directory + "/C.cs", "namespace Three; public sealed class SharedAC { }\n");
		workspace.WriteFile(directory + "/Seed.cs", """
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
	}
}
