using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class GeneratedCompletionNativeShellIntegrationTests
{
	private const string CompletionLine = "devprojex analyze . --format ";
	private const string CompletionShellsVariable =
		"DEVPROJEX_COMPLETION_SHELLS";
	private static readonly TimeSpan WindowsPowerShellProcessTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	[Theory]
	[InlineData("bash")]
	[InlineData("zsh")]
	[InlineData("fish")]
	[InlineData("powershell")]
	public async Task GeneratedScriptLoadsAndCompletesAgainstUnifiedHost(string shell)
	{
		SkipShellOutsideConfiguredMatrix(shell);
		var shellExecutable = ResolveShellOrSkip(shell);
		using var workspace = new TemporaryDirectory();
		await EnsureShellCanExecuteWindowsHostOrSkipAsync(
			shell,
			shellExecutable,
			workspace,
			TestContext.Current.CancellationToken);
		var unifiedHost = PublishedApplicationLocator.FindExecutable();
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
			TestContext.Current.CancellationToken);

		Assert.True(
			completed.ExitCode == 0,
			$"powershell completion exited with {completed.ExitCode}. " +
			$"stdout=[{completed.StandardOutput}] stderr=[{completed.StandardError}]");
		Assert.True(
			string.IsNullOrWhiteSpace(completed.StandardError),
			$"powershell completion wrote stderr: {completed.StandardError}");
		using var document = JsonDocument.Parse(completed.StandardOutput);
		var payload = document.RootElement;
		var lineBase64 = EncodeUtf8Base64(line);
		Assert.Equal(
			lineBase64,
			payload.GetProperty("lineBase64").GetString());
		var rawCandidateBase64 = EncodeUtf8Base64(rawCandidate);
		var candidates = payload
			.GetProperty("candidates")
			.EnumerateArray()
			.ToArray();
		var matchingCandidates = candidates
			.Where(candidate => string.Equals(
				candidate.GetProperty("listItemTextBase64").GetString(),
				rawCandidateBase64,
				StringComparison.Ordinal))
			.ToArray();
		Assert.True(
			matchingCandidates.Length == 1,
			BuildPowerShellCompletionDiagnostic(
				lineBase64,
				rawCandidateBase64,
				payload,
				candidates));
		var result = matchingCandidates[0];
		var expectedCompletionText =
			"'" + rawCandidate.Replace("'", "''", StringComparison.Ordinal) + "'";
		Assert.Equal(
			EncodeUtf8Base64(expectedCompletionText),
			result.GetProperty("completionTextBase64").GetString());
		Assert.Equal(
			rawCandidateBase64,
			result.GetProperty("listItemTextBase64").GetString());
		Assert.Equal(
			rawCandidateBase64,
			result.GetProperty("parsedValueBase64").GetString());
		Assert.Equal(0, result.GetProperty("parseErrorCount").GetInt32());
	}

	private static string BuildPowerShellCompletionDiagnostic(
		string expectedLineBase64,
		string expectedRawCandidateBase64,
		JsonElement payload,
		IReadOnlyList<JsonElement> candidates)
	{
		var candidateSummary = candidates
			.Select((candidate, index) =>
				$"#{index}(" +
				$"completion={candidate.GetProperty("completionTextBase64").GetString()}," +
				$"list={candidate.GetProperty("listItemTextBase64").GetString()}," +
				$"parsed={candidate.GetProperty("parsedValueBase64").GetString()}," +
				$"errors={candidate.GetProperty("parseErrorCount").GetInt32()})");
		return
			"PowerShell completion did not return exactly one byte-exact candidate. " +
			$"expectedLineBase64=[{expectedLineBase64}] " +
			$"actualLineBase64=[{payload.GetProperty("lineBase64").GetString()}] " +
			$"expectedListItemBase64=[{expectedRawCandidateBase64}] " +
			$"replacementIndex={payload.GetProperty("replacementIndex").GetInt32()} " +
			$"replacementLength={payload.GetProperty("replacementLength").GetInt32()} " +
			$"candidates=[{string.Join(";", candidateSummary)}]";
	}

	private static string EncodeUtf8Base64(string value) =>
		Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

	[Fact(Timeout = 120_000)]
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
		startInfo.Environment["DPX_COMPLETION_LINE_BASE64"] =
			Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
		var driver =
			"""
			$ErrorActionPreference = 'Stop'
			. $env:DPX_COMPLETION_SCRIPT
			$line = [System.Text.Encoding]::UTF8.GetString(
			    [Convert]::FromBase64String($env:DPX_COMPLETION_LINE_BASE64))
			$completion = TabExpansion2 -inputScript $line -cursorColumn $line.Length
			$candidateResults = @($completion.CompletionMatches | ForEach-Object {
			    $candidate = $_
			    $completedLine = $line.Remove(
			        $completion.ReplacementIndex,
			        $completion.ReplacementLength).Insert(
			            $completion.ReplacementIndex,
			            $candidate.CompletionText)
			    $tokens = $null
			    $parseErrors = $null
			    $ast = [System.Management.Automation.Language.Parser]::ParseInput(
			        $completedLine,
			        [ref]$tokens,
			        [ref]$parseErrors)
			    $commandAst = $ast.Find(
			        { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
			        $true)
			    $parsedValue = [string]$commandAst.CommandElements[-1].Value
			    [pscustomobject]@{
			        completionTextBase64 = [Convert]::ToBase64String(
			            [System.Text.Encoding]::UTF8.GetBytes(
			                [string]$candidate.CompletionText))
			        listItemTextBase64 = [Convert]::ToBase64String(
			            [System.Text.Encoding]::UTF8.GetBytes(
			                [string]$candidate.ListItemText))
			        parsedValueBase64 = [Convert]::ToBase64String(
			            [System.Text.Encoding]::UTF8.GetBytes($parsedValue))
			        parseErrorCount = @($parseErrors).Count
			    }
			})
			[pscustomobject]@{
			    lineBase64 = [Convert]::ToBase64String(
			        [System.Text.Encoding]::UTF8.GetBytes($line))
			    replacementIndex = $completion.ReplacementIndex
			    replacementLength = $completion.ReplacementLength
			    candidates = $candidateResults
			} | ConvertTo-Json -Compress -Depth 4
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
		return await RunProcessAsync(
			startInfo,
			cancellationToken,
			WindowsPowerShellProcessTimeout);
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
				startInfo.ArgumentList.Add("--interactive");
				if (!startInfo.Environment.TryGetValue("TERM", out var term) ||
				    string.IsNullOrWhiteSpace(term))
					startInfo.Environment["TERM"] = "xterm-256color";
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

	private static async Task EnsureShellCanExecuteWindowsHostOrSkipAsync(
		string shell,
		string shellExecutable,
		TemporaryDirectory workspace,
		CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows() || shell == "powershell")
			return;

		var windowsHost = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.Windows),
			"System32",
			"cmd.exe");
		if (!File.Exists(windowsHost))
		{
			ReportUnavailableShellCapability(
				shell,
				$"{shell} resolved to '{shellExecutable}', but the Windows-host " +
				$"interop probe executable '{windowsHost}' does not exist.");
			return;
		}

		var startInfo = CreateShellStartInfo(shell, shellExecutable);
		var probe = string.Join(
			"\n",
			BuildPosixPathAssignment(
				"windows_host_probe",
				windowsHost,
				shell),
			"\"$windows_host_probe\" /d /c exit 0");
		var probePath = workspace.WriteFile(
			$"{shell} Windows host interop probe",
			probe.ReplaceLineEndings("\n"));
		startInfo.ArgumentList.Add(ConvertDriverPathForShell(
			probePath,
			shellExecutable));

		var result = await RunProcessAsync(startInfo, cancellationToken);
		if (result.ExitCode == 0)
			return;

		var diagnostic = FormatCapabilityDiagnostic(result);
		var reason =
			$"{shell} resolved to '{shellExecutable}', but its Windows-host " +
			$"interop probe exited with code {result.ExitCode}{diagnostic}. " +
			"Native completion integration requires the shell to execute the " +
			"Windows DevProjex host.";
		ReportUnavailableShellCapability(shell, reason);
	}

	private static void ReportUnavailableShellCapability(
		string shell,
		string reason)
	{
		if (IsRequiredCompletionShell(shell))
			Assert.Fail($"{reason} The shell is required by {CompletionShellsVariable}.");
		Assert.Skip(reason);
	}

	private static string FormatCapabilityDiagnostic(ShellProcessResult result)
	{
		var detail = string.IsNullOrWhiteSpace(result.StandardError)
			? result.StandardOutput
			: result.StandardError;
		detail = detail.Trim().ReplaceLineEndings(" ");
		if (detail.Length == 0)
			return string.Empty;
		const int maximumLength = 300;
		if (detail.Length > maximumLength)
			detail = detail[..maximumLength] + "...";
		return $": {detail}";
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
				compadd() { print -rl -- "${candidates[@]}"; }
				BUFFER='{{CompletionLine}}'
				CURSOR=${#BUFFER}
				_devprojex_complete
				""",
			"fish" =>
				$"""
				{pathSetup}
				chmod +x "$wrapper_path"
				set -gx PATH "$integration_root" $PATH
				# Fish 3.7 does not expose the simulated cursor to commandline -C without a reader.
				function commandline
				    if test (count $argv) -eq 1
				        if test "$argv[1]" = "-C"
				            string length -- (builtin commandline)
				            return
				        end
				    end
				    builtin commandline $argv
				end
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
		CancellationToken cancellationToken,
		TimeSpan? processTimeout = null)
	{
		using var processTimeoutCancellation = new CancellationTokenSource(
			processTimeout ?? TimeSpan.FromSeconds(20));
		using var processCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			processTimeoutCancellation.Token);
		using var outputCancellation = new CancellationTokenSource();
		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), $"Could not start {startInfo.FileName}.");
		var standardOutput = process.StandardOutput.ReadToEndAsync(outputCancellation.Token);
		var standardError = process.StandardError.ReadToEndAsync(outputCancellation.Token);
		process.StandardInput.Close();
		try
		{
			try
			{
				await process.WaitForExitAsync(processCancellation.Token);
			}
			catch (OperationCanceledException) when (processCancellation.IsCancellationRequested)
			{
				await TerminateProcessTreeAsync(process);
				var cancelledOutput = await DrainProcessOutputAsync(
					standardOutput,
					standardError,
					outputCancellation);
				cancellationToken.ThrowIfCancellationRequested();
				throw new TimeoutException(
					$"{startInfo.FileName} completion integration timed out. " +
					$"stdout=[{cancelledOutput.StandardOutput}] " +
					$"stderr=[{cancelledOutput.StandardError}]");
			}

			var output = await DrainProcessOutputAsync(
				standardOutput,
				standardError,
				outputCancellation);
			if (!output.Completed)
			{
				throw new TimeoutException(
					$"{startInfo.FileName} completion integration output did not close. " +
					$"stdout=[{output.StandardOutput}] stderr=[{output.StandardError}]");
			}

			return new ShellProcessResult(
				process.ExitCode,
				output.StandardOutput,
				output.StandardError);
		}
		finally
		{
			await TerminateProcessTreeAsync(process);
			if (!standardOutput.IsCompleted || !standardError.IsCompleted)
			{
				outputCancellation.Cancel();
				await ObserveReadersAsync(standardOutput, standardError);
			}
		}
	}

	private static async Task<ProcessOutput> DrainProcessOutputAsync(
		Task<string> standardOutput,
		Task<string> standardError,
		CancellationTokenSource outputCancellation)
	{
		try
		{
			var output = await Task
				.WhenAll(standardOutput, standardError)
				.WaitAsync(ProcessCleanupTimeout);
			return new ProcessOutput(output[0], output[1], Completed: true);
		}
		catch (Exception exception) when (exception is
			       TimeoutException or
			       OperationCanceledException or
			       IOException or
			       ObjectDisposedException)
		{
			outputCancellation.Cancel();
			return new ProcessOutput(
				ReadCompletedOutput(standardOutput),
				ReadCompletedOutput(standardError),
				Completed: false);
		}
	}

	private static string ReadCompletedOutput(Task<string> reader) =>
		reader.Status == TaskStatus.RanToCompletion
			? reader.Result
			: "<output drain incomplete>";

	private static async Task ObserveReadersAsync(params Task<string>[] readers)
	{
		try
		{
			await Task.WhenAll(readers).WaitAsync(ProcessCleanupTimeout);
		}
		catch (Exception exception) when (exception is
			       TimeoutException or
			       OperationCanceledException or
			       IOException or
			       ObjectDisposedException)
		{
			// The streams are disposed with the process after bounded cleanup.
		}
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

		if (IsRequiredCompletionShell(shell))
		{
			Assert.Fail(
				$"{shell} is required by {CompletionShellsVariable} but is not installed " +
				"or cannot be resolved through PATH.");
		}
		Assert.Skip($"{shell} is not installed on this test host.");
		return string.Empty;
	}

	private static void SkipShellOutsideConfiguredMatrix(string shell)
	{
		var configuredShells = GetConfiguredCompletionShells();
		if (configuredShells is null || configuredShells.Contains(shell))
			return;

		// CI owns a deterministic platform matrix; incidental tools installed on a
		// runner must not silently become release requirements. Local runs without
		// the variable still exercise every shell that can be resolved through PATH.
		Assert.Skip(
			$"{shell} is not selected by the {CompletionShellsVariable} release matrix.");
	}

	private static bool IsRequiredCompletionShell(string shell)
	{
		var configuredShells = GetConfiguredCompletionShells();
		return configuredShells is not null && configuredShells.Contains(shell);
	}

	private static HashSet<string>? GetConfiguredCompletionShells()
	{
		var configured = Environment.GetEnvironmentVariable(CompletionShellsVariable);
		if (string.IsNullOrWhiteSpace(configured))
			return null;

		return configured
			.Split(
				[',', ';'],
				StringSplitOptions.RemoveEmptyEntries |
				StringSplitOptions.TrimEntries)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
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
			// Process cleanup is best effort; the caller still bounds every wait.
		}
	}

	private static async Task TerminateProcessTreeAsync(Process process)
	{
		TryKill(process);
		try
		{
			if (process.HasExited)
				return;

			using var cleanupCancellation = new CancellationTokenSource(ProcessCleanupTimeout);
			await process.WaitForExitAsync(cleanupCancellation.Token);
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       OperationCanceledException)
		{
			// Cleanup must stay bounded even if the process cannot be observed or killed.
		}
	}

	private sealed record ProcessOutput(
		string StandardOutput,
		string StandardError,
		bool Completed);

	private sealed record ShellProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);
}
