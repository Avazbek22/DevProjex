using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessSupportsBatchReadsAndRedactsRelatedFilesInlineAndFromStorage()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}\n");
		workspace.WriteFile("project/Small.txt", $"one\ntwo {Secret}\nthree\n");
		var imports = new StringBuilder();
		for (var index = 0; index < 700; index++)
		{
			var name = $"target{index:D4}-{Secret}";
			workspace.WriteFile($"project/{name}.ts", $"export default {index};\n");
			imports.Append("import value").Append(index).Append(" from './").Append(name).AppendLine(".js';");
		}
		workspace.WriteFile("project/Main.ts", imports.ToString());

		var (process, client, errorTask, output) = await StartPublishedMcpAsync(
			workspace,
			project,
			["--allow-remote", "--remote-hosts", "github.com"]);
		try
		{
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			var getFile = Assert.Single(tools, static tool => tool.Name == "get_file");
			Assert.True(getFile.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("requests", out _));
			Assert.False(Assert.Single(tools, static tool => tool.Name == "related_files")
				.ProtocolTool.Annotations?.IdempotentHint);

			var batch = await client.CallToolAsync("get_file", new Dictionary<string, object?>
			{
				["requests"] = new object[]
				{
					new { path = "Small.txt", ranges = new[] { new { start_line = 1, end_line = 2 } } },
					new { path = "Small.txt", ranges = new[] { new { start_line = 2, end_line = 9 } } }
				}
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			var batchText = Assert.IsType<TextContentBlock>(Assert.Single(batch.Content)).Text;
			Assert.NotEqual(true, batch.IsError);
			Assert.Contains("Requests: 1.1, 2.1", batchText, StringComparison.Ordinal);
			Assert.DoesNotContain(Secret, batchText, StringComparison.Ordinal);

			var related = await client.CallToolAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "Main.ts",
				["direction"] = "dependencies"
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			var relatedText = Assert.IsType<TextContentBlock>(Assert.Single(related.Content)).Text;
			var packMatch = Regex.Match(relatedText, "Related-files result stored as '([^']+)'");
			Assert.True(packMatch.Success, relatedText);
			Assert.DoesNotContain(Secret, relatedText, StringComparison.Ordinal);

			var page = await client.CallToolAsync("read_pack", new Dictionary<string, object?>
			{
				["pack_id"] = packMatch.Groups[1].Value
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			var pageText = Assert.IsType<TextContentBlock>(Assert.Single(page.Content)).Text;
			Assert.DoesNotContain(Secret, pageText, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[", pageText, StringComparison.Ordinal);

			var denied = await client.CallToolAsync("get_tree", new Dictionary<string, object?>
			{
				["project"] = "https://gitlab.com/owner/repository.git"
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.True(denied.IsError);
			Assert.StartsWith("DPX-MCP-REMOTE-HOST-DENIED",
				Assert.IsType<TextContentBlock>(Assert.Single(denied.Content)).Text, StringComparison.Ordinal);
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}

		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
			output.WaitForSourceEofAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
		var standardError = await errorTask;
		Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {standardError}");
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
		process.Dispose();
	}

	[Fact(Timeout = 120_000)]
	public async Task RealProcessThrottlesDependencyProgressForTenThousandFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 10_000; index++)
			workspace.WriteFile($"project/File{index:D5}.txt", "value\n");
		var (process, client, errorTask, output) = await StartPublishedMcpAsync(workspace, project, []);
		var progress = new InlineProgress<ProgressNotificationValue>();
		try
		{
			var result = await client.CallToolAsync("related_files", new Dictionary<string, object?>
			{
				["path"] = "File00000.txt"
			}, progress, new RequestOptions { ProgressToken = new ProgressToken("throttle") },
				TestContext.Current.CancellationToken);

			Assert.NotEqual(true, result.IsError);
			Assert.InRange(progress.Values.Count, 2, 20);
			Assert.Equal(5f, progress.Values[0].Progress);
			Assert.Equal(100f, progress.Values[^1].Progress);
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}
		await CompletePublishedMcpAsync(process, errorTask, output);
	}

	[Fact(Timeout = 120_000)]
	public async Task RealProcessReportsWhenSearchStopsAtTheInspectedByteBudget()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 5; index++)
		{
			var prefix = index == 4 ? "needle-after-budget\n" : "clean\n";
			workspace.WriteFile($"project/Large{index}.txt", prefix + new string('x', 14 * 1024 * 1024));
		}
		var (process, client, errorTask, output) = await StartPublishedMcpAsync(workspace, project, []);
		try
		{
			var result = await client.CallToolAsync("search_project", new Dictionary<string, object?>
			{
				["pattern"] = "needle-after-budget",
				["ignore_case"] = false,
				["context_lines"] = 0
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
			Assert.NotEqual(true, result.IsError);
			Assert.DoesNotContain("Large4.txt:1:", text, StringComparison.Ordinal);
			Assert.Contains("[Search incomplete] The inspected-text byte budget was reached; " +
			                "additional selected files were not searched and match counts are partial.", text,
				StringComparison.Ordinal);
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}
		await CompletePublishedMcpAsync(process, errorTask, output);
	}

	[Fact]
	public async Task RealProcessRejectsAPortableProfileReplacedByAnOutsideDirectoryAlias()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var profiles = workspace.CreateDirectory("project/profiles");
		var outside = workspace.CreateDirectory("outside");
		workspace.WriteFile("project/Visible.txt", "visible\n");
		var profile = JsonSerializer.Serialize(new
		{
			schemaVersion = PortableProjectProfileService.CurrentSchemaVersion,
			kind = PortableProjectProfileService.DocumentKind,
			selection = new
			{
				roots = (string[]?)null,
				extensions = new[] { ".txt" },
				selectedPaths = (string[]?)null,
				gitMode = "none",
				exclusions = Array.Empty<string>(),
				hideSecrets = false,
				hidePrivateData = false
			}
		});
		File.WriteAllText(Path.Combine(profiles, "profile.json"), profile);
		File.WriteAllText(Path.Combine(outside, "profile.json"), profile);

		var (process, client, errorTask, output) = await StartPublishedMcpAsync(workspace, project, []);
		try
		{
			var before = await client.CallToolAsync("get_file", new Dictionary<string, object?>
			{
				["profile"] = "profiles/profile.json",
				["path"] = "Visible.txt"
			}, progress: null, options: null, TestContext.Current.CancellationToken);
			Assert.True(
				before.IsError != true,
				Assert.IsType<TextContentBlock>(Assert.Single(before.Content)).Text);

			Directory.Delete(profiles, recursive: true);
			CreatePortableProfileDirectoryAliasOrSkip(profiles, outside);
			var after = await client.CallToolAsync("get_file", new Dictionary<string, object?>
			{
				["profile"] = "profiles/profile.json",
				["path"] = "Visible.txt"
			}, progress: null, options: null, TestContext.Current.CancellationToken);

			Assert.True(after.IsError);
			Assert.StartsWith(
				"DPX-MCP-ROOT-VIOLATION",
				Assert.IsType<TextContentBlock>(Assert.Single(after.Content)).Text,
				StringComparison.Ordinal);
		}
		finally
		{
			process.StandardInput.Close();
			await client.DisposeAsync();
		}

		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
			output.WaitForSourceEofAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
		var standardError = await errorTask;
		Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {standardError}");
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
		process.Dispose();
	}

	private static async Task<(Process Process, McpClient Client, Task<string> Error, RecordingReadStream Output)>
		StartPublishedMcpAsync(
			TemporaryDirectory workspace,
			string project,
			IReadOnlyList<string> additionalArguments)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = project
		};
		start.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
		start.ArgumentList.Add("mcp");
		start.ArgumentList.Add("--root");
		start.ArgumentList.Add(project);
		foreach (var argument in additionalArguments)
			start.ArgumentList.Add(argument);
		start.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start.");
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var output = new RecordingReadStream(process.StandardOutput.BaseStream);
		var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, output),
			clientOptions: null,
			loggerFactory: null,
			TestContext.Current.CancellationToken);
		return (process, client, error, output);
	}

	private static async Task CompletePublishedMcpAsync(
		Process process,
		Task<string> errorTask,
		RecordingReadStream output)
	{
		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken),
			output.WaitForSourceEofAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
		var standardError = await errorTask;
		Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {standardError}");
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
		process.Dispose();
	}

	private static void CreatePortableProfileDirectoryAliasOrSkip(string linkPath, string targetPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			try
			{
				Directory.CreateSymbolicLink(linkPath, targetPath);
				return;
			}
			catch (Exception exception) when (
				exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
			{
				Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
			}
		}

		using var process = Process.Start(new ProcessStartInfo(
			"cmd.exe",
			$"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		if (process is null || !process.WaitForExit(5_000) || process.ExitCode != 0 || !Directory.Exists(linkPath))
			Assert.Skip("Windows junction creation is unavailable.");
	}

}
