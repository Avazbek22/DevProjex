using System.Diagnostics;
using System.Threading.Channels;
using DevProjex.Application.Services;
using DevProjex.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	private static readonly string[] ExpectedTools =
	[
		"list_projects",
		"get_tree",
		"analyze",
		"pack_context",
		"read_pack",
		"search_project",
		"get_file"
	];
	private const string Secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const string PrivateEmail = "alice.smith" + "@company.io";
	private const string PrivatePath = "/home/alice-smith/DevProjexMcpProcessProbe/project";

	[Theory]
	[InlineData("none")]
	[InlineData("off")]
	public async Task RealProcessMcpAcceptsThePersistentNoFilteringAliases(string gitMode)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
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
		startInfo.ArgumentList.Add("--git-mode");
		startInfo.ArgumentList.Add(gitMode);
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

		var standardOutput = await output;
		var standardError = await error;
		Assert.True(process.ExitCode == 0, standardError);
		Assert.Empty(standardOutput);
		Assert.Empty(standardError);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessPublishesTheExclusionsParameterOnlyWhenDelegated(bool agentExclusions)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.cs", "anchor-process-marker\n");
		workspace.WriteFile("project/.dotted.cs", "dotted-process-marker\n");
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
		if (agentExclusions)
			startInfo.ArgumentList.Add("--allow-agent-exclusions");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var clientPhase = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		clientPhase.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			clientPhase.Token))
		{
			var tools = await client.ListToolsAsync(options: null, clientPhase.Token);
			var tree = tools.Single(static tool => tool.Name == "get_tree");
			Assert.Equal(
				agentExclusions,
				tree.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("exclusions", out _));

			if (agentExclusions)
			{
				var opened = await client.CallToolAsync(
					"get_tree",
					new Dictionary<string, object?> { ["exclusions"] = Array.Empty<string>() },
					progress: null,
					options: null,
					clientPhase.Token);
				Assert.NotEqual(true, opened.IsError);
				Assert.Contains(
					".dotted.cs",
					Assert.IsType<TextContentBlock>(Assert.Single(opened.Content)).Text,
					StringComparison.Ordinal);
			}
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
	}

	[Theory]
	[InlineData(new[] { "none" }, true)]
	[InlineData(new[] { "dot-files" }, false)]
	[InlineData(new[] { "default" }, true)]
	[InlineData(new[] { "default", "dot-files" }, false)]
	[InlineData(new[] { "DEFAULT", "Dot-Files" }, false)]
	public async Task RealProcessAppliesTheExclusionBaselineThroughTheCli(
		string[] exclusions,
		bool dotFileVisible)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.cs", "anchor-baseline-marker\n");
		workspace.WriteFile("project/.dotted.cs", "dotted-baseline-marker\n");
		workspace.WriteFile("project/Empty.cs", string.Empty);
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
		foreach (var exclusion in exclusions)
		{
			startInfo.ArgumentList.Add("--exclude");
			startInfo.ArgumentList.Add(exclusion);
		}
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var clientPhase = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		clientPhase.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			clientPhase.Token))
		{
			var tree = await client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				clientPhase.Token);
			Assert.NotEqual(true, tree.IsError);
			var text = Assert.IsType<TextContentBlock>(Assert.Single(tree.Content)).Text;

			// The supplied names replace the default set unless 'default' is listed, and the
			// default set never hides empty files, so Empty.cs is visible on every line here.
			Assert.Equal(dotFileVisible, text.Contains(".dotted.cs", StringComparison.Ordinal));
			Assert.Contains("Empty.cs", text, StringComparison.Ordinal);

			// The footer states the set the line produced: 'default' contributes smart-ignore
			// and empty-folders, a bare name list stands alone.
			var expectedExclusions = exclusions.Any(static value => value.Equals("none", StringComparison.OrdinalIgnoreCase))
				? "none"
				: string.Join(
					", ",
					new[] { "smart-ignore", "empty-folders", "dot-files" }
						.Where(token =>
							token == "dot-files"
								? exclusions.Any(static value => value.Equals("dot-files", StringComparison.OrdinalIgnoreCase))
								: exclusions.Any(static value => value.Equals("default", StringComparison.OrdinalIgnoreCase))));
			Assert.Contains($"[Effective filters] git: gitignore; exclusions: {expectedExclusions}.", text, StringComparison.Ordinal);
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
	}

	[Fact]
	public async Task RealProcessReportsListedCaseAndKeepsReadPackContinuationTrusted()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/WpfApp2/MainWindow.xaml.cs",
			string.Join('\n', Enumerable.Range(1, 44).Select(static line =>
				line == 1 ? "case-process-marker" : $"process-file-line-{line:D2}")));
		workspace.WriteFile("project/image_58500.txt", "markdown-process-marker\n");
		workspace.WriteFile(
			"project/Large.txt",
			string.Join('\n', Enumerable.Range(1, 1_500).Select(static line =>
				$"process-pack-line-{line:D4}-{new string('x', 20)}")));
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
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var clientPhase = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		clientPhase.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			clientPhase.Token))
		{
			var wrongCase = await client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?> { ["path"] = "wpfapp2/mainwindow.xaml.cs" },
				progress: null,
				options: null,
				clientPhase.Token);
			var wrongCaseText = Assert.IsType<TextContentBlock>(Assert.Single(wrongCase.Content)).Text;
			Assert.True(wrongCase.IsError);
			Assert.Null(wrongCase.StructuredContent);
			Assert.Contains(
				"differs only in letter case from the listed path 'WpfApp2/MainWindow.xaml.cs'",
				wrongCaseText,
				StringComparison.Ordinal);
			Assert.DoesNotContain("effective filters", wrongCaseText, StringComparison.Ordinal);

			var clampedRange = await client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?>
				{
					["path"] = "WpfApp2/MainWindow.xaml.cs",
					["start_line"] = 1,
					["end_line"] = 60
				},
				progress: null,
				options: null,
				clientPhase.Token);
			var clampedText = Assert.IsType<TextContentBlock>(Assert.Single(clampedRange.Content)).Text;
			var clampedClosingIndex = clampedText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
			var clampedNoticeIndex = clampedText.IndexOf(
				"[Showing lines 1-44 of 44; end_line 60 exceeded the file.]",
				StringComparison.Ordinal);
			Assert.NotEqual(true, clampedRange.IsError);
			Assert.Contains("process-file-line-44", clampedText, StringComparison.Ordinal);
			Assert.True(clampedNoticeIndex > clampedClosingIndex, clampedText);

			foreach (var toolName in new[] { "analyze", "pack_context" })
			{
				var wrongCaseSelection = await client.CallToolAsync(
					toolName,
					new Dictionary<string, object?> { ["paths"] = new[] { "wpfapp2/mainwindow.xaml.cs" } },
					progress: null,
					options: null,
					clientPhase.Token);
				var wrongCaseSelectionText = Assert.IsType<TextContentBlock>(
					Assert.Single(wrongCaseSelection.Content)).Text;
				Assert.True(wrongCaseSelection.IsError);
				Assert.Null(wrongCaseSelection.StructuredContent);
				Assert.Contains(
					"differs only in letter case from the listed path 'WpfApp2/MainWindow.xaml.cs'",
					wrongCaseSelectionText,
					StringComparison.Ordinal);
			}

			var markdownPath = await client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?> { ["path"] = @"image\_58500.txt" },
				progress: null,
				options: null,
				clientPhase.Token);
			Assert.NotEqual(true, markdownPath.IsError);
			Assert.Null(markdownPath.StructuredContent);
			Assert.Contains(
				"markdown-process-marker",
				Assert.IsType<TextContentBlock>(Assert.Single(markdownPath.Content)).Text,
				StringComparison.Ordinal);

			var stored = await client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["paths"] = new[] { "Large.txt" },
					["view"] = "content",
					["format"] = "text"
				},
				progress: null,
				options: null,
				clientPhase.Token);
			var storedText = Assert.IsType<TextContentBlock>(Assert.Single(stored.Content)).Text;
			Assert.NotEqual(true, stored.IsError);
			Assert.Null(stored.StructuredContent);

			var page = await client.CallToolAsync(
				"read_pack",
				new Dictionary<string, object?> { ["pack_id"] = ExtractPackId(storedText) },
				progress: null,
				options: null,
				clientPhase.Token);
			var pageText = Assert.IsType<TextContentBlock>(Assert.Single(page.Content)).Text;
			var closingIndex = pageText.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
			var continuationIndex = pageText.IndexOf("[Showing lines 1-1000 of ", StringComparison.Ordinal);
			Assert.NotEqual(true, page.IsError);
			Assert.Null(page.StructuredContent);
			Assert.True(closingIndex >= 0, pageText);
			Assert.True(continuationIndex > closingIndex, pageText);
			Assert.Contains("continue with start_line=1001", pageText, StringComparison.Ordinal);
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessUnrestrictedOpensBothFilterAxesThroughTheCli(bool unrestricted)
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available in this test environment.");
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.cs", "anchor-unrestricted-marker\n");
		workspace.WriteFile("project/.dotted.cs", "dotted-unrestricted-marker\n");
		workspace.WriteFile("project/.gitignore", "ignored.cs\n");
		workspace.WriteFile("project/ignored.cs", "ignored-unrestricted-marker\n");
		workspace.CreateDirectory("project/hollow");
		InitializeIsolatedRepository(project);
		RunGit(project, "add", "Anchor.cs", ".gitignore");
		RunGit(project, "commit", "--quiet", "-m", "baseline");
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
		if (unrestricted)
			startInfo.ArgumentList.Add("--unrestricted");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var clientPhase = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		clientPhase.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			clientPhase.Token))
		{
			var tree = await client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				clientPhase.Token);
			Assert.NotEqual(true, tree.IsError);
			var text = Assert.IsType<TextContentBlock>(Assert.Single(tree.Content)).Text;

			// One flag opens both axes: the empty folder held back by the default
			// exclusion set and the gitignored file held back by the Git baseline. The
			// dotted file is visible on both lines — the default set never hides it.
			Assert.Contains("Anchor.cs", text, StringComparison.Ordinal);
			Assert.Contains(".dotted.cs", text, StringComparison.Ordinal);
			Assert.Equal(unrestricted, text.Contains("hollow", StringComparison.Ordinal));
			Assert.Equal(unrestricted, text.Contains("ignored.cs", StringComparison.Ordinal));
			Assert.Contains(
				unrestricted
					? "[Effective filters] git: none; exclusions: none."
					: "[Effective filters] git: gitignore; exclusions: smart-ignore, empty-folders.",
				text,
				StringComparison.Ordinal);

			// The .git administrative area is a product boundary: it stays excluded
			// even at the widest baseline. HEAD and COMMIT_EDITMSG exist in every
			// fresh repository, so their absence proves the subtree never surfaces.
			Assert.DoesNotContain("HEAD", text, StringComparison.Ordinal);
			Assert.DoesNotContain("COMMIT_EDITMSG", text, StringComparison.Ordinal);
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
	}

	[Fact]
	public async Task RealProcessUnrestrictedComposesWithAgentExclusionDelegation()
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available in this test environment.");
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/Anchor.cs", "anchor-compose-marker\n");
		workspace.WriteFile("project/.dotted.cs", "dotted-compose-marker\n");
		workspace.WriteFile("project/.gitignore", "ignored.cs\n");
		workspace.WriteFile("project/ignored.cs", "ignored-compose-marker\n");
		InitializeIsolatedRepository(project);
		RunGit(project, "add", "Anchor.cs", ".gitignore");
		RunGit(project, "commit", "--quiet", "-m", "baseline");
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
		startInfo.ArgumentList.Add("--unrestricted");
		startInfo.ArgumentList.Add("--allow-agent-exclusions");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		using var clientPhase = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		clientPhase.CancelAfter(TimeSpan.FromMinutes(2));
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
			clientOptions: null,
			loggerFactory: null,
			clientPhase.Token))
		{
			// Delegation stays published on an unrestricted server.
			var tools = await client.ListToolsAsync(options: null, clientPhase.Token);
			var tree = tools.Single(static tool => tool.Name == "get_tree");
			Assert.True(tree.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("exclusions", out _));

			var wide = await client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				clientPhase.Token);
			Assert.NotEqual(true, wide.IsError);
			var wideText = Assert.IsType<TextContentBlock>(Assert.Single(wide.Content)).Text;
			Assert.Contains(".dotted.cs", wideText, StringComparison.Ordinal);
			Assert.Contains("ignored.cs", wideText, StringComparison.Ordinal);

			// A per-call set outranks the [] baseline while the Git axis stays open:
			// the dotted file disappears, the gitignored file stays visible.
			var narrowed = await client.CallToolAsync(
				"get_tree",
				new Dictionary<string, object?> { ["exclusions"] = new[] { "dot-files" } },
				progress: null,
				options: null,
				clientPhase.Token);
			Assert.NotEqual(true, narrowed.IsError);
			var narrowedText = Assert.IsType<TextContentBlock>(Assert.Single(narrowed.Content)).Text;
			Assert.DoesNotContain(".dotted.cs", narrowedText, StringComparison.Ordinal);
			Assert.Contains("ignored.cs", narrowedText, StringComparison.Ordinal);
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessAppliesServerRedactionPolicyAndStopsOnStandardInputEof(
		bool hidePrivateData)
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userProfile))
			Assert.Skip("The environment does not expose a user profile directory.");
		using var workspace = new TemporaryDirectory(userProfile);
		var project = workspace.CreateDirectory("project");
		var physicalProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
		if (OutputRootPathPresentation.MaskLocalUserSegment(physicalProject) == physicalProject)
			Assert.Skip("The user profile path does not use a supported local-user layout.");
		var ignoredEnvironmentRoot = workspace.CreateDirectory("environment-root");
		workspace.WriteFile(
			"project/app.cs",
			$"internal sealed class ProcessMarker {{ const string Token = \"{Secret}\"; }}\n" +
			$"// Contact {PrivateEmail}\n" +
			$"// Project {PrivatePath}\n");
		var application = PublishedApplicationLocator.FindApplicationAssembly();
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = ignoredEnvironmentRoot
		};
		startInfo.ArgumentList.Add(application);
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		if (hidePrivateData)
			startInfo.ArgumentList.Add("--hide-private-data");
		startInfo.Environment["CLAUDE_PROJECT_DIR"] = ignoredEnvironmentRoot;
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await using var recordingOutput = new RecordingReadStream(process.StandardOutput.BaseStream);
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, recordingOutput),
			clientOptions: null,
			loggerFactory: null,
			TestContext.Current.CancellationToken))
		{
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(ExpectedTools, tools.Select(static tool => tool.Name));
			var result = await client.CallToolAsync(
				"list_projects",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotNull(result.StructuredContent);
			var structured = result.StructuredContent.Value;
			var listedProject = structured.GetProperty("projects")[0].GetProperty("path").GetString();
			var expectedProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
			var expectedIgnoredEnvironmentRoot = McpRootRegistry.ResolvePhysicalExistingPath(
				ignoredEnvironmentRoot,
				requireDirectory: true);
			Assert.True(string.Equals(expectedProject, listedProject, PathComparison));
			Assert.False(string.Equals(expectedIgnoredEnvironmentRoot, listedProject, PathComparison));
			var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
			using var textDocument = JsonDocument.Parse(text);
			Assert.True(JsonElement.DeepEquals(structured, textDocument.RootElement));

			var file = await client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?> { ["path"] = "app.cs" },
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			AssertRedactionPolicy(file, hidePrivateData);

			var progress = new InlineProgress<ProgressNotificationValue>();
			var pack = await client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "tree-content",
					["format"] = "text",
					["max_tokens"] = "100000"
				},
				progress,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, pack.IsError);
			AssertRedactionPolicy(pack, hidePrivateData);
			AssertGeneratedRootPathPolicy(pack, expectedProject, hidePrivateData);
			Assert.Contains(
				"Token budget: 100000 estimated tokens.",
				Assert.IsType<TextContentBlock>(Assert.Single(pack.Content)).Text,
				StringComparison.Ordinal);
		}

		process.StandardInput.Close();
		var standardOutputEofTask = recordingOutput.WaitForSourceEofAsync(TestContext.Current.CancellationToken);
		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
			standardOutputEofTask
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");

		var messages = ParseJsonRpcMessages(recordingOutput.GetRecordedText());
		Assert.Contains(messages, static message =>
		{
			using var document = JsonDocument.Parse(message);
			return document.RootElement.TryGetProperty("method", out var method) &&
			       method.GetString() == NotificationMethods.ProgressNotification;
		});
	}

	[Fact]
	public async Task RealProcessClonesOptInRemoteAndAppliesTokenBudgetWithoutExposingCachePath()
	{
		if (!await IsGitAvailableAsync())
			Assert.Skip("Git is not available in this test environment.");

		using var workspace = new TemporaryDirectory();
		var root = workspace.CreateDirectory("configured-root");
		var repository = workspace.CreateDirectory("configured-root/remote-source.git");
		workspace.WriteFile("configured-root/remote-source.git/A-large.txt", new string('a', 40));
		workspace.WriteFile("configured-root/remote-source.git/B-small.txt", "b");
		await RunGitAsync(repository, "init");
		await RunGitAsync(repository, "config", "user.email", "tests@devprojex.local");
		await RunGitAsync(repository, "config", "user.name", "DevProjex Tests");
		await RunGitAsync(repository, "add", ".");
		await RunGitAsync(repository, "commit", "-m", "initial");

		var dataRoot = workspace.CreateDirectory("data");
		var repositoryUrl = new Uri(Path.GetFullPath(repository)).AbsoluteUri;
		var application = PublishedApplicationLocator.FindApplicationAssembly();
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = root
		};
		startInfo.ArgumentList.Add(application);
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(root);
		startInfo.ArgumentList.Add("--allow-remote");
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = dataRoot;

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await using var recordingOutput = new RecordingReadStream(process.StandardOutput.BaseStream);
		McpClient? client = null;
		try
		{
			client = await McpClient.CreateAsync(
				new StreamClientTransport(process.StandardInput.BaseStream, recordingOutput),
				clientOptions: null,
				loggerFactory: null,
				TestContext.Current.CancellationToken);
			var pack = await client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["project"] = repositoryUrl,
					["view"] = "content",
					["format"] = "text",
					["max_tokens"] = "1"
				},
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			var text = Assert.IsType<TextContentBlock>(Assert.Single(pack.Content)).Text;

			Assert.True(pack.IsError != true, text);
			Assert.Contains("B-small.txt", text, StringComparison.Ordinal);
			Assert.DoesNotContain(new string('a', 40), text, StringComparison.Ordinal);
			Assert.Contains("Included: 1 file (1 estimated tokens).", text, StringComparison.Ordinal);
			Assert.Contains("Skipped: 1 file", text, StringComparison.Ordinal);
			Assert.DoesNotContain(dataRoot, text, PathComparison);
		}
		finally
		{
			process.StandardInput.Close();
			if (client is not null)
				await client.DisposeAsync();
		}

		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
			recordingOutput.WaitForSourceEofAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
		var standardError = await standardErrorTask;
		Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {standardError}");
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");
		Assert.NotEmpty(ParseJsonRpcMessages(recordingOutput.GetRecordedText()));
		Assert.True(Directory.Exists(Path.Combine(dataRoot, "RepoCache")));
	}

	[Fact]
	public async Task PublishedSingleFileCompletesHandshakeListsToolsCallsToolAndExitsOnEof()
	{
		var application = GetPublishedSingleFileOrSkip();
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "internal sealed class PublishedMcpMarker {}\n");
		var startInfo = new ProcessStartInfo(application)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = project
		};
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await using var recordingOutput = new RecordingReadStream(process.StandardOutput.BaseStream);
		Exception? clientFailure = null;
		McpClient? client = null;
		try
		{
			client = await McpClient.CreateAsync(
				new StreamClientTransport(process.StandardInput.BaseStream, recordingOutput),
				clientOptions: null,
				loggerFactory: null,
				TestContext.Current.CancellationToken);
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(ExpectedTools, tools.Select(static tool => tool.Name));
			var result = await client.CallToolAsync(
				"list_projects",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, result.IsError);
			var listedProject = result.StructuredContent!.Value
				.GetProperty("projects")[0]
				.GetProperty("path")
				.GetString();
			Assert.True(string.Equals(
				McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true),
				listedProject,
				PathComparison));
		}
		catch (Exception exception)
		{
			clientFailure = exception;
		}
		finally
		{
			process.StandardInput.Close();
			if (client is not null)
				await client.DisposeAsync();
		}

		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
			recordingOutput.WaitForSourceEofAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
		var standardError = await standardErrorTask;
		Assert.True(
			clientFailure is null,
			$"Published MCP interaction failed. ExitCode={process.ExitCode}. " +
			$"stderr: {standardError}{Environment.NewLine}{clientFailure}");
		Assert.True(process.ExitCode == 0, $"Unexpected exit code {process.ExitCode}. stderr: {standardError}");

		Assert.NotEmpty(ParseJsonRpcMessages(recordingOutput.GetRecordedText()));
	}

	private static string ExtractPackId(string text)
	{
		const string prefix = "Pack stored as '";
		var start = text.IndexOf(prefix, StringComparison.Ordinal);
		Assert.True(start >= 0, text);
		start += prefix.Length;
		var end = text.IndexOf('\'', start);
		Assert.True(end > start, text);
		return text[start..end];
	}

	private static string GetPublishedSingleFileOrSkip()
	{
		var explicitPath = Environment.GetEnvironmentVariable("DEVPROJEX_TUI_TEST_BINARY");
		if (string.IsNullOrWhiteSpace(explicitPath))
			Assert.Skip("Published MCP smoke requires DEVPROJEX_TUI_TEST_BINARY.");

		var application = Path.GetFullPath(explicitPath);
		Assert.True(File.Exists(application), $"Published application does not exist: {application}");
		Assert.False(File.Exists(Path.ChangeExtension(application, ".runtimeconfig.json")));
		Assert.False(File.Exists(Path.ChangeExtension(application, ".deps.json")));
		return application;
	}

	private static async Task<bool> IsGitAvailableAsync()
	{
		try
		{
			var result = await RunProcessAsync(
				"git",
				Directory.GetCurrentDirectory(),
				["--version"]);
			return result.ExitCode == 0;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
	{
		var result = await RunProcessAsync("git", workingDirectory, arguments);
		Assert.True(
			result.ExitCode == 0,
			$"Git failed with exit code {result.ExitCode}. stdout: {result.Output} stderr: {result.Error}");
	}

	private static async Task<ProcessResult> RunProcessAsync(
		string fileName,
		string workingDirectory,
		IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo(fileName)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = workingDirectory
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException($"Process '{fileName}' did not start.");
		process.StandardInput.Close();
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
	}

	private static IReadOnlyList<string> ParseJsonRpcMessages(string transcript)
	{
		var lines = transcript.Split('\n');
		var messageCount = lines.Length;
		if (messageCount > 0 && lines[^1].Length == 0)
			messageCount--;

		Assert.True(messageCount > 0, "MCP stdout did not contain any JSON-RPC messages.");
		var messages = new string[messageCount];
		for (var index = 0; index < messageCount; index++)
		{
			var line = lines[index];
			Assert.False(
			line.EndsWith('\r'),
			"MCP stdout used CRLF framing; the stdio transport must emit LF-only bytes on every OS.");
		var message = line;
			Assert.False(
				string.IsNullOrWhiteSpace(message),
				$"MCP stdout contained an empty non-protocol line at index {index}.");
			Assert.DoesNotContain('\r', message);
			Assert.StartsWith("{", message, StringComparison.Ordinal);
			Assert.EndsWith("}", message, StringComparison.Ordinal);
			using var document = JsonDocument.Parse(message);
			Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
			Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
			var hasMethod = document.RootElement.TryGetProperty("method", out var method) &&
			                method.ValueKind == JsonValueKind.String &&
			                !string.IsNullOrWhiteSpace(method.GetString());
			var hasId = document.RootElement.TryGetProperty("id", out _);
			var hasResult = document.RootElement.TryGetProperty("result", out _);
			var hasError = document.RootElement.TryGetProperty("error", out _);
			Assert.True(
				hasMethod ? !hasResult && !hasError : hasId && hasResult != hasError,
				$"MCP stdout contained an invalid JSON-RPC message at index {index}: {message}");
			messages[index] = message;
		}

		return messages;
	}

	private static void AssertRedactionPolicy(
		CallToolResult result,
		bool hidePrivateData)
	{
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("ProcessMarker", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		if (hidePrivateData)
		{
			Assert.DoesNotContain(PrivateEmail, text, StringComparison.Ordinal);
			Assert.DoesNotContain(PrivatePath, text, StringComparison.Ordinal);
		}
		else
		{
			Assert.Contains(PrivateEmail, text, StringComparison.Ordinal);
			Assert.Contains(PrivatePath, text, StringComparison.Ordinal);
		}
	}

	private static void AssertGeneratedRootPathPolicy(
		CallToolResult result,
		string project,
		bool hidePrivateData)
	{
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		var protectedProject = OutputRootPathPresentation.MaskLocalUserSegment(project);
		var expectedProject = hidePrivateData ? protectedProject : project;
		Assert.Contains(expectedProject, text, StringComparison.Ordinal);
		Assert.DoesNotContain(hidePrivateData ? project : protectedProject, text, StringComparison.Ordinal);
	}

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private sealed record ProcessResult(int ExitCode, string Output, string Error);

	private sealed class InlineProgress<T> : IProgress<T>
	{
		private readonly List<T> _values = [];
		private readonly object _sync = new();

		public IReadOnlyList<T> Values
		{
			get
			{
				lock (_sync)
					return _values.ToArray();
			}
		}

		public void Report(T value)
		{
			lock (_sync)
				_values.Add(value);
		}
	}

	// Mirrors the hardened EnsureRepository fixture: a signing requirement, hook
	// template, or global excludes file from the host must not reach the fixture.
	private static void InitializeIsolatedRepository(string path)
	{
		RunGit(path, "init", "--quiet", "--initial-branch=main");
		var hooksPath = Directory.CreateDirectory(Path.Combine(path, ".git", "devprojex-test-hooks")).FullName;
		var excludesPath = Path.Combine(path, ".git", "devprojex-test-excludes");
		File.WriteAllText(excludesPath, string.Empty);
		RunGit(path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(path, "config", "commit.gpgSign", "false");
		RunGit(path, "config", "core.hooksPath", hooksPath);
		RunGit(path, "config", "core.excludesFile", excludesPath);
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		var result = TerminalTestProcess.Run(startInfo);
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {result.StandardOutput}{result.StandardError}");
	}

	private sealed class RecordingReadStream : Stream
	{
		private const int ReadBufferSize = 8 * 1024;
		private static readonly Encoding StrictUtf8 = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
		private readonly Stream _source;
		private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
			new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = true,
				AllowSynchronousContinuations = false
			});
		private readonly MemoryStream _recording = new();
		private readonly object _sync = new();
		private readonly CancellationTokenSource _lifetime = new();
		private readonly Task _pumpTask;
		private byte[]? _currentChunk;
		private int _currentOffset;
		private int _disposed;

		public RecordingReadStream(Stream source)
		{
			_source = source;
			_pumpTask = PumpAsync();
		}

		public string GetRecordedText()
		{
			lock (_sync)
				return StrictUtf8.GetString(_recording.ToArray());
		}

		public Task WaitForSourceEofAsync(CancellationToken cancellationToken) =>
			_pumpTask.WaitAsync(cancellationToken);

		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
			while (true)
			{
				if (_currentChunk is not null)
				{
					var count = Math.Min(buffer.Length, _currentChunk.Length - _currentOffset);
					_currentChunk.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
					_currentOffset += count;
					if (_currentOffset == _currentChunk.Length)
					{
						_currentChunk = null;
						_currentOffset = 0;
					}
					return count;
				}

				if (_chunks.Reader.TryRead(out _currentChunk))
					continue;
				if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
					return 0;
			}
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		private async Task PumpAsync()
		{
			Exception? failure = null;
			try
			{
				while (true)
				{
					var buffer = new byte[ReadBufferSize];
					var read = await _source
						.ReadAsync(buffer, _lifetime.Token)
						.ConfigureAwait(false);
					if (read == 0)
						break;
					if (read != buffer.Length)
						Array.Resize(ref buffer, read);

					lock (_sync)
						_recording.Write(buffer);
					await _chunks.Writer
						.WriteAsync(buffer, _lifetime.Token)
						.ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
			{
			}
			catch (Exception exception)
			{
				failure = exception;
				throw;
			}
			finally
			{
				_chunks.Writer.TryComplete(failure);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				DisposeAsyncCore().AsTask().GetAwaiter().GetResult();
			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			await DisposeAsyncCore().ConfigureAwait(false);
			GC.SuppressFinalize(this);
		}

		private async ValueTask DisposeAsyncCore()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			_lifetime.Cancel();
			await _source.DisposeAsync().ConfigureAwait(false);
			try
			{
				await _pumpTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			catch (IOException) when (_lifetime.IsCancellationRequested)
			{
			}
			finally
			{
				_lifetime.Dispose();
				_recording.Dispose();
			}
		}

		public override bool CanRead => Volatile.Read(ref _disposed) == 0;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
