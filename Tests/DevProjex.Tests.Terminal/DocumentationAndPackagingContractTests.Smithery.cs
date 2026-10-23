using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Mcp;

namespace DevProjex.Tests.Terminal;

// The Smithery bundle is built by hand from Packaging/Smithery, and Smithery shows what its manifest
// declares instead of asking the server. These checks keep that manifest in step with the product,
// so a stale listing fails here rather than after publication. They belong to this class because
// the documentation job is what runs for a change limited to Packaging/.
public sealed partial class DocumentationAndPackagingContractTests
{
	private const string SmitheryProjectRootPlaceholder = "${user_config.project_root}";

	[Fact]
	public void SmitheryManifestVersionIsTheProductVersion()
	{
		var rootPath = FindRepositoryRoot();

		Assert.Equal(ReadHeadlessPackageVersion(rootPath), SmitheryText(SmitheryManifest(rootPath), "version"));
	}

	[Fact]
	public async Task SmitheryManifestToolsAreExactlyTheToolsTheServerRegisters()
	{
		using var workspace = new TemporaryDirectory();
		await using var packs = new McpPackRegistry(workspace.CreateDirectory("packs"));
		var target = new DevProjexMcpTools(
			new McpRootRegistry([workspace.CreateDirectory("project")]),
			new Lazy<McpProjectService>(static () => throw new InvalidOperationException("Listing tools opens no project.")),
			packs);
		// The bundle starts the server without flags, so the listing shows the default catalog.
		var registered = new DevProjexMcpToolCatalog(target, allowRemote: false)
			.Select(static tool => tool.ProtocolTool)
			.ToArray();
		var manifest = SmitheryManifest(FindRepositoryRoot());
		var declared = manifest["tools"]!.AsArray()
			.Select(static tool => tool!.AsObject())
			.ToArray();
		Assert.Contains(
			$"{CountWord(registered.Length)} read-only tools",
			SmitheryText(manifest, "long_description"),
			StringComparison.OrdinalIgnoreCase);

		Assert.Equal(
			registered.Select(static tool => tool.Name),
			declared.Select(static tool => SmitheryText(tool, "name")));
		foreach (var (tool, entry) in registered.Zip(declared))
		{
			Assert.Equal(tool.Description, SmitheryText(entry, "description"));
			Assert.Equal(["description", "name"], entry.Select(static property => property.Key).Order(StringComparer.Ordinal));
		}
	}

	[Fact]
	public void SmitheryLaunchCommandIsTheNpmPackageWithTheChosenProjectFolder()
	{
		var configuration = SmitheryMcpConfiguration();
		var arguments = SmitheryArguments(configuration);

		// The Smithery runner wraps exactly "npx" in cmd /c on Windows; any other spelling would
		// leave Windows users with a command that cannot start.
		Assert.Equal("npx", SmitheryText(configuration, "command"));
		Assert.Equal(["-y", "devprojex", "mcp", "--root", SmitheryProjectRootPlaceholder], arguments);
		Assert.Empty(configuration["env"]!.AsObject());

		// Claude Desktop installs the same bundle and applies the override itself. /d keeps an AutoRun
		// command from the registry from writing into the protocol stream before npx starts.
		var overrides = configuration["platform_overrides"]!.AsObject();
		Assert.Equal(["win32"], overrides.Select(static platform => platform.Key));
		var windows = overrides["win32"]!.AsObject();
		Assert.Equal("cmd", SmitheryText(windows, "command"));
		Assert.Equal(["/d", "/c", "npx", .. arguments], SmitheryArguments(windows));
	}

	[Fact]
	public void SmitheryLaunchArgumentsParseAgainstTheProductionCommandTree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project with spaces");
		var serverArguments = SmitherySubstitute(SmitheryArguments(SmitheryMcpConfiguration()), project).Skip(2).ToArray();

		var parseResult = new DevProjexCommandTree(new TestTerminalEnvironment()).Build().Parse(serverArguments);

