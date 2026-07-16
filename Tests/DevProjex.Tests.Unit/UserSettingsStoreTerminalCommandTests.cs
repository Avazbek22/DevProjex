using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
public sealed class UserSettingsStoreTerminalCommandTests
{
	[Fact]
	public void Load_Defaults_DoNotDismissTerminalCommandPrompt()
	{
		using var temp = new TemporaryDirectory();
		var store = new UserSettingsStore(() => temp.Path);

		var db = store.Load();

		Assert.False(db.ViewSettings.IsTerminalCommandPromptDismissed);
	}

	[Fact]
	public void SaveAndLoad_RoundTripsTerminalCommandPromptDismissal()
	{
		using var temp = new TemporaryDirectory();
		var store = new UserSettingsStore(() => temp.Path);
		var db = store.Load();
		db.ViewSettings = db.ViewSettings with
		{
			IsTerminalCommandPromptDismissed = true
		};

		store.Save(db);
		var reloaded = store.Load();

		Assert.True(reloaded.ViewSettings.IsTerminalCommandPromptDismissed);
	}

	[Fact]
	public void ThemeReset_DoesNotReenableTerminalCommandPrompt()
	{
		using var temp = new TemporaryDirectory();
		var store = new UserSettingsStore(() => temp.Path);
		var themeStore = new ThemeSettingsStore(() => temp.Path);
		var db = store.Load();
		db.ViewSettings = db.ViewSettings with
		{
			IsTerminalCommandPromptDismissed = true
		};
		store.Save(db);

		themeStore.ResetToDefaults();
		var reloaded = store.Load();

		Assert.True(reloaded.ViewSettings.IsTerminalCommandPromptDismissed);
	}
}
