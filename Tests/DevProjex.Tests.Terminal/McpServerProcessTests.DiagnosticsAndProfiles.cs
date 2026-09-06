using System.Diagnostics;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Kernel.Models;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessSearchReportsASelectedFileThatCannotBeInspected()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Readable.txt", "readable-process-marker\n");
		var blockedPath = workspace.WriteFile("project/Blocked.txt", "blocked-process-marker\n");
		await using var blocker = new FileStream(
			blockedPath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None);
		try
		{
			using var probe = File.OpenRead(blockedPath);
			Assert.Skip("Exclusive file sharing is not enforced by this platform and file system.");
		}
		catch (IOException)
		{
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));
		var search = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "readable-process-marker|blocked-process-marker",
				["context_lines"] = 0,
				["ignore_case"] = false
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(search);

		Assert.NotEqual(true, search.IsError);
		Assert.Contains("Readable.txt:1:readable-process-marker", text, StringComparison.Ordinal);
		Assert.DoesNotContain("blocked-process-marker", text, StringComparison.Ordinal);
		Assert.Contains("[Warning DPX-MCP-PAYLOAD-TRUNCATED]", text, StringComparison.Ordinal);
		Assert.Contains("could not fully inspect 1 selected file", text, StringComparison.Ordinal);
		Assert.True(
			text.IndexOf("[Warning DPX-MCP-PAYLOAD-TRUNCATED]", StringComparison.Ordinal) >
			text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal),
			text);
	}

	[Fact]
	public async Task RealProcessRemoteCheckoutUsesStartupAndDelegatedExclusionsWithHonestEcho()
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available in this test environment.");

		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("configured-root");
		var source = workspace.CreateDirectory("configured-root/source");
		workspace.WriteFile("configured-root/source/Visible.cs", "remote-visible-process-marker\n");
		workspace.WriteFile("configured-root/source/.dotted.cs", "remote-dotted-process-marker\n");
		await RunGitAsync(source, "init", "--quiet");
		await RunGitAsync(source, "config", "user.name", "DevProjex Tests");
		await RunGitAsync(source, "config", "user.email", "devprojex@example.invalid");
		await RunGitAsync(source, "add", "--all");
		await RunGitAsync(source, "commit", "--quiet", "-m", "remote fixture");
		var bareRepository = Path.Combine(root, "origin.git");
		await RunGitAsync(root, "clone", "--quiet", "--bare", source, bareRepository);
		var repositoryUrl = new Uri(Path.GetFullPath(bareRepository)).AbsoluteUri;

		await using var server = await ActualMcpProcess.StartAsync(
			root,
			workspace.CreateDirectory("data"),
			arguments:
			[
				"--allow-remote",
				"--allow-agent-exclusions",
				"--exclude",
				"dot-files"
			],
			allowFileGitTransport: true);
		var baseline = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = repositoryUrl },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var baselineText = AllProcessText(baseline);

		Assert.NotEqual(true, baseline.IsError);
		Assert.Contains("Visible.cs", baselineText, StringComparison.Ordinal);
		Assert.DoesNotContain(".dotted.cs", baselineText, StringComparison.Ordinal);
		Assert.Contains(
			"[Effective filters] git: gitignore; exclusions: dot-files.",
			baselineText,
			StringComparison.Ordinal);

		var delegated = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?>
			{
				["project"] = repositoryUrl,
				["exclusions"] = Array.Empty<string>()
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var delegatedText = AllProcessText(delegated);

		Assert.NotEqual(true, delegated.IsError);
		Assert.Contains(".dotted.cs", delegatedText, StringComparison.Ordinal);
		Assert.Contains(
			"[Effective filters] git: gitignore; exclusions: none.",
			delegatedText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessLocalProfileEchoDiffersFromTheListedStartupBaseline()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Visible.cs", "visible-local-profile-marker\n");
		workspace.WriteFile("project/.dotted.cs", "dotted-local-profile-marker\n");
		workspace.WriteFile("project/Empty.cs", string.Empty);
		var dataRoot = workspace.CreateDirectory("data");

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments:
			[
				"--allow-agent-exclusions",
				"--exclude",
				"empty-files"
			]);
		var initial = await server.Client.CallToolAsync(
			"list_projects",
			new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var initialList = Assert.IsType<JsonElement>(initial.StructuredContent);
		var canonicalProject = Assert.Single(initialList.GetProperty("projects").EnumerateArray())
			.GetProperty("path")
			.GetString()!;
		// macOS temporary roots may be spelled through /var while the server's root jail resolves
		// /private/var. Persist against the canonical project value the real client receives.
		new ProjectProfileStore(() => dataRoot).SaveProfile(
			canonicalProject,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: [IgnoreOptionId.DotFiles],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.SmartIgnore] = false,
					[IgnoreOptionId.EmptyFolders] = false,
					[IgnoreOptionId.EmptyFiles] = false,
					[IgnoreOptionId.HiddenFolders] = false,
					[IgnoreOptionId.HiddenFiles] = false,
					[IgnoreOptionId.DotFolders] = false,
					[IgnoreOptionId.DotFiles] = true,
					[IgnoreOptionId.ExtensionlessFiles] = false
				}));
		var listed = await server.Client.CallToolAsync(
			"list_projects",
			new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var list = Assert.IsType<JsonElement>(listed.StructuredContent);
		Assert.Equal(
			["empty-files"],
			list.GetProperty("baseline").GetProperty("exclusions").EnumerateArray()
				.Select(static value => value.GetString()));
		var profile = Assert.Single(list.GetProperty("profiles").EnumerateArray());
		Assert.Equal("local", profile.GetProperty("name").GetString());

		var pack = await server.Client.CallToolAsync(
			"pack_context",
			new Dictionary<string, object?>
			{
				["profile"] = "local",
				["view"] = "tree-content",
				["format"] = "text"
			},
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var packText = AllProcessText(pack);

		Assert.NotEqual(true, pack.IsError);
		Assert.Contains("Visible.cs", packText, StringComparison.Ordinal);
		Assert.Contains("Empty.cs", packText, StringComparison.Ordinal);
		Assert.DoesNotContain(".dotted.cs", packText, StringComparison.Ordinal);
		Assert.Contains(
			"[Effective filters]",
			packText,
			StringComparison.Ordinal);
		Assert.Contains(
			"exclusions: dot-files.",
			packText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("exclusions: empty-files", packText, StringComparison.Ordinal);
	}

	private static string AllProcessText(CallToolResult result) =>
		string.Join(
			"\n",
			result.Content.OfType<TextContentBlock>().Select(static block => block.Text));

	private sealed class ActualMcpProcess : IAsyncDisposable
	{
		private readonly Process process;
		private readonly Task<string> standardError;

		private ActualMcpProcess(Process process, McpClient client, Task<string> standardError)
		{
			this.process = process;
			Client = client;
			this.standardError = standardError;
		}

		public McpClient Client { get; }

		public static async Task<ActualMcpProcess> StartAsync(
			string project,
			string dataRoot,
			IReadOnlyList<string>? arguments = null,
			bool allowFileGitTransport = false)
		{
			var startInfo = new ProcessStartInfo("dotnet")
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = project
			};
			startInfo.ArgumentList.Add(PublishedApplicationLocator.FindApplicationAssembly());
			startInfo.ArgumentList.Add("mcp");
			startInfo.ArgumentList.Add("--root");
			startInfo.ArgumentList.Add(project);
			foreach (var argument in arguments ?? [])
				startInfo.ArgumentList.Add(argument);
			startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = dataRoot;
			if (allowFileGitTransport)
			{
				startInfo.Environment[GitRepositoryService.TestFileTransportPolicyVariable] = "1";
			}

			var process = Process.Start(startInfo) ??
			              throw new InvalidOperationException("MCP process did not start.");
			var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
			try
			{
				var client = await McpClient.CreateAsync(
					new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
					clientOptions: null,
					loggerFactory: null,
					TestContext.Current.CancellationToken);
				return new ActualMcpProcess(process, client, error);
			}
			catch
			{
				process.StandardInput.Close();
				process.Dispose();
				throw;
			}
		}

		public async ValueTask DisposeAsync()
		{
			process.StandardInput.Close();
			await Client.DisposeAsync();
			await process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
			var error = await standardError;
			try
			{
				Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {error}");
				Assert.True(string.IsNullOrWhiteSpace(error), $"Unexpected stderr: {error}");
			}
			finally
			{
				process.Dispose();
			}
		}
	}
}
