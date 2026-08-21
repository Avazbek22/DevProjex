using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class DesktopShortcutLocalizationContractTests
{
	private static readonly string[] DesktopTooltipKeys =
	[
		"Filter.Tooltip",
		"Preview.Search.Tooltip",
		"Search.Previous.Tooltip",
		"Preview.Tooltip",
		"Preview.Secret.Redacted.Tooltip",
		"Preview.Secret.Kept.Tooltip",
		"DropZone.Shortcut"
	];

	[Fact]
	public void DesktopTooltips_UseTokensWhileTerminalTuiStringsStayLiteral_InEveryLocale()
	{
		foreach (var file in GetLocalizationFiles())
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			foreach (var key in DesktopTooltipKeys)
			{
				var value = document.RootElement.GetProperty(key).GetString()!;
				Assert.DoesNotContain("Ctrl+", value, StringComparison.Ordinal);
				Assert.DoesNotContain("Alt+", value, StringComparison.Ordinal);
				Assert.Contains('{', value);
			}

			var terminalValues = document.RootElement
				.EnumerateObject()
				.Where(static property => property.Name.StartsWith("Terminal.Tui.", StringComparison.Ordinal))
				.Select(static property => property.Value.GetString() ?? string.Empty)
				.ToArray();
			Assert.Contains(
				terminalValues,
				static value => value.Contains("Ctrl+", StringComparison.Ordinal) ||
				                value.Contains("Strg+", StringComparison.Ordinal));
			Assert.DoesNotContain(
				terminalValues,
				static value => ContainsDesktopToken(value));
		}
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows)]
	[InlineData(DesktopPlatform.MacOS)]
	[InlineData(DesktopPlatform.Linux)]
	public void DesktopTooltips_RenderWithoutShortcutTokens_ForEveryLocale(
		DesktopPlatform platform)
	{
		foreach (var file in GetLocalizationFiles())
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			foreach (var key in DesktopTooltipKeys)
			{
				var template = document.RootElement.GetProperty(key).GetString()!;
				var rendered = DesktopShortcutTextFormatter.Format(template, platform);
				if (rendered.Contains("{0}", StringComparison.Ordinal))
					rendered = string.Format(rendered, "rule");

				Assert.False(ContainsDesktopToken(rendered), $"Unrendered token in {file}, key {key}.");
				Assert.DoesNotContain('{', rendered);
				Assert.DoesNotContain('}', rendered);
				if (platform == DesktopPlatform.MacOS)
				{
					Assert.DoesNotContain("Ctrl+", rendered, StringComparison.Ordinal);
					Assert.DoesNotContain("Alt+", rendered, StringComparison.Ordinal);
				}
				else
				{
					Assert.DoesNotContain('⌘', rendered);
					Assert.DoesNotContain('⇧', rendered);
					Assert.DoesNotContain('⌥', rendered);
				}
			}
		}
	}

	private static bool ContainsDesktopToken(string value) =>
		value.Contains("{mod}", StringComparison.Ordinal) ||
		value.Contains("{shift}", StringComparison.Ordinal) ||
		value.Contains("{alt}", StringComparison.Ordinal) ||
		value.Contains("{collapseAll}", StringComparison.Ordinal);

	private static string[] GetLocalizationFiles()
	{
		var files = Directory.GetFiles(
			Path.Combine(FindRepositoryRoot(), "Assets", "Localization"),
			"*.json");
		Assert.Equal(11, files.Length);
		return files;
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
