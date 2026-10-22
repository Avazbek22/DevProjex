using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class McpExistingEntryConflictPresentationTests
{
	private const string Existing = "npx -y devprojex mcp --root /home/person/old";
	private const string Proposed = "/opt/DevProjex mcp --root /home/person/project --live";

	[Fact]
	public void ALauncherIsDescribedAsDevProjexStartedAnotherWay()
	{
		var text = Describe(new McpExistingEntryConflict(Existing, Proposed, [], IsDevProjexLauncher: true));

		Assert.Contains("starts DevProjex another way", text, StringComparison.Ordinal);
		Assert.Contains(Existing, text, StringComparison.Ordinal);
		Assert.Contains(Proposed, text, StringComparison.Ordinal);
		Assert.True(text.IndexOf(Existing, StringComparison.Ordinal) < text.IndexOf(Proposed, StringComparison.Ordinal));
		Assert.DoesNotContain("Not kept", text, StringComparison.Ordinal);
	}

	[Fact]
	public void AForeignEntryIsDescribedAsNotCreatedByDevProjex()
	{
		var text = Describe(new McpExistingEntryConflict("node server.js", Proposed, [], IsDevProjexLauncher: false));

		Assert.Contains("did not create", text, StringComparison.Ordinal);
		Assert.Contains("only if you choose Replace", text, StringComparison.Ordinal);
		Assert.Contains("node server.js", text, StringComparison.Ordinal);
	}

	[Fact]
	public void ArgumentsThatAReplacementDropsAreListed()
	{
		var text = Describe(new McpExistingEntryConflict(Existing, Proposed, ["--hide-private-data", "--exclude", "default"], true));

		Assert.EndsWith("Not kept after replacing: --hide-private-data --exclude default", text, StringComparison.Ordinal);
	}

	[Fact]
	public void TextFromTheConfigurationFileCannotReshapeTheDialog()
	{
		var hostile = "proxy \u001b]0;owned\u0007\u001b[2J\nInjected line\r\u0085\u2028";
		var text = Describe(new McpExistingEntryConflict(hostile, Proposed, ["--x\u001b[31m\nnext"], false));

		Assert.DoesNotContain('\u001b', text);
		Assert.DoesNotContain('\u0007', text);
		Assert.DoesNotContain('\r', text);
		Assert.DoesNotContain('\u0085', text);
		Assert.DoesNotContain('\u2028', text);
		Assert.DoesNotContain("\nInjected line", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\nnext", text, StringComparison.Ordinal);
		Assert.Contains("proxy", text, StringComparison.Ordinal);
	}

	[Fact]
	public void EveryLanguageFillsEveryPlaceholder()
	{
		var catalog = new JsonLocalizationCatalog();
		foreach (var language in Enum.GetValues<AppLanguage>())
		{
			var localization = new LocalizationService(catalog, language);
			foreach (var launcher in new[] { true, false })
			{
				var text = McpExistingEntryConflictPresentation.Describe(
					localization,
					"Claude Code",
					new McpExistingEntryConflict(Existing, Proposed, ["--hide-private-data"], launcher));

				Assert.DoesNotMatch(@"\{\d\}", text);
				Assert.Contains("Claude Code", text, StringComparison.Ordinal);
				Assert.Contains(Existing, text, StringComparison.Ordinal);
				Assert.Contains(Proposed, text, StringComparison.Ordinal);
				Assert.Contains("--hide-private-data", text, StringComparison.Ordinal);
			}
			foreach (var key in new[] { "Title", "Keep", "CopyCommand", "Replace", "CommandCopied" })
				Assert.False(string.IsNullOrWhiteSpace(localization["Mcp.Connect.ExistingEntry." + key]), $"{language}: {key}");
			Assert.DoesNotMatch(@"\{\d\}", localization.Format("Mcp.Connect.ExistingEntry.ReplaceRequired", "Codex", Existing));
			Assert.DoesNotMatch(@"\{\d\}", localization.Format("Mcp.Connect.ExistingEntry.AlreadyConnected", "Codex", Existing));
			Assert.DoesNotMatch(@"\{\d\}", localization.Format("Mcp.Connect.ExistingEntry.Changed", "Codex"));
			Assert.Contains("--replace", localization.Format("Mcp.Connect.ExistingEntry.ReplaceRequired", "Codex", Existing), StringComparison.Ordinal);
		}
	}

	private static string Describe(McpExistingEntryConflict conflict) =>
		McpExistingEntryConflictPresentation.Describe(
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			"Claude Code",
			conflict);
}
