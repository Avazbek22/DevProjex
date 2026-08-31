namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class TerminalReleaseContractRegressionTests
{
	[Fact]
	public void MarkerlessReadableDirectoryIsAValidCurrentWorkspace()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "plain directory");

		var context = TerminalWelcomePolicy.Create(workspace.Path, []);

		Assert.True(TerminalWelcomePolicy.IsSafeProjectWorkspace(workspace.Path));
		Assert.True(context.CanOpenCurrentDirectory);
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData("1", true)]
	[InlineData(" ", true)]
	public void NoColorRequiresANonEmptyValue(string? value, bool expected)
	{
		using var environmentVariable = new EnvironmentVariableScope("NO_COLOR", value);

		var environment = new InvocationEnvironment(hasAttachedConsole: false);

		Assert.Equal(expected, environment.IsNoColor);
	}

	[Theory]
	[InlineData(null, null, false, TerminalScreenMode.Alternate)]
	[InlineData("tmux-session", null, false, TerminalScreenMode.Inline)]
	[InlineData(null, "zellij-session", false, TerminalScreenMode.Inline)]
	[InlineData(null, null, true, TerminalScreenMode.Inline)]
	public void AutomaticScreenModeUsesDocumentedConservativeSignals(
		string? tmux,
		string? zellij,
		bool isCi,
		TerminalScreenMode expected)
	{
		var environment = new TestTerminalEnvironment
		{
			IsCi = isCi,
			Variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["TMUX"] = tmux,
				["ZELLIJ"] = zellij
			}
		};

		Assert.Equal(
			expected,
			TerminalScreenModeResolver.Resolve(TerminalScreenMode.Auto, environment));
	}

	[Theory]
	[InlineData(TerminalScreenMode.Inline)]
	[InlineData(TerminalScreenMode.Alternate)]
	public void ExplicitScreenModeOverridesAutomaticSignals(TerminalScreenMode requested)
	{
		var environment = new TestTerminalEnvironment
		{
			IsCi = true,
			Variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["TMUX"] = "tmux-session",
				["ZELLIJ"] = "zellij-session"
			}
		};

		Assert.Equal(requested, TerminalScreenModeResolver.Resolve(requested, environment));
	}

	[Theory]
	[InlineData(TerminalMouseMode.Auto, true, true, false, true)]
	[InlineData(TerminalMouseMode.Auto, true, false, false, false)]
	[InlineData(TerminalMouseMode.Auto, true, true, true, false)]
	[InlineData(TerminalMouseMode.Enabled, false, false, true, true)]
	[InlineData(TerminalMouseMode.Disabled, true, true, false, false)]
	public void MouseModeKeepsAutoAndExplicitSessionPoliciesDistinct(
		TerminalMouseMode mode,
		bool inputInteractive,
		bool outputInteractive,
		bool termDumb,
		bool expected)
	{
		var environment = new TestTerminalEnvironment
		{
			IsInputInteractive = inputInteractive,
			IsOutputInteractive = outputInteractive,
			IsTermDumb = termDumb
		};

		Assert.Equal(expected, TerminalWorkspace.ResolveMouseEnabled(mode, environment));
	}

	[Fact]
	public async Task SessionCompletionRunsWhenStartFails()
	{
		var expected = new InvalidOperationException("start failed");
		var runCalled = false;
		var completionCalled = false;

		var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			TerminalWorkspace.RunSessionLifecycleAsync(
				() => throw expected,
				() =>
				{
					runCalled = true;
					return Task.CompletedTask;
				},
				() =>
				{
					completionCalled = true;
					return Task.CompletedTask;
				}));

		Assert.Same(expected, actual);
		Assert.False(runCalled);
		Assert.True(completionCalled);
	}

	[Fact]
	public async Task EquivalentCommandDoesNotEmitShellMetacharacterValuesAsRawTokens()
	{
		using var workspace = new TemporaryDirectory();
		const string projectName = "project space'$HOME&%DPX%!^()`Ж";
		var project = workspace.CreateDirectory(projectName);
		workspace.WriteFile($"{projectName}/src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var destination = Path.Combine(workspace.Path, "result$HOME&%DPX%!^()Ж.zip");

		var representation = TerminalWorkspaceController.BuildEquivalentProjectCommand(
			state,
			ProjectCopyExportFormat.Zip,
			destination);

		var arguments = ParseArgumentVector(representation);

		Assert.Equal("devprojex", arguments[0]);
		Assert.Equal(["export", "project"], arguments[1..3]);
		Assert.Contains(state.Plan.SourceRoot, arguments);
		Assert.Contains(Path.GetFullPath(destination), arguments);
		Assert.Equal(arguments.Length, representation.Split(Environment.NewLine).Length);
		Assert.DoesNotContain(
			$"project {state.Plan.SourceRoot} --as",
			representation,
			StringComparison.Ordinal);

		var contextDestination = Path.Combine(workspace.Path, "context '$HOME&%DPX%`Ж.md");
		var contextArguments = ParseArgumentVector(
			TerminalWorkspaceController.BuildEquivalentContextCommand(
				state,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Markdown,
				contextDestination,
				dryRun: true));

		Assert.Equal(["devprojex", "export", "context"], contextArguments[..3]);
		Assert.Contains(Path.GetFullPath(contextDestination), contextArguments);
		Assert.Equal("--dry-run", contextArguments[^1]);
	}

	[Fact]
	public async Task EquivalentCommandsPreserveExplicitEmptySelection()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		state.SelectNone();

		var contextArguments = ParseArgumentVector(
			TerminalWorkspaceController.BuildEquivalentContextCommand(
				state,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Markdown,
				Path.Combine(workspace.Path, "context.md")));
		var projectArguments = ParseArgumentVector(
			TerminalWorkspaceController.BuildEquivalentProjectCommand(
				state,
				ProjectCopyExportFormat.Folder,
				Path.Combine(workspace.Path, "export")));

		var expectedSource = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
		AssertEmptySelectionSource(contextArguments, expectedSource);
		AssertEmptySelectionSource(projectArguments, expectedSource);
		Assert.Empty(await SelectionPathListReader.ReadAsync(
			expectedSource,
			new TestTerminalEnvironment(),
			TestContext.Current.CancellationToken));
	}

	private static void AssertEmptySelectionSource(
		string[] arguments,
		string expectedSource)
	{
		var optionIndex = Array.IndexOf(arguments, "--select-from");
		Assert.InRange(optionIndex, 0, arguments.Length - 2);
		Assert.Equal(expectedSource, arguments[optionIndex + 1]);
		Assert.DoesNotContain("--select", arguments);
	}

	private static string[] ParseArgumentVector(string representation)
	{
		var lines = representation.Split(Environment.NewLine);
		var arguments = new string[lines.Length];
		for (var index = 0; index < lines.Length; index++)
		{
			var prefix = $"argv[{index}] = ";
			Assert.StartsWith(prefix, lines[index], StringComparison.Ordinal);
			arguments[index] = JsonSerializer.Deserialize<string>(lines[index][prefix.Length..])!;
		}
		return arguments;
	}

	private sealed class EnvironmentVariableScope : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		public EnvironmentVariableScope(string name, string? value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
