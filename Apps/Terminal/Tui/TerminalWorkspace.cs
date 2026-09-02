using System.Globalization;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

public enum TerminalMouseMode
{
	Auto,
	Enabled,
	Disabled
}

public sealed record TerminalWorkspaceOptions(
	string ProjectPath,
	ProjectProfileReference Profile,
	TerminalScreenMode ScreenMode,
	TerminalMouseMode MouseMode,
	TerminalColorMode ColorMode,
	bool Plain,
	bool ShowWelcome = false);

public sealed class TerminalWorkspace
{
	private readonly TerminalServices services;
	private readonly ITerminalEnvironment environment;
	private readonly ITerminalOperationObserver operationObserver;

	public TerminalWorkspace(
		TerminalServices services,
		ITerminalEnvironment environment)
		: this(
			services,
			environment,
			NullTerminalOperationObserver.Instance)
	{
	}

	internal TerminalWorkspace(
		TerminalServices services,
		ITerminalEnvironment environment,
		ITerminalOperationObserver operationObserver)
	{
		this.services = services ??
			throw new ArgumentNullException(nameof(services));
		this.environment = environment ??
			throw new ArgumentNullException(nameof(environment));
		this.operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
	}

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

		var mouseEnabled = ResolveMouseEnabled(options.MouseMode, environment);
		using var mousePolicy = new TerminalGuiMousePolicy(mouseEnabled);
		// Terminal.Gui can leave the cursor hidden on either side of application
		// disposal, so bracket its teardown with idempotent visibility restoration.
		using var postApplicationCursorRestoration =
			new TerminalCursorRestoration(environment.Output);
		using var inputShutdownGuard = new WindowsTerminalInputShutdownGuard();
		using var inputShutdownRegistration = cancellationToken.Register(inputShutdownGuard.Arm);
		using IApplication application = global::Terminal.Gui.App.Application.Create();
		using var preDisposeCursorRestoration = new TerminalCursorRestoration(environment.Output);
		application.Mouse.IsMouseDisabled = !mouseEnabled;
		var initialized = false;
		Window? root = null;
		try
		{
			var screenMode = TerminalScreenModeResolver.Resolve(options.ScreenMode, environment);
			application.AppModel = screenMode == TerminalScreenMode.Inline
				? AppModel.Inline
				: AppModel.FullScreen;
			application.Init();
			initialized = true;
			application.Mouse.IsMouseDisabled = !mouseEnabled;
			if (!mouseEnabled)
			{
				// Terminal.Gui enables tracking during ANSI driver initialization.
				// Disable it before session input is processed.
				application.Driver?.WriteRaw(
					global::Terminal.Gui.Drivers.EscSeqUtils.CSI_DisableMouseEvents);
			}
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
				operationObserver,
				cancellationToken,
				inputShutdownGuard.Arm);
			await RunSessionLifecycleAsync(
				session.Start,
				() => application.RunAsync(root, cancellationToken),
				session.CompleteAsync).ConfigureAwait(false);

