using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class ExportContextDetailForContractTests
{
	private const string BodyMarker = "unique-body-marker-for-the-verbatim-file";

	/// <summary>
	/// Ten C# files plus prose: three overridden to signatures, one to full, the rest on whatever the
	/// call asks for, under a call that compresses.
	/// </summary>
	private static void CreateFixture(TemporaryDirectory workspace)
	{
		Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
		Directory.CreateDirectory(Path.Combine(workspace.Path, "docs"));
		for (var index = 0; index < 9; index++)
		{
			workspace.WriteFile(
				$"src/file{index}.cs",
				$$"""
				// leading comment for file {{index}}
				namespace Fixture;

				public static class Sample{{index}}
				{
					public static int Compute()
					{
						var accumulated = {{index}};
						for (var step = 0; step < 10; step++)
							accumulated += step;
						return accumulated;
					}
				}
				""");
		}
		workspace.WriteFile(
			"src/verbatim.cs",
			$$"""
			namespace Fixture;

			public static class Verbatim
			{
				public static string Describe()
				{
					return "{{BodyMarker}}";
				}
			}
			""");
		workspace.WriteFile("docs/notes.md", "# Notes\n\nProse no language pack transforms.\n");
	}

	private static Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		IReadOnlyList<string> detailFor,
		bool dryRun = false,
		bool compressCode = false,
		string format = "text",
		string view = "content")
	{
		var arguments = new List<string>
		{
			"--language", "en",
			"export", "context", workspace.Path,
			"--view", view,
			"--format", format,
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-"
		};
		if (dryRun)
			arguments.Add("--dry-run");
		if (compressCode)
			arguments.Add("--compress-code");
		foreach (var value in detailFor)
		{
			arguments.Add("--detail-for");
			arguments.Add(value);
		}

		return new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
	}

	[Fact]
	public async Task AnOverrideCollapsesOnlyTheFilesItClaims()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(workspace, environment, ["src/file0.cs=signatures"]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(BodyMarker, environment.StandardOutput, StringComparison.Ordinal);
		// file0 lost its body; a file the glob did not claim kept its own.
		Assert.Contains("accumulated += step", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AGlobMayContainTheSeparatorBecauseTheValueSplitsOnTheLastOne()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		Directory.CreateDirectory(Path.Combine(workspace.Path, "gen=1"));
		workspace.WriteFile("gen=1/machine.cs", "namespace Gen;\n\npublic static class Machine\n{\n}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(workspace, environment, ["gen=1/**=signatures"], dryRun: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		// A level never contains the separator, so the tail is unambiguous and the head keeps it.
		Assert.Contains("Detail mix:", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Detail patterns matching nothing",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnOverrideToFullReallyMeansFullBecauseTheToggleIsTheCallLevel()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["src/verbatim.cs=full"],
			compressCode: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		// The command line has no detail level of its own: the three toggles are the call, so an
		// override back to full really does mean full for the file it claims.
		Assert.Contains(BodyMarker, environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheDryRunPrintsTheMixAndNamesAGlobThatClaimedNothing()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["src/**=signatures", "docs/**=full", "absent/**=compact"],
			dryRun: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		var error = environment.StandardError;
		Assert.Contains("Detail mix: full 1; compact 0; signatures 10", error, StringComparison.Ordinal);
		Assert.Contains(
			"Detail patterns matching nothing: absent/**",
			error,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ADryRunWithPerFileDetailWritesNothingAndMaterialisesNothing()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["src/**=signatures"],
			dryRun: true,
			compressCode: true);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Equal(0, diagnostics.PreparedFilesMaterialized);
		Assert.Equal(0, diagnostics.PreparedWriteBytes);
		Assert.Equal(0, diagnostics.DocumentWriteBytes);
	}

	[Fact]
	public async Task AnOrdinaryDryRunPrintsNoDetailLines()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(workspace, environment, [], dryRun: true);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("Detail mix:", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task StructuredEntriesCarryTheEffectiveDetailOfEachFile()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["src/**=signatures", "docs/**=full"],
			format: "json");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var byPath = document.RootElement.GetProperty("files").EnumerateArray()
			.ToDictionary(
				file => file.GetProperty("path").GetString()!.Replace('\\', '/'),
				file => file.GetProperty("detail").GetString());
		Assert.Equal("signatures", byPath.Single(pair => pair.Key.EndsWith("src/file0.cs", StringComparison.Ordinal)).Value);
		Assert.Equal("full", byPath.Single(pair => pair.Key.EndsWith("docs/notes.md", StringComparison.Ordinal)).Value);
	}

	[Fact]
	public async Task AnOrdinaryStructuredExportGainsNoDetailField()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(workspace, environment, [], format: "json");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.All(
			document.RootElement.GetProperty("files").EnumerateArray(),
			file => Assert.False(file.TryGetProperty("detail", out _)));
	}

	[Theory]
	[InlineData("src/**")]
	[InlineData("src/**=verbose")]
	[InlineData("=compact")]
	[InlineData("../escape=compact")]
	public async Task AnInvalidValueIsAUsageError(string value)
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(workspace, environment, [value]);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--detail-for", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PerFileDetailIsAUsageErrorForATreeOnlyExport()
	{
		using var workspace = new TemporaryDirectory();
		CreateFixture(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["src/**=signatures"],
			view: "tree");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--detail-for", environment.StandardError, StringComparison.Ordinal);
	}
}
