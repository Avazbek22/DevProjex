using System.Globalization;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

public sealed record TerminalWorkspaceOptions(
	string ProjectPath,
	ProjectProfileReference Profile,
	TerminalScreenMode ScreenMode,
	bool MouseEnabled,
	TerminalColorMode ColorMode,
	bool Plain,
	bool ShowWelcome = false);

public sealed class TerminalWorkspace(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> RunAsync(
		TerminalWorkspaceOptions options,
		CancellationToken cancellationToken)
	{
		if (!environment.IsInputInteractive ||
		    !environment.IsOutputInteractive ||
		    environment.IsTermDumb)
		{
			environment.Error.WriteLine("error[DPX-TUI-NOT-INTERACTIVE]:");
			environment.Error.WriteLine(L("Terminal.Tui.Error.NotInteractive"));
			environment.Error.WriteLine(L("Terminal.Tui.Hint.DirectCommands"));
			return CommandLineExitCodes.UsageError;
		}

		using IApplication application = global::Terminal.Gui.App.Application.Create();
		var initialized = false;
		Window? root = null;
		try
		{
			var screenMode = TerminalScreenModeResolver.Resolve(options.ScreenMode, environment);
			application.AppModel = screenMode == TerminalScreenMode.Inline
				? AppModel.Inline
				: AppModel.FullScreen;
			application.Init(SelectTerminalDriver(OperatingSystem.IsMacOS()));
			initialized = true;
			application.Mouse.IsMouseDisabled = !options.MouseEnabled;
			var rootWidth = environment.Width;
			var rootHeight = environment.Height;
			if (screenMode == TerminalScreenMode.Inline &&
			    application.Driver is { } driver)
			{
				rootWidth = Math.Max(environment.Width, driver.Screen.Width);
				rootHeight = Math.Max(environment.Height, driver.Screen.Height);
				application.ForceInlinePosition = new System.Drawing.Point(0, 0);
				application.Screen = new System.Drawing.Rectangle(0, 0, rootWidth, rootHeight);
			}

			var presentation = TerminalWorkspacePresentationPolicy.Resolve(
				options.ColorMode,
				options.Plain,
				environment);
			TerminalWorkspaceTheme.Register(presentation.UseMonochromeScheme);

			root = new Window
			{
				Width = screenMode == TerminalScreenMode.Inline ? rootWidth : Dim.Fill(),
				Height = screenMode == TerminalScreenMode.Inline ? rootHeight : Dim.Fill(),
				BorderStyle = LineStyle.None,
				SchemeName = TerminalWorkspaceTheme.Base
			};
			using var session = new TerminalWorkspaceSession(
				application,
				root,
				services,
				environment,
				options,
				this,
				cancellationToken);
			session.Start();
			try
			{
				await application.RunAsync(root, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				await session.CompleteAsync().ConfigureAwait(false);
			}

			return cancellationToken.IsCancellationRequested && !session.ExitRequested
				? CommandLineExitCodes.Canceled
				: CommandLineExitCodes.Success;
		}
		finally
		{
			if (initialized)
			{
				try
				{
					application.RequestStop(root);
				}
				catch
				{
					// Disposing the application still restores the terminal after a failed stop request.
				}
			}

			root?.Dispose();
		}
	}

	internal static string? CompletePrompt(bool accepted, string text) =>
		accepted ? text : null;

	internal static string? SelectTerminalDriver(bool isMacOs)
	{
		// Terminal.Gui 2.4.17's ANSI raw-mode helper uses the Linux termios ABI.
		// The managed driver avoids memory corruption on Darwin while retaining its ANSI parser.
		return isMacOs ? DriverRegistry.Names.DOTNET : null;
	}

	internal static bool TryToggleTreeRow(TerminalWorkspaceState state, int? selectedRow)
	{
		if (selectedRow is null || selectedRow < 0 || selectedRow >= state.VisibleRows.Count)
			return false;

		state.ToggleSelection(selectedRow.Value);
		return true;
	}

	internal string BuildExportSummaryText(TerminalExportSummary summary)
	{
		var outputKind = summary.Kind switch
		{
			TerminalExportKind.Context => L("Terminal.Tui.ExportContext"),
			TerminalExportKind.Folder => L("Terminal.Tui.Folder"),
			_ => "ZIP"
		};
		var destinationState = summary.DestinationState == TerminalExportDestinationState.Ready
			? L("Terminal.Tui.DestinationReady")
			: L("Terminal.Tui.DestinationConflict");
		var gitMode = summary.GitMode switch
		{
			GitFilteringMode.RespectGitIgnore => ".gitignore",
			GitFilteringMode.TrackedFilesOnly => L("Terminal.Tui.GitTracked"),
			_ => L("Terminal.Tui.GitNone")
		};
		var exclusions = summary.Exclusions.Count == 0
			? L("Terminal.Tui.NoneAvailable")
			: string.Join(", ", summary.Exclusions.Select(LocalizeExclusion));
		var lines = new List<string>
		{
			$"{L("Terminal.Tui.OutputKind")}: {outputKind}"
		};
		if (summary.View is { } view)
			lines.Add($"{L("Terminal.Tui.View")}: {LocalizeView(view)}");
		if (summary.DocumentFormat is { } format)
			lines.Add($"{L("Terminal.Tui.Format")}: {format}");
		lines.AddRange(
		[
			$"{L("Terminal.Analysis.Files")}: {summary.FileCount:N0}",
			$"{L("Terminal.Analysis.Folders")}: {summary.FolderCount:N0}",
			$"{L("Terminal.Analysis.Size")}: {FormatBytes(summary.Bytes)}",
			$"{L("Terminal.Analysis.Characters")}: {summary.Characters:N0}",
			$"{L("Terminal.Analysis.Tokens")}: {summary.EstimatedTokens:N0}",
			$"{L("Terminal.Tui.Destination").TrimEnd(':')}: {summary.Destination}",
			$"{L("Terminal.Tui.DestinationState")}: {destinationState}",
			$"{L("Terminal.Tui.GitFiltering")}: {gitMode}",
			$"{L("Terminal.Tui.Exclusions")}: {exclusions}",
			$"{L("Terminal.Tui.Warnings")}: {summary.DiagnosticCount:N0}"
		]);
		return string.Join(Environment.NewLine, lines);
	}

	internal string LocalizeView(ProjectContextView view) =>
		view switch
		{
			ProjectContextView.Tree => L("Preview.Mode.Tree"),
			ProjectContextView.Content => L("Preview.Mode.Content"),
			_ => L("Preview.Mode.TreeAndContent")
		};

	internal string LocalizeExclusion(ProjectExclusion exclusion) =>
		L(exclusion switch
		{
			ProjectExclusion.SmartIgnore => "Settings.Ignore.SmartIgnore",
			ProjectExclusion.HiddenFolders => "Settings.Ignore.HiddenFolders",
			ProjectExclusion.HiddenFiles => "Settings.Ignore.HiddenFiles",
			ProjectExclusion.DotFolders => "Settings.Ignore.DotFolders",
			ProjectExclusion.DotFiles => "Settings.Ignore.DotFiles",
			ProjectExclusion.EmptyFolders => "Settings.Ignore.EmptyFolders",
			ProjectExclusion.EmptyFiles => "Settings.Ignore.EmptyFiles",
			ProjectExclusion.ExtensionlessFiles => "Settings.Ignore.ExtensionlessFiles",
			_ => throw new ArgumentOutOfRangeException(nameof(exclusion), exclusion, null)
		});

	internal static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var value = Math.Max(0, bytes);
		var display = (double)value;
		var unit = 0;
		while (display >= 1024 && unit < units.Length - 1)
		{
			display /= 1024;
			unit++;
		}

		return unit == 0
			? $"{value} {units[unit]}"
			: $"{display:0.##} {units[unit]}";
	}

	internal static string QuoteForDisplay(string value) =>
		value.Any(char.IsWhiteSpace)
			? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
			: value;

	internal string FormatCount(long value) =>
		value.ToString("N0", CultureInfo.CurrentCulture);

	internal string L(string key) => services.Localization[key];
}
