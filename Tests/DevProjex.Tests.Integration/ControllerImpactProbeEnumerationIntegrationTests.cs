using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Integration;

public sealed class ControllerImpactProbeEnumerationIntegrationTests
{
	[Theory]
	[InlineData(".cs", true)]
	[InlineData(".txt", false)]
	public void NestedFileUsesOneEntryEnumerationPerVisitedDirectory(
		string selectedExtension,
		bool expectedVisible)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("candidate/nested/visible.cs", "class Visible {}");
		var candidate = Path.Combine(temp.Path, "candidate");
		var rules = CreateRules();

		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		var visible = FileSystemScanner.HasVisibleContentForControllerImpactCandidate(
			candidate,
			"candidate",
			rules,
			new ExtensionSetInclusionPolicy(
				new HashSet<string>([selectedExtension], StringComparer.OrdinalIgnoreCase)),
			TestContext.Current.CancellationToken,
			out var hadScanFailure);
		var diagnostics = measurement.Capture();

		Assert.Equal(expectedVisible, visible);
		Assert.False(hadScanFailure);
		Assert.Equal(0, diagnostics.FileEnumerations);
		Assert.Equal(0, diagnostics.DirectoryEnumerations);
		Assert.Equal(2, diagnostics.CombinedEntryEnumerations);
	}

	private static IgnoreRules CreateRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
