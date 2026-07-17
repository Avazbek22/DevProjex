using Avalonia.Controls.Documents;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Avalonia.Services;

internal enum TerminalCommandDialogAction
{
	None,
	InstallOrRepair,
	Reinstall,
	DismissPrompt
}

internal sealed record TerminalCommandDialogResult(
	TerminalCommandDialogAction Action,
	bool DontShowAgain);

internal static class TerminalCommandSetupDialog
{
	public static async Task<TerminalCommandDialogResult> ShowAsync(
		Window owner,
		LocalizationService localization,
		TerminalCommandSetupSnapshot snapshot,
		bool isAutomaticPrompt)
	{
		var themeVariant = DialogSurfaceFactory.ResolveThemeVariant(owner);
		var brushes = ResolveDialogBrushes(owner, themeVariant);
		var content = TerminalCommandSetupDialogText.Create(localization, snapshot, isAutomaticPrompt);
		var completion = new TaskCompletionSource<TerminalCommandDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var dimensions = TerminalCommandDialogDimensions.ForContent(isAutomaticPrompt, content);

		var dontShowAgain = new CheckBox
		{
			Content = localization["Dialog.TerminalCommand.DontShowAgain"],
			IsVisible = isAutomaticPrompt && TerminalCommandPromptPolicy.IsDismissibleAutomaticPrompt(snapshot)
		};

		var body = BuildContent(owner, localization, content, snapshot, isAutomaticPrompt, dontShowAgain, completion);
		var dialog = DialogSurfaceFactory.CreateWindow(
			content.Title,
			themeVariant,
			brushes,
			body,
			dimensions.Width,
			dimensions.Height,
			dimensions.MinWidth,
			dimensions.MinHeight);

		dialog.Closed += (_, _) =>
		{
			completion.TrySetResult(new TerminalCommandDialogResult(
				TerminalCommandDialogAction.None,
				dontShowAgain.IsChecked == true));
		};

		_ = dialog.ShowDialog(owner);

		return await completion.Task.ConfigureAwait(false);
	}

	private static Control BuildContent(
		Window owner,
		LocalizationService localization,
		TerminalCommandDialogText content,
		TerminalCommandSetupSnapshot snapshot,
		bool isAutomaticPrompt,
		CheckBox dontShowAgain,
		TaskCompletionSource<TerminalCommandDialogResult> completion)
	{
		var title = new TextBlock
		{
			Text = content.Title,
			FontSize = 18,
			FontWeight = FontWeight.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12, 12, 12, 4)
		};

		dontShowAgain.Margin = new Thickness(0, 0, 12, 0);
		dontShowAgain.VerticalAlignment = VerticalAlignment.Center;

		var message = new TextBlock
		{
			TextWrapping = TextWrapping.Wrap,
			Margin = isAutomaticPrompt
				? new Thickness(12, 18, 12, 8)
				: new Thickness(12, 4, 12, 8)
		};
		if (isAutomaticPrompt)
			message.Inlines = BuildAutomaticPromptBodyInlines(content.Body);
		else
			message.Text = content.Body;

