using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Terminal;

public sealed class CliProfileDataSafetyProcessTests
{
	private const string GithubToken = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";

	[Fact]
	public async Task ExclusionOverridePreservesPortableProfileSecretRedactionInRealProcess()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/source.txt", $"token={GithubToken}\n");
		var profile = workspace.WriteFile(
			"profile.json",
			"""
			{
			  "schemaVersion": 1,
			  "kind": "devprojex-profile",
			  "selection": {
			    "gitMode": "none",
			    "exclusions": [],
			    "hideSecrets": true
			  }
			}
			""");

		var result = await RunAsync(
			workspace,
			"export", "context", project,
			"--profile", profile,
			"--exclude", "hidden-files",
			"--view", "content",
			"--format", "text",
			"--plain", "-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.DoesNotContain(GithubToken, result.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", result.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(result.StandardError);
	}

	[Fact]
	public async Task FailOnFindingsReturnsPolicyFailureForUnscannableFileInRealProcess()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		await File.WriteAllTextAsync(
			Path.Combine(project, "oversized.txt"),
			new string('x', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);

		var result = await RunAsync(
			workspace,
			"analyze", project,
			"--git-mode", "none",
			"--fail-on-findings",
			"--format", "json",
			"--plain", "-o", "-");

		Assert.Equal(CommandLineExitCodes.PolicyFailure, result.ExitCode);
		using var document = JsonDocument.Parse(result.StandardOutput);
		Assert.Equal(0, document.RootElement.GetProperty("findingCount").GetInt32());
		Assert.Single(document.RootElement
			.GetProperty("contentInspection")
			.GetProperty("unscannableFiles")
			.EnumerateArray());
	}

	[Fact]
	public async Task ExplicitEmptyPortableSelectionRemainsEmptyInRealProcess()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/source.txt", "selected\n");
		var profile = workspace.WriteFile(
			"profile.json",
			"""
			{
			  "schemaVersion": 1,
			  "selection": {
			    "roots": null,
			    "extensions": null,
			    "selectedPaths": [],
			    "gitMode": "none",
			    "exclusions": []
			  }
			}
			""");

		var result = await RunAsync(
			workspace,
			"analyze", project,
			"--profile", profile,
			"--format", "json",
			"--plain", "-o", "-");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Empty(result.StandardError);
		using var document = JsonDocument.Parse(result.StandardOutput);
		Assert.Equal(0, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
		Assert.Empty(document.RootElement.GetProperty("selection").GetProperty("selectedPaths").EnumerateArray());
	}

	private static async Task<ProcessResult> RunAsync(
		TemporaryDirectory workspace,
		params string[] arguments)
	{
		var applicationAssembly = PublishedApplicationLocator.FindApplicationAssembly();
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			}
		};
		process.StartInfo.ArgumentList.Add(applicationAssembly);
		foreach (var argument in arguments)
			process.StartInfo.ArgumentList.Add(argument);
		process.StartInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		process.StartInfo.Environment[InvocationEnvironment.InternalDataRootVariable] =
			workspace.CreateDirectory("app-data");
		process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";

		Assert.True(process.Start());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		var standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
		var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			await process.WaitForExitAsync(timeout.Token);
			return new ProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}
		}
	}

	private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