			return cancellationToken.IsCancellationRequested && !session.ExitRequested
				? CommandLineExitCodes.Canceled
				: CommandLineExitCodes.Success;
		}
		finally
		{
			// Terminal.Gui 2.4.17 (#5442) can race its one-shot CancelIoEx against a new
			// synchronous Windows console read. Keep cancellation active until the
			// application disposal below has actually joined the input worker.
			inputShutdownGuard.Arm();
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

	internal static bool ResolveMouseEnabled(
		TerminalMouseMode mode,
		ITerminalEnvironment terminalEnvironment) =>
		mode switch
		{
			TerminalMouseMode.Auto =>
				terminalEnvironment.IsInputInteractive &&
				terminalEnvironment.IsOutputInteractive &&
				!terminalEnvironment.IsTermDumb,
			TerminalMouseMode.Enabled => true,
			TerminalMouseMode.Disabled => false,
			_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
		};

	internal static async Task RunSessionLifecycleAsync(
		Action start,
		Func<Task> runAsync,
		Func<Task> completeAsync)
	{
		ArgumentNullException.ThrowIfNull(start);
		ArgumentNullException.ThrowIfNull(runAsync);
		ArgumentNullException.ThrowIfNull(completeAsync);
		try
		{
			start();
			await runAsync().ConfigureAwait(false);
		}
		finally
		{
			await completeAsync().ConfigureAwait(false);
		}
	}

	private sealed class TerminalGuiMousePolicy : IDisposable
	{
		private readonly bool _previousValue =
			global::Terminal.Gui.Configuration.ApplicationSettings.Defaults.IsMouseDisabled;

		public TerminalGuiMousePolicy(bool mouseEnabled)
		{
			global::Terminal.Gui.Configuration.ApplicationSettings.Defaults.IsMouseDisabled =
				!mouseEnabled;
		}

		public void Dispose()
		{
			global::Terminal.Gui.Configuration.ApplicationSettings.Defaults.IsMouseDisabled =
				_previousValue;
		}
	}

	private sealed class TerminalCursorRestoration(TextWriter output) : IDisposable
	{
		public void Dispose()
		{
			output.Write(global::Terminal.Gui.Drivers.EscSeqUtils.CSI_ShowCursor);
			output.Flush();
		}
	}

	internal static string? CompletePrompt(bool accepted, string text) =>
		accepted ? text : null;

	internal static bool TryToggleTreeRow(TerminalWorkspaceState state, int? selectedRow)
	{
		if (selectedRow is null || selectedRow < 0 || selectedRow >= state.VisibleRows.Count)
			return false;

		state.ToggleSelection(selectedRow.Value);
		return true;
	}

	internal string BuildExportSummaryText(
		TerminalExportSummary summary,
		int maximumValueColumns = int.MaxValue)
	{
		var gitMode = summary.GitMode == GitFilteringMode.Diff
			? GitScopeSelection.ToToken(summary.GitMode, summary.GitDiffRange)
			: L(ProjectPresentationCatalog.Get(summary.GitMode).LabelKey);
		var exclusions = summary.Exclusions.Count == 0
			? L("Terminal.Tui.NoneSelected")
			: string.Join(", ", summary.Exclusions.Select(LocalizeExclusion));
		string Fit(string value) => maximumValueColumns == int.MaxValue
			? value
			: TerminalParameterRow.FitLabel(value, maximumValueColumns, useUnicode: true);
		var rows = new List<string[]>
		{
			new[]
			{
				L("Terminal.Tui.Destination").TrimEnd(':'),
				Fit(TerminalTextEscaping.EscapeSingleLine(summary.Destination))
			},
			new[] { L("Terminal.Analysis.Files"), summary.FileCount.ToString("N0", CultureInfo.CurrentCulture) },
			new[] { L("Terminal.Analysis.Folders"), summary.FolderCount.ToString("N0", CultureInfo.CurrentCulture) },
			new[] { L("Terminal.Analysis.Size"), FormatBytes(summary.Bytes) },
			new[] { L("Terminal.Analysis.Tokens"), summary.EstimatedTokens.ToString("N0", CultureInfo.CurrentCulture) },
			new[] { L("Terminal.Tui.Filters"), Fit($"{gitMode}; {exclusions}") },
			new[] { L("Terminal.Tui.Diagnostics"), summary.DiagnosticCount.ToString("N0", CultureInfo.CurrentCulture) }
		};
		var redactionKey = (summary.SecretsRedacted, summary.PrivateDataRedacted) switch
		{
			(true, true) => "Terminal.Tui.Export.RedactionInline",
			(true, false) => "Terminal.Tui.Export.SecretsOnlyRedactionInline",
			(false, true) => "Terminal.Tui.Export.PrivateDataOnlyRedactionInline",
			_ => null
		};
		if (redactionKey is not null)
			rows.Add([L("Terminal.Tui.Redaction"), L(redactionKey)]);
		return string.Join(Environment.NewLine, TerminalColumnLayout.Format(rows));
	}

	internal string LocalizeView(ProjectContextView view) =>
		L(ProjectPresentationCatalog.Get(view).LabelKey);

	internal string LocalizeExclusion(ProjectExclusion exclusion) =>
		L(ProjectPresentationCatalog.Get(exclusion).LabelKey);

	internal static string FormatContextFormat(ProjectContextDocumentFormat format) =>
		ProjectPresentationCatalog.Get(format).UserLabel;

	internal static string FormatBytes(long bytes, IFormatProvider? formatProvider = null)
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
			? $"{value.ToString(formatProvider)} {units[unit]}"
			: $"{display.ToString("0.##", formatProvider)} {units[unit]}";
	}

	internal string FormatCount(long value) =>
		value.ToString("N0", CultureInfo.CurrentCulture);

	internal string L(string key) => services.Localization[key];
}
