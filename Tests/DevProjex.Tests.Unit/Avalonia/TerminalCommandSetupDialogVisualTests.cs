using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class TerminalCommandSetupDialogVisualTests
{
	[AvaloniaFact]
	public void ResolveDialogBrushes_UsesSolidBackgroundColorInsteadOfTransparentThemeBrush()
	{
		var app = global::Avalonia.Application.Current;
		Assert.NotNull(app);

		var resources = app!.Resources;
		var originalBackgroundBrush = CaptureResource(resources, "AppBackgroundBrush", out var hadBackgroundBrush);
		var originalBackgroundColor = CaptureResource(resources, "AppBackgroundColor", out var hadBackgroundColor);

		try
		{
			resources["AppBackgroundBrush"] = Brushes.Transparent;
			resources["AppBackgroundColor"] = Colors.White;

			var brushes = TerminalCommandSetupDialog.ResolveDialogBrushes(null, ThemeVariant.Default);
			var background = Assert.IsType<SolidColorBrush>(brushes.Background);

			Assert.Equal(Colors.White, background.Color);
		}
		finally
		{
			RestoreResource(resources, "AppBackgroundBrush", originalBackgroundBrush, hadBackgroundBrush);
			RestoreResource(resources, "AppBackgroundColor", originalBackgroundColor, hadBackgroundColor);
		}
	}

	[Fact]
	public void DialogDimensions_UsesCompactSizeForAutomaticPromptAndInstalledManualContent()
	{
		var detailedManualContent = new TerminalCommandDialogText(
			"Title",
			"Body",
			"Details",
			"devprojex --help",
			"devprojex",
			ShowCopyButton: false,
			InstallButtonText: "Enable",
			ShowInstallButton: true);
		var compactManualContent = detailedManualContent with
		{
			Details = string.Empty,
			CommandLine = string.Empty,
			ShowInstallButton = false
		};
		var automatic = TerminalCommandDialogDimensions.ForContent(isAutomaticPrompt: true, detailedManualContent);
		var manual = TerminalCommandDialogDimensions.ForContent(isAutomaticPrompt: false, detailedManualContent);
		var compactManual = TerminalCommandDialogDimensions.ForContent(isAutomaticPrompt: false, compactManualContent);

		Assert.Equal(480, automatic.Width);
		Assert.Equal(180, automatic.Height);
		Assert.True(automatic.Width < manual.Width);
		Assert.True(automatic.Height < manual.Height);
		Assert.True(compactManual.Width < manual.Width);
		Assert.True(compactManual.Height < manual.Height);
		Assert.True(compactManual.Height > automatic.Height);
	}

	[AvaloniaFact]
	public void BuildContent_CentersAutomaticPromptButtonCaptions()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);
		var text = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt: true);
		var dontShowAgain = new CheckBox();
		var completion = new TaskCompletionSource<TerminalCommandDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

		var content = InvokeBuildContent(new Window(), localization, text, snapshot, isAutomaticPrompt: true, dontShowAgain, completion);
		var buttons = FindDescendants<Button>(content)
			.Where(static button => button is not CheckBox)
			.ToArray();

		Assert.Equal(2, buttons.Length);
		Assert.All(buttons, button =>
		{
			Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
			Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
		});
	}

	[AvaloniaFact]
	public void BuildContent_AutomaticPromptOmitsBodyTitleAndPlacesCheckboxInFooter()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);
		var text = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt: true);
		var dontShowAgain = new CheckBox { Content = localization["Dialog.TerminalCommand.DontShowAgain"] };
		var completion = new TaskCompletionSource<TerminalCommandDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

		var content = InvokeBuildContent(new Window(), localization, text, snapshot, isAutomaticPrompt: true, dontShowAgain, completion);
		var visibleTextBlocks = FindDescendants<TextBlock>(content).Select(ReadTextBlockText).ToArray();
		var footer = FindDescendants<Grid>(content)
			.Single(grid => grid.Children.Contains(dontShowAgain));

		Assert.DoesNotContain(text.Title, visibleTextBlocks);
		Assert.Contains(text.Body, visibleTextBlocks);
		Assert.Contains(footer.Children, child => child is StackPanel stackPanel && stackPanel.Children.OfType<Button>().Any());
		Assert.Equal(VerticalAlignment.Center, dontShowAgain.VerticalAlignment);
	}

	[AvaloniaFact]
	public void BuildContent_AutomaticPromptBoldsEveryDevprojexInline()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var snapshot = new TerminalCommandSetupSnapshot(
			CommandLineExecutableAliases.UnixCommand,
			TerminalCommandSetupState.NotInstalled,
			CommandPath: @"C:\Users\me\AppData\Local\DevProjex\bin\devprojex.cmd",
			TargetExecutablePath: @"C:\Users\me\DevProjex\DevProjex.exe",
			InstalledTargetExecutablePath: null,
			UserBinDirectory: @"C:\Users\me\AppData\Local\DevProjex\bin",
			UserBinDirectoryIsInPath: false,
			CanInstall: true,
			CanRepair: false,
			ShellProfileHint: null);
		var text = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt: true);
		var dontShowAgain = new CheckBox();
		var completion = new TaskCompletionSource<TerminalCommandDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);

		var content = InvokeBuildContent(new Window(), localization, text, snapshot, isAutomaticPrompt: true, dontShowAgain, completion);
		var message = FindDescendants<TextBlock>(content)
			.Single(textBlock => ReadTextBlockText(textBlock) == text.Body);
		var commandRuns = message.Inlines!
			.OfType<Run>()
			.Where(static run => string.Equals(run.Text, "devprojex", StringComparison.Ordinal))
			.ToArray();

		Assert.Contains("\n\n", ReadTextBlockText(message), StringComparison.Ordinal);
		Assert.Equal(2, commandRuns.Length);
		Assert.All(commandRuns, run => Assert.Equal(FontWeight.Bold, run.FontWeight));
	}

	private static object? CaptureResource(IResourceDictionary resources, string key, out bool exists)
	{
		exists = resources.ContainsKey(key);
		return exists ? resources[key] : null;
	}

	private static void RestoreResource(IResourceDictionary resources, string key, object? value, bool exists)
	{
		if (exists)
			resources[key] = value;
		else
			resources.Remove(key);
	}

	private static Control InvokeBuildContent(
		Window owner,
		LocalizationService localization,
		TerminalCommandDialogText text,
		TerminalCommandSetupSnapshot snapshot,
		bool isAutomaticPrompt,
		CheckBox dontShowAgain,
		TaskCompletionSource<TerminalCommandDialogResult> completion)
	{
		var method = typeof(TerminalCommandSetupDialog).GetMethod(
			"BuildContent",
			BindingFlags.NonPublic | BindingFlags.Static);

		Assert.NotNull(method);
		var content = (Control?)method!.Invoke(
			null,
			[owner, localization, text, snapshot, isAutomaticPrompt, dontShowAgain, completion]);

		Assert.NotNull(content);
		return content!;
	}

	private static IEnumerable<T> FindDescendants<T>(Control root)
		where T : Control
	{
		if (root is T match)
			yield return match;

		foreach (var child in GetChildControls(root))
		{
			foreach (var descendant in FindDescendants<T>(child))
				yield return descendant;
		}
	}

	private static IEnumerable<Control> GetChildControls(Control control)
	{
		if (control is Panel panel)
		{
			foreach (var child in panel.Children.OfType<Control>())
				yield return child;
		}

		if (control is ContentControl { Content: Control contentControlChild })
			yield return contentControlChild;
	}

	private static string ReadTextBlockText(TextBlock textBlock)
	{
		if (textBlock.Inlines is { Count: > 0 } inlines)
			return string.Concat(inlines.OfType<Run>().Select(static run => run.Text));

		return textBlock.Text ?? string.Empty;
	}
}