		var details = new TextBlock
		{
			Text = content.Details,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12, 0, 12, 8),
			FontSize = 12,
			Opacity = 0.82
		};

		var commandText = new SelectableTextBlock
		{
			Text = content.CommandLine,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(12, 0, 12, 8),
			FontFamily = FontFamily.Parse("Consolas,Menlo,Monospace"),
			FontSize = 13
		};

		var buttonPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0)
		};

		if (content.ShowCopyButton)
		{
			var copyButton = new Button
			{
				Content = localization["Dialog.TerminalCommand.CopyCommand"],
				MinWidth = 118,
				Margin = new Thickness(0, 0, 8, 0),
				IsEnabled = !string.IsNullOrWhiteSpace(content.CommandToCopy),
				HorizontalContentAlignment = HorizontalAlignment.Center,
				VerticalContentAlignment = VerticalAlignment.Center
			};
			copyButton.Click += async (_, _) =>
			{
				try
				{
					await owner.Clipboard!.SetTextAsync(content.CommandToCopy);
				}
				catch
				{
					// Clipboard can be unavailable in headless or restricted sessions.
				}
			};
			buttonPanel.Children.Add(copyButton);
		}

		if (content.ShowInstallButton)
		{
			var installButton = new Button
			{
				Content = content.InstallButtonText,
				MinWidth = 130,
				Margin = new Thickness(0, 0, 8, 0),
				HorizontalContentAlignment = HorizontalAlignment.Center,
				VerticalContentAlignment = VerticalAlignment.Center
			};
			if (!snapshot.CanReinstall)
				installButton.Classes.Add("primary-action");
			installButton.Click += (_, _) =>
			{
				completion.TrySetResult(new TerminalCommandDialogResult(
					snapshot.CanReinstall
						? TerminalCommandDialogAction.Reinstall
						: TerminalCommandDialogAction.InstallOrRepair,
					dontShowAgain.IsChecked == true));
				(TopLevel.GetTopLevel(buttonPanel) as Window)?.Close();
			};
			buttonPanel.Children.Add(installButton);
		}

		var closeButton = new Button
		{
			Content = isAutomaticPrompt
				? localization["Dialog.TerminalCommand.NotNow"]
				: localization["Dialog.OK"],
			MinWidth = 104,
			HorizontalContentAlignment = HorizontalAlignment.Center,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		if (snapshot.CanReinstall)
			closeButton.Classes.Add("primary-action");
		closeButton.Click += (_, _) =>
		{
			var action = dontShowAgain.IsChecked == true
				? TerminalCommandDialogAction.DismissPrompt
				: TerminalCommandDialogAction.None;
			completion.TrySetResult(new TerminalCommandDialogResult(action, dontShowAgain.IsChecked == true));
			(TopLevel.GetTopLevel(buttonPanel) as Window)?.Close();
		};
		buttonPanel.Children.Add(closeButton);

		var footer = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions("*,Auto"),
			Margin = new Thickness(12)
		};
		Grid.SetColumn(dontShowAgain, 0);
		Grid.SetColumn(buttonPanel, 1);
		footer.Children.Add(dontShowAgain);
		footer.Children.Add(buttonPanel);

		var panel = new DockPanel();
		DockPanel.SetDock(footer, Dock.Bottom);

		panel.Children.Add(footer);
		var contentStack = new StackPanel();
		if (!isAutomaticPrompt)
			contentStack.Children.Add(title);
		contentStack.Children.Add(message);

		if (!string.IsNullOrWhiteSpace(content.Details))
			contentStack.Children.Add(details);
		if (!string.IsNullOrWhiteSpace(content.CommandLine))
			contentStack.Children.Add(commandText);

		panel.Children.Add(new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			Content = contentStack
		});

		return panel;
	}

	private static InlineCollection BuildAutomaticPromptBodyInlines(string body)
	{
		const string commandName = "devprojex";
		var inlines = new InlineCollection();
		var startIndex = 0;

		while (startIndex < body.Length)
		{
			var commandIndex = body.IndexOf(commandName, startIndex, StringComparison.OrdinalIgnoreCase);
			if (commandIndex < 0)
			{
				inlines.Add(new Run(body[startIndex..]));
				break;
			}

			if (commandIndex > startIndex)
				inlines.Add(new Run(body[startIndex..commandIndex]));

			inlines.Add(new Run(body.Substring(commandIndex, commandName.Length))
			{
				FontWeight = FontWeight.Bold
			});

			startIndex = commandIndex + commandName.Length;
		}

		return inlines;
	}

	internal static DialogSurfaceBrushes ResolveDialogBrushes(Window? owner, ThemeVariant themeVariant)
	{
		return DialogSurfaceFactory.ResolveBrushes(owner, themeVariant);
	}
}

internal sealed record TerminalCommandDialogDimensions(
	double Width,
	double Height,
	double MinWidth,
	double MinHeight)
{
	public static TerminalCommandDialogDimensions ForContent(
		bool isAutomaticPrompt,
		TerminalCommandDialogText content)
	{
		if (isAutomaticPrompt)
			return new TerminalCommandDialogDimensions(480, 180, 420, 170);

		return IsCompactManualContent(content)
			? new TerminalCommandDialogDimensions(500, 190, 420, 180)
			: new TerminalCommandDialogDimensions(560, 320, 480, 280);
	}

	private static bool IsCompactManualContent(TerminalCommandDialogText content) =>
		string.IsNullOrWhiteSpace(content.Details) &&
		string.IsNullOrWhiteSpace(content.CommandLine);
}

internal sealed record TerminalCommandDialogText(
	string Title,
	string Body,
	string Details,
	string CommandLine,
	string CommandToCopy,
	bool ShowCopyButton,
	string InstallButtonText,
	bool ShowInstallButton);

internal static class TerminalCommandSetupDialogText
{
	public static TerminalCommandDialogText Create(
		LocalizationService localization,
		TerminalCommandSetupSnapshot snapshot,
		bool isAutomaticPrompt = false)
	{
		var title = localization["Dialog.TerminalCommand.Title"];
		var body = GetBody(localization, snapshot, isAutomaticPrompt);
		var details = isAutomaticPrompt ? string.Empty : GetDetails(localization, snapshot);
		var commandToCopy = GetCommandToCopy(snapshot);
		var commandLine = isAutomaticPrompt || ShouldHideCommandLine(snapshot)
			? string.Empty
			: localization.Format("Dialog.TerminalCommand.CommandLine", commandToCopy);
		var showCopyButton = !isAutomaticPrompt && ShouldShowCopyButton(snapshot, commandToCopy);
		var installText = snapshot.CanReinstall
			? localization["Dialog.TerminalCommand.Reconfigure"]
			: snapshot.CanRepair
				? localization["Dialog.TerminalCommand.Repair"]
				: localization["Dialog.TerminalCommand.Enable"];

		return new TerminalCommandDialogText(
			title,
			body,
			details,
			commandLine,
			commandToCopy,
			showCopyButton,
			installText,
			snapshot.IsActionable || snapshot.CanReinstall);
	}

