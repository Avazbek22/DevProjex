using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed class HeadlessHostContractTests
{
	[Fact]
	public async Task HeadlessVersionMatchesTheDesktopHostByteForByte()
	{
		var desktop = await RunAsync(PublishedApplicationHost.Desktop, ["--version"]);
		var headless = await RunAsync(PublishedApplicationHost.Headless, ["--version"]);

		Assert.Equal(0, desktop.ExitCode);
		Assert.Equal(0, headless.ExitCode);
		Assert.Equal(desktop.StandardOutputBytes, headless.StandardOutputBytes);
		Assert.Equal(desktop.StandardErrorBytes, headless.StandardErrorBytes);
	}

	[Fact]
	public async Task HeadlessTreeAnalyzeAndContextExportUseTheTerminalContract()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/Program.cs",
			"public class Program { public int Value() { var removed = 42; return removed; } }\n");

		string[] treeArguments = ["tree", project, "--git-mode", "none", "--exclude", "none"];
		var tree = await RunAsync(PublishedApplicationHost.Headless, treeArguments);
		var desktopTree = await RunAsync(PublishedApplicationHost.Desktop, treeArguments);
		Assert.Equal(0, tree.ExitCode);
		Assert.Contains("Program.cs", tree.StandardOutput, StringComparison.Ordinal);
		AssertSameContract(desktopTree, tree);

		string[] analyzeArguments =
			["analyze", project, "--format", "json", "--git-mode", "none", "--exclude", "none", "-o", "-"];
		string[] compressedArguments =
			["analyze", project, "--format", "json", "--git-mode", "none", "--exclude", "none", "--compress-code", "-o", "-"];
		var full = await RunAsync(PublishedApplicationHost.Headless, analyzeArguments);
		var desktopFull = await RunAsync(PublishedApplicationHost.Desktop, analyzeArguments);
		var compressed = await RunAsync(PublishedApplicationHost.Headless, compressedArguments);
		var desktopCompressed = await RunAsync(PublishedApplicationHost.Desktop, compressedArguments);
		Assert.Equal(0, full.ExitCode);
		Assert.Equal(0, compressed.ExitCode);
		AssertSameContract(desktopFull, full);
		AssertSameContract(desktopCompressed, compressed);
		using var fullJson = JsonDocument.Parse(full.StandardOutput);
		using var compressedJson = JsonDocument.Parse(compressed.StandardOutput);
		Assert.True(
			compressedJson.RootElement.GetProperty("metrics").GetProperty("content").GetProperty("chars").GetInt64() <
			fullJson.RootElement.GetProperty("metrics").GetProperty("content").GetProperty("chars").GetInt64());

		string[] contextArguments =
			["export", "context", project, "--git-mode", "none", "--exclude", "none", "-o", "-"];
		var context = await RunAsync(PublishedApplicationHost.Headless, contextArguments);
		var desktopContext = await RunAsync(PublishedApplicationHost.Desktop, contextArguments);
		Assert.Equal(0, context.ExitCode);
		Assert.Contains("return removed", context.StandardOutput, StringComparison.Ordinal);
		AssertSameContract(desktopContext, context);
	}

	[Fact]
	public async Task HeadlessMcpPerformsARealInitializeHandshake()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Program.cs", "public class Program {}\n");
		var startInfo = CreateStartInfo(PublishedApplicationHost.Headless);
		startInfo.WorkingDirectory = project;
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] =
			workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Headless MCP process did not start.");
		var standardError = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			timeout.Token))
		{
			var tools = await client.ListToolsAsync(options: null, timeout.Token);
			Assert.Contains(tools, static tool => tool.Name == "get_tree");
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(15), timeout.Token);
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(await standardError));
	}

	[Fact]
	public async Task HeadlessOpenFailsWithoutRestartingItself()
	{
		using var workspace = new TemporaryDirectory();
		var result = await RunAsync(
			PublishedApplicationHost.Headless,
			["open", workspace.Path, "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.DesktopUnavailable, result.ExitCode);
		Assert.Empty(result.StandardOutput);
		Assert.Contains("DPX-DESKTOP-NOT-INCLUDED", result.StandardError, StringComparison.Ordinal);
		Assert.Contains("this distribution has no desktop app", result.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Docs/Installation.md", result.StandardError, StringComparison.Ordinal);
	}

	private static async Task<ProcessResult> RunAsync(
		PublishedApplicationHost host,
		IReadOnlyList<string> arguments)
	{
		var startInfo = CreateStartInfo(host);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException($"{host} process did not start.");
		var output = ReadBytesAsync(process.StandardOutput.BaseStream, TestContext.Current.CancellationToken);
		var error = ReadBytesAsync(process.StandardError.BaseStream, TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
		return new ProcessResult(process.ExitCode, await output, await error);
	}

	private static ProcessStartInfo CreateStartInfo(PublishedApplicationHost host)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly(host));
		return startInfo;
	}

	private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken cancellationToken)
	{
		using var memory = new MemoryStream();
		await stream.CopyToAsync(memory, cancellationToken);
		return memory.ToArray();
	}

	private static void AssertSameContract(ProcessResult expected, ProcessResult actual)
	{
		Assert.Equal(expected.ExitCode, actual.ExitCode);
		Assert.Equal(expected.StandardOutputBytes, actual.StandardOutputBytes);
		Assert.Equal(expected.StandardErrorBytes, actual.StandardErrorBytes);
	}

	private sealed record ProcessResult(int ExitCode, byte[] StandardOutputBytes, byte[] StandardErrorBytes)
	{
		public string StandardOutput => System.Text.Encoding.UTF8.GetString(StandardOutputBytes);
		public string StandardError => System.Text.Encoding.UTF8.GetString(StandardErrorBytes);
	}
}
