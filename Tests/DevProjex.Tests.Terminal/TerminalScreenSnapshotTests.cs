namespace DevProjex.Tests.Terminal;

public sealed class TerminalScreenSnapshotTests
{
	[Theory]
	[InlineData("/var/folders/project", "/private/var/folders/project")]
	[InlineData("/tmp/project", "/private/tmp/project")]
	[InlineData("/private/var/folders/project", "/var/folders/project")]
	[InlineData("/private/tmp/project", "/tmp/project")]
	public void GetMacOsPathAlias_MapsBothDarwinTemporaryPathForms(
		string path,
		string expected)
	{
		Assert.Equal(
			expected,
			TerminalScreenSnapshot.GetMacOsPathAlias(path, isMacOs: true));
	}

	[Theory]
	[InlineData("/Users/developer/project", true)]
	[InlineData("/var/folders/project", false)]
	public void GetMacOsPathAlias_IgnoresUnrelatedPathsAndOtherPlatforms(
		string path,
		bool isMacOs)
	{
		Assert.Null(TerminalScreenSnapshot.GetMacOsPathAlias(path, isMacOs));
	}
}
