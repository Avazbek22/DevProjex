using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DevProjex.Tests.Terminal;

public sealed class GeneratedCompletionNativeShellIntegrationTests
{
	private const string CompletionLine = "devprojex analyze . --format ";
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	[Theory]
	[InlineData("bash")]
	[InlineData("zsh")]
	[InlineData("fish")]
	[InlineData("powershell")]
	public async Task GeneratedScriptLoadsAndCompletesAgainstUnifiedHost(string shell)
	{
		var shellExecutable = ResolveShellOrSkip(shell);
		var unifiedHost = PublishedApplicationLocator.FindExecutable();
		using var workspace = new TemporaryDirectory();
		var integrationRoot = workspace.CreateDirectory("completion host Юникод space");
		var wrapper = CreatePathWrapper(integrationRoot, unifiedHost, shell);
		var generated = await RunUnifiedHostAsync(
			unifiedHost,
			["completion", shell],
			TestContext.Current.CancellationToken);
		Assert.Equal(0, generated.ExitCode);
		Assert.Empty(generated.StandardError);
		Assert.Contains("dev complete", generated.StandardOutput, StringComparison.Ordinal);
		var scriptExtension = shell == "powershell" ? "ps1" : shell;
		var completionScript = workspace.WriteFile(
			Path.Combine(
				"completion host Юникод space",
				$"generated-{shell}.{scriptExtension}"),
			generated.StandardOutput);

		var completed = await RunCompletionAsync(
			shell,
			shellExecutable,
			integrationRoot,
			wrapper,
			completionScript,
			workspace.Path,
			TestContext.Current.CancellationToken);

		Assert.True(
			completed.ExitCode == 0,
			$"{shell} completion exited with {completed.ExitCode}. " +
			$"stdout=[{completed.StandardOutput}] stderr=[{completed.StandardError}]");
		Assert.True(
			string.IsNullOrWhiteSpace(completed.StandardError),
			$"{shell} completion wrote stderr: {completed.StandardError}");
		var candidates = completed.StandardOutput
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(static candidate => candidate.Split('\t', 2)[0])
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.True(
			candidates.SequenceEqual(["json", "text"], StringComparer.Ordinal),
			$"{shell} returned [{string.Join(", ", candidates)}].");
		Assert.DoesNotContain("--as", candidates);
		Assert.DoesNotContain("reset", candidates);
		Assert.DoesNotContain("dev", candidates);
	}

	[Fact]
	public async Task PowerShellCompletionQuotesPathAndPreservesDisplayAndParsedValue()
	{
		var shellExecutable = ResolveShellOrSkip("powershell");
		var unifiedHost = PublishedApplicationLocator.FindExecutable();
		using var workspace = new TemporaryDirectory();
		var workingDirectory = workspace.CreateDirectory("completion cwd");
		var integrationRoot = workspace.CreateDirectory("completion host Юникод space");
		CreatePathWrapper(integrationRoot, unifiedHost, "powershell");
		var generated = await RunUnifiedHostAsync(
			unifiedHost,
			["completion", "powershell"],
			TestContext.Current.CancellationToken);
		Assert.Equal(0, generated.ExitCode);
		Assert.Empty(generated.StandardError);
		var completionScript = workspace.WriteFile(
			Path.Combine(
				"completion host Юникод space",
				"generated-powershell.ps1"),
			generated.StandardOutput);
		var directoryName = "Проект O'Brien & $draft";
		Directory.CreateDirectory(Path.Combine(workingDirectory, directoryName));
		var rawCandidate =
			$".{Path.DirectorySeparatorChar}{directoryName}{Path.DirectorySeparatorChar}";
		var line =
			$"devprojex analyze \".{Path.DirectorySeparatorChar}Проект O";

		var completed = await RunPowerShellPathCompletionAsync(
			shellExecutable,
			integrationRoot,
			completionScript,
			workingDirectory,
			line,
			rawCandidate,
			TestContext.Current.CancellationToken);

		Assert.True(
			completed.ExitCode == 0,
			$"powershell completion exited with {completed.ExitCode}. " +
			$"stdout=[{completed.StandardOutput}] stderr=[{completed.StandardError}]");
		Assert.True(
			string.IsNullOrWhiteSpace(completed.StandardError),
			$"powershell completion wrote stderr: {completed.StandardError}");
		using var document = JsonDocument.Parse(completed.StandardOutput);
		var result = document.RootElement;
		var expectedCompletionText =
			"'" + rawCandidate.Replace("'", "''", StringComparison.Ordinal) + "'";
		Assert.Equal(
			expectedCompletionText,
			result.GetProperty("completionText").GetString());
		Assert.Equal(
			rawCandidate,
			result.GetProperty("listItemText").GetString());
		Assert.Equal(
			rawCandidate,
			result.GetProperty("parsedValue").GetString());
		Assert.Equal(0, result.GetProperty("parseErrorCount").GetInt32());
	}

