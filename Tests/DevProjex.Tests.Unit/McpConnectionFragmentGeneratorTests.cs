using System.Diagnostics;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class McpConnectionFragmentGeneratorTests
{
	public static TheoryData<int, string, string> StructuredFragmentCases
	{
		get
		{
			var cases = new TheoryData<int, string, string>();
			foreach (var mode in Enum.GetValues<McpConnectionMode>())
			{
				foreach (var (executable, root) in PathCases())
					cases.Add((int)mode, executable, root);
			}
			return cases;
		}
	}

	public static TheoryData<int> SetupStates
	{
		get
		{
			var states = new TheoryData<int>();
			foreach (var state in Enum.GetValues<TerminalCommandSetupState>())
				states.Add((int)state);
			return states;
		}
	}

	[Theory]
	[InlineData((int)McpConnectionClient.ClaudeCode, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.ClaudeCode, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.Codex, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.Codex, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.Cursor, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.Cursor, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.VsCode, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.VsCode, (int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionClient.Json, (int)McpConnectionMode.Live)]
	[InlineData((int)McpConnectionClient.Json, (int)McpConnectionMode.Standard)]
	public void Generate_PreservesAbsoluteUnicodePathsAndMode(int clientValue, int modeValue)
	{
		const string executable = @"C:\Program Files\DevProjex\DevProjex.exe";
		const string root = @"C:\Проекты\Мой проект";
		var client = (McpConnectionClient)clientValue;
		var mode = (McpConnectionMode)modeValue;

		var fragment = McpConnectionFragmentGenerator.Generate(client, mode, executable, root);

		Assert.Contains("DevProjex", fragment, StringComparison.Ordinal);
		Assert.Contains("Проекты", fragment, StringComparison.Ordinal);
		Assert.Equal(mode == McpConnectionMode.Live, fragment.Contains("--live", StringComparison.Ordinal));
		if (client == McpConnectionClient.Codex)
		{
			Assert.Contains("C:\\\\Program Files\\\\DevProjex", fragment, StringComparison.Ordinal);
		}
		else if (client is McpConnectionClient.Cursor or McpConnectionClient.VsCode or McpConnectionClient.Json)
		{
			using var document = JsonDocument.Parse(fragment);
			var containerName = client == McpConnectionClient.VsCode ? "servers" : "mcpServers";
			var server = document.RootElement.GetProperty(containerName).GetProperty("devprojex");
			Assert.Equal(client == McpConnectionClient.VsCode, server.TryGetProperty("type", out _));
			Assert.Equal(executable, server.GetProperty("command").GetString());
			Assert.Contains(root, server.GetProperty("args").EnumerateArray().Select(static value => value.GetString()));
		}
	}

	[Fact]
	public void Generate_UsesThePublishedClientFormats()
	{
		const string executable = "/Applications/DevProjex.app/Contents/MacOS/DevProjex";
		const string root = "/Users/me/My Project";

		Assert.Equal(
			"cd \"/Users/me/My Project\" && claude mcp add --scope local devprojex -- \"/Applications/DevProjex.app/Contents/MacOS/DevProjex\" mcp --root \"/Users/me/My Project\" --live",
			McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				executable,
				root));
		Assert.Equal(
			"[mcp_servers.devprojex]" + Environment.NewLine +
			"command = \"/Applications/DevProjex.app/Contents/MacOS/DevProjex\"" + Environment.NewLine +
			"args = [\"mcp\", \"--root\", \"/Users/me/My Project\"]",
			McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.Codex,
				McpConnectionMode.Standard,
				executable,
				root));
	}

	[Fact]
	public void Generate_ClaudeCodeWindowsPowerShellUsesSeparateCommands()
	{
		var fragment = McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.ClaudeCode,
			McpConnectionMode.Standard,
			@"C:\Program Files\DevProjex\DevProjex.exe",
			@"C:\Projects\My Project");

		var lines = fragment.Split(Environment.NewLine, StringSplitOptions.None);
		Assert.Equal(2, lines.Length);
		Assert.Equal("cd \"C:\\Projects\\My Project\"", lines[0]);
		Assert.Equal(
			"claude mcp add --scope local devprojex -- \"C:\\Program Files\\DevProjex\\DevProjex.exe\" mcp --root \"C:\\Projects\\My Project\"",
			lines[1]);
		Assert.DoesNotContain("&&", fragment, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Generate_ClaudeCodeWindowsFragmentRunsInWindowsPowerShell51()
	{
		if (!OperatingSystem.IsWindows())
			return;

		const string executable = @"C:\Program Files\DevProjex\DevProjex.exe";
		const string root = @"C:\Projects\My Project";
		var fragment = McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.ClaudeCode,
			McpConnectionMode.Standard,
			executable,
			root);

		var parsedFragments = await ParseWithPowerShellAsync([fragment], "powershell.exe");

		var parsed = Assert.Single(parsedFragments);
		// The PowerShell function shim consumes --; the fragment assertion above checks the literal separator.
		Assert.Equal(["mcp", "add", "--scope", "local", "devprojex", executable, "mcp", "--root", root], parsed);
	}

	[Fact]
	public void Generate_AppImageExtractionFallbackCarriesTheRequiredEnvironment()
	{
		var previous = Environment.GetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", "1");

			var json = McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.Json,
				McpConnectionMode.Live,
				"/home/me/DevProjex.AppImage",
				"/home/me/project");
			var codex = McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.Codex,
				McpConnectionMode.Live,
				"/home/me/DevProjex.AppImage",
				"/home/me/project");
			var claude = McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.ClaudeCode,
				McpConnectionMode.Live,
				"/home/me/DevProjex.AppImage",
				"/home/me/project");

			using var document = JsonDocument.Parse(json);
			Assert.Equal(
				"1",
				document.RootElement.GetProperty("mcpServers").GetProperty("devprojex")
					.GetProperty("env").GetProperty("APPIMAGE_EXTRACT_AND_RUN").GetString());
			Assert.Contains("APPIMAGE_EXTRACT_AND_RUN = \"1\"", codex, StringComparison.Ordinal);
			Assert.Contains("-e APPIMAGE_EXTRACT_AND_RUN=1", claude, StringComparison.Ordinal);
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", previous);
		}
	}

	[Theory]
	[MemberData(nameof(StructuredFragmentCases))]
	public void Generate_JsonClientsRoundTripEveryPathCharacter(
		int modeValue,
		string executable,
		string root)
	{
		var mode = (McpConnectionMode)modeValue;
		foreach (var client in new[]
				 {
					 McpConnectionClient.Cursor,
					 McpConnectionClient.VsCode,
					 McpConnectionClient.Json
				 })
		{
			var fragment = McpConnectionFragmentGenerator.Generate(
				client,
				mode,
				executable,
				root);

			using var document = JsonDocument.Parse(fragment);
			var containerName = client == McpConnectionClient.VsCode ? "servers" : "mcpServers";
			var server = document.RootElement.GetProperty(containerName).GetProperty("devprojex");
			if (client == McpConnectionClient.VsCode)
				Assert.Equal("stdio", server.GetProperty("type").GetString());
			else
				Assert.False(server.TryGetProperty("type", out _));
			AssertConnection(
				server.GetProperty("command").GetString(),
				server.GetProperty("args").EnumerateArray().Select(static value => value.GetString()).ToArray(),
				mode,
				executable,
				root);
		}
	}

	[Theory]
	[MemberData(nameof(StructuredFragmentCases))]
	public void Generate_TomlRoundTripsEveryPathCharacter(
		int modeValue,
		string executable,
		string root)
	{
		var mode = (McpConnectionMode)modeValue;
		var fragment = McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.Codex,
			mode,
			executable,
			root);
		var model = Assert.IsType<TomlTable>(TomlSerializer.Deserialize<TomlTable>(fragment));
		var servers = Assert.IsType<TomlTable>(model["mcp_servers"]);
		var server = Assert.IsType<TomlTable>(servers["devprojex"]);
		var arguments = Assert.IsType<TomlArray>(server["args"])
			.Cast<object>()
			.Select(static value => Assert.IsType<string>(value))
			.Cast<string?>()
			.ToArray();
		AssertConnection(
			Assert.IsType<string>(server["command"]),
			arguments,
			mode,
			executable,
			root);
	}

	[Theory]
	[InlineData((int)McpConnectionMode.Standard)]
	[InlineData((int)McpConnectionMode.Live)]
	public async Task Generate_ClaudeCodeRoundTripsThroughTheNativeShell(int modeValue)
	{
		var mode = (McpConnectionMode)modeValue;
		var pathCases = NativeShellPathCases().ToArray();
		var fragments = pathCases
			.Select(pathCase => McpConnectionFragmentGenerator.Generate(
				McpConnectionClient.ClaudeCode,
				mode,
				pathCase.Executable,
				pathCase.Root))
			.ToArray();
		var parsedFragments = OperatingSystem.IsWindows()
			? await ParseWithPowerShellAsync(fragments)
			: await ParseWithPosixShellAsync(fragments);

		Assert.Equal(pathCases.Length, parsedFragments.Count);
		for (var index = 0; index < pathCases.Length; index++)
		{
			var (executable, root) = pathCases[index];
			var parsed = parsedFragments[index];

			Assert.Equal(["mcp", "add", "--scope", "local", "devprojex"], parsed.Take(5));
			var connectionArguments = parsed.Skip(5).ToList();
			if (connectionArguments.FirstOrDefault() == "--")
				connectionArguments.RemoveAt(0);
			AssertConnection(
				connectionArguments[0],
				connectionArguments.Skip(1).Cast<string?>().ToArray(),
				mode,
				executable,
				root);
		}
	}

	[Theory]
	[InlineData("DevProjex.exe", "/project")]
	[InlineData("/opt/DevProjex", "project")]
	public void Generate_RejectsRelativePaths(string executable, string root)
	{
		Assert.Throws<ArgumentException>(() => McpConnectionFragmentGenerator.Generate(
			McpConnectionClient.Json,
			McpConnectionMode.Live,
			executable,
			root));
	}

	[Fact]
	public void Resolve_UsesTheWindowsStoreExecutionAlias()
	{
		var snapshot = Snapshot(
			TerminalCommandSetupState.ManagedByOperatingSystem,
			targetExecutablePath: null,
			CommandLineExecutableAliases.WindowsStoreAlias);

		var path = McpConnectionExecutablePathResolver.Resolve(
			snapshot,
			TerminalCommandHostPlatform.Windows,
			@"C:\Users\me\AppData\Local");

		Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\devprojex.exe", path);
	}

	[Theory]
	[InlineData((int)TerminalCommandHostPlatform.Windows, @"D:\Tools\DevProjex.exe")]
	[InlineData((int)TerminalCommandHostPlatform.Linux, "/home/me/Apps/DevProjex.AppImage")]
	[InlineData((int)TerminalCommandHostPlatform.MacOS, "/Applications/DevProjex.app/Contents/MacOS/DevProjex")]
	public void Resolve_PreservesTheInstalledArtifactPath(int platformValue, string targetPath)
	{
		var snapshot = Snapshot(TerminalCommandSetupState.Installed, targetPath);

		Assert.Equal(
			targetPath,
			McpConnectionExecutablePathResolver.Resolve(
				snapshot,
				(TerminalCommandHostPlatform)platformValue));
	}

	[Theory]
	[MemberData(nameof(SetupStates))]
	public void Resolve_PreservesTheCurrentArtifactForEveryNonStoreState(int stateValue)
	{
		const string targetPath = "/opt/devprojex/DevProjex";
		var state = (TerminalCommandSetupState)stateValue;
		var snapshot = Snapshot(state, targetPath);

		Assert.Equal(
			targetPath,
			McpConnectionExecutablePathResolver.Resolve(
				snapshot,
				TerminalCommandHostPlatform.Other));
	}

	[Theory]
	[MemberData(nameof(SetupStates))]
	public void Resolve_RejectsEveryStateWithoutAnArtifactPathOutsideTheWindowsStore(int stateValue)
	{
		var state = (TerminalCommandSetupState)stateValue;
		var snapshot = Snapshot(state, targetExecutablePath: null);

		var exception = Assert.Throws<InvalidOperationException>(() =>
			McpConnectionExecutablePathResolver.Resolve(
				snapshot,
				TerminalCommandHostPlatform.Other));

		Assert.Contains("executable path is unavailable", exception.Message, StringComparison.Ordinal);
	}

	private static IEnumerable<(string Executable, string Root)> PathCases()
	{
		yield return (@"C:\Program Files\DevProjex\DevProjex.exe", @"C:\Projects\My Project");
		yield return ("/opt/Приложения/DevProjex", "/Проекты/Мой проект");
		yield return ("/opt/quo\"te'd/DevProjex", "/projects/quo\"te'd/root");
		yield return (@"/opt/back\slash/DevProjex", @"/projects/back\slash/root");
		yield return ("/opt/$release/DevProjex", "/projects/$release/root");
		yield return ("/opt/100%/DevProjex", "/projects/100%/root");
		yield return ("/" + new string('e', 249), "/" + new string('r', 249));
	}

	private static IEnumerable<(string Executable, string Root)> NativeShellPathCases()
	{
		if (!OperatingSystem.IsWindows())
			return PathCases().Where(static value => value.Executable.StartsWith('/'));

		return
		[
			(@"C:\Program Files\DevProjex\DevProjex.exe", @"C:\Projects\My Project"),
			(@"C:\Приложения\DevProjex.exe", @"C:\Проекты\Мой проект"),
			("C:\\quo\"te'd\\DevProjex.exe", "C:\\quo\"te'd\\root"),
			(@"C:\back\slash\DevProjex.exe", @"C:\back\slash\root"),
			(@"C:\$release\DevProjex.exe", @"C:\$release\root"),
			(@"C:\100%\DevProjex.exe", @"C:\100%\root"),
			("C:\\" + new string('e', 247), "C:\\" + new string('r', 247))
		];
	}

	private static void AssertConnection(
		string? actualExecutable,
		IReadOnlyList<string?> actualArguments,
		McpConnectionMode mode,
		string expectedExecutable,
		string expectedRoot)
	{
		Assert.Equal(expectedExecutable, actualExecutable);
		Assert.Equal("mcp", actualArguments[0]);
		Assert.Equal("--root", actualArguments[1]);
		Assert.Equal(expectedRoot, actualArguments[2]);
		Assert.Equal(
			mode == McpConnectionMode.Live ? ["--live"] : [],
			actualArguments.Skip(3).Select(static value => value ?? string.Empty));
	}

	private static async Task<IReadOnlyList<string[]>> ParseWithPowerShellAsync(
		IReadOnlyList<string> fragments,
		string shell = "pwsh")
	{
		const string script = "Remove-Item Alias:cd -ErrorAction SilentlyContinue; " +
							  "Set-Item Function:cd -Value { param([string]$Path) }; " +
							  "$payload = ConvertFrom-Json $env:DPX_CONNECTION_FRAGMENT; " +
							  "Set-Item -Path ('Function:' + $payload.command) " +
							  "-Value { $script:capturedArgs = @($args) }; " +
							  "$results = [System.Collections.Generic.List[object]]::new(); " +
							  "foreach ($fragment in $payload.fragments) { " +
							  "$script:capturedArgs = $null; " +
							  "Invoke-Expression $fragment; " +
							  "$results.Add([object[]]$script:capturedArgs) }; " +
							  "[Console]::Out.Write((ConvertTo-Json -Compress -Depth 4 -InputObject $results))";
		var result = await RunShellAsync(
			shell,
			["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
			JsonSerializer.Serialize(new { command = "claude", fragments }));
		return JsonSerializer.Deserialize<string[][]>(result) ?? [];
	}

	private static async Task<IReadOnlyList<string[]>> ParseWithPosixShellAsync(
		IReadOnlyList<string> fragments)
	{
		var results = new List<string[]>(fragments.Count);
		foreach (var fragment in fragments)
			results.Add(await RunPosixShellAsync(fragment));

		return results;
	}

	private static async Task<string[]> RunPosixShellAsync(string fragment)
	{
		const string script = "cd() { :; }; claude() { printf '%s\\n' \"$@\"; }; eval \"$DPX_CONNECTION_FRAGMENT\"";
		var result = await RunShellAsync("/bin/sh", ["-c", script], fragment);
		return result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
	}

	private static async Task<string> RunShellAsync(
		string executable,
		IEnumerable<string> arguments,
		string fragment)
	{
		var startInfo = new ProcessStartInfo(executable)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment["DPX_CONNECTION_FRAGMENT"] = fragment;
		using var process = Process.Start(startInfo) ??
							throw new InvalidOperationException($"Could not start {executable}.");
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		try
		{
			await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
			throw;
		}
		var error = await standardError;
		Assert.True(process.ExitCode == 0, $"{executable} exited with {process.ExitCode}: {error}");
		return await standardOutput;
	}

	private static TerminalCommandSetupSnapshot Snapshot(
		TerminalCommandSetupState state,
		string? targetExecutablePath,
		string commandName = "devprojex") =>
		new(
			commandName,
			state,
			CommandPath: null,
			targetExecutablePath,
			InstalledTargetExecutablePath: null,
			UserBinDirectory: null,
			UserBinDirectoryIsInPath: state == TerminalCommandSetupState.ManagedByOperatingSystem,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);
}