		Assert.True(
			parseResult.Errors.Count == 0,
			string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message)));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SmitheryLaunchCommandIsRecognizedAsTheDevProjexLauncher(bool windows)
	{
		using var workspace = new TemporaryDirectory();
		var project = Path.GetFullPath(workspace.CreateDirectory("project"));
		var configuration = SmitheryMcpConfiguration();
		if (windows)
			configuration = configuration["platform_overrides"]!["win32"]!.AsObject();

		var entry = McpExistingEntryParser.Parse(
			SmitheryText(configuration, "command"),
			SmitherySubstitute(SmitheryArguments(configuration), project),
			new Dictionary<string, string>());

		Assert.Equal(McpExistingEntryKind.Launcher, entry.Kind);
		Assert.Equal("npx", entry.Launcher);
		Assert.Equal(project, entry.ProjectRoot);
		Assert.Equal(McpConnectionMode.Standard, entry.Mode);
		Assert.Empty(entry.UnsupportedArguments);
	}

	[Fact]
	public void SmitheryUserConfigurationAsksForOneRequiredProjectFolder()
	{
		var manifest = SmitheryManifest(FindRepositoryRoot());
		var option = Assert.Single(manifest["user_config"]!.AsObject());

		Assert.Equal("project_root", option.Key);
		var folder = option.Value!.AsObject();
		Assert.Equal("directory", SmitheryText(folder, "type"));
		Assert.True(folder["required"]!.GetValue<bool>());
		// One folder per server: a list would expand into extra arguments after a single --root.
		Assert.False(folder.ContainsKey("multiple"));
		Assert.False(folder.ContainsKey("default"));
		Assert.False(string.IsNullOrWhiteSpace(SmitheryText(folder, "title")));
		Assert.False(string.IsNullOrWhiteSpace(SmitheryText(folder, "description")));

		var referenced = Regex.Matches(manifest["server"]!.ToJsonString(), @"\$\{user_config\.(?<key>[^}]+)\}")
			.Select(static match => match.Groups["key"].Value)
			.Distinct(StringComparer.Ordinal);
		Assert.Equal(["project_root"], referenced);
	}

	[Fact]
	public async Task SmitheryEntryPointBuildsTheManifestCommandOnEveryPlatform()
	{
		var rootPath = FindRepositoryRoot();
		var server = SmitheryManifest(rootPath)["server"]!.AsObject();
		Assert.Equal("node", SmitheryText(server, "type"));
		Assert.Equal("server/index.js", SmitheryText(server, "entry_point"));
		const string Project = "/projects/example project";
		var configuration = SmitheryMcpConfiguration();
		var expected = SmitherySubstitute(SmitheryArguments(configuration), Project);
		var expectedWindows = SmitherySubstitute(
			SmitheryArguments(configuration["platform_overrides"]!["win32"]!.AsObject()),
			Project);

		var commands = await RunSmitheryEntryPointAsync(
			Path.Combine(rootPath, "Packaging", "Smithery", "server", "index.js"),
			["--root", Project]);

		foreach (var platform in new[] { "linux", "darwin" })
		{
			Assert.Equal("npx", commands[platform].Command);
			Assert.Equal(expected, commands[platform].Arguments);
		}
		Assert.Equal("cmd.exe", commands["win32"].Command);
		Assert.Equal(expectedWindows, commands["win32"].Arguments);
	}

	[Fact]
	public void SmitheryBundleFilesExistWhereTheBuildScriptTakesThem()
	{
		var rootPath = FindRepositoryRoot();
		var manifest = SmitheryManifest(rootPath);

		// The build script copies the icon into the bundle; Claude Desktop shows only a bundled icon.
		Assert.Equal("icon.png", SmitheryText(manifest, "icon"));
		var script = File.ReadAllText(Path.Combine(rootPath, "Scripts", "Build-SmitheryBundle.ps1"));
		const string IconSource = "Assets/AppIcon/Linux/png/512.png";
		Assert.Contains($"\"icon.png\" = Join-Path $repoRoot \"{IconSource}\"", script, StringComparison.Ordinal);
		Assert.True(File.Exists(Path.Combine(rootPath, IconSource)));
		Assert.Contains("\"manifest.json\" = Join-Path $sourceRoot \"manifest.json\"", script, StringComparison.Ordinal);
		Assert.Contains("\"server/index.js\" = Join-Path $sourceRoot \"server/index.js\"", script, StringComparison.Ordinal);
	}

	[Fact]
	public void SmitheryListingMetadataMatchesTheNpmPackageItStarts()
	{
		var rootPath = FindRepositoryRoot();
		var manifest = SmitheryManifest(rootPath);
		using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(
			rootPath, "Packaging", "Npm", "devprojex", "package.json.template")));
		var packageRoot = package.RootElement;

		Assert.Equal("0.3", SmitheryText(manifest, "manifest_version"));
		Assert.Equal("devprojex", SmitheryText(manifest, "name"));
		Assert.Equal("DevProjex", SmitheryText(manifest, "display_name"));
		Assert.Equal(packageRoot.GetProperty("author").GetString(), SmitheryText(manifest["author"]!.AsObject(), "name"));
		Assert.Equal(packageRoot.GetProperty("license").GetString(), SmitheryText(manifest, "license"));
		var minimumNode = Regex.Match(packageRoot.GetProperty("engines").GetProperty("node").GetString()!, @"^>=(?<major>\d+)$");
		Assert.True(minimumNode.Success);
		Assert.Equal(
			$">={minimumNode.Groups["major"].Value}.0.0",
			SmitheryText(manifest["compatibility"]!["runtimes"]!.AsObject(), "node"));
		Assert.Equal(
			["darwin", "win32", "linux"],
			manifest["compatibility"]!["platforms"]!.AsArray().Select(static platform => platform!.GetValue<string>()));

		foreach (var key in new[] { "homepage", "documentation", "support" })
			Assert.StartsWith("https://github.com/Avazbek22/DevProjex", SmitheryText(manifest, key), StringComparison.Ordinal);
		Assert.Equal("https://github.com/Avazbek22/DevProjex.git", SmitheryText(manifest["repository"]!.AsObject(), "url"));
		Assert.InRange(SmitheryText(manifest, "description").Length, 1, 400);
		Assert.False(string.IsNullOrWhiteSpace(SmitheryText(manifest, "long_description")));
		var keywords = manifest["keywords"]!.AsArray().Select(static keyword => keyword!.GetValue<string>()).ToArray();
		Assert.NotEmpty(keywords);
		Assert.Equal(keywords.Length, keywords.Distinct(StringComparer.Ordinal).Count());
	}

	/// <summary>
	/// Runs the real entry point under Node, so a syntax error or a drifted argument list fails here
	/// instead of on a user's machine.
	/// </summary>
	private static async Task<Dictionary<string, (string Command, string[] Arguments)>> RunSmitheryEntryPointAsync(
		string entryPoint,
		IReadOnlyList<string> serverArguments)
	{
		const string Script =
			"const [entry, ...serverArguments] = process.argv.slice(1);" +
			"const { createCommand } = require(entry);" +
			"const commands = {};" +
			"for (const platform of ['linux', 'darwin', 'win32'])" +
			// null, not undefined: undefined would fall back to this machine's ComSpec.
			" commands[platform] = createCommand(serverArguments, platform, null);" +
			"process.stdout.write(JSON.stringify(commands));";
		var startInfo = new ProcessStartInfo("node")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (var argument in new[] { "-e", Script, entryPoint }.Concat(serverArguments))
			startInfo.ArgumentList.Add(argument);

		Process process;
		try
		{
			process = Process.Start(startInfo)!;
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			throw new InvalidOperationException("Node.js must be on PATH to check the Smithery entry point.", exception);
		}

		using (process)
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
				var error = process.StandardError.ReadToEndAsync(timeout.Token);
				await process.WaitForExitAsync(timeout.Token);
				Assert.True(process.ExitCode == 0, await error);
				using var document = JsonDocument.Parse(await output);
				return document.RootElement.EnumerateObject().ToDictionary(
					static platform => platform.Name,
					static platform => (
						platform.Value.GetProperty("command").GetString()!,
						platform.Value.GetProperty("args").EnumerateArray().Select(static argument => argument.GetString()!).ToArray()));
			}
			finally
			{
				if (!process.HasExited)
					process.Kill(entireProcessTree: true);
			}
		}
	}

	private static JsonObject SmitheryManifest(string rootPath) =>
		JsonNode.Parse(File.ReadAllText(Path.Combine(rootPath, "Packaging", "Smithery", "manifest.json")))!.AsObject();

	private static JsonObject SmitheryMcpConfiguration() =>
		SmitheryManifest(FindRepositoryRoot())["server"]!["mcp_config"]!.AsObject();

	private static string SmitheryText(JsonObject node, string property) => node[property]!.GetValue<string>();

	private static string[] SmitheryArguments(JsonObject configuration) =>
		configuration["args"]!.AsArray().Select(static argument => argument!.GetValue<string>()).ToArray();

	private static string[] SmitherySubstitute(IEnumerable<string> arguments, string projectRoot) =>
		arguments
			.Select(argument => argument.Replace(SmitheryProjectRootPlaceholder, projectRoot, StringComparison.Ordinal))
			.ToArray();
}
