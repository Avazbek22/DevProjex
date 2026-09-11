using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class CliTransportPolicyProcessTests
{
	private const string RetiredPolicyVariable = "DEVPROJEX_INTERNAL_TEST_ALLOW_FILE_GIT";

	[Fact]
	public async Task BuiltApplicationRejectsALocalFileUrlWhenTheRetiredVariableIsSet()
	{
		using var workspace = new TemporaryDirectory();
		var origin = workspace.CreateDirectory("origin.git");
		var environment = new Dictionary<string, string> { [RetiredPolicyVariable] = "1" };

		// Control: the same process, the same variable, and an ordinary local project path still
		// analyze successfully, so the refusal below cannot be a broken host or a parse failure.
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class App;\n");
		var control = await RunAsync(workspace, environment, "analyze", project, "--format", "json");

		Assert.Equal(CommandLineExitCodes.Success, control.ExitCode);
		Assert.Contains("\"inventory\"", control.StandardOutput, StringComparison.Ordinal);

		var result = await RunAsync(
			workspace,
			environment,
			"analyze",
			new Uri(origin).AbsoluteUri,
			"--format",
			"json");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains("DPX-CLI-GIT-URL-INVALID", result.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuiltApplicationRejectsALocalFileUrlWithoutTheRetiredVariable()
	{
		using var workspace = new TemporaryDirectory();
		var origin = workspace.CreateDirectory("origin.git");
		var source = new Uri(origin).AbsoluteUri;

		var result = await RunAsync(workspace, environment: null, "analyze", source, "--format", "json");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains("DPX-CLI-GIT-URL-INVALID", result.StandardError, StringComparison.Ordinal);
	}

	private static async Task<ProcessResult> RunAsync(
		TemporaryDirectory workspace,
		IReadOnlyDictionary<string, string>? environment,
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
		if (environment is not null)
		{
			foreach (var pair in environment)
				process.StartInfo.Environment[pair.Key] = pair.Value;
		}

		Assert.True(process.Start());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
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
