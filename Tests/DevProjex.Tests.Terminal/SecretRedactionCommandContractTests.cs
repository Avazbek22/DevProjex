using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Terminal;

public sealed class SecretRedactionCommandContractTests
{
	private const string GithubToken = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const string ManuallyMarkedValue = "manualprojectvalue";
	private const string PrivateEmail = "owner@corp.internal";

	[Fact]
	public async Task ExportContext_LocalProfileAppliesPersistentManualSecretMarks()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		await AddPersistentManualSecretAsync(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--profile", "local",
				"--view", "content",
				"--format", "text",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(ManuallyMarkedValue, environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REDACTED[manual-secret#1]",
			environment.StandardOutput,
			StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeJson_LocalProfileCountsPersistentManualSecretMarks()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		await AddPersistentManualSecretAsync(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--profile", "local",
				"--format", "json",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var redaction = document.RootElement.GetProperty("redaction");
		Assert.Equal(1, redaction.GetProperty("matchedCount").GetInt32());
		Assert.Equal(1, redaction.GetProperty("redactedCount").GetInt32());
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ExportProject_LocalProfileAppliesPersistentManualSecretMarks(string kind)
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		await AddPersistentManualSecretAsync(workspace);
		var environment = new TestTerminalEnvironment();
		var destination = kind == "folder"
			? Path.Combine(workspace.OutputRoot, "manual-redacted")
			: Path.Combine(workspace.OutputRoot, "manual-redacted.zip");

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "project", workspace.ProjectRoot,
				"--profile", "local",
				"--as", kind,
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		string content;
		if (kind == "folder")
		{
			content = File.ReadAllText(Path.Combine(destination, "src", "manual.txt"));
		}
		else
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var entry = Assert.Single(
				archive.Entries,
				static candidate => candidate.FullName.EndsWith(
					"src/manual.txt",
					StringComparison.Ordinal));
			using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
			content = reader.ReadToEnd();
		}

		Assert.DoesNotContain(ManuallyMarkedValue, content, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportContext_AdditiveOptionWorksWithNoPathExclusions()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		workspace.Temporary.WriteFile(
			"project/appsettings.json",
			"{ \"ConnectionStrings\": { \"Main\": \"Host=db;Username=admin;Pass" +
			"word=postgres;Database=app\" } }");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "none",
				"--hide-secrets",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("Password=postgres", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(
			"Password=DEVPROJEX_REDACTED[connection-password#1]",
			environment.StandardOutput,
			StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ExportContext_HidePrivateDataHonorsExplicitBoolean(bool enabled)
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		workspace.Temporary.WriteFile("project/src/contact.txt", $"contact={PrivateEmail}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--hide-private-data", enabled.ToString().ToLowerInvariant(),
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(!enabled, environment.StandardOutput.Contains(PrivateEmail, StringComparison.Ordinal));
		Assert.Equal(enabled, environment.StandardOutput.Contains("DEVPROJEX_REDACTED[email#1]", StringComparison.Ordinal));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ExportContext_HidePrivateDataTriStateInheritsAndOverridesLocalProfile()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		workspace.Temporary.WriteFile("project/src/contact.txt", $"contact={PrivateEmail}\n");
		var store = new ProjectProfileStore(() => workspace.AppDataRoot);
		store.SaveProfile(
			workspace.ProjectRoot,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HidePrivateData],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HidePrivateData] = true
				}));
		var inherited = new TestTerminalEnvironment();
		var overridden = new TestTerminalEnvironment();

		var inheritedExit = await RunAsync(
			workspace,
			inherited,
			[
				"export", "context", workspace.ProjectRoot,
				"--profile", "local", "--view", "content", "--format", "text", "--plain", "-o", "-"
			]);
		var overriddenExit = await RunAsync(
			workspace,
			overridden,
			[
				"export", "context", workspace.ProjectRoot,
				"--profile", "local", "--hide-private-data", "false",
				"--view", "content", "--format", "text", "--plain", "-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, inheritedExit);
		Assert.DoesNotContain(PrivateEmail, inherited.StandardOutput, StringComparison.Ordinal);
		Assert.Equal(CommandLineExitCodes.Success, overriddenExit);
		Assert.Contains(PrivateEmail, overridden.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeJson_ReportsPrivateDataSeparatelyFromSecretRedaction()
	{
		using var workspace = CreateWorkspace();
		workspace.Temporary.WriteFile("project/src/contact.txt", $"contact={PrivateEmail}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--format", "json", "--git-mode", "none",
				"--hide-secrets", "--hide-private-data", "-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.True(document.RootElement.GetProperty("selection").GetProperty("hidePrivateData").GetBoolean());
		Assert.Equal(1, document.RootElement.GetProperty("redaction").GetProperty("matchedCount").GetInt32());
		Assert.Equal(1, document.RootElement.GetProperty("privacy").GetProperty("matchedCount").GetInt32());
		Assert.Equal(1, document.RootElement.GetProperty("privacy").GetProperty("redactedCount").GetInt32());
	}

	[Fact]
	public async Task AnalyzeText_ReportsSecretAndPrivateDataCountersOnSeparateRows()
	{
		using var workspace = CreateWorkspace();
		workspace.Temporary.WriteFile("project/src/contact.txt", $"contact={PrivateEmail}\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--format", "text", "--git-mode", "none",
				"--language", "en",
				"--hide-secrets", "--hide-private-data", "-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Rule matches: 1", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Redacted values: 1", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Private-data matches: 1", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Redacted private values: 1", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeText_ZeroPrivateMatchesReportsLimitedClaimInsteadOfSafety()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--format", "text", "--git-mode", "none",
				"--language", "en",
				"--hide-private-data", "-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Private-data matches: 0", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(
			"The private-data rules matched nothing; this is not a privacy guarantee.",
			environment.StandardOutput,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task AnalyzeJson_ReportsMatchesWithoutClaimingSafety()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--format=JSON",
				"--git-mode", "none",
				"--hide-secrets",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var redaction = document.RootElement.GetProperty("redaction");
		Assert.Equal(1, redaction.GetProperty("matchedCount").GetInt32());
		Assert.Equal(1, redaction.GetProperty("redactedCount").GetInt32());
		Assert.Equal(
			"Do not treat placeholder text as a real value.",
			redaction.GetProperty("notice").GetString());
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task AnalyzeJson_ZeroMatchesExplicitlyDoesNotClaimSafety()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"analyze", workspace.ProjectRoot,
				"--format", "json",
				"--git-mode", "none",
				"--hide-secrets",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		var redaction = document.RootElement.GetProperty("redaction");
		Assert.Equal(0, redaction.GetProperty("matchedCount").GetInt32());
		Assert.Equal(0, redaction.GetProperty("redactedCount").GetInt32());
		Assert.Contains(
			"not a safety guarantee",
			redaction.GetProperty("notice").GetString(),
			StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("text")]
	[InlineData("markdown")]
	[InlineData("json")]
	[InlineData("xml")]
	public async Task ExportContext_HideSecretsProducesCleanMachinePayload(string format)
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "tree-content",
				"--format", format,
				"--git-mode", "none",
				"--hide-secrets",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(GithubToken, environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Values redacted by DevProjex", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("Do not treat placeholder text as a real value.", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardOutput);
		Assert.Empty(environment.StandardError);
		if (format == "json")
		{
			using var document = JsonDocument.Parse(environment.StandardOutput);
			Assert.False(document.RootElement.TryGetProperty("redaction", out _));
		}
		else if (format == "xml")
		{
			var document = System.Xml.Linq.XDocument.Parse(environment.StandardOutput);
			Assert.Null(document.Root?.Element("redaction"));
		}
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ExportProject_HideSecretsRedactsPhysicalCopyWithoutAddingFiles(string kind)
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();
		var destination = kind == "folder"
			? Path.Combine(workspace.OutputRoot, "redacted")
			: Path.Combine(workspace.OutputRoot, "redacted.zip");

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "project", workspace.ProjectRoot,
				"--as", kind,
				"--git-mode", "none",
				"--hide-secrets",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(Path.GetFullPath(destination), environment.StandardOutput.Trim());
		Assert.Empty(environment.StandardError);
		if (kind == "folder")
		{
			var content = File.ReadAllText(Path.Combine(destination, "src", "app.cs"));
			Assert.DoesNotContain(GithubToken, content, StringComparison.Ordinal);
			Assert.False(File.Exists(Path.Combine(destination, "DEVPROJEX_REDACTIONS.txt")));
		}
		else
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var source = Assert.Single(archive.Entries, entry =>
				entry.FullName.EndsWith("src/app.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(source.Open(), Encoding.UTF8);
			Assert.DoesNotContain(GithubToken, reader.ReadToEnd(), StringComparison.Ordinal);
			Assert.DoesNotContain(archive.Entries, entry =>
				entry.FullName.EndsWith("DEVPROJEX_REDACTIONS.txt", StringComparison.Ordinal));
		}
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ExportProject_HidePrivateDataRedactsPhysicalCopyWithoutAddingFiles(string kind)
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		workspace.Temporary.WriteFile("project/src/contact.txt", $"contact={PrivateEmail}\n");
		var environment = new TestTerminalEnvironment();
		var destination = kind == "folder"
			? Path.Combine(workspace.OutputRoot, "redacted-private")
			: Path.Combine(workspace.OutputRoot, "redacted-private.zip");

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "project", workspace.ProjectRoot,
				"--as", kind,
				"--git-mode", "none",
				"--hide-private-data",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		string content;
		if (kind == "folder")
		{
			content = await File.ReadAllTextAsync(
				Path.Combine(destination, "src", "contact.txt"),
				TestContext.Current.CancellationToken);
			Assert.False(File.Exists(Path.Combine(destination, "DEVPROJEX_REDACTIONS.txt")));
		}
		else
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var source = Assert.Single(archive.Entries, entry =>
				entry.FullName.EndsWith("src/contact.txt", StringComparison.Ordinal));
			await using var stream = source.Open();
			using var reader = new StreamReader(stream, Encoding.UTF8);
			content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
			Assert.DoesNotContain(archive.Entries, entry =>
				entry.FullName.EndsWith("DEVPROJEX_REDACTIONS.txt", StringComparison.Ordinal));
		}

		Assert.DoesNotContain(PrivateEmail, content, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[email#1]", content, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--hide-secrets")]
	[InlineData("--hide-private-data")]
	public async Task ExportProjectDryRun_ExplainsThatRedactedCopyIsNotFaithfulAndCreatesNothing(
		string redactionOption)
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();
		var destination = Path.Combine(workspace.OutputRoot, "redacted");

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "project", workspace.ProjectRoot,
				"--as", "folder",
				"--git-mode", "none",
				redactionOption,
				"--dry-run",
				"--language", "en",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.False(Directory.Exists(destination));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("not be a byte-for-byte copy", environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Binary files remain unchanged", environment.StandardError, StringComparison.Ordinal);
	}

	/// <summary>
	/// A document omits text past the read limit whether Hide Secrets is on or off, so one oversized
	/// file costs the user that file - not the export. Nothing unscanned reaches the output either way.
	/// </summary>
	[Fact]
	public async Task ExportContext_OversizedSelectedTextIsOmittedAndTheDocumentIsStillWritten()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var environment = new TestTerminalEnvironment();
		var destination = Path.Combine(workspace.OutputRoot, "context.txt");
		await File.WriteAllTextAsync(
			Path.Combine(workspace.ProjectRoot, "oversized.txt"),
			new string('a', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--hide-secrets",
				"--plain",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(
			"DPX-SECRET-SCAN-LIMIT-EXCEEDED",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.True(File.Exists(destination));
		var document = await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken);
		Assert.Contains("oversized.txt", document, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('a', 4096), document, StringComparison.Ordinal);
	}

	/// <summary>
	/// A dry run has to predict the real run, and the two commands now differ: a context document
	/// omits an unreadable file and still ships, a project copy refuses because it reproduces bytes.
	/// </summary>
	[Theory]
	[InlineData("context", "--hide-secrets")]
	[InlineData("context", "--hide-private-data")]
	[InlineData("project", "--hide-secrets")]
	[InlineData("project", "--hide-private-data")]
	public async Task DryRun_RedactionPerformsScanPreflightBeforeReportingReadiness(
		string command,
		string redactionOption)
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var environment = new TestTerminalEnvironment();
		var destination = command == "context"
			? Path.Combine(workspace.OutputRoot, "context.txt")
			: Path.Combine(workspace.OutputRoot, "project-copy");
		await File.WriteAllTextAsync(
			Path.Combine(workspace.ProjectRoot, "oversized.txt"),
			new string('a', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);
		var arguments = command == "context"
			? new[]
			{
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				redactionOption,
				"--dry-run",
				"-o", destination
			}
			: new[]
			{
				"export", "project", workspace.ProjectRoot,
				"--as", "folder",
				"--git-mode", "none",
				redactionOption,
				"--dry-run",
				"-o", destination
			};

		var exitCode = await RunAsync(workspace, environment, arguments);

		// Both commands now complete: neither refuses a whole operation over one unreadable file.
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(
			"DPX-SECRET-SCAN-LIMIT-EXCEEDED",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.False(Path.Exists(destination));
	}

	/// <summary>
	/// A dry run has to predict the real run. The copy leaves an unreadable file out, so the dry
	/// run says so rather than reporting a faithful copy.
	/// </summary>
	[Fact]
	public async Task ExportProjectDryRun_AnnouncesThatAnUnreadableFileWillBeLeftOut()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var environment = new TestTerminalEnvironment();
		var destination = Path.Combine(workspace.OutputRoot, "project-copy");
		await File.WriteAllTextAsync(
			Path.Combine(workspace.ProjectRoot, "oversized.txt"),
			new string('a', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "project", workspace.ProjectRoot,
				"--as", "folder",
				"--git-mode", "none",
				"--hide-secrets",
				"--dry-run",
				"--language", "en",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("left out of this copy", environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.False(Path.Exists(destination));
	}

	[Fact]
	public async Task ExportContext_LegacyExcludeTokenRemainsAccepted()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "hide-secrets",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(GithubToken, environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ExportContext_ExplicitFalseOverridesLegacyHideSecretsToken()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--exclude", "hide-secrets",
				"--hide-secrets", "false",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(GithubToken, environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ExportContext_LocalProfileAppliesSourceBoundMarkToOnlySelectedOccurrence()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var content = $"first={ManuallyMarkedValue}\nsecond={ManuallyMarkedValue}\n";
		workspace.Temporary.WriteFile("project/src/manual.txt", content);
		await AddPersistentManualSecretAsync(
			workspace,
			relativePath: "src/manual.txt",
			sourceOffset: content.IndexOf(ManuallyMarkedValue, StringComparison.Ordinal),
			writeSource: false);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--profile", "local",
				"--view", "content",
				"--format", "text",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(1, CountOccurrences(environment.StandardOutput, ManuallyMarkedValue));
		Assert.Equal(
			1,
			CountOccurrences(environment.StandardOutput, "DEVPROJEX_REDACTED[manual-secret#1]"));
		Assert.Empty(environment.StandardError);
	}

	[Fact(Timeout = 20_000)]
	public async Task ExportContext_FindingBudgetExceeded_ProducesNoPartialOutput()
	{
		using var workspace = CreateWorkspace(includeSecret: false);
		var destination = Path.Combine(workspace.OutputRoot, "budget-exceeded.txt");
		var repeated = string.Join(
			'\n',
			Enumerable.Repeat(
				$"token={GithubToken}",
				SecretInspectionLimits.MaximumFindingsPerFile + 1));
		workspace.Temporary.WriteFile("project/src/pathological.env", repeated);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			[
				"export", "context", workspace.ProjectRoot,
				"--view", "content",
				"--format", "text",
				"--git-mode", "none",
				"--hide-secrets",
				"--plain",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.False(File.Exists(destination));
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SECRET-DETECTION-FAILED", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExportContextHelpAndCompletionExposeCanonicalToken()
	{
		using var workspace = CreateWorkspace();
		var help = new TestTerminalEnvironment();
		var completion = new TestTerminalEnvironment();

		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(workspace, help, ["export", "context", "--language", "en", "--help"]));
		Assert.Contains("--hide-secrets", help.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--hide-private-data", help.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"none|smart-ignore|hide-secrets",
			help.StandardOutput,
			StringComparison.Ordinal);
		const string completionLine = "devprojex export context . --exclude ";
		Assert.Equal(
			CommandLineExitCodes.Success,
			await RunAsync(
				workspace,
				completion,
				[
					"dev", "complete",
					"--position", completionLine.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
					completionLine
				]));
		Assert.DoesNotContain(
			"hide-secrets",
			completion.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public async Task OpenHelp_DoesNotExposeHidePrivateDataDuringPhaseOne()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			["open", "--language", "en", "--help"]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain("--hide-private-data", environment.StandardOutput, StringComparison.Ordinal);
	}

	private static Task<int> RunAsync(
		Workspace workspace,
		TestTerminalEnvironment environment,
		string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.AppDataRoot))
			.RunAsync(arguments, TestContext.Current.CancellationToken);

	private static async Task AddPersistentManualSecretAsync(
		Workspace workspace,
		string? relativePath = null,
		int? sourceOffset = null,
		bool writeSource = true)
	{
		if (writeSource)
		{
			workspace.Temporary.WriteFile(
				"project/src/manual.txt",
				$"setting={ManuallyMarkedValue}\n");
		}
		var store = new ProjectProfileStore(() => workspace.AppDataRoot);
		store.SaveProfile(
			workspace.ProjectRoot,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HideSecrets] = true
				}));
		using var identityProvider = new PersistentSecretIdentityProvider(() => workspace.AppDataRoot);
		Assert.Equal(PersistentSecretIdentityAvailability.Ready, await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		Assert.True(PersistentSecretIdentity.TryCreateV2(
			identityProvider,
			ManuallyMarkedValue,
			out var identity));
		var result = await store.AddMarkAsync(
			workspace.ProjectRoot,
			new MarkedSecretProfileEntry(
				identity,
				"setting",
				ManuallyMarkedValue.Length,
				relativePath,
				sourceOffset),
			TestContext.Current.CancellationToken);
		Assert.True(result.Succeeded);
	}

	private static int CountOccurrences(string value, string search)
	{
		var count = 0;
		for (var offset = 0; (offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0; offset += search.Length)
			count++;
		return count;
	}

	private static Workspace CreateWorkspace(bool includeSecret = true)
	{
		var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		temporary.WriteFile(
			"project/src/app.cs",
			includeSecret
				? $"const string token = \"{GithubToken}\";\n"
				: "internal static class App { }\n");
		return new Workspace(
			temporary,
			projectRoot,
			temporary.CreateDirectory("output"),
			temporary.CreateDirectory("app-data"));
	}

	private sealed class Workspace(
		TemporaryDirectory temporary,
		string projectRoot,
		string outputRoot,
		string appDataRoot) : IDisposable
	{
		public TemporaryDirectory Temporary { get; } = temporary;
		public string ProjectRoot { get; } = projectRoot;
		public string OutputRoot { get; } = outputRoot;
		public string AppDataRoot { get; } = appDataRoot;
		public void Dispose() => Temporary.Dispose();
	}
}
