using DevProjex.Application.Updates;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Avalonia.Services;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakIntegrationBoundaryTests
{
	private const string AppId = "io.github.Avazbek22.DevProjex";
	private static LocalizationService Localization() => new(new JsonLocalizationCatalog(), AppLanguage.En);
	private static IDisposable Sandbox() => FlatpakEnvironment.UseForTesting(new(true, AppId));

	[Theory]
	[InlineData(McpConnectionClient.Cursor, "mcpServers")]
	[InlineData(McpConnectionClient.VsCode, "servers")]
	[InlineData(McpConnectionClient.Json, "mcpServers")]
	public void JsonConfigurationUsesHostCommandAndPreservesArgumentBoundaries(McpConnectionClient client, string container)
	{
		using var sandbox = Sandbox();
		using var json = JsonDocument.Parse(McpConnectionFragmentGenerator.Generate(
			client, McpConnectionMode.Live, "/app/lib/devprojex/DevProjex", "/home/person/project with spaces"));
		var entry = json.RootElement.GetProperty(container).GetProperty("devprojex");
		Assert.Equal("flatpak", entry.GetProperty("command").GetString());
		Assert.Equal(new[] { "run", AppId, "mcp", "--root", "/home/person/project with spaces", "--live" },
			entry.GetProperty("args").EnumerateArray().Select(static item => item.GetString()));
	}

	[Theory]
	[InlineData(McpConnectionClient.ClaudeCode)]
	[InlineData(McpConnectionClient.Codex)]
	public void ManualClientCommandRunsFlatpakInsteadOfAnInternalPath(McpConnectionClient client)
	{
		using var sandbox = Sandbox();
		var request = new McpConnectionRequest(client, McpConnectionMode.Live, "/app/devprojex", "/home/person/project");
		var text = McpConnectionFragmentGenerator.GenerateHostClientCommand(request);
		Assert.Contains("-- flatpak run " + AppId + " mcp --root /home/person/project --live", text);
		Assert.DoesNotContain("/app/devprojex", text);
		var config = McpConnectionFragmentGenerator.Generate(client, request.Mode, request.ExecutablePath, request.ProjectRoot);
		Assert.Contains(client == McpConnectionClient.Codex ? "command = \"flatpak\"" : "-- flatpak run " + AppId, config);
	}

	[Fact]
	public void ShimCanReturnToTheOriginalPortableBytes()
	{
		const string target = "/opt/DevProjex";
		var original = TerminalCommandSetupService.BuildWrapperContent(target);
		using (Sandbox())
		{
			var wrapper = TerminalCommandSetupService.BuildWrapperContent(target);
			Assert.Contains("exec flatpak run " + AppId + " \"$@\"\n", wrapper);
			Assert.Contains("# target: " + target, wrapper);
			Assert.DoesNotContain("exec '/opt/DevProjex'", wrapper);
		}
		Assert.Equal(original, TerminalCommandSetupService.BuildWrapperContent(target));
	}

	[Theory]
	[InlineData(McpConnectionClient.ClaudeCode, "&& claude")]
	[InlineData(McpConnectionClient.Codex, "&& codex")]
	[InlineData(McpConnectionClient.Cursor, "cursor ")]
	[InlineData(McpConnectionClient.VsCode, "code ")]
	public async Task ClientLaunchReturnsCopyCommandWithoutStartingAnyProcess(McpConnectionClient client, string command)
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var service = new McpClientLaunchService(Localization());
		var result = await service.OpenAsync(new(client, directory.Path), TestContext.Current.CancellationToken);
		Assert.Equal(McpClientLaunchStatus.CopyCommand, result.Status);
		Assert.Contains(command, result.ManualCommand);
		Assert.Contains("own terminal", result.ErrorMessage);
	}

	[Fact]
	public void ManagedShimRepairsBothDirectionsWithoutTouchingForeignFiles()
	{
		using var directory = new TemporaryDirectory();
		var executable = directory.CreateFile("DevProjex", "placeholder");
		var bin = Path.Combine(directory.Path, ".local", "bin");
		var service = new TerminalCommandSetupService(new TerminalCommandSetupServiceOptions
		{
			Platform = TerminalCommandHostPlatform.Linux,
			HomeDirectoryProvider = () => directory.Path,
			ExecutablePathProvider = () => executable,
			PathVariableProvider = () => bin,
			ProcessPathVariableWriter = _ => { }
		});
		Assert.True(service.InstallOrRepair().Success);
		var path = Path.Combine(bin, "devprojex");
		var original = File.ReadAllText(path);
		using (Sandbox())
		{
			Assert.Equal(TerminalCommandSetupState.Stale, service.Probe().State);
			Assert.True(service.InstallOrRepair().Success);
			Assert.Contains("exec flatpak run " + AppId, File.ReadAllText(path));
			Assert.Equal(TerminalCommandSetupState.Installed, service.Probe().State);
		}
		Assert.Equal(TerminalCommandSetupState.Stale, service.Probe().State);
		Assert.True(service.InstallOrRepair().Success);
		Assert.Equal(original, File.ReadAllText(path));
		File.WriteAllText(path, "foreign command");
		using (Sandbox())
			Assert.False(service.InstallOrRepair().Success);
		Assert.Equal("foreign command", File.ReadAllText(path));
	}

	[Fact]
	public void DocumentPathsAreNeverPrintedAsHostClientRoots()
	{
		using var sandbox = Sandbox();
		var service = new DevProjex.Infrastructure.TerminalCommands.McpConnectionService(Localization());
		var text = service.CreatePrintableConfiguration(new(McpConnectionClient.Cursor,
			McpConnectionMode.Live, Path.GetFullPath("DevProjex"), "/run/user/1000/doc/abc/project"));
		Assert.DoesNotContain("/run/user/", text);
		Assert.DoesNotContain("--filesystem=", text);
		Assert.Contains("document portal", text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MissingOrUnstartableClientFallsBackWithoutRawErrors(bool found)
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var runner = new FailedRunner();
		var service = new DevProjex.Infrastructure.TerminalCommands.McpConnectionService(Localization(),
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Linux,
				PathVariableProvider = () => directory.Path,
				FileExists = _ => found,
				IsExecutable = _ => found
			}), runner, new McpProjectConfigurationWriter());
		var request = new McpConnectionRequest(McpConnectionClient.Codex, McpConnectionMode.Standard,
			Path.Combine(directory.Path, "DevProjex"), directory.Path);
		var result = await service.ConnectAsync(request, TestContext.Current.CancellationToken);
		Assert.Equal(McpConnectionStatus.ManualConfiguration, result.Status);
		Assert.Contains("Could not verify", result.UserMessage);
		Assert.Contains("codex mcp add devprojex -- flatpak run " + AppId, result.ManualConfiguration);
		Assert.Null(result.CommandOutput);
		Assert.DoesNotContain("SECRET-ERROR", result.UserMessage);
		var inspection = await service.InspectAsync(request, TestContext.Current.CancellationToken);
		Assert.False(inspection.Succeeded);
		Assert.Contains("Could not verify", inspection.ErrorMessage);
	}

	[Theory]
	[InlineData("/run/user/1000/doc/abc/project", "/home/person/project")]
	[InlineData("/run/user/1000/doc/abc/project/src/test.cs", "/home/person/project/src/test.cs")]
	public async Task DocumentPortalMapsOnlyTheDisplayPath(string path, string expected)
	{
		using var sandbox = Sandbox();
		var resolver = new FlatpakHostPathResolver((id, _) => Task.FromResult<string?>(id == "abc" ? "/home/person/project" : null));
		Assert.Equal(expected, await resolver.ResolveAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task PortalFailureFallsBackToTheDocumentPath()
	{
		using var sandbox = Sandbox();
		const string path = "/run/user/1000/doc/abc/project";
		var resolver = new FlatpakHostPathResolver((_, _) => throw new IOException());
		Assert.Equal(path, await resolver.ResolveAsync(path, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task OrdinaryChannelNeverCallsPortalAndRetainsShellLinkLaunch()
	{
		using var ordinary = FlatpakEnvironment.UseForTesting(new(false, null));
		var resolver = new FlatpakHostPathResolver((_, _) => throw new InvalidOperationException());
		Assert.Equal("/run/user/1000/doc/abc/project", await resolver.ResolveAsync(
			"/run/user/1000/doc/abc/project", TestContext.Current.CancellationToken));
		System.Diagnostics.ProcessStartInfo? captured = null;
		Assert.False(ExternalLinkLauncher.TryOpen("https://example.test", start => { captured = start; return null; }));
		Assert.True(captured!.UseShellExecute);
		Assert.Equal("https://example.test", captured.FileName);
	}

	[Fact]
	public void FlatpakFileManagerAndLinksUsePortalHelperWithoutHostBusAccess()
	{
		using var sandbox = Sandbox();
		var start = Assert.Single(ProjectPathStartInfoFactory.CreateCandidates(DesktopPlatform.Linux, "/home/person/a.txt", false));
		Assert.Equal("xdg-open", start.StartInfo.FileName);
		Assert.Equal("/home/person", Assert.Single(start.StartInfo.ArgumentList));
		System.Diagnostics.ProcessStartInfo? captured = null;
		ExternalLinkLauncher.TryOpen("https://example.test", info => { captured = info; return null; });
		Assert.Equal("xdg-open", captured!.FileName);
		Assert.Equal("https://example.test", Assert.Single(captured.ArgumentList));
	}

	[Fact]
	public async Task FlatpakCannotOpenUpdatePopoverOrCallUpdateService()
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		using var model = new MainWindowViewModel(Localization(), new HelpContentProvider());
		using var coordinator = new ApplicationUpdateCoordinator(model, new RejectUpdateService(),
			new UserSettingsStore(() => directory.Path), "5.2.1");
		await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
		await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
		await coordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);
		model.UpdatePopoverOpen = true;
		Assert.False(model.UpdatePopoverOpen);
		Assert.False(model.IsUpdateCheckButtonVisible);
		Assert.Contains("Updated through Flathub", model.HelpAboutBody);
		model.SetFlatpakDiagnostics("git version 2.54", "/home/person/.var/app/" + AppId + "/data");
		Assert.Contains("bundled git: git version 2.54", model.HelpAboutBody);
	}

	[Fact]
	public async Task FileManagerPortalFailureDoesNotFallThroughToAHostService()
	{
		using var sandbox = Sandbox();
		var calls = new List<string>();
		var launcher = new ProjectPathLauncher(DesktopPlatform.Linux, _ => true, _ => true,
			(candidate, _) =>
			{
				calls.Add(candidate.StartInfo.FileName);
				return Task.FromResult(false);
			});
		var result = await launcher.LaunchAsync("/home/person/project", true, TestContext.Current.CancellationToken);
		Assert.False(result.Succeeded);
		Assert.Equal("xdg-open", Assert.Single(calls));
	}

	[Fact]
	public void AskPassKeepsCredentialsOutOfTheHelperAndDisablesCredentialHelpers()
	{
		using var sandbox = Sandbox();
		string helper;
		using (var session = GitAskPassSession.Create(new("https://example.test/private.git", "person", "test-password-only")))
		{
			helper = session.HelperPath;
			Assert.DoesNotContain("test-password-only", File.ReadAllText(helper));
			var start = new System.Diagnostics.ProcessStartInfo("git");
			session.Apply(start);
			Assert.Equal(helper, start.Environment["GIT_ASKPASS"]);
			Assert.Equal(string.Empty, start.Environment["GIT_CONFIG_VALUE_1"]);
			Assert.Equal("credential.helper", start.Environment["GIT_CONFIG_KEY_1"]);
		}
		Assert.False(File.Exists(helper));
	}

	[Fact]
	public void LongFlatpakSocketPathsFailRatherThanUsingAPerLaunchTemporaryDirectory()
	{
		using var sandbox = Sandbox();
		using var directory = new TemporaryDirectory();
		var root = directory.CreateFolder(new string('x', 100));
		var paths = new DesktopControlPaths(() => root);
		Assert.Throws<IOException>(() => paths.GetSocketPath("12345678901234567890"));
	}

	[Fact]
	public void AllTwentyLocalesHaveNaturalFlatpakStringsAndMatchingPlaceholders()
	{
		var catalog = new JsonLocalizationCatalog();
		var languages = Enum.GetValues<AppLanguage>();
		Assert.Equal(20, languages.Length);
		foreach (var language in languages)
		{
			var strings = catalog.Get(language);
			foreach (var key in catalog.Get(AppLanguage.En).Keys.Where(static key => key.StartsWith("Flatpak.")))
			{
				Assert.True(strings.TryGetValue(key, out var value), $"{language}: {key}");
				Assert.False(string.IsNullOrWhiteSpace(value));
				if (language != AppLanguage.En)
					Assert.NotEqual(catalog.Get(AppLanguage.En)[key], value);
			}
			Assert.Contains("{0}", strings["Flatpak.PathUnavailable"]);
			Assert.Contains("{2}", strings["Flatpak.Environment"]);
		}
	}

	[Fact]
	public void MissingPathUsesAnAmbiguousExplanationAndShellSafeOverride()
	{
		using var sandbox = Sandbox();
		var message = McpConnectionProjectRoot.DescribeProblem(Localization(), "/mnt/missing project");
		Assert.Contains("may not exist or may be inaccessible", message);
		Assert.Contains("flatpak override --user '--filesystem=/mnt/missing project' " + AppId, message);
	}

	private sealed class FailedRunner : IMcpConnectionProcessRunner
	{
		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken) =>
			Task.FromResult(new McpConnectionProcessResult(null, "", "", StartError: "SECRET-ERROR"));
	}

	private sealed class RejectUpdateService : IApplicationUpdateService
	{
		public Task<ApplicationUpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("Flatpak must never call the update service.");
	}
}
