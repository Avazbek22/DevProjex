namespace DevProjex.Tests.Unit;

public sealed class LocalizationPreviewSearchKeysTests
{
	private static readonly string[] RequiredKeys =
	[
		"Preview.Search.Tooltip",
		"Search.Next.Tooltip",
		"Search.Previous.Tooltip"
	];

	[Fact]
	public void LocalizationFiles_ContainNonEmptyPreviewSearchTooltipsWithDocumentedHotkeys()
	{
		var localizationDirectory = Path.Combine(
			FindRepositoryRoot(),
			"Assets",
			"Localization");
		var files = Directory.GetFiles(localizationDirectory, "*.json");
		Assert.Equal(11, files.Length);

		foreach (var file in files)
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			foreach (var key in RequiredKeys)
			{
				Assert.True(
					document.RootElement.TryGetProperty(key, out var property),
					$"Missing {key} in {Path.GetFileName(file)}");
				Assert.False(
					string.IsNullOrWhiteSpace(property.GetString()),
					$"{key} is empty in {Path.GetFileName(file)}");
			}

			Assert.Contains("Ctrl+Shift+F", document.RootElement.GetProperty(RequiredKeys[0]).GetString());
			Assert.Contains("F3", document.RootElement.GetProperty(RequiredKeys[1]).GetString());
			Assert.Contains("Shift+F3", document.RootElement.GetProperty(RequiredKeys[2]).GetString());
		}
	}

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
				return directory;

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
