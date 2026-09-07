using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class RelatedCommandProcessTests
{
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
}
