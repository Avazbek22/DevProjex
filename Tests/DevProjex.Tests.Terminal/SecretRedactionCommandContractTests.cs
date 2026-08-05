using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Terminal;

public sealed class SecretRedactionCommandContractTests
{
	private const string GithubToken = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";

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
				"--exclude=HIDE-SECRETS",
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
				"--exclude", "hide-secrets",
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
				"--exclude", "hide-secrets",
				"--plain",
				"-o", "-"
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.DoesNotContain(GithubToken, environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardOutput);
		Assert.Empty(environment.StandardError);
		if (format == "json")
		{
			using var document = JsonDocument.Parse(environment.StandardOutput);
			Assert.Equal(1, document.RootElement.GetProperty("redaction").GetProperty("count").GetInt32());
		}
		else if (format == "xml")
		{
			var document = System.Xml.Linq.XDocument.Parse(environment.StandardOutput);
			Assert.Equal("1", document.Root?.Element("redaction")?.Element("count")?.Value);
		}
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task ExportProject_HideSecretsRedactsPhysicalCopyAndAddsLegend(string kind)
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
				"--exclude", "hide-secrets",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(Path.GetFullPath(destination), environment.StandardOutput.Trim());
		Assert.Empty(environment.StandardError);
		if (kind == "folder")
		{
			var content = File.ReadAllText(Path.Combine(destination, "src", "app.cs"));
			Assert.DoesNotContain(GithubToken, content, StringComparison.Ordinal);
			Assert.True(File.Exists(Path.Combine(destination, "DEVPROJEX_REDACTIONS.txt")));
		}
		else
		{
			using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
			var source = Assert.Single(archive.Entries, entry =>
				entry.FullName.EndsWith("src/app.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(source.Open(), Encoding.UTF8);
			Assert.DoesNotContain(GithubToken, reader.ReadToEnd(), StringComparison.Ordinal);
			Assert.Contains(archive.Entries, entry =>
				entry.FullName.EndsWith("DEVPROJEX_REDACTIONS.txt", StringComparison.Ordinal));
		}
	}

	[Fact]
	public async Task ExportProjectDryRun_ExplainsThatRedactedCopyIsNotFaithfulAndCreatesNothing()
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
				"--exclude", "hide-secrets",
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

	[Fact]
	public async Task ExportContext_OversizedSelectedTextFailsClosedWithoutCreatingOutput()
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
				"--exclude", "hide-secrets",
				"--plain",
				"-o", destination
			]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SECRET-SCAN-LIMIT-EXCEEDED", environment.StandardError, StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
	}

	[Theory]
	[InlineData("context")]
	[InlineData("project")]
	public async Task DryRun_HideSecretsPerformsScanPreflightBeforeReportingReadiness(string command)
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
				"--exclude", "hide-secrets",
				"--dry-run",
				"-o", destination
			}
			: new[]
			{
				"export", "project", workspace.ProjectRoot,
				"--as", "folder",
				"--git-mode", "none",
				"--exclude", "hide-secrets",
				"--dry-run",
				"-o", destination
			};

		var exitCode = await RunAsync(workspace, environment, arguments);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SECRET-SCAN-LIMIT-EXCEEDED", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("Dry run:", environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.False(Path.Exists(destination));
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
		Assert.Contains("hide-secrets", help.StandardOutput, StringComparison.Ordinal);
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
		Assert.Contains(
			"hide-secrets",
			completion.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
	}

	private static Task<int> RunAsync(
		Workspace workspace,
		TestTerminalEnvironment environment,
		string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.AppDataRoot))
			.RunAsync(arguments, TestContext.Current.CancellationToken);

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
		public string ProjectRoot { get; } = projectRoot;
		public string OutputRoot { get; } = outputRoot;
		public string AppDataRoot { get; } = appDataRoot;
		public void Dispose() => temporary.Dispose();
	}
}
