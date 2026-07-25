namespace DevProjex.Tests.Unit;

public sealed class LocalizationExportMenuKeysTests
{
	private static readonly string[] RequiredExportKeys =
	[
		"Menu.File.Export",
		"Menu.File.Export.Tree",
		"Menu.File.Export.Content",
		"Menu.File.Export.TreeAndContent",
		"Toast.Export.Tree",
		"Toast.Export.Content",
		"Toast.Export.TreeAndContent",
		"Menu.File.ExportProjectCopy",
		"Menu.File.ExportProjectCopy.Folder",
		"Menu.File.ExportProjectCopy.Zip",
		"Menu.File.ExportProjectCopy.Folder.Help",
		"Menu.File.ExportProjectCopy.Zip.Help",
		"Toast.ProjectCopy.Folder",
		"Toast.ProjectCopy.Zip",
		"Toast.ProjectCopy.Canceled",
		"Status.Operation.ExportingProjectCopy",
		"Status.Operation.ExportingProjectCopy.Progress",
		"Picker.ProjectCopy.Folder",
		"Picker.ProjectCopy.Zip",
		"Error.ProjectCopy.LocalDestinationRequired",
		"Error.ProjectCopy.DestinationInsideSource",
		"Error.ProjectCopy.SymbolicLinkNotSupported",
		"Error.ProjectCopy.UnsafeSourcePath",
		"Error.ProjectCopy.InvalidRequest",
		"Error.ProjectCopy.DestinationUnavailable",
		"Error.ProjectCopy.SourceUnavailable",
		"Error.ProjectCopy.AccessDenied",
		"Error.ProjectCopy.IoFailure",
		"Error.ProjectCopy.UnsafeDestinationPath",
		"Error.ProjectCopy.UnexpectedFailure"
	];

	[Fact]
	public void LocalizationFiles_ContainAllExportKeys()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");
		Assert.NotEmpty(files);

		foreach (var file in files)
		{
			var json = File.ReadAllText(file);
			var keys = ReadKeys(json);

			foreach (var required in RequiredExportKeys)
				Assert.Contains(required, keys);
		}
	}

	[Fact]
	public void ProjectCopyLocalization_UsesNativeTranslationsInsteadOfEnglishFallbacks()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var english = ReadKeyValues(File.ReadAllText(Path.Combine(localizationDir, "en.json")));
		foreach (var file in Directory.GetFiles(localizationDir, "*.json"))
		{
			if (Path.GetFileName(file).Equals("en.json", StringComparison.OrdinalIgnoreCase))
				continue;

			var values = ReadKeyValues(File.ReadAllText(file));
			foreach (var key in RequiredExportKeys.Where(static key => key.Contains("ProjectCopy", StringComparison.Ordinal)))
			{
				Assert.NotEqual(
					english[key],
					values[key]);
			}
		}
	}

	[Fact]
	public void ExportKeys_HaveNonEmptyValuesInEveryLanguage()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var files = Directory.GetFiles(localizationDir, "*.json");

		foreach (var file in files)
		{
			var values = ReadKeyValues(File.ReadAllText(file));
			foreach (var required in RequiredExportKeys)
			{
				Assert.True(values.TryGetValue(required, out var value), $"Missing {required} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value), $"{required} is empty in {Path.GetFileName(file)}");
			}
		}
	}

	[Fact]
	public void ProjectCopyLocalization_RemovesObsoleteSharedTooltipAndFormattedRawError()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		foreach (var file in Directory.GetFiles(localizationDir, "*.json"))
		{
			var keys = ReadKeys(File.ReadAllText(file));
			Assert.DoesNotContain("Menu.File.ExportProjectCopy.Help", keys);
			Assert.DoesNotContain("Error.ProjectCopy.Failed", keys);
		}
	}

	[Fact]
	public void ProjectCopyMenu_UsesShortParentAndActionSpecificHelpText()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var english = ReadKeyValues(File.ReadAllText(Path.Combine(localizationDir, "en.json")));
		var russian = ReadKeyValues(File.ReadAllText(Path.Combine(localizationDir, "ru.json")));

		Assert.Equal("Export Project", english["Menu.File.ExportProjectCopy"]);
		Assert.Equal("Экспорт проекта", russian["Menu.File.ExportProjectCopy"]);
		Assert.StartsWith("Creates a copy of the current tree", english["Menu.File.ExportProjectCopy.Folder.Help"]);
		Assert.StartsWith("Creates a ZIP archive from the current tree", english["Menu.File.ExportProjectCopy.Zip.Help"]);
		Assert.StartsWith("Создаёт копию текущего дерева", russian["Menu.File.ExportProjectCopy.Folder.Help"]);
		Assert.StartsWith("Создаёт ZIP-архив из текущего дерева", russian["Menu.File.ExportProjectCopy.Zip.Help"]);
	}

	[Fact]
	public void ProjectCopyProgress_UsesCompactEntryCountWithoutNoun()
	{
		var localizationDir = Path.Combine(FindRepositoryRoot(), "Assets", "Localization");
		var english = ReadKeyValues(File.ReadAllText(Path.Combine(localizationDir, "en.json")));
		var russian = ReadKeyValues(File.ReadAllText(Path.Combine(localizationDir, "ru.json")));

		Assert.Equal("Export Project", english["Status.Operation.ExportingProjectCopy"]);
		Assert.Equal("Экспорт проекта", russian["Status.Operation.ExportingProjectCopy"]);
		Assert.Equal("Export Project: {0}/{1}", english["Status.Operation.ExportingProjectCopy.Progress"]);
		Assert.Equal("Экспорт проекта: {0}/{1}", russian["Status.Operation.ExportingProjectCopy.Progress"]);
	}

	[Theory]
	[InlineData("de")]
	[InlineData("en")]
	[InlineData("es")]
	[InlineData("fr")]
	[InlineData("it")]
	[InlineData("kk")]
	[InlineData("pt")]
	[InlineData("pt-pt")]
	[InlineData("ru")]
	[InlineData("tg")]
	[InlineData("uz")]
	public void ProjectCopyPickerAndResultToast_PresentNameAndReadablePath(string locale)
	{
		var localizationPath = Path.Combine(FindRepositoryRoot(), "Assets", "Localization", $"{locale}.json");
		var values = ReadKeyValues(File.ReadAllText(localizationPath));

		Assert.False(string.IsNullOrWhiteSpace(values["Picker.ProjectCopy.Folder"]));
		Assert.Contains("{0}", values["Picker.ProjectCopy.Folder"], StringComparison.Ordinal);
		var pickerTitle = string.Format(
			CultureInfo.InvariantCulture,
			values["Picker.ProjectCopy.Folder"],
			"Project-copy");
		Assert.Contains("Project-copy", pickerTitle, StringComparison.Ordinal);
		Assert.DoesNotContain("{0}", pickerTitle, StringComparison.Ordinal);
		Assert.Contains("\n{0}", values["Toast.ProjectCopy.Folder"], StringComparison.Ordinal);
		Assert.Contains("\n{0}", values["Toast.ProjectCopy.Zip"], StringComparison.Ordinal);
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
