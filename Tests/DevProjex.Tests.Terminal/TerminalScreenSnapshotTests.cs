namespace DevProjex.Tests.Terminal;

public sealed class TerminalScreenSnapshotTests
{
	[Theory]
	[InlineData(
		"Project: /var/folders/session/project",
		"/private/var/folders/session/project",
		"<PROJECT_ROOT>",
		"Project: <PROJECT_ROOT>")]
	[InlineData(
		"Project: /private/var/folders/session/project",
		"/var/folders/session/project",
		"<PROJECT_ROOT>",
		"Project: <PROJECT_ROOT>")]
	[InlineData(
		"Source: file:///private/var/folders/session/origin/repository",
		"/var/folders/session/origin",
		"<ORIGIN_ROOT>",
		"Source: file:///<ORIGIN_ROOT>/repository")]
	[InlineData(
		"Source: file:///tmp/session/origin/repository",
		"/private/tmp/session/origin",
		"<ORIGIN_ROOT>",
		"Source: file:///<ORIGIN_ROOT>/repository")]
	[InlineData(
		"│ /var/folders/session/project-ident │",
		"/private/var/folders/session/project-identifier",
		"<PROJECT_ROOT>",
		"│ <PROJECT_ROOT> │")]
	public void ReplacePathForSnapshot_NormalizesDarwinAliasesBeforeReplacement(
		string screen,
		string source,
		string replacement,
		string expected)
	{
		Assert.Equal(
			expected,
			TerminalScreenSnapshot.ReplacePathForSnapshot(
				screen,
				source,
				replacement,
				isMacOs: true));
	}

	[Fact]
	public void NormalizeMacOsSystemPathAliases_DoesNotChangeOtherPlatforms()
	{
		const string path = "/private/var/folders/session/project";

		Assert.Equal(
			path,
			TerminalScreenSnapshot.NormalizeMacOsSystemPathAliases(
				path,
				isMacOs: false));
	}
}
