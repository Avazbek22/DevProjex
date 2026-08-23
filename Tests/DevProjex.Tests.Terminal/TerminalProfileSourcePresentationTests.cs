using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalProfileSourcePresentationTests
{
	[Fact]
	public void StandardAndMissingSourcesDoNotProduceAnIndicator()
	{
		Assert.Null(Format(null));
		Assert.Null(Format(ProjectProfileReference.Standard));
	}

	[Fact]
	public void LocalSourceUsesTheProjectLabel()
	{
		Assert.Equal(
			"Saved settings: Saved project settings",
			Format(ProjectProfileReference.Local));
	}

	[Fact]
	public void PortableSourceUsesOnlyTheProfileFileName()
	{
		var source = new ProjectProfileReference(
			ProjectProfileSourceKind.Portable,
			Path.Combine(Path.GetTempPath(), "profiles", "team-profile.json"));

		var result = Format(source);

		Assert.Equal("Saved settings: File: team-profile.json", result);
		Assert.DoesNotContain(Path.GetTempPath(), result, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void LongPortableSourceIsEllipsizedByTerminalColumns()
	{
		var source = new ProjectProfileReference(
			ProjectProfileSourceKind.Portable,
			Path.Combine(Path.GetTempPath(), "界界界界界界-profile.json"));

		var result = Format(source, maxColumns: 24);

		Assert.NotNull(result);
		Assert.EndsWith("…", result, StringComparison.Ordinal);
		Assert.True(result.GetColumns() <= 24);
	}

	private static string? Format(
		ProjectProfileReference? source,
		int maxColumns = 200) =>
		TerminalProfileSourcePresentation.Format(
			source,
			"Saved settings",
			"Saved project settings",
			"File",
			maxColumns,
			useUnicode: true);
}
