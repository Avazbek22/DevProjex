namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
public sealed class TerminalCommandLocalizationTests
{
	private static readonly string[] RequiredKeys =
	[
		"Menu.Help.TerminalCommand",
		"Dialog.TerminalCommand.Title",
		"Dialog.TerminalCommand.AutomaticPrompt.Body",
		"Dialog.TerminalCommand.Body.ManagedByOS",
		"Dialog.TerminalCommand.Body.UnsupportedPackage",
		"Dialog.TerminalCommand.Body.UnsupportedPlatform",
		"Dialog.TerminalCommand.Body.HomeMissing",
		"Dialog.TerminalCommand.Body.NotInstalled",
		"Dialog.TerminalCommand.Body.Installed",
		"Dialog.TerminalCommand.Body.Stale",
		"Dialog.TerminalCommand.Body.Conflict",
		"Dialog.TerminalCommand.Body.PermissionDenied",
		"Dialog.TerminalCommand.Body.Failed",
		"Dialog.TerminalCommand.Detail.State",
		"Dialog.TerminalCommand.Detail.Command",
		"Dialog.TerminalCommand.Detail.CommandPath",
		"Dialog.TerminalCommand.Detail.Target",
		"Dialog.TerminalCommand.Detail.InstalledTarget",
		"Dialog.TerminalCommand.Detail.PathHint",
		"Dialog.TerminalCommand.CommandLine",
		"Dialog.TerminalCommand.CopyCommand",
		"Dialog.TerminalCommand.Setup",
		"Dialog.TerminalCommand.AddToPath",
		"Dialog.TerminalCommand.Repair",
		"Dialog.TerminalCommand.NotNow",
		"Dialog.TerminalCommand.DontShowAgain",
		"Dialog.TerminalCommand.InstallFailed"
	];

	[Fact]
	public void LocalizationFiles_ContainAllTerminalCommandKeys()
	{
		foreach (var file in GetLocalizationFiles())
		{
			var keys = ReadKeys(File.ReadAllText(file));
			foreach (var key in RequiredKeys)
				Assert.Contains(key, keys);
		}
	}

	[Fact]
	public void LocalizationFiles_HaveNonEmptyTerminalCommandValues()
	{
		foreach (var file in GetLocalizationFiles())
		{
			var values = ReadValues(File.ReadAllText(file));
			foreach (var key in RequiredKeys)
			{
				Assert.True(values.TryGetValue(key, out var value), $"Missing {key} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty in {Path.GetFileName(file)}");
			}
		}
	}

	[Fact]
	public void TerminalCommandFormattedKeys_KeepRequiredPlaceholders()
	{
		var formattedKeys = new[]
		{
			"Dialog.TerminalCommand.Detail.State",
			"Dialog.TerminalCommand.Detail.Command",
			"Dialog.TerminalCommand.Detail.CommandPath",
			"Dialog.TerminalCommand.Detail.Target",
			"Dialog.TerminalCommand.Detail.InstalledTarget",
			"Dialog.TerminalCommand.Detail.PathHint",
			"Dialog.TerminalCommand.CommandLine"
		};

		foreach (var file in GetLocalizationFiles())
		{
			var values = ReadValues(File.ReadAllText(file));
			foreach (var key in formattedKeys)
				Assert.Contains("{0}", values[key], StringComparison.Ordinal);
		}
	}

	private static IReadOnlyList<string> GetLocalizationFiles()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");
		Assert.NotEmpty(files);
		return files;
	}

	private static HashSet<string> ReadKeys(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.EnumerateObject()
			.Select(property => property.Name)
			.ToHashSet(StringComparer.Ordinal);
	}

	private static Dictionary<string, string> ReadValues(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var property in doc.RootElement.EnumerateObject())
			values[property.Name] = property.Value.GetString() ?? string.Empty;
		return values;
	}

	private static string FindRepositoryRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (dir is not null)
		{
			if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "DevProjex.sln")))
				return dir;

			dir = Directory.GetParent(dir)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
