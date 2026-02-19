namespace DevProjex.Tests.Integration;

public sealed class ResetConfirmationWorkflowIntegrationTests
{
	[Fact]
	public void MainWindow_OnResetSettings_UsesConfirmationDialog()
	{
		var content = ReadMainWindowCode();
		var start = content.IndexOf("private async void OnResetSettings(", StringComparison.Ordinal);
		var end = content.IndexOf("private async void OnResetData(", StringComparison.Ordinal);

		Assert.True(start >= 0, "OnResetSettings method not found.");
		Assert.True(end > start, "OnResetSettings boundary not found.");

		var body = content.Substring(start, end - start);
		Assert.Contains("MessageDialog.ShowConfirmationAsync(", body);
		Assert.Contains("_localization[\"Dialog.ResetSettings.Title\"]", body);
		Assert.Contains("_localization[\"Dialog.ResetSettings.Message\"]", body);
		Assert.Contains("_localization[\"Dialog.ResetSettings.Confirm\"]", body);
		Assert.Contains("_localization[\"Dialog.Cancel\"]", body);
		Assert.Contains("ResetThemeSettings();", body);
	}

	[Fact]
	public void MainWindow_OnResetData_UsesConfirmationDialog()
	{
		var content = ReadMainWindowCode();
		var start = content.IndexOf("private async void OnResetData(", StringComparison.Ordinal);
		var end = content.IndexOf("/// <summary>", start >= 0 ? start : 0, StringComparison.Ordinal);

		Assert.True(start >= 0, "OnResetData method not found.");
		Assert.True(end > start, "OnResetData boundary not found.");

		var body = content.Substring(start, end - start);
		Assert.Contains("MessageDialog.ShowConfirmationAsync(", body);
		Assert.Contains("_localization[\"Dialog.ResetData.Title\"]", body);
		Assert.Contains("_localization[\"Dialog.ResetData.Message\"]", body);
		Assert.Contains("_localization[\"Dialog.ResetData.Confirm\"]", body);
		Assert.Contains("_localization[\"Dialog.Cancel\"]", body);
		Assert.Contains("_projectProfileStore.ClearAllProfiles();", body);
	}

	[Fact]
	public void MainWindow_OnResetSettings_CancelSkipsReset()
	{
		var content = ReadMainWindowCode();
		var start = content.IndexOf("private async void OnResetSettings(", StringComparison.Ordinal);
		var end = content.IndexOf("private async void OnResetData(", StringComparison.Ordinal);

		Assert.True(start >= 0, "OnResetSettings method not found.");
		Assert.True(end > start, "OnResetSettings boundary not found.");

		var body = content.Substring(start, end - start);
		var cancelBranch = body.IndexOf("if (!confirmed)", StringComparison.Ordinal);
		var resetCall = body.IndexOf("ResetThemeSettings();", StringComparison.Ordinal);

		Assert.True(cancelBranch >= 0, "Cancel branch not found.");
		Assert.True(resetCall > cancelBranch, "Reset should occur only after confirmation.");
	}

	[Fact]
	public void MessageDialog_HasConfirmationApi()
	{
		var content = ReadMessageDialogCode();
		Assert.Contains("public static async Task<bool> ShowConfirmationAsync(", content);
		Assert.Contains("BuildConfirmationContent(", content);
	}

	private static string ReadMainWindowCode()
	{
		var repoRoot = FindRepositoryRoot();
		var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml.cs");
		return File.ReadAllText(file);
	}

	private static string ReadMessageDialogCode()
	{
		var repoRoot = FindRepositoryRoot();
		var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "Services", "MessageDialog.cs");
		return File.ReadAllText(file);
	}

	private static string FindRepositoryRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir, ".git")) ||
			    File.Exists(Path.Combine(dir, "DevProjex.sln")))
				return dir;

			dir = Directory.GetParent(dir)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
