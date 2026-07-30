using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class PublishedSingleFileExtractionProcessTests
{
	private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

	[Fact]
	public async Task WritableDefaultExtractionSupportsColdAndWarmDirectCli()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("default/home");
		var temporary = workspace.CreateDirectory("default/temp");
		var dataRoot = workspace.CreateDirectory("default/data");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			bundleExtractionRoot: null);

		var cold = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		var expectedOutput = AssertCleanVersionResult(cold);
		var defaultExtractionRoot = OperatingSystem.IsWindows()
			? Path.Combine(temporary, ".net")
			: Path.Combine(home, ".net");
		var coldSnapshot = CaptureExtractionSnapshot(defaultExtractionRoot);

		var warm = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		AssertCleanVersionResult(warm, expectedOutput);
		var warmSnapshot = CaptureExtractionSnapshot(defaultExtractionRoot);

		Assert.Equal(coldSnapshot, warmSnapshot);
	}

	[Fact]
	public async Task ExplicitExtractionRootSupportsUnsetHome()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("unset-home/home");
		var temporary = workspace.CreateDirectory("unset-home/temp");
		var dataRoot = workspace.CreateDirectory("unset-home/data");
		var extractionRoot = workspace.CreateDirectory("unset-home/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		environment["HOME"] = null;

		var result = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);

		AssertCleanVersionResult(result);
		CaptureExtractionSnapshot(extractionRoot);
		Assert.False(Directory.Exists(Path.Combine(temporary, ".net")));
	}

	[Fact]
	public async Task ExplicitExtractionRootBypassesWindowsTempAndTmpThatAreFiles()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip(
				"This regression covers the Windows TEMP/TMP bundle-host fallback.");
			return;
		}

		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("invalid-windows-temp/home");
		var fallbackTemporaryDirectory = workspace.CreateDirectory(
			"invalid-windows-temp/fallback");
		var unusableTempPath = workspace.WriteFile(
			"invalid-windows-temp/temp-is-a-file",
			"TEMP sentinel\n");
		var unusableTmpPath = workspace.WriteFile(
			"invalid-windows-temp/tmp-is-a-file",
			"TMP sentinel\n");
		var dataRoot = workspace.CreateDirectory("invalid-windows-temp/data");
		var extractionRoot = workspace.CreateDirectory(
			"invalid-windows-temp/extraction");
		var environment = CreateEnvironment(
			home,
			fallbackTemporaryDirectory,
			dataRoot,
			extractionRoot);
		environment["TEMP"] = unusableTempPath;
		environment["TMP"] = unusableTmpPath;

		var result = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);

		AssertCleanVersionResult(result);
		CaptureExtractionSnapshot(extractionRoot);
		Assert.Equal(
			"TEMP sentinel\n",
			File.ReadAllText(unusableTempPath));
		Assert.Equal(
			"TMP sentinel\n",
			File.ReadAllText(unusableTmpPath));
	}

	[Fact]
	public async Task ParallelColdStartAndWarmReuseShareOneCompleteExtraction()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("parallel/home");
		var temporary = workspace.CreateDirectory("parallel/temp");
		var dataRoot = workspace.CreateDirectory("parallel/data");
		var extractionRoot = workspace.CreateDirectory("parallel/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);

		var coldStarts = Enumerable.Range(0, 6)
			.Select(_ => RunVersionAsync(
				application,
				environment,
				TestContext.Current.CancellationToken))
			.ToArray();
		var coldResults = await Task.WhenAll(coldStarts);
		var expectedOutput = AssertCleanVersionResult(coldResults[0]);
		foreach (var result in coldResults.Skip(1))
			AssertCleanVersionResult(result, expectedOutput);
		var coldSnapshot = CaptureExtractionSnapshot(extractionRoot);

		var warm = await RunVersionAsync(
			application,
			environment,
			TestContext.Current.CancellationToken);
		AssertCleanVersionResult(warm, expectedOutput);
		var warmSnapshot = CaptureExtractionSnapshot(extractionRoot);

		Assert.Equal(coldSnapshot, warmSnapshot);
	}

	[Fact]
	public async Task ExplicitExtractionRootBypassesReadOnlyUnixHomeAndTemp()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip(
				"Windows directory readonly attributes do not deny writes. " +
				"The Windows default %TEMP% path and explicit extraction root are " +
				"covered without unreliable hosted-runner ACL mutation.");
			return;
		}

		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("readonly/home");
		var temporary = workspace.CreateDirectory("readonly/temp");
		var dataRoot = workspace.CreateDirectory("readonly/data");
		var extractionRoot = workspace.CreateDirectory("readonly/extraction");
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		var readOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
		var writableMode =
			UnixFileMode.UserRead |
			UnixFileMode.UserWrite |
			UnixFileMode.UserExecute;

		File.SetUnixFileMode(home, readOnlyMode);
		File.SetUnixFileMode(temporary, readOnlyMode);
		try
		{
			var result = await RunVersionAsync(
				application,
				environment,
				TestContext.Current.CancellationToken);

			AssertCleanVersionResult(result);
			CaptureExtractionSnapshot(extractionRoot);
			Assert.Empty(Directory.EnumerateFileSystemEntries(home));
			Assert.Empty(Directory.EnumerateFileSystemEntries(temporary));
		}
		finally
		{
			File.SetUnixFileMode(home, writableMode);
			File.SetUnixFileMode(temporary, writableMode);
		}
	}

	[Fact]
	public async Task EmptyInlineAssignmentCannotConsumeHelpOrWriteAnArtifact()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var home = workspace.CreateDirectory("integrity/home");
		var temporary = workspace.CreateDirectory("integrity/temp");
		var dataRoot = workspace.CreateDirectory("integrity/data");
		var extractionRoot = workspace.CreateDirectory("integrity/extraction");
		var project = workspace.CreateDirectory("integrity/source");
		File.WriteAllText(
			Path.Combine(project, "App.cs"),
			"class App {}\n",
			new UTF8Encoding(false));
		var environment = CreateEnvironment(
			home,
			temporary,
			dataRoot,
			extractionRoot);
		var accidentalArtifact = Path.Combine(workspace.Path, "--help");

		var result = await RunAsync(
			application,
			[
				"profile", "export", project,
				"--profile", "standard",
				"--output=",
				"--help",
				"--language", "en"
			],
			environment,
			workspace.Path,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]",
			result.StandardError,
			StringComparison.Ordinal);
		Assert.False(File.Exists(accidentalArtifact));
		Assert.Equal(
			["App.cs"],
			Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories)
				.Select(path => Path.GetRelativePath(project, path))
				.ToArray());
	}

	private static string GetPublishedSingleFileOrSkip()
	{
		var explicitPath = Environment.GetEnvironmentVariable(
			"DEVPROJEX_TUI_TEST_BINARY");
		if (string.IsNullOrWhiteSpace(explicitPath))
		{
			Assert.Skip(
				"Single-file extraction requires a native published artifact supplied " +
				"through DEVPROJEX_TUI_TEST_BINARY.");
		}

		var application = Path.GetFullPath(explicitPath);
		Assert.True(
			File.Exists(application),
			$"Published application does not exist: {application}");
		Assert.False(
			File.Exists(Path.ChangeExtension(application, ".runtimeconfig.json")),
			$"The extraction gate requires a single-file artifact: {application}");
		Assert.False(
			File.Exists(Path.ChangeExtension(application, ".deps.json")),
			$"The extraction gate requires a single-file artifact: {application}");
		return application;
	}

	private static Dictionary<string, string?> CreateEnvironment(
		string home,
		string temporary,
		string dataRoot,
		string? bundleExtractionRoot) =>
		new(StringComparer.OrdinalIgnoreCase)
		{
			["HOME"] = home,
			["USERPROFILE"] = home,
			["TEMP"] = temporary,
			["TMP"] = temporary,
			["TMPDIR"] = temporary,
			["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = bundleExtractionRoot,
			["DOTNET_NOLOGO"] = "1",
			["CI"] = "1",
			[InvocationEnvironment.TerminalHostVariable] = "1",
			[InvocationEnvironment.InternalDataRootVariable] = dataRoot
		};

	private static async Task<VersionProcessResult> RunVersionAsync(
		string application,
		IReadOnlyDictionary<string, string?> environment,
		CancellationToken cancellationToken) =>
		await RunAsync(
			application,
			["--version"],
			environment,
			workingDirectory: null,
			cancellationToken);

	private static async Task<VersionProcessResult> RunAsync(
		string application,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string?> environment,
		string? workingDirectory,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = application,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		foreach (var entry in environment)
		{
			if (entry.Value is null)
				startInfo.Environment.Remove(entry.Key);
			else
				startInfo.Environment[entry.Key] = entry.Value;
		}

		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), $"Could not start {application}.");
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken);
		timeout.CancelAfter(ProcessTimeout);
		try
		{
			var standardOutputTask =
				process.StandardOutput.ReadToEndAsync(timeout.Token);
			var standardErrorTask =
				process.StandardError.ReadToEndAsync(timeout.Token);
			await process.WaitForExitAsync(timeout.Token);
			return new VersionProcessResult(
				process.ExitCode,
				await standardOutputTask,
				await standardErrorTask);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}

			throw new TimeoutException(
				$"Published direct command did not exit within {ProcessTimeout}.");
		}
	}

	private static string AssertCleanVersionResult(
		VersionProcessResult result,
		string? expectedOutput = null)
	{
		Assert.True(
			result.ExitCode == 0,
			$"Published --version exited with {result.ExitCode}. stderr=[{result.StandardError}]");
		Assert.Empty(result.StandardError);
		Assert.DoesNotContain('\u001b', result.StandardOutput);
		var normalized = result.StandardOutput.ReplaceLineEndings("\n");
		var lines = normalized.Split(
			'\n',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		Assert.Single(lines);
		Assert.True(
			Version.TryParse(lines[0], out _),
			$"Published --version returned an invalid version: [{result.StandardOutput}]");
		Assert.EndsWith("\n", normalized, StringComparison.Ordinal);
		if (expectedOutput is not null)
			Assert.Equal(expectedOutput, normalized);
		return normalized;
	}

	private static ExtractedFile[] CaptureExtractionSnapshot(string extractionRoot)
	{
		Assert.True(
			Directory.Exists(extractionRoot),
			$"Bundle extraction root was not created: {extractionRoot}");
		var files = Directory
			.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
			.Select(path => new ExtractedFile(
				Path.GetRelativePath(extractionRoot, path)
					.Replace('\\', '/'),
				new FileInfo(path).Length))
			.OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
			.ToArray();
		Assert.NotEmpty(files);
		return files;
	}

	private sealed record VersionProcessResult(
		int ExitCode,
		string StandardOutput,
		string StandardError);

	private sealed record ExtractedFile(string RelativePath, long Length);
}