	[Fact]
	public async Task WindowsPowerShell51CompletesAfterClosedQuotedWhitespacePath()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows PowerShell 5.1 is available only on Windows.");
			return;
		}

		var shellExecutable = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			"System32",
			"WindowsPowerShell",
			"v1.0",
			"powershell.exe");
		if (!File.Exists(shellExecutable))
		{
			Assert.Skip("Windows PowerShell 5.1 is not installed on this Windows host.");
			return;
		}

		var unifiedHost = PublishedApplicationLocator.FindExecutable();
		using var workspace = new TemporaryDirectory();
		var workingDirectory = workspace.CreateDirectory("completion cwd");
		Directory.CreateDirectory(Path.Combine(workingDirectory, "Program Files"));
		var integrationRoot = workspace.CreateDirectory(
			"completion host Юникод space");
		CreatePathWrapper(integrationRoot, unifiedHost, "powershell");
		var generated = await RunUnifiedHostAsync(
			unifiedHost,
			["completion", "powershell"],
			TestContext.Current.CancellationToken);
		Assert.Equal(0, generated.ExitCode);
		Assert.Empty(generated.StandardError);
		var completionScript = workspace.WriteFile(
			Path.Combine(
				"completion host Юникод space",
				"generated-windows-powershell.ps1"),
			generated.StandardOutput);
		const string line =
			"devprojex analyze \".\\Program Files\" --format ";

		var completed = await RunPowerShellCandidateCompletionAsync(
			shellExecutable,
			integrationRoot,
			completionScript,
			workingDirectory,
			line,
			TestContext.Current.CancellationToken);

		Assert.True(
			completed.ExitCode == 0,
			$"Windows PowerShell completion exited with {completed.ExitCode}. " +
			$"stdout=[{completed.StandardOutput}] stderr=[{completed.StandardError}]");
		Assert.True(
			string.IsNullOrWhiteSpace(completed.StandardError),
			$"Windows PowerShell completion wrote stderr: {completed.StandardError}");
		Assert.Equal(
			["json", "text"],
			completed.StandardOutput
				.ReplaceLineEndings("\n")
				.Split(
					'\n',
					StringSplitOptions.RemoveEmptyEntries |
					StringSplitOptions.TrimEntries)
				.Order(StringComparer.Ordinal));
	}

	private static async Task<ShellProcessResult> RunCompletionAsync(
		string shell,
		string shellExecutable,
		string integrationRoot,
		string wrapper,
		string completionScript,
		string isolatedHome,
		CancellationToken cancellationToken)
	{
		var startInfo = CreateShellStartInfo(shell, shellExecutable);
		startInfo.Environment["HOME"] = isolatedHome;
		startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(isolatedHome, "xdg");
		startInfo.Environment["ZDOTDIR"] = Path.Combine(isolatedHome, "zsh");
		if (shell == "powershell")
		{
			startInfo.Environment["PATH"] = integrationRoot +
			                               Path.PathSeparator +
			                               (startInfo.Environment["PATH"] ??
			                                Environment.GetEnvironmentVariable("PATH") ??
			                                string.Empty);
			startInfo.Environment["DPX_COMPLETION_SCRIPT"] = completionScript;
			var driver =
				"""
				$ErrorActionPreference = 'Stop'
				. $env:DPX_COMPLETION_SCRIPT
				$line = 'devprojex analyze . --format '
				TabExpansion2 -inputScript $line -cursorColumn $line.Length |
				    Select-Object -ExpandProperty CompletionMatches |
				    ForEach-Object { $_.CompletionText }
				""";
			var driverPath = Path.Combine(
				isolatedHome,
				"PowerShell completion driver Юникод.ps1");
			File.WriteAllText(driverPath, driver, Utf8WithoutBom);
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(driverPath);
		}
		else
		{
			var driver = BuildPosixDriver(
				shell,
				integrationRoot,
				wrapper,
				completionScript);
			var driverPath = Path.Combine(
				isolatedHome,
				$"{shell} completion driver Юникод");
			File.WriteAllText(
				driverPath,
				driver.ReplaceLineEndings("\n"),
				Utf8WithoutBom);
			startInfo.ArgumentList.Add(ConvertDriverPathForShell(
				driverPath,
				shellExecutable));
		}

		return await RunProcessAsync(startInfo, cancellationToken);
	}

	private static async Task<ShellProcessResult> RunPowerShellPathCompletionAsync(
		string shellExecutable,
		string integrationRoot,
		string completionScript,
		string workingDirectory,
		string line,
		string expectedRawCandidate,
		CancellationToken cancellationToken)
	{
		var startInfo = CreateShellStartInfo("powershell", shellExecutable);
		startInfo.WorkingDirectory = workingDirectory;
		startInfo.Environment["PATH"] = integrationRoot +
		                               Path.PathSeparator +
		                               (startInfo.Environment["PATH"] ??
		                                Environment.GetEnvironmentVariable("PATH") ??
		                                string.Empty);
		startInfo.Environment["DPX_COMPLETION_SCRIPT"] = completionScript;
		startInfo.Environment["DPX_COMPLETION_LINE"] = line;
		startInfo.Environment["DPX_EXPECTED_RAW"] = expectedRawCandidate;
		var driver =
			"""
			$ErrorActionPreference = 'Stop'
			. $env:DPX_COMPLETION_SCRIPT
			$line = $env:DPX_COMPLETION_LINE
			$completion = TabExpansion2 -inputScript $line -cursorColumn $line.Length
			$match = $completion.CompletionMatches |
			    Where-Object { $_.ListItemText -eq $env:DPX_EXPECTED_RAW } |
			    Select-Object -First 1
			if ($null -eq $match) {
			    throw "Expected completion candidate was not returned."
			}
			$completedLine = $line.Remove(
			    $completion.ReplacementIndex,
			    $completion.ReplacementLength).Insert(
			        $completion.ReplacementIndex,
			        $match.CompletionText)
			$tokens = $null
			$parseErrors = $null
			$ast = [System.Management.Automation.Language.Parser]::ParseInput(
			    $completedLine,
			    [ref]$tokens,
			    [ref]$parseErrors)
			$commandAst = $ast.Find(
			    { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
			    $true)
			$parsedValue = $commandAst.CommandElements[-1].Value
			[pscustomobject]@{
			    completionText = $match.CompletionText
			    listItemText = $match.ListItemText
			    parsedValue = $parsedValue
			    parseErrorCount = @($parseErrors).Count
			} | ConvertTo-Json -Compress
			""";
		var driverPath = Path.Combine(
			workingDirectory,
			"PowerShell path completion driver.ps1");
		File.WriteAllText(driverPath, driver, Utf8WithoutBom);
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(driverPath);
		return await RunProcessAsync(startInfo, cancellationToken);
	}

	private static async Task<ShellProcessResult> RunPowerShellCandidateCompletionAsync(
		string shellExecutable,
		string integrationRoot,
		string completionScript,
		string workingDirectory,
		string line,
		CancellationToken cancellationToken)
	{
		var startInfo = CreateShellStartInfo("powershell", shellExecutable);
		startInfo.WorkingDirectory = workingDirectory;
		startInfo.Environment["PATH"] = integrationRoot +
		                               Path.PathSeparator +
		                               (startInfo.Environment["PATH"] ??
		                                Environment.GetEnvironmentVariable("PATH") ??
		                                string.Empty);
		startInfo.Environment["DPX_COMPLETION_SCRIPT"] = completionScript;
		startInfo.Environment["DPX_COMPLETION_LINE"] = line;
		var driver =
			"""
			$ErrorActionPreference = 'Stop'
			. $env:DPX_COMPLETION_SCRIPT
			$line = $env:DPX_COMPLETION_LINE
			TabExpansion2 -inputScript $line -cursorColumn $line.Length |
			    Select-Object -ExpandProperty CompletionMatches |
			    ForEach-Object { $_.CompletionText }
			""";
		var driverPath = Path.Combine(
			workingDirectory,
			"Windows PowerShell quoted completion driver.ps1");
		File.WriteAllText(driverPath, driver, Utf8WithoutBom);
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(driverPath);
		return await RunProcessAsync(startInfo, cancellationToken);
	}

	private static ProcessStartInfo CreateShellStartInfo(
		string shell,
		string executable)
	{
		var startInfo = CreateRedirectedStartInfo(executable);
		switch (shell)
		{
			case "bash":
				startInfo.ArgumentList.Add("--noprofile");
				startInfo.ArgumentList.Add("--norc");
				break;
			case "zsh":
				startInfo.ArgumentList.Add("-f");
				break;
			case "fish":
				startInfo.ArgumentList.Add("--no-config");
				break;
			case "powershell":
				startInfo.ArgumentList.Add("-NoLogo");
				startInfo.ArgumentList.Add("-NoProfile");
				startInfo.ArgumentList.Add("-NonInteractive");
				if (OperatingSystem.IsWindows())
				{
					startInfo.ArgumentList.Add("-ExecutionPolicy");
					startInfo.ArgumentList.Add("Bypass");
				}
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(shell), shell, null);
		}

		return startInfo;
	}

	private static string BuildPosixDriver(
		string shell,
		string integrationRoot,
		string wrapper,
		string completionScript)
	{
		var pathSetup = string.Join(
			"\n",
			BuildPosixPathAssignment("integration_root", integrationRoot, shell),
			BuildPosixPathAssignment("wrapper_path", wrapper, shell),
			BuildPosixPathAssignment("completion_script", completionScript, shell));
		return shell switch
		{
			"bash" =>
				$$"""
				{{pathSetup}}
				chmod +x "$wrapper_path"
				export PATH="$integration_root:$PATH"
				. "$completion_script"
				COMP_LINE='{{CompletionLine}}'
				COMP_POINT=${#COMP_LINE}
				_devprojex_complete
				printf '%s\n' "${COMPREPLY[@]}"
				""",
			"zsh" =>
				$$"""
				autoload -Uz compinit
				compinit -u
				{{pathSetup}}
				chmod +x "$wrapper_path"
				export PATH="$integration_root:$PATH"
				source "$completion_script"
				_describe() { print -rl -- "${candidates[@]}"; }
				BUFFER='{{CompletionLine}}'
				CURSOR=${#BUFFER}
				_devprojex_complete
				""",
			"fish" =>
				$"""
				{pathSetup}
				chmod +x "$wrapper_path"
				set -gx PATH "$integration_root" $PATH
				source "$completion_script"
				complete -C '{CompletionLine}'
				""",
			_ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
		};
	}

	private static string ConvertDriverPathForShell(
		string path,
		string shellExecutable)
	{
		if (!OperatingSystem.IsWindows())
			return path;

		var root = Path.GetPathRoot(path);
		if (root is null || root.Length < 2 || root[1] != ':')
			return path.Replace('\\', '/');

		var drive = char.ToLowerInvariant(root[0]);
		var suffix = path[root.Length..].Replace('\\', '/');
		if (shellExecutable.Contains(
			    $"{Path.DirectorySeparatorChar}Windows{Path.DirectorySeparatorChar}System32",
			    StringComparison.OrdinalIgnoreCase))
		{
			return $"/mnt/{drive}/{suffix}";
		}
		if (shellExecutable.Contains(
			    $"{Path.DirectorySeparatorChar}cygwin",
			    StringComparison.OrdinalIgnoreCase))
		{
			return $"/cygdrive/{drive}/{suffix}";
		}
		return $"/{drive}/{suffix}";
	}

	private static string BuildPosixPathAssignment(
		string variable,
		string path,
		string shell)
	{
		var quoted = QuotePosix(path);
		if (!OperatingSystem.IsWindows())
			return shell == "fish"
				? $"set {variable} {quoted}"
				: $"{variable}={quoted}";

		if (shell == "fish")
		{
			return $"""
				if type -q wslpath
				    set {variable} (wslpath -u {quoted})
				else if type -q cygpath
				    set {variable} (cygpath -u {quoted})
				else
				    set {variable} {quoted}
				end
				""";
		}

		return $"""
			if command -v wslpath >/dev/null 2>&1; then
			    {variable}=$(wslpath -u {quoted})
			elif command -v cygpath >/dev/null 2>&1; then
			    {variable}=$(cygpath -u {quoted})
			else
			    {variable}={quoted}
			fi
			""";
	}

	private static string CreatePathWrapper(
		string integrationRoot,
		string unifiedHost,
		string shell)
	{
		if (shell == "powershell" && OperatingSystem.IsWindows())
		{
			var wrapper = Path.Combine(integrationRoot, "devprojex.cmd");
			var dotnetHost = ResolveDotNetHost();
			var managedHost = Path.ChangeExtension(unifiedHost, ".dll");
			var batchInvocation = File.Exists(managedHost)
				? string.Join(
					"\r\n",
					$"set \"DPX_DOTNET={EscapeBatchValue(dotnetHost)}\"",
					$"set \"DPX_HOST={EscapeBatchValue(managedHost)}\"",
					"\"%DPX_DOTNET%\" \"%DPX_HOST%\" %*")
				: string.Join(
					"\r\n",
					$"set \"DPX_HOST={EscapeBatchValue(unifiedHost)}\"",
					"\"%DPX_HOST%\" %*");
			File.WriteAllText(
				wrapper,
				string.Join(
					"\r\n",
					"@echo off",
					"setlocal DisableDelayedExpansion",
					"set \"DEVPROJEX_TERMINAL_HOST=1\"",
					batchInvocation,
					"exit /b %ERRORLEVEL%",
					string.Empty),
				Utf8WithoutBom);
			return wrapper;
		}

		var unixWrapper = Path.Combine(integrationRoot, "devprojex");
		string invocation;
		if (OperatingSystem.IsWindows())
		{
			var managedHost = QuotePosix(Path.ChangeExtension(unifiedHost, ".dll"));
			if (File.Exists(Path.ChangeExtension(unifiedHost, ".dll")))
			{
				var dotnetHost = QuotePosix(ResolveDotNetHost());
				invocation =
					$"""
					if command -v wslpath >/dev/null 2>&1; then
					    dotnet_host=$(wslpath -u {dotnetHost})
					elif command -v cygpath >/dev/null 2>&1; then
					    dotnet_host=$(cygpath -u {dotnetHost})
					else
					    dotnet_host={dotnetHost}
					fi
					exec "$dotnet_host" {managedHost} "$@"
					""";
			}
			else
			{
				var applicationHost = QuotePosix(unifiedHost);
				invocation =
					$"""
					if command -v wslpath >/dev/null 2>&1; then
					    application_host=$(wslpath -u {applicationHost})
					elif command -v cygpath >/dev/null 2>&1; then
					    application_host=$(cygpath -u {applicationHost})
					else
					    application_host={applicationHost}
					fi
					exec "$application_host" "$@"
					""";
			}
		}
		else
		{
			invocation = $"exec {QuotePosix(unifiedHost)} \"$@\"";
		}

		File.WriteAllText(
			unixWrapper,
			$"""
			#!/bin/sh
			export DEVPROJEX_TERMINAL_HOST=1
			{invocation}
			""".ReplaceLineEndings("\n"),
			Utf8WithoutBom);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				unixWrapper,
				UnixFileMode.UserRead |
				UnixFileMode.UserWrite |
				UnixFileMode.UserExecute);
		}
		return unixWrapper;
	}

	private static async Task<ShellProcessResult> RunUnifiedHostAsync(
		string unifiedHost,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		ProcessStartInfo startInfo;
		if (OperatingSystem.IsWindows() &&
		    File.Exists(Path.ChangeExtension(unifiedHost, ".dll")))
		{
			startInfo = CreateRedirectedStartInfo(ResolveDotNetHost());
			startInfo.ArgumentList.Add(Path.ChangeExtension(unifiedHost, ".dll"));
		}
		else
		{
			startInfo = CreateRedirectedStartInfo(unifiedHost);
		}
		startInfo.Environment["DEVPROJEX_TERMINAL_HOST"] = "1";
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		return await RunProcessAsync(startInfo, cancellationToken);
	}

	private static async Task<ShellProcessResult> RunProcessAsync(
		ProcessStartInfo startInfo,
		CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));
		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), $"Could not start {startInfo.FileName}.");
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		process.StandardInput.Close();
		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			TryKill(process);
			throw new TimeoutException(
				$"{startInfo.FileName} completion integration timed out. " +
				$"stdout=[{await standardOutput}] stderr=[{await standardError}]");
		}
		return new ShellProcessResult(
			process.ExitCode,
			await standardOutput,
			await standardError);
	}

	private static ProcessStartInfo CreateRedirectedStartInfo(string executable) =>
		new()
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = Utf8WithoutBom,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};

	private static string ResolveShellOrSkip(string shell)
	{
		var executableNames = shell == "powershell"
			? new[] { "pwsh", "powershell" }
			: new[] { shell };
		foreach (var executableName in executableNames)
		{
			if (TryResolveExecutable(executableName, out var executable))
				return executable;
		}

		Assert.Skip($"{shell} is not installed on this test host.");
		return string.Empty;
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
			         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			foreach (var extension in extensions.Prepend(string.Empty))
			{
				var candidate = Path.Combine(directory, executable + extension);
				if (!File.Exists(candidate))
					continue;
				resolved = candidate;
				return true;
			}
		}

		resolved = string.Empty;
		return false;
	}

	private static string ResolveDotNetHost() =>
		Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } configured
			? configured
			: TryResolveExecutable("dotnet", out var resolved)
				? resolved
				: "dotnet";

	private static string EscapeBatchValue(string value) =>
		value.Replace("%", "%%", StringComparison.Ordinal);

	private static string QuotePosix(string value) =>
		"'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Timeout cleanup is best effort.
		}
	}

	private sealed record ShellProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);
}
