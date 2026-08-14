namespace DevProjex.Tests.Unit;

public sealed class LocalizationGitCacheKeysTests
{
	private static readonly string[] RequiredKeys =
	[
		"Git.Clone.Recent",
		"Git.Clone.LocalCache",
		"Git.Clone.LocalCache.Zip",
		"Git.Clone.LocalCache.ActiveDeleteTooltip",
		"Toast.Git.CachedUpdateFailed",
		"Toast.Git.CacheEntryMissing"
	];
	private static readonly string[] RemovedKeys =
	[
		"Git.Clone.LocalCache.Empty",
		"Git.Clone.LocalCache.Clear",
		"Git.Clone.LocalCache.Usage"
	];

	[Fact]
	public void EveryLocaleContainsNonEmptyGitCacheKeysWithMatchingParity()
	{
		var files = Directory.GetFiles(GetLocalizationDirectory(), "*.json");
		Assert.Equal(11, files.Length);
		HashSet<string>? expectedKeys = null;
		foreach (var file in files)
		{
			using var document = JsonDocument.Parse(File.ReadAllBytes(file));
			var values = document.RootElement
				.EnumerateObject()
				.ToDictionary(
					static property => property.Name,
					static property => property.Value.GetString() ?? string.Empty,
					StringComparer.Ordinal);
			foreach (var key in RequiredKeys)
			{
				Assert.True(values.TryGetValue(key, out var value), $"Missing {key} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty in {Path.GetFileName(file)}");
			}
			foreach (var key in RemovedKeys)
				Assert.False(values.ContainsKey(key), $"Obsolete {key} remains in {Path.GetFileName(file)}");

			var currentKeys = values.Keys.ToHashSet(StringComparer.Ordinal);
			expectedKeys ??= currentKeys;
			Assert.True(expectedKeys.SetEquals(currentKeys), $"Localization key parity differs in {Path.GetFileName(file)}");
		}
	}

	[Fact]
	public void RecentRepositoryLabelDescribesLinksNotCacheEntries()
	{
		using var english = JsonDocument.Parse(
			File.ReadAllBytes(Path.Combine(GetLocalizationDirectory(), "en.json")));
		using var russian = JsonDocument.Parse(
			File.ReadAllBytes(Path.Combine(GetLocalizationDirectory(), "ru.json")));

		Assert.Equal("Recent links", english.RootElement.GetProperty("Git.Clone.Recent").GetString());
		Assert.Equal("Недавние ссылки", russian.RootElement.GetProperty("Git.Clone.Recent").GetString());
	}

	private static string GetLocalizationDirectory()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			var candidate = Path.Combine(directory, "Assets", "Localization");
			if (Directory.Exists(candidate))
				return candidate;
			directory = Directory.GetParent(directory)?.FullName;
		}
		throw new InvalidOperationException("Localization directory was not found.");
	}
}
