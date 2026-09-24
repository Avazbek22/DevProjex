using System.IO.Compression;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class ExportProjectContentReadTests
{
	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	[InlineData("zip-stdout")]
	public async Task ProjectCopyDoesNotReadContentForUnusedAnalysisMetrics(string destinationKind)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = new string('a', 128 * 1024) + "\r\n";
		workspace.WriteFile("project/src/app.cs", source);
		workspace.WriteFile("project/README.md", "# Пример\r\nProject copy\n");
		var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["src/app.cs"] = Encoding.UTF8.GetBytes(source),
			["README.md"] = Encoding.UTF8.GetBytes("# Пример\r\nProject copy\n"),
			["assets.bin"] = [0x00, 0xFF, 0x7F, 0x01]
		};
		await File.WriteAllBytesAsync(
			Path.Combine(project, "assets.bin"),
			expectedFiles["assets.bin"],
			TestContext.Current.CancellationToken);
		workspace.CreateDirectory("project/empty");
		var output = destinationKind == "zip-stdout"
			? "-"
			: Path.Combine(workspace.Path, destinationKind == "folder" ? "copy" : "copy.zip");
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(project, output, destinationKind, environment, appData.Path);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Equal(
			destinationKind == "zip-stdout" ? string.Empty : Path.GetFullPath(output) + Environment.NewLine,
			environment.StandardOutput);
		if (destinationKind == "folder")
		{
			Assert.True(Directory.Exists(Path.Combine(output, "empty")));
			Assert.Equal(expectedFiles.Count, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
			foreach (var (relativePath, expectedBytes) in expectedFiles)
			{
				Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(
					Path.Combine(output, relativePath),
					TestContext.Current.CancellationToken));
			}
		}
		else
		{
			await using var archiveSource = destinationKind == "zip-stdout"
				? (Stream)new MemoryStream(environment.StandardOutputBytes, writable: false)
				: File.OpenRead(output);
			using var archive = new ZipArchive(archiveSource, ZipArchiveMode.Read);
			Assert.Contains(archive.Entries, static entry => entry.FullName == "project/empty/");
			Assert.Equal(expectedFiles.Count, archive.Entries.Count(static entry => entry.Name.Length > 0));
			foreach (var (relativePath, expectedBytes) in expectedFiles)
			{
				var entry = Assert.Single(archive.Entries, entry => entry.FullName == $"project/{relativePath}");
				await using var entrySource = entry.Open();
				using var copiedBytes = new MemoryStream();
				await entrySource.CopyToAsync(copiedBytes, TestContext.Current.CancellationToken);
				Assert.Equal(expectedBytes, copiedBytes.ToArray());
			}
		}
		Assert.Equal(0, diagnostics.FullFileReads);
		Assert.Equal(0, diagnostics.FullFileReadBytes);
	}

	[Theory]
	[InlineData("folder")]
	[InlineData("zip")]
	public async Task DryRunKeepsContentMetricsWithoutCreatingTheDestination(string destinationKind)
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var first = workspace.WriteFile("project/A.txt", "first file\n");
		var second = workspace.WriteFile("project/B.txt", "second file\n");
		var expectedMetrics = await ProjectContentMetricsCalculator.CalculateAsync(
			new FileContentAnalyzer(),
			[first, second],
			TestContext.Current.CancellationToken);
		var output = Path.Combine(workspace.Path, destinationKind == "folder" ? "copy" : "copy.zip");
		var environment = new TestTerminalEnvironment();
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var exitCode = await RunAsync(project, output, destinationKind, environment, appData.Path, dryRun: true);
		var diagnostics = measurement.Capture();

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains($"estimated tokens: {expectedMetrics.Tokens}", environment.StandardError, StringComparison.Ordinal);
		Assert.False(Path.Exists(output));
		Assert.Equal(2, diagnostics.FullFileReads);
		Assert.Equal(new FileInfo(first).Length + new FileInfo(second).Length, diagnostics.FullFileReadBytes);
	}

	private static Task<int> RunAsync(
		string project,
		string output,
		string destinationKind,
		TestTerminalEnvironment environment,
		string appDataPath,
		bool dryRun = false)
	{
		var arguments = new List<string>
		{
			"export", "project", project,
			"--as", destinationKind == "folder" ? "folder" : "zip",
			"-o", output,
			"--git-mode", "none",
			"--exclude", "none",
			"--progress", "never",
			"--language", "en"
		};
		if (dryRun)
			arguments.Add("--dry-run");
		return new TerminalApplication(environment, new TerminalServiceFactory(() => appDataPath))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}
}
