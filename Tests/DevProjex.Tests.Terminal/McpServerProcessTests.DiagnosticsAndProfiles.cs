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
	public async Task RealProcessAcceptsUniqueListedProjectNameAndUnknownProjectNamesBothForms()
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateDirectory("alpha-project");
		var second = workspace.CreateDirectory("beta-project");
		workspace.WriteFile("alpha-project/alpha.txt", "alpha-marker\n");
		workspace.WriteFile("beta-project/beta.txt", "beta-marker\n");
		await using var server = await ActualMcpProcess.StartAsync(
			first,
			workspace.CreateDirectory("data"),
			["--root", second]);

		var listed = await server.Client.CallToolAsync(
			"list_projects",
			new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(true, listed.IsError);
		var projects = Structured(listed).GetProperty("projects");
		Assert.Contains(projects.EnumerateArray(), item => item.GetProperty("name").GetString() == "beta-project");

		var byName = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = "beta-project", ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.NotEqual(true, byName.IsError);
		Assert.Contains("beta.txt", AllProcessText(byName), StringComparison.Ordinal);
		Assert.DoesNotContain("alpha.txt", AllProcessText(byName), StringComparison.Ordinal);

		var unknown = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["project"] = "missing-project" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.True(unknown.IsError);
		Assert.StartsWith("DPX-MCP-UNKNOWN-PROJECT", AllProcessText(unknown), StringComparison.Ordinal);
		Assert.Contains("name or path", AllProcessText(unknown), StringComparison.Ordinal);
		Assert.Contains("alpha-project", AllProcessText(unknown), StringComparison.Ordinal);
		Assert.Contains("beta-project", AllProcessText(unknown), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcess_ReusesInventoryForANarrowQueryAndInvalidatesItAfterTreeChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var directory = 0; directory < 40; directory++)
		{
			for (var file = 0; file < 50; file++)
				workspace.WriteFile($"project/src/{directory:D2}/File{file:D2}.cs", "internal sealed class Fixture { }\n");
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));
		var first = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?>(),
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var second = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["include_patterns"] = new[] { "src/39/**" } },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		Assert.NotEqual(true, first.IsError);
		Assert.NotEqual(true, second.IsError);
		Assert.Contains("File49.cs", AllProcessText(second), StringComparison.Ordinal);

		workspace.WriteFile("project/src/39/AddedAfterCache.cs", "internal sealed class AddedAfterCache { }\n");
		var changed = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["include_patterns"] = new[] { "src/39/**" } },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		Assert.NotEqual(true, changed.IsError);
		Assert.Contains("AddedAfterCache.cs", AllProcessText(changed), StringComparison.Ordinal);
	}

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
		Assert.Contains("Readable.txt", text, StringComparison.Ordinal);
		Assert.Contains("1:readable-process-marker", text, StringComparison.Ordinal);
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
		var initialList = Structured(initial);
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
		var list = Structured(listed);
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

	[Fact]
	public async Task RealProcessLiveContextRefreshesAStoredSelectionWithoutRestarting()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { const string Marker = \"inside-marker\"; }\n");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { const string Marker = \"outside-marker\"; }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]));

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var initial = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var initialText = AllProcessText(initial);
		Assert.Contains("Inside.cs", initialText, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.cs", initialText, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 1 · 1 files selected in the window", initialText, StringComparison.Ordinal);

		store.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["docs"]));
		var refreshed = await server.Client.CallToolAsync(
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "outside-marker" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var refreshedText = AllProcessText(refreshed);
		Assert.Contains("docs/Outside.cs", refreshedText, StringComparison.Ordinal);
		Assert.DoesNotContain("src/Inside.cs", refreshedText, StringComparison.Ordinal);
		Assert.Contains("[Live context] changed since revision 1: +1 folder, -1 folder", refreshedText, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 2 · 1 files selected in the window", refreshedText, StringComparison.Ordinal);

		var namedOutsideSelection = await server.Client.CallToolAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "src/Inside.cs" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.StartsWith(
			"[Live context] the named path is outside the current window selection; returned because you named it.",
			AllProcessText(namedOutsideSelection),
			StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RealProcessLiveContextUsesDefaultsOnlyWhenTheProfileIsAbsent()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }\n");
		var dataRoot = workspace.CreateDirectory("data");

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var result = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(result);

		Assert.NotEqual(true, result.IsError);
		Assert.Contains("Inside.cs", text, StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] no window selection saved for this root; using server defaults.",
			text,
			StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RealProcessLiveContextRejectsAnUnreadableInitialProfile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		Assert.True(store.EnsureStorageExists());
		File.WriteAllText(store.GetPath(), "{\"schemaVersion\":3,\"profiles\":");

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var result = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var text = AllProcessText(result);

		Assert.True(result.IsError);
		Assert.Contains("DPX-MCP-PROJECT-UNAVAILABLE", text, StringComparison.Ordinal);
		Assert.Contains("retry this call", text, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(
			"[Live context] saved window selection could not be read; retry this call.",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Inside.cs", text, StringComparison.Ordinal);
		Assert.DoesNotContain("using server defaults", text, StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RealProcessLiveContextKeepsTheLastSelectionWhileTheProfileIsLockedAndRecoversAfterUnlock()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }\n");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		Assert.True(store.TrySaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"])));

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var initial = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var initialText = AllProcessText(initial);
		Assert.Contains("Inside.cs", initialText, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.cs", initialText, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 1 · 1 files selected in the window", initialText, StringComparison.Ordinal);

		await using (var held = new FileStream(
			store.GetPath(),
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			try
			{
				using var probe = File.OpenRead(store.GetPath());
				Assert.Skip("Exclusive file sharing is not enforced by this platform and file system.");
			}
			catch (IOException)
			{
			}

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			var locked = await server.Client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?> { ["format"] = "text" },
				progress: null,
				options: null,
				timeout.Token);
			var lockedText = AllProcessText(locked);

			Assert.Contains("Inside.cs", lockedText, StringComparison.Ordinal);
			Assert.DoesNotContain("Outside.cs", lockedText, StringComparison.Ordinal);
			Assert.Contains(
				"[Live context] saved window selection could not be read; using revision 1. Retry this call.",
				lockedText,
				StringComparison.Ordinal);
			Assert.Contains(
				"[Live context] revision 1 · 1 files selected in the window",
				lockedText,
				StringComparison.Ordinal);
			Assert.DoesNotContain("changed since revision", lockedText, StringComparison.Ordinal);
		}

		Assert.True(store.TrySaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["docs"])));
		var recovered = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var recoveredText = AllProcessText(recovered);

		Assert.Contains("Outside.cs", recoveredText, StringComparison.Ordinal);
		Assert.DoesNotContain("Inside.cs", recoveredText, StringComparison.Ordinal);
		Assert.DoesNotContain("could not be read", recoveredText, StringComparison.Ordinal);
		Assert.Contains("[Live context] changed since revision 1: +1 folder, -1 folder", recoveredText, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 2 · 1 files selected in the window", recoveredText, StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RealProcessLiveContextReportsRecoveryFromTheProfileBackup()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }\n");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		Assert.True(store.TrySaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"])));
		File.WriteAllText(store.GetPath(), "{\"schemaVersion\":3,\"profiles\":");

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var recovered = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var recoveredText = AllProcessText(recovered);

		Assert.Contains("Inside.cs", recoveredText, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.cs", recoveredText, StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] saved window selection could not be read; using revision 1. Retry this call.",
			recoveredText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] revision 1 · 1 files selected in the window",
			recoveredText,
			StringComparison.Ordinal);

		var afterRepair = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain("could not be read", AllProcessText(afterRepair), StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RealProcessLiveContextTreatsProfileRemovalAsARevisionChangeToServerDefaults()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }\n");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		Assert.True(store.TrySaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"])));

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			arguments: ["--live"],
			clientInfo: new Implementation { Name = "process-client", Version = "1.0" });
		var initial = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var initialText = AllProcessText(initial);
		Assert.Contains("Inside.cs", initialText, StringComparison.Ordinal);
		Assert.DoesNotContain("Outside.cs", initialText, StringComparison.Ordinal);
		Assert.Contains("[Live context] revision 1 · 1 files selected in the window", initialText, StringComparison.Ordinal);

		Assert.Equal(ProjectProfileClearStatus.Cleared, store.ClearAllProfiles());
		var afterReset = await server.Client.CallToolAsync(
			"get_tree",
			new Dictionary<string, object?> { ["format"] = "text" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);
		var afterResetText = AllProcessText(afterReset);

		Assert.Contains("Inside.cs", afterResetText, StringComparison.Ordinal);
		Assert.Contains("Outside.cs", afterResetText, StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] no window selection saved for this root; using server defaults.",
			afterResetText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] changed since revision 1: -1 folder, +all",
			afterResetText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Live context] revision 2 · 2 files selected in the window",
			afterResetText,
			StringComparison.Ordinal);
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
			bool allowFileGitTransport = false,
			IReadOnlyDictionary<string, string>? environment = null,
			Implementation? clientInfo = null)
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
			// Only the terminal test host grants the local file transport that a synthetic origin
			// needs; it serves the same MCP server from the same libraries as the shipped host.
			startInfo.ArgumentList.Add(allowFileGitTransport
				? PublishedApplicationLocator.FindTerminalTestHostAssembly()
				: PublishedApplicationLocator.FindApplicationAssembly());
			if (allowFileGitTransport)
				startInfo.ArgumentList.Add(TerminalTransportPolicyProtocol.TerminalCommandArgument);
			startInfo.ArgumentList.Add("mcp");
			startInfo.ArgumentList.Add("--root");
			startInfo.ArgumentList.Add(project);
			foreach (var argument in arguments ?? [])
				startInfo.ArgumentList.Add(argument);
			startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = dataRoot;
			foreach (var pair in environment ?? new Dictionary<string, string>())
				startInfo.Environment[pair.Key] = pair.Value;
			if (allowFileGitTransport)
			{
				startInfo.Environment[TerminalTransportPolicyProtocol.AllowLocalFileTransportVariable] =
					TerminalTransportPolicyProtocol.Enabled;
			}

			var process = Process.Start(startInfo) ??
						  throw new InvalidOperationException("MCP process did not start.");
			var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
			try
			{
				var client = await McpClient.CreateAsync(
					new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
					clientOptions: clientInfo is null ? null : new McpClientOptions { ClientInfo = clientInfo },
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
