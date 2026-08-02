namespace DevProjex.Tests.Unit;

public sealed class LocalizationHelpMenuKeysTests
{
	private static readonly string[] RequiredHelpMenuKeys =
	[
		"Menu.Help",
		"Menu.Help.Help",
		"Menu.Help.CheckUpdates",
		"Menu.Help.About",
		"Menu.Help.ResetSettings",
		"Menu.Help.ResetData",
		"Update.Title",
		"Update.Prompt",
		"Update.AutomaticWeekly",
		"Update.Available",
		"Update.UpToDate",
		"Update.CurrentVersionNewer",
		"Update.Failed",
		"Update.FailedMessage",
		"Update.Check",
		"Update.CheckAgain",
		"Update.Checking",
		"Update.Retry",
		"Update.OpenRepository",
		"Update.CurrentVersion",
		"Update.LatestVersion"
	];

	[Fact]
	public void LocalizationFiles_ContainAllHelpMenuKeys()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");
		Assert.NotEmpty(files);

		foreach (var file in files)
		{
			var keys = ReadKeys(File.ReadAllText(file));
			foreach (var required in RequiredHelpMenuKeys)
				Assert.Contains(required, keys);
		}
	}

	[Fact]
	public void HelpMenuKeys_HaveNonEmptyValuesInEveryLanguage()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		foreach (var file in files)
		{
			var map = ReadKeyValues(File.ReadAllText(file));
			foreach (var required in RequiredHelpMenuKeys)
			{
				Assert.True(map.TryGetValue(required, out var value), $"Missing {required} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{required} is empty in {Path.GetFileName(file)}");
			}
		}
	}

	[Fact]
	public void AboutBody_UsesDynamicCopyrightYearAndDeveloperHandleInEveryLanguage()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		foreach (var file in files)
		{
			var values = ReadKeyValues(File.ReadAllText(file));
			var body = values["Help.About.Body"];

			Assert.Contains("{0}", body, StringComparison.Ordinal);
			Assert.Contains("Avazbek Olimov (Avazbek22)", body, StringComparison.Ordinal);
			Assert.DoesNotContain("2025–2026", body, StringComparison.Ordinal);
			Assert.DoesNotContain("GPL-3.0", body, StringComparison.OrdinalIgnoreCase);
		}
	}

	private static HashSet<string> ReadKeys(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.EnumerateObject()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);
	}

	private static Dictionary<string, string> ReadKeyValues(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var property in doc.RootElement.EnumerateObject())
			map[property.Name] = property.Value.GetString() ?? string.Empty;
		return map;
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
