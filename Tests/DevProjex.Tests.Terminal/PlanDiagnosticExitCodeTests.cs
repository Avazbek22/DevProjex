using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class PlanDiagnosticExitCodeTests
{
	// Every code a project context plan can carry with error severity, and the producer that adds it.
	private static readonly string[] PlanErrorCodes =
	[
		"DPX-PROJECT-ROOT-ACCESS-DENIED", // ProjectContextPlanner analysis diagnostics
		ProjectContextGitReadiness.UnavailableDiagnosticCode, // tracked Git mode without an index
		GitScopeFilter.UnavailableDiagnosticCode, // momentary Git scopes
		GitScopeFilter.UnsafeFilterDiagnosticCode // refused Git filter
	];

	[GeneratedRegex(@"^\| `(?<code>DPX-[A-Z0-9-]+)` \| (?<exit>[^|]+?) \|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex CatalogRowPattern();

	[Fact]
	public void NoErrorDiagnosticsIsSuccess()
	{
		Assert.Equal(CommandLineExitCodes.Success, PlanDiagnosticExitCode.For([]));
		Assert.Equal(
			CommandLineExitCodes.Success,
			PlanDiagnosticExitCode.For(
			[
				Warning("DPX-PROJECT-PARTIAL-ACCESS"),
				Warning("DPX-GIT-STATE-DELETED")
			]));
	}

	[Fact]
	public void RootAccessDeniedIsAUsageError()
	{
		Assert.Equal(
			CommandLineExitCodes.UsageError,
			PlanDiagnosticExitCode.For([Error("DPX-PROJECT-ROOT-ACCESS-DENIED")]));
	}

	[Theory]
	[InlineData("DPX-GIT-STATE-UNAVAILABLE")]
	[InlineData("DPX-GIT-TRACKED-INDEX-UNAVAILABLE")]
	[InlineData("DPX-GIT-UNSAFE-FILTER")]
	[InlineData("DPX-SOMETHING-NEW")]
	public void GitStateAndUnknownErrorsArePolicyFailures(string code)
	{
		Assert.Equal(CommandLineExitCodes.PolicyFailure, PlanDiagnosticExitCode.For([Error(code)]));
	}

	[Fact]
	public void UsageErrorOutranksAPolicyFailureInEitherOrder()
	{
		var usage = Error("DPX-PROJECT-ROOT-ACCESS-DENIED");
		var policy = Error("DPX-GIT-STATE-UNAVAILABLE");

		Assert.Equal(CommandLineExitCodes.UsageError, PlanDiagnosticExitCode.For([usage, policy]));
		Assert.Equal(CommandLineExitCodes.UsageError, PlanDiagnosticExitCode.For([policy, usage]));
	}

	[Fact]
	public void WarningWithAUsageCodeDoesNotChangeAPolicyFailure()
	{
		Assert.Equal(
			CommandLineExitCodes.PolicyFailure,
			PlanDiagnosticExitCode.For(
			[
				Warning("DPX-SELECTION-PATH-MISSING"),
				Error("DPX-GIT-STATE-UNAVAILABLE")
			]));
	}

	[Fact]
	public void EveryPlanErrorCodeIsClassifiedAsTheCatalogStates()
	{
		var catalog = ReadCatalog();

		foreach (var code in PlanErrorCodes)
		{
			Assert.True(catalog.TryGetValue(code, out var documented), $"{code} is not in the catalog.");
			// The catalog lists Git-state codes as warnings; as plan errors the CLI-V1 contract fails them closed with 3.
			var expected = int.TryParse(documented, out var exitCode)
				? exitCode
				: CommandLineExitCodes.PolicyFailure;
			Assert.Equal(expected, PlanDiagnosticExitCode.For([Error(code)]));
		}
	}

	[Fact]
	public void EveryProjectAndSelectionCodeWithAnExitCodeMatchesTheCatalog()
	{
		var section = ReadCatalogSection("### Project and selection");
		var rows = CatalogRowPattern().Matches(section)
			.Where(static row => int.TryParse(row.Groups["exit"].Value, out _))
			.ToArray();

		Assert.NotEmpty(rows);
		foreach (var row in rows)
		{
			Assert.Equal(
				int.Parse(row.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture),
				PlanDiagnosticExitCode.ForCode(row.Groups["code"].Value));
		}
	}

	[Fact]
	public void TheClassifierNeverReportsUsageForACodeTheCatalogDoesNotListAsUsage()
	{
		var catalog = ReadCatalog();

		foreach (var (code, documented) in catalog)
		{
			if (PlanDiagnosticExitCode.ForCode(code) == CommandLineExitCodes.UsageError)
				Assert.Equal("2", documented);
		}
	}

	private static ContextDiagnostic Error(string code) =>
		new(code, ContextDiagnosticSeverity.Error, "message");

	private static ContextDiagnostic Warning(string code) =>
		new(code, ContextDiagnosticSeverity.Warning, "message");

	private static Dictionary<string, string> ReadCatalog() =>
		CatalogRowPattern().Matches(ReadCatalogSection("## Error code catalog"))
			.GroupBy(static row => row.Groups["code"].Value, StringComparer.Ordinal)
			.ToDictionary(
				static group => group.Key,
				static group => group.First().Groups["exit"].Value.Trim(),
				StringComparer.Ordinal);

	private static string ReadCatalogSection(string heading)
	{
		var text = File.ReadAllText(Path.Combine(
				PublishedApplicationLocator.FindRepositoryRoot(),
				"Docs",
				"CLI-Output-Contract.md"))
			.Replace("\r\n", "\n", StringComparison.Ordinal);
		var start = text.IndexOf("\n" + heading + "\n", StringComparison.Ordinal);
		Assert.True(start >= 0, heading);
		var nextHeading = heading.StartsWith("### ", StringComparison.Ordinal) ? "\n##" : "\n## ";
		var end = text.IndexOf(nextHeading, start + heading.Length + 1, StringComparison.Ordinal);
		return end < 0 ? text[start..] : text[start..end];
	}
}
