namespace DevProjex.Tests.Terminal;

public sealed class ProfileCommandContractTests
{
	[Fact]
	public async Task StandardProfileIsDeterministicAndDoesNotReadLocalState()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "show", workspace.Path, "--profile", "standard", "--format", "json");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-profile", document.RootElement.GetProperty("kind").GetString());
		var selection = document.RootElement.GetProperty("selection");
		Assert.Equal("gitignore", selection.GetProperty("gitMode").GetString());
		Assert.Contains(
			selection.GetProperty("exclusions").EnumerateArray(),
			static value => value.GetString() == "smart-ignore");
		Assert.Equal(JsonValueKind.Null, selection.GetProperty("roots").ValueKind);
		Assert.Equal(JsonValueKind.Null, selection.GetProperty("extensions").ValueKind);
	}

	[Fact]
	public async Task TextProfileUsesCanonicalPublicSelectionTokens()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "show", workspace.Path, "--profile", "standard", "--format", "text");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("gitignore", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("smart-ignore", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("hidden-folders", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("RespectGitIgnore", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("SmartIgnore", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("HiddenFolders", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task PortableProfileAllowsUnknownAdditiveFields()
	{
		using var workspace = CreateWorkspace();
		var profile = WriteProfile(
			workspace,
			"""
			{
			  "schemaVersion": 1,
			  "futureDocumentField": { "enabled": true },
			  "selection": {
			    "roots": null,
			    "extensions": null,
			    "selectedPaths": [],
			    "gitMode": "none",
			    "exclusions": ["hidden-files"],
			    "futureSelectionField": "preserved-by-newer-readers"
			  }
			}
			""");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "validate", profile);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal("valid" + Environment.NewLine, environment.StandardOutput);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task PortableProfileValuesAreAppliedToAnalyze()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile("src/other.txt", "other");
		var profile = WriteProfile(
			workspace,
			"""
			{
			  "schemaVersion": 1,
			  "selection": {
			    "roots": null,
			    "extensions": [".cs"],
			    "selectedPaths": ["src"],
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path, "--profile", profile, "--format", "json");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var selection = document.RootElement.GetProperty("selection");
		Assert.Equal("none", selection.GetProperty("gitMode").GetString());
		Assert.Empty(selection.GetProperty("exclusions").EnumerateArray());
		Assert.Equal([".cs"], selection.GetProperty("extensions")
			.EnumerateArray().Select(static value => value.GetString()));
		Assert.Equal(["src"], selection.GetProperty("selectedPaths")
			.EnumerateArray().Select(static value => value.GetString()));
		Assert.Equal(1, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
	}

	[Fact]
	public async Task ExplicitOptionsOverridePortableProfileAsExactSets()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile("src/other.txt", "other");
		var profile = WriteProfile(
			workspace,
			"""
			{
			  "schemaVersion": 1,
			  "selection": {
			    "roots": null,
			    "extensions": [".cs"],
			    "selectedPaths": [],
			    "gitMode": "gitignore",
			    "exclusions": ["smart-ignore", "hidden-files"]
			  }
			}
			""");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path,
			"--profile", profile,
			"--git-mode", "none",
			"--exclude", "none",
			"--extension", ".txt",
			"--format", "json");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var selection = document.RootElement.GetProperty("selection");
		Assert.Equal("none", selection.GetProperty("gitMode").GetString());
		Assert.Empty(selection.GetProperty("exclusions").EnumerateArray());
		Assert.Equal([".txt"], selection.GetProperty("extensions")
			.EnumerateArray().Select(static value => value.GetString()));
		Assert.Equal(1, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
	}

	[Fact]
	public async Task ImportApplyCreatesLocalProfileAndResetIsIdempotent()
	{
		using var workspace = CreateWorkspace();
		var profile = WriteProfile(
			workspace,
			"""
			{
			  "schemaVersion": 1,
			  "selection": {
			    "roots": null,
			    "extensions": [".cs"],
			    "selectedPaths": [],
			    "gitMode": "none",
			    "exclusions": ["empty-files"]
			  }
			}
			""");
		var importEnvironment = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				importEnvironment,
				"profile", "import", profile, workspace.Path, "--apply"));

		var showEnvironment = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				showEnvironment,
				"profile", "show", workspace.Path, "--profile", "local", "--format", "json"));
		using (var document = JsonDocument.Parse(showEnvironment.StandardOutput))
		{
			var selection = document.RootElement.GetProperty("selection");
			Assert.Equal("none", selection.GetProperty("gitMode").GetString());
			Assert.Equal(["empty-files"], selection.GetProperty("exclusions")
				.EnumerateArray().Select(static value => value.GetString()));
		}

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				new TestTerminalEnvironment(),
				"profile", "reset", workspace.Path));
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				new TestTerminalEnvironment(),
				"profile", "reset", workspace.Path));

		var missingEnvironment = new TestTerminalEnvironment();
		Assert.Equal(
			CommandLineExitCodes.UsageError,
			await RunAsync(
				workspace,
				missingEnvironment,
				"profile", "show", workspace.Path, "--profile", "local", "--format", "json"));
		Assert.Contains("DPX-CLI-PROFILE-NOT-FOUND", missingEnvironment.StandardError, StringComparison.Ordinal);
		Assert.Contains(
			"The selected profile could not be resolved.",
			missingEnvironment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"The local project profile was not found",
			missingEnvironment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportProfileUsesAtomicConflictPolicy()
	{
		using var workspace = CreateWorkspace();
		var destination = workspace.WriteFile("profiles/portable.json", "unchanged");
		var conflictEnvironment = new TestTerminalEnvironment();

		var conflict = await RunAsync(
			workspace,
			conflictEnvironment,
			"profile", "export", workspace.Path,
			"--profile", "standard",
			"-o", destination);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, conflict);
		Assert.Equal("unchanged", File.ReadAllText(destination));
		Assert.Contains("DPX-PROFILE-DESTINATION-EXISTS", conflictEnvironment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--force", conflictEnvironment.StandardError, StringComparison.Ordinal);

		var forceEnvironment = new TestTerminalEnvironment();
		var success = await RunAsync(
			workspace,
			forceEnvironment,
			"profile", "export", workspace.Path,
			"--profile", "standard",
			"-o", destination,
			"--force");
		Assert.Equal(CommandLineExitCodes.Success, success);
		using var document = JsonDocument.Parse(File.ReadAllText(destination));
		Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal("devprojex-profile", document.RootElement.GetProperty("kind").GetString());
		Assert.Equal(Path.GetFullPath(destination) + Environment.NewLine, forceEnvironment.StandardOutput);
	}

	[Fact]
	public async Task PortableProfileRejectsAConflictingDocumentKind()
	{
		using var workspace = CreateWorkspace();
		var profile = WriteProfile(
			workspace,
			"""
			{
			  "schemaVersion": 1,
			  "kind": "different-product-document",
			  "selection": {
			    "roots": null,
			    "extensions": null,
			    "selectedPaths": [],
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "validate", profile);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-PROFILE-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public async Task InvalidProfileDoesNotExposeJsonParserMessage()
	{
		using var workspace = CreateWorkspace();
		var profile = workspace.WriteFile("broken.json", "{ definitely not JSON");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "show", workspace.Path, "--profile", profile, "--format", "json");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-PROFILE-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("definitely", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("JsonReaderException", environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData("../outside")]
	[InlineData("src/../../outside")]
	[InlineData("/absolute/path")]
	[InlineData("C:\\absolute\\path")]
	[InlineData("\\\\server\\share\\path")]
	public async Task ProfileValidationRejectsPathsUnsafeOnAnySupportedPlatform(
		string selectedPath)
	{
		using var workspace = CreateWorkspace();
		var json = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			selection = new
			{
				roots = (string[]?)null,
				extensions = (string[]?)null,
				selectedPaths = new[] { selectedPath },
				gitMode = "none",
				exclusions = Array.Empty<string>()
			}
		});
		var profile = WriteProfile(workspace, json);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"profile", "validate", profile);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-PROFILE-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(selectedPath, environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	private static TemporaryDirectory CreateWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}\n");
		return workspace;
	}

	private static string WriteProfile(TemporaryDirectory workspace, string json) =>
		workspace.WriteFile("profiles/input.json", json);

	private static Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		params string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync([.. arguments, "--language", "en"], TestContext.Current.CancellationToken);
}
