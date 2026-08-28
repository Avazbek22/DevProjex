using System.Diagnostics;
using System.Text.Json;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalPolishTests
{
	[Fact]
	public void ColumnLayoutAddsHeadersAndMiddleTruncatesOnlyForInteractiveOutput()
	{
		string[][] rows = [["repository", "C:/very/long/project/cache/path/with/a-tail"]];
		string[] headers = ["Type", "Path"];
		var interactive = new TestTerminalEnvironment
		{
			IsOutputInteractive = true,
			HasAttachedConsole = true,
			IsTerminalHost = true,
			Width = 30
		};

		var tty = TerminalColumnLayout.FormatForOutput(
			rows,
			headers,
			interactive,
			new TerminalOutputOptions(),
			truncationColumn: 1);
		var redirected = TerminalColumnLayout.FormatForOutput(
			rows,
			headers,
			new TestTerminalEnvironment { Width = 30 },
			new TerminalOutputOptions(),
			truncationColumn: 1);

		Assert.Equal(2, tty.Count);
		Assert.Contains('…', tty[1]);
		Assert.Single(redirected);
		Assert.DoesNotContain('…', redirected[0]);
		Assert.All(tty.Concat(redirected), static line => Assert.DoesNotContain('\t', line));
	}

	[Fact]
	public void FocusModelCapturesAndRestoresPaneSectionAndAggregateTogether()
	{
		var model = new WorkspaceFocusModel
		{
			Pane = TerminalWorkspacePane.Controls,
			ControlSection = TerminalControlSection.Exclusions,
			AggregateSection = TerminalControlSection.Exclusions
		};
		var snapshot = model.Capture();
		model.Pane = TerminalWorkspacePane.Tree;
		model.ControlSection = TerminalControlSection.Content;

		model.Restore(snapshot);

		Assert.Equal(TerminalWorkspacePane.Controls, model.Pane);
		Assert.Equal(TerminalControlSection.Exclusions, model.ControlSection);
		Assert.Equal(TerminalControlSection.Exclusions, model.AggregateSection);
	}

	[Fact]
	public void OperationCoordinatorReplacesCancelsAndCompletesNamedOperations()
	{
		using var session = new CancellationTokenSource();
		using var coordinator = new AsyncOperationCoordinator(session.Token);
		var first = coordinator.Start(WorkspaceOperationKind.Preview);
		var second = coordinator.Start(WorkspaceOperationKind.Preview);

		Assert.True(first.IsCancellationRequested);
		Assert.True(coordinator.IsCurrent(WorkspaceOperationKind.Preview, second));
		coordinator.Track(WorkspaceOperationKind.Preview, second, Task.CompletedTask);
		Assert.Same(Task.CompletedTask, coordinator.GetTask(WorkspaceOperationKind.Preview));

		coordinator.Complete(WorkspaceOperationKind.Preview, second);
		Assert.False(coordinator.IsRunning(WorkspaceOperationKind.Preview));
	}

	[Fact]
	public void CommandCatalogHasOneGrammarDescriptorForEveryVerb()
	{
		Assert.Equal(Enum.GetValues<TerminalWorkspaceCommandVerb>().Length,
			TerminalWorkspaceCommandCatalog.All.Count);
		Assert.Equal(TerminalWorkspaceCommandCatalog.All.Count,
			TerminalWorkspaceCommandCatalog.All.Select(static item => item.Id).Distinct().Count());
		Assert.All(TerminalWorkspaceCommandCatalog.All,
			static definition => Assert.False(string.IsNullOrWhiteSpace(definition.Token)));
	}

	[Theory]
	[InlineData(160, 24, TerminalWorkspaceLayoutMode.Split)]
	[InlineData(160, 30, TerminalWorkspaceLayoutMode.Wide)]
	public void WideLowLayoutUsesTwoPanels(int width, int height, TerminalWorkspaceLayoutMode expected) =>
		Assert.Equal(expected, TerminalWorkspaceLayout.Resolve(width, height));

	[Fact]
	public void RealProcessAcceptsTerminalPolishFlagsAndAtomicallyReplacesTreeOutput()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/App.cs", "internal sealed class App { }\n");
		var dataRoot = workspace.CreateDirectory("data");
		var application = PublishedApplicationLocator.FindApplicationAssembly();

		var analysis = RunApplication(
			application,
			dataRoot,
			"analyze", project,
			"--hide-secrets", "on",
			"--no-strip-comments",
			"--git-mode", "none",
			"--exclude", "none",
			"--format", "json",
			"--plain",
			"--quiet");

		Assert.Equal(CommandLineExitCodes.Success, analysis.ExitCode);
		Assert.Empty(analysis.StandardError);
		using (var document = JsonDocument.Parse(analysis.StandardOutput))
			Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());

		var output = Path.Combine(workspace.Path, "tree.txt");
		File.WriteAllText(output, "stale");
		var tree = RunApplication(
			application,
			dataRoot,
			"tree", project,
			"-o", output,
			"--force",
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"-q");

		Assert.Equal(CommandLineExitCodes.Success, tree.ExitCode);
		Assert.Empty(tree.StandardError);
		Assert.Equal(Path.GetFullPath(output) + Environment.NewLine, tree.StandardOutput);
		Assert.Contains("App.cs", File.ReadAllText(output), StringComparison.Ordinal);
		Assert.DoesNotContain("stale", File.ReadAllText(output), StringComparison.Ordinal);
	}

	private static TerminalTestProcessResult RunApplication(
		string application,
		string dataRoot,
		params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(application);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return TerminalTestProcess.Run(startInfo);
	}
}
