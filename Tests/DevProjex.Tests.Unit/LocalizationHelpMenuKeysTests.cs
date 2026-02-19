namespace DevProjex.Tests.Unit;

public sealed class LocalizationHelpMenuKeysTests
{
	private static readonly string[] RequiredHelpMenuKeys =
	{
		"Menu.Help",
		"Menu.Help.Help",
		"Menu.Help.About",
		"Menu.Help.ResetSettings",
		"Menu.Help.ResetData"
	};

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
