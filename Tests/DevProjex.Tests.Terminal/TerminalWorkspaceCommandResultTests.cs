using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandResultTests
{
	[Fact]
	public void NormalizeCommandResultPreservesMetricsAndEscapesTerminalControls()
	{
		var result = TerminalWorkspaceSession.NormalizeCommandResult(
			"Characters: 12\r\nTokens: 3\n\rforged\tsegment\u001b]8;;https://example.invalid\u0007");

		Assert.Equal(
			"Characters: 12 · Tokens: 3 · forged\\tsegment\\u001B]8;;https://example.invalid\\u0007",
			result);
		Assert.DoesNotContain('\u001b', result);
		Assert.DoesNotContain('\u0007', result);
	}

	[Fact]
	public void ContextDiagnosticsFormattingPreservesEverySeverityCodeAndPath()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		ContextDiagnostic[] diagnostics =
		[
			new("DPX-SELECTION-PATH-MISSING", ContextDiagnosticSeverity.Warning, "ignored", "src/missing.cs"),
			new("DPX-PROJECT-ROOT-ACCESS-DENIED", ContextDiagnosticSeverity.Error, "ignored", "src/private"),
			new("DPX-PROJECT-SELECTION-WARNING", ContextDiagnosticSeverity.Information, "ignored", "legacy value")
		];

		var result = TerminalWorkspaceSession.FormatContextDiagnostics(localization, diagnostics);

		Assert.Contains("warning [DPX-SELECTION-PATH-MISSING]", result, StringComparison.Ordinal);
		Assert.Contains("path: src/missing.cs", result, StringComparison.Ordinal);
		Assert.Contains("error [DPX-PROJECT-ROOT-ACCESS-DENIED]", result, StringComparison.Ordinal);
		Assert.Contains("path: src/private", result, StringComparison.Ordinal);
		Assert.Contains("info [DPX-PROJECT-SELECTION-WARNING]", result, StringComparison.Ordinal);
		Assert.Contains("value: legacy value", result, StringComparison.Ordinal);
}

	[Theory]
	[InlineData(AppLanguage.En, "more than 256 files")]
	[InlineData(AppLanguage.Ru, "превысил 256 файлов")]
	public void RelatedTraversalLimitIsLocalized(AppLanguage language, string expected)
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), language);

		var result = localization.Format(
			"Terminal.Related.TraversalLimit",
			DependencyFactsEngine.MaximumRelatedTraversalSeeds);

		Assert.Contains(expected, result, StringComparison.Ordinal);
	}
}