	private static bool ShouldShowCopyButton(TerminalCommandSetupSnapshot snapshot, string commandToCopy)
	{
		if (string.IsNullOrWhiteSpace(commandToCopy))
			return false;

		return snapshot.State is
			TerminalCommandSetupState.ManagedByOperatingSystem or
			TerminalCommandSetupState.UnsupportedOnCurrentPackage;
	}

	private static string GetCommandToCopy(TerminalCommandSetupSnapshot snapshot)
	{
		if (snapshot.State == TerminalCommandSetupState.UnsupportedOnCurrentPackage &&
		    !string.IsNullOrWhiteSpace(snapshot.TargetExecutablePath))
			return QuoteForDisplay(snapshot.TargetExecutablePath);

		return snapshot.CommandName;
	}

	private static string QuoteForDisplay(string value)
	{
		if (!value.Any(static ch => char.IsWhiteSpace(ch) || ch == '"'))
			return value;

		return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private static string GetBody(
		LocalizationService localization,
		TerminalCommandSetupSnapshot snapshot,
		bool isAutomaticPrompt)
	{
		if (isAutomaticPrompt &&
		    snapshot.State == TerminalCommandSetupState.NotInstalled &&
		    snapshot.IsActionable)
			return localization["Dialog.TerminalCommand.AutomaticPrompt.Body"];

		if (snapshot.State == TerminalCommandSetupState.Installed && !snapshot.UserBinDirectoryIsInPath)
			return localization["Dialog.TerminalCommand.Body.InstalledPathMissing"];

		return snapshot.State switch
		{
			TerminalCommandSetupState.ManagedByOperatingSystem =>
				localization["Dialog.TerminalCommand.Body.ManagedByOS"],
			TerminalCommandSetupState.UnsupportedOnCurrentPackage =>
				localization["Dialog.TerminalCommand.Body.UnsupportedPackage"],
			TerminalCommandSetupState.UnsupportedOnCurrentPlatform =>
				localization["Dialog.TerminalCommand.Body.UnsupportedPlatform"],
			TerminalCommandSetupState.HomeDirectoryUnavailable =>
				localization["Dialog.TerminalCommand.Body.HomeMissing"],
			TerminalCommandSetupState.NotInstalled =>
				localization["Dialog.TerminalCommand.Body.NotInstalled"],
			TerminalCommandSetupState.Installed =>
				localization["Dialog.TerminalCommand.Body.Installed"],
			TerminalCommandSetupState.Stale =>
				localization["Dialog.TerminalCommand.Body.Stale"],
			TerminalCommandSetupState.ConflictingCommand =>
				localization["Dialog.TerminalCommand.Body.Conflict"],
			TerminalCommandSetupState.PermissionDenied =>
				localization["Dialog.TerminalCommand.Body.PermissionDenied"],
			_ => localization["Dialog.TerminalCommand.Body.Failed"]
		};
	}

	private static string GetDetails(LocalizationService localization, TerminalCommandSetupSnapshot snapshot)
	{
		if (snapshot.State == TerminalCommandSetupState.Installed)
		{
			return string.IsNullOrWhiteSpace(snapshot.ShellProfileHint)
				? string.Empty
				: localization.Format("Dialog.TerminalCommand.Detail.PathHint", snapshot.ShellProfileHint);
		}

		var lines = new List<string>();

		if (snapshot.State != TerminalCommandSetupState.UnsupportedOnCurrentPackage)
			lines.Add(localization.Format("Dialog.TerminalCommand.Detail.Command", snapshot.CommandName));

		if (!string.IsNullOrWhiteSpace(snapshot.CommandPath))
			lines.Add(localization.Format("Dialog.TerminalCommand.Detail.CommandPath", snapshot.CommandPath));
		if (!string.IsNullOrWhiteSpace(snapshot.TargetExecutablePath))
			lines.Add(localization.Format("Dialog.TerminalCommand.Detail.Target", snapshot.TargetExecutablePath));
		if (!string.IsNullOrWhiteSpace(snapshot.InstalledTargetExecutablePath))
			lines.Add(localization.Format("Dialog.TerminalCommand.Detail.InstalledTarget", snapshot.InstalledTargetExecutablePath));
		if (!string.IsNullOrWhiteSpace(snapshot.ShellProfileHint))
			lines.Add(localization.Format("Dialog.TerminalCommand.Detail.PathHint", snapshot.ShellProfileHint));

		return string.Join(Environment.NewLine, lines);
	}

	private static bool ShouldHideCommandLine(TerminalCommandSetupSnapshot snapshot) =>
		snapshot.State == TerminalCommandSetupState.Installed;

}
