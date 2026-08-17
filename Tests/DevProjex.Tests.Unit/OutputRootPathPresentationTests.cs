using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class OutputRootPathPresentationTests
{
	[Theory]
	[InlineData(@"C:\Users\alice\source\repo", @"C:\Users\[local-user-1]\source\repo")]
	[InlineData("C:/Users/alice/source/repo", "C:/Users/[local-user-1]/source/repo")]
	[InlineData("/home/alice/source/repo", "/home/[local-user-1]/source/repo")]
	[InlineData("/Users/alice/source/repo", "/Users/[local-user-1]/source/repo")]
	[InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
	public void MaskLocalUserSegment_ReplacesOnlyTheUserSegment(string path, string expected)
	{
		Assert.Equal(expected, OutputRootPathPresentation.MaskLocalUserSegment(path));
	}

	[Fact]
	public void Resolve_DisabledPrivacyReturnsTheOriginalDisplayRootInstance()
	{
		var displayRoot = new string("C:\\Users\\alice\\repo".ToCharArray());

		var result = OutputRootPathPresentation.Resolve("ignored", displayRoot, hidePrivateData: false);

		Assert.Same(displayRoot, result);
	}

	[Fact]
	public void ResolvePath_UsesOneStableOccurrenceForRedactedAndKeptPresentations()
	{
		const string path = @"C:\Users\alice\repo\src\Program.cs";
		var redactedDecision = new OutputPathRedactionDecision("generated-path", Keep: false);
		var keptDecision = redactedDecision with { Keep = true };

		var redacted = OutputRootPathPresentation.ResolvePath(path, redactedDecision);
		var kept = OutputRootPathPresentation.ResolvePath(path, keptDecision);

		Assert.Equal(@"C:\Users\[local-user-1]\repo\src\Program.cs", redacted.Text);
		Assert.Equal(path, kept.Text);
		Assert.Equal("generated-path", redacted.OccurrenceId);
		Assert.Equal(redacted.OccurrenceId, kept.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.Redacted, redacted.State);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, kept.State);
		Assert.Equal("alice".Length, redacted.SourceLength);
		Assert.Equal("alice".Length, kept.SegmentLength);
	}

	[Fact]
	public void RelativeContentHeaderMapper_IsStablePerRootAndProducesPortablePaths()
	{
		using var project = new TemporaryDirectory();
		var file = project.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}");

		var first = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path);
		var second = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path);

		Assert.Same(first, second);
		Assert.Equal("src/Program.cs", first(file));
	}
}
