using System.Diagnostics;
using System.Text.Json;
using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Terminal;

public sealed class RelatedCommandProcessTests
{
	[Fact]
	public void DepthTraversesOnlyResolvedEdgesAndOnePreservesTheExistingShape()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/A.cs", "public sealed class A { public B Value { get; } }\n");
		workspace.WriteFile("project/B.cs", "public sealed class B { public C Value { get; } }\n");
		workspace.WriteFile("project/C.cs", "public sealed class C {}\n");

		var implicitDepth = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--format", "json", "--git-mode", "none", "--exclude", "none");
		var explicitDepthOne = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--depth", "1", "--format", "json",
			"--git-mode", "none", "--exclude", "none");
		var implicitTextDepth = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--format", "text", "--git-mode", "none", "--exclude", "none");
		var explicitTextDepthOne = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--depth", "1", "--format", "text",
			"--git-mode", "none", "--exclude", "none");
		var depthTwo = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--depth", "2", "--format", "json",
			"--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, implicitDepth.ExitCode);
		Assert.Equal(implicitDepth.StandardOutput, explicitDepthOne.StandardOutput);
		Assert.Equal(implicitTextDepth.StandardOutput, explicitTextDepthOne.StandardOutput);
		Assert.Equal(0, depthTwo.ExitCode);
		using var document = JsonDocument.Parse(depthTwo.StandardOutput);
		var seeds = document.RootElement.GetProperty("seeds").EnumerateArray().ToArray();
		Assert.Equal(["A.cs", "B.cs"], seeds.Select(static seed => seed.GetProperty("seed").GetString()));
		Assert.Contains(
			seeds[1].GetProperty("dependencies").EnumerateArray(),
			static dependency => dependency.GetProperty("path").GetString() == "C.cs");
	}

	[Fact]
	public void DepthUsesDeterministicBreadthFirstOrderWithoutRepeatingCyclesOrSharedTargets()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/A.cs", "public sealed class A { public B B { get; } public C C { get; } }\n");
		workspace.WriteFile("project/B.cs", "public sealed class B { public A A { get; } public D D { get; } }\n");
		workspace.WriteFile("project/C.cs", "public sealed class C { public D D { get; } }\n");
		workspace.WriteFile("project/D.cs", "public sealed class D { public A A { get; } }\n");

		var first = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--depth", "10", "--format", "json",
			"--git-mode", "none", "--exclude", "none");
		var second = Run(workspace, "related", "A.cs", "--project", project,
			"--direction", "dependencies", "--depth", "10", "--format", "json",
			"--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, first.ExitCode);
		Assert.Equal(first.StandardOutput, second.StandardOutput);
		using var document = JsonDocument.Parse(first.StandardOutput);
		var seeds = document.RootElement.GetProperty("seeds").EnumerateArray().ToArray();
		Assert.Equal(
			["A.cs", "B.cs", "C.cs", "D.cs"],
			seeds.Select(static seed => seed.GetProperty("seed").GetString()));
	}

	[Fact]
	public void DepthDoesNotTraverseUnresolvedOrExternalEvidence()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("project/package.json", "{\"name\":\"sample\",\"dependencies\":{\"react\":\"1.0.0\"}}\n");
		workspace.WriteFile("project/main.ts", "import React from 'react';\nimport Missing from 'not-declared';\nvoid React;\nvoid Missing;\n");

		var result = Run(workspace, "related", "main.ts", "--project", project,
			"--direction", "dependencies", "--depth", "10", "--format", "json",
			"--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var seed = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray());
		Assert.Equal("main.ts", seed.GetProperty("seed").GetString());
		var resolution = document.RootElement.GetProperty("resolution");
		Assert.True(resolution.GetProperty("unresolved").GetInt32() > 0);
		Assert.True(resolution.GetProperty("external").GetInt32() > 0);
	}

	[Fact]
	public void DepthKeepsResolvedPythonNamespaceEvidenceWithoutTreatingItAsAFileSeed()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/pyproject.toml", "[project]\nname = \"fixture\"\n");
		workspace.WriteFile("project/consumer.py", "import ns\n");
		workspace.WriteFile("project/ns/portion.py", "value = 1\n");

		var result = Run(workspace, "related", "consumer.py", "--project", project,
			"--direction", "dependencies", "--depth", "2", "--format", "json",
			"--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var seed = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray());
		Assert.Equal("consumer.py", seed.GetProperty("seed").GetString());
		var dependency = Assert.Single(seed.GetProperty("dependencies").EnumerateArray());
		Assert.Equal("namespace:ns", dependency.GetProperty("path").GetString());
		Assert.Equal("resolved", dependency.GetProperty("status").GetString());
		Assert.Contains(
			dependency.GetProperty("reasons").EnumerateArray(),
			static reason => reason.GetString() == "one namespace-package entity");
	}

	[Fact]
	public void DepthFailsHonestlyBeforeRenderingWhenTraversalExceedsTheSeedLimit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		var source = new StringBuilder("public sealed class Seed {\n");
		for (var index = 0; index < DependencyFactsEngine.MaximumRelatedTraversalSeeds; index++)
		{
			var type = $"Target{index:D3}";
			source.Append("public ").Append(type).Append(' ').Append(type).Append("Value { get; }\n");
			workspace.WriteFile($"project/{type}.cs", $"public sealed class {type} {{}}\n");
		}
		source.Append("}\n");
		workspace.WriteFile("project/Seed.cs", source.ToString());

		var result = Run(workspace, "related", "Seed.cs", "--project", project,
			"--direction", "dependencies", "--depth", "2", "--format", "json",
			"--git-mode", "none", "--exclude", "none");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains(DependencyTraversalLimitException.ErrorCode, result.StandardError, StringComparison.Ordinal);
		Assert.Contains(
			DependencyFactsEngine.MaximumRelatedTraversalSeeds.ToString(),
			result.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public void DepthDoesNotTraverseAmbiguousCandidates()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/lib.sh", "root_value=1\n");
		workspace.WriteFile("project/scripts/lib.sh", "script_value=1\n");
		workspace.WriteFile("project/scripts/main.sh", "source ./lib.sh\n");

		var result = Run(
			workspace,
			"related", "scripts/main.sh",
			"--project", project,
			"--direction", "dependencies",
			"--depth", "2",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		using var document = JsonDocument.Parse(result.StandardOutput);
		var seed = Assert.Single(document.RootElement.GetProperty("seeds").EnumerateArray());
		Assert.Equal("scripts/main.sh", seed.GetProperty("seed").GetString());
		var dependency = Assert.Single(seed.GetProperty("dependencies").EnumerateArray());
		Assert.Equal("ambiguous", dependency.GetProperty("status").GetString());
	}

	[Theory]
	[InlineData("0")]
	[InlineData("11")]
	public void DepthOutsideThePublishedRangeIsRejected(string depth)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/main.cs", "public sealed class Main {}\n");

		var result = Run(
			workspace,
			"related", "main.cs",
			"--project", project,
			"--depth", depth,
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Contains("--depth must be between 1 and 10", result.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void ConditionalParameterProjectionReportsOnlyTheOmittedSourceLines()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Target.cs", "public class Target {}\n");
		workspace.WriteFile("project/Consumer.cs",
			"public class Consumer {\npublic void Run(\n#if FEATURE\nInside first,\n#else\nInside second,\n#endif\nTarget last) {}\n}\n");
		var text = Run(workspace, "related", "Consumer.cs", "--project", project,
			"--format", "text", "--git-mode", "none", "--exclude", "none");
		var json = Run(workspace, "related", "Consumer.cs", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, text.ExitCode);
		Assert.Equal(0, json.ExitCode);
		Assert.Contains("Target.cs", text.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("[Dependency partial parse] path=Consumer.cs · dropped=1 · lines=3-7", text.StandardOutput, StringComparison.Ordinal);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var diagnostic = Assert.Single(document.RootElement.GetProperty("coverage")
			.GetProperty("partialParseDiagnostics").EnumerateArray());
		Assert.Equal("Consumer.cs", diagnostic.GetProperty("path").GetString());
		Assert.Equal(1, diagnostic.GetProperty("droppedConstructs").GetInt32());
		var range = Assert.Single(diagnostic.GetProperty("ranges").EnumerateArray());
		Assert.Equal(3, range.GetProperty("startLine").GetInt32());
		Assert.Equal(7, range.GetProperty("endLine").GetInt32());
	}

	[Fact]
	public void UnresolvedEvidenceIsExplicitInTextAndJsonOutput()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/package.json", "{\"name\":\"sample\"}\n");
		workspace.WriteFile("project/index.js", "module.exports = require('./lib/express');\n");
		workspace.WriteFile("project/lib/express.js", "module.exports = {};\n");

		var text = Run(workspace, "related", "index.js", "--project", project, "--format", "text",
			"--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, text.ExitCode);
		Assert.Contains("[Resolution] resolved=0 · ambiguous=0 · unresolved=1 · external=0", text.StandardOutput,
			StringComparison.Ordinal);
		var json = Run(workspace, "related", "index.js", "--project", project, "--format", "json",
			"--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, json.ExitCode);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var resolution = document.RootElement.GetProperty("resolution");
		Assert.Equal(0, resolution.GetProperty("resolved").GetInt32());
		Assert.Equal(1, resolution.GetProperty("unresolved").GetInt32());
	}

	[Fact]
	public void RealPublishedCommandReportsCHeaderDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/include/model.h", "typedef struct Model { int value; } Model;\n");
		workspace.WriteFile("project/src/app.c", "#include \"../include/model.h\"\nModel read_model(void);\n");
		var result = Run(workspace, "related", "src/app.c", "--project", project, "--direction", "dependencies",
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("include/model.h", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandReportsCppHeaderDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/include/model.hpp", "namespace Models { class Model {}; }\n");
		workspace.WriteFile("project/src/app.cpp", "#include \"../include/model.hpp\"\nModels::Model read_model();\n");
		var result = Run(workspace, "related", "src/app.cpp", "--project", project, "--direction", "dependencies",
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("include/model.hpp", result.StandardOutput, StringComparison.Ordinal);
	}

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
	public void RealPublishedCommandReportsKotlinManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/library/Remote.kt", "package library\nclass Remote");
		workspace.WriteFile(
			"project/app/Consumer.kt",
			"package app\nimport library.Remote\nclass Consumer(val value: Remote)");

		var result = Run(
			workspace,
			"related", "app/Consumer.kt",
			"--project", project,
			"--direction", "dependencies",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("library/Remote.kt", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\"status\": \"unresolved\"", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandReportsRubyManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/lib/model.rb", "module Models\n class User\n end\nend\n");
		workspace.WriteFile("project/lib/service.rb", "require_relative 'model'\nclass Service\n VALUE = Models::User\nend\n");

		var result = Run(
			workspace, "related", "lib/service.rb", "--project", project,
			"--direction", "dependencies", "--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("lib/model.rb", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\"status\": \"unresolved\"", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandDoesNotExpandReopenedRubyContainersToEveryFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/lib/sinatra/indifferent_hash.rb", "module Sinatra\n class IndifferentHash\n end\nend\n");
		workspace.WriteFile("project/lib/sinatra/base.rb", "module Sinatra\n class Base\n end\nend\n");
		workspace.WriteFile("project/test/consumer.rb", "class Consumer\n include Sinatra\n VALUE = Sinatra::Base\nend\n");

		var result = Run(workspace, "related", "test/consumer.rb", "--project", project,
			"--direction", "dependencies", "--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("lib/sinatra/base.rb", result.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("lib/sinatra/indifferent_hash.rb", result.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void RealPublishedCommandReportsPhpManifestDependencies()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Remote.php", "<?php namespace Library; class Remote {}");
		workspace.WriteFile("project/src/App.php", "<?php namespace App; use Library\\Remote; class App { private Remote $value; }");
		var result = Run(workspace, "related", "src/App.php", "--project", project, "--direction", "dependencies",
			"--format", "json", "--git-mode", "none", "--exclude", "none");
		Assert.Equal(0, result.ExitCode);
		Assert.Contains("src/Remote.php", result.StandardOutput, StringComparison.Ordinal);
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
		Assert.Contains("\"unresolved\": 0", result.StandardOutput, StringComparison.Ordinal);
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
	public void PartiallyParsedSourceReportsDroppedConstructionLinesInTextAndJson()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
		workspace.WriteFile("project/Target.cs", "public sealed class Target { }\n");
		workspace.WriteFile("project/Consumer.cs", """
			public sealed class Consumer { Target value; }
			public static class Broken
			{
				public static void Run(
				{
					Missing value;
				}
			}
			""");

		var text = Run(workspace, "related", "Consumer.cs", "--project", project,
			"--format", "text", "--git-mode", "none", "--exclude", "none");
		var json = Run(workspace, "related", "Consumer.cs", "--project", project,
			"--format", "json", "--git-mode", "none", "--exclude", "none");

		Assert.Equal(0, text.ExitCode);
		Assert.Equal(0, json.ExitCode);
		using var document = JsonDocument.Parse(json.StandardOutput);
		var diagnostic = Assert.Single(document.RootElement.GetProperty("coverage")
			.GetProperty("partialParseDiagnostics").EnumerateArray());
		Assert.Equal("Consumer.cs", diagnostic.GetProperty("path").GetString());
		Assert.Equal(1, diagnostic.GetProperty("droppedConstructs").GetInt32());
		var range = Assert.Single(diagnostic.GetProperty("ranges").EnumerateArray());
		var startLine = range.GetProperty("startLine").GetInt32();
		var endLine = range.GetProperty("endLine").GetInt32();
		Assert.True(startLine <= 5 && endLine >= 5);
		var renderedRange = startLine == endLine ? startLine.ToString() : $"{startLine}-{endLine}";
		Assert.Contains($"[Dependency partial parse] path=Consumer.cs · dropped=1 · lines={renderedRange}",
			text.StandardOutput, StringComparison.Ordinal);
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
