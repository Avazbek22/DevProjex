using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class CompletionCommandContractTests
{
	[Theory]
	[InlineData("bash", "complete -F _devprojex_complete devprojex")]
	[InlineData("zsh", "#compdef devprojex")]
	[InlineData("fish", "complete -c devprojex")]
	[InlineData("powershell", "Register-ArgumentCompleter -Native -CommandName devprojex")]
	public void ScriptsDelegateCompletionToThePublicCommandTree(
		string shell,
		string shellMarker)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var script = CompletionScriptGenerator.Generate(root, shell);

		Assert.Contains(shellMarker, script, StringComparison.Ordinal);
		Assert.Contains("dev complete", script, StringComparison.Ordinal);
		Assert.Contains("--position", script, StringComparison.Ordinal);
		if (shell == "powershell")
		{
			Assert.Contains("--working-directory-base64", script, StringComparison.Ordinal);
			Assert.Contains("FromBase64String", script, StringComparison.Ordinal);
			Assert.Contains("UTF8Encoding", script, StringComparison.Ordinal);
		}
		else
		{
			Assert.Contains("--null", script, StringComparison.Ordinal);
			var transportMarkers = shell switch
			{
				"bash" => new[] { "read -r -d '' candidate", "compopt -o filenames" },
				"zsh" => ["${(V)candidate}", "compadd -d displays -a candidates"],
				"fish" => ["string split0"],
				_ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
			};
			foreach (var marker in transportMarkers)
				Assert.Contains(marker, script, StringComparison.Ordinal);
		}
		Assert.DoesNotContain("analyze", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--git-mode", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--exclude", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--no-ui", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--report", script, StringComparison.Ordinal);
		Assert.DoesNotContain("--copy", script, StringComparison.Ordinal);
		Assert.DoesNotContain("benchmark", script, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("bash")]
	[InlineData("zsh")]
	[InlineData("fish")]
	[InlineData("powershell")]
	public async Task CompletionCommandWritesOnlyTheRequestedScript(string shell)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["completion", shell],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.NotEmpty(environment.StandardOutput);
		Assert.Empty(environment.StandardError);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\r", environment.StandardOutput, StringComparison.Ordinal);
		Assert.EndsWith("\n", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("bash", "bash", "-n")]
	[InlineData("zsh", "zsh", "-n")]
	[InlineData("fish", "fish", "-n")]
	[InlineData("powershell", "powershell", "-NoProfile|-NonInteractive|-Command|-")]
	public async Task GeneratedScriptPassesAvailableNativeShellSyntaxCheck(
		string shell,
		string executable,
		string argumentText)
	{
		if (shell == "powershell" &&
		    !TryResolveExecutable("pwsh", out executable) &&
		    !TryResolveExecutable("powershell", out executable))
		{
			Assert.Skip("PowerShell is not installed on this test host.");
		}
		else if (shell != "powershell" &&
		         !TryResolveExecutable(executable, out executable))
		{
			Assert.Skip($"{shell} is not installed on this test host.");
		}

		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var script = CompletionScriptGenerator.Generate(root, shell);
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = executable,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				StandardInputEncoding = new UTF8Encoding(false)
			}
		};
		foreach (var argument in argumentText.Split('|', StringSplitOptions.RemoveEmptyEntries))
			process.StartInfo.ArgumentList.Add(argument);
		process.Start();
		await process.StandardInput.WriteAsync(script);
		process.StandardInput.Close();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
			when (!TestContext.Current.CancellationToken.IsCancellationRequested)
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			throw new TimeoutException(
				$"{shell} syntax check did not finish within 30 seconds.");
		}
		var standardError = await process.StandardError.ReadToEndAsync(timeout.Token);

		Assert.Equal(0, process.ExitCode);
		Assert.True(
			string.IsNullOrWhiteSpace(standardError),
			$"{shell} syntax check wrote stderr: {standardError}");
	}

	[Fact]
	public void UnsupportedShellIsRejectedByTheProductionParser()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var result = root.Parse(["completion", "cmd"]);

		Assert.NotEmpty(result.Errors);
	}

	private static bool TryResolveExecutable(
		string executable,
		out string resolved)
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var extensions = OperatingSystem.IsWindows()
			? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
				.Split(';', StringSplitOptions.RemoveEmptyEntries)
			: [string.Empty];
		foreach (var directory in path.Split(
			         Path.PathSeparator,
			         StringSplitOptions.RemoveEmptyEntries))
		{
			foreach (var extension in extensions)
			{
				var candidate = Path.Combine(directory, executable + extension);
				if (File.Exists(candidate))
				{
					resolved = candidate;
					return true;
				}
			}
		}

		resolved = string.Empty;
		return false;
	}
}
