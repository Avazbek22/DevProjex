namespace DevProjex.Tests.Unit;

public sealed class LocalizationToastKeysTests
{
	private static readonly string[] RequiredToastKeys =
	[
		"Toast.Copy.Tree",
		"Toast.Copy.Content",
		"Toast.Copy.TreeAndContent",
		"Toast.Export.Tree",
		"Toast.Export.Content",
		"Toast.Export.TreeAndContent",
		"Toast.NoMatches",
		"Toast.Tree.CheckedSelectionHidden",
		"Toast.Secret.AlreadyHidden",
		"Toast.Git.CloneSuccess",
		"Toast.Git.CloneError",
		"Toast.Git.CachedUpdateFailed",
		"Toast.Git.CacheEntryMissing",
		"Toast.Git.UpdatesApplied",
		"Toast.Git.NoUpdates",
		"Toast.Git.BranchSwitched",
		"Toast.Refresh.Success",
		"Toast.Settings.Reset",
		"Toast.Data.Reset"
	];

	[Fact]
	public void LocalizationFiles_ContainAllToastKeys()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		Assert.NotEmpty(files);

		foreach (var file in files)
		{
			var json = File.ReadAllText(file);
			var keys = ReadKeys(json);

			foreach (var required in RequiredToastKeys)
				Assert.Contains(required, keys);
		}
	}

	[Fact]
	public void ToastKeys_HaveNonEmptyValues()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		foreach (var file in files)
		{
			var map = ReadKeyValues(File.ReadAllText(file));

			foreach (var required in RequiredToastKeys)
			{
				Assert.True(map.TryGetValue(required, out var value), $"Missing {required} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{required} is empty in {Path.GetFileName(file)}");
			}
		}
	}

	[Theory]
	[InlineData("Toast.Git.BranchSwitched")]
	[InlineData("Toast.Tree.CheckedSelectionHidden")]
	public void FormattedToast_ContainsPlaceholder(string key)
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		foreach (var file in files)
		{
			var map = ReadKeyValues(File.ReadAllText(file));
			Assert.True(map.TryGetValue(key, out var value),
				$"Missing {key} in {Path.GetFileName(file)}");
			Assert.Contains("{0}", value);
		}
	}

	private static HashSet<string> ReadKeys(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.EnumerateObject()
			.Select(p => p.Name)
			.ToHashSet(StringComparer.Ordinal);
	}

	private static Dictionary<string, string> ReadKeyValues(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var dict = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var prop in doc.RootElement.EnumerateObject())
			dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
		return dict;
	}

	private static string FindRepositoryRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (dir != null)
		{
			if (Directory.Exists(Path.Combine(dir, ".git")) ||
				File.Exists(Path.Combine(dir, "DevProjex.sln")))
				return dir;

			dir = Directory.GetParent(dir)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
