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
	[InlineData(
		"│ /private/var/folders/session/project-ident │",
		"/var/folders/session/project-identifier",
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

	[Theory]
	[InlineData("/private/var", "/var")]
	[InlineData("/private/tmp", "/tmp")]
	public void NormalizeMacOsSystemPathAliases_NormalizesClippedAliasRoots(
		string actual,
		string expected)
	{
		Assert.Equal(
			expected,
			TerminalScreenSnapshot.NormalizeMacOsSystemPathAliases(
				actual,
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

	[Fact]
	public void ReplacePathForSnapshot_NormalizesJsonEscapedWindowsPath()
	{
		const string path = @"C:\Temp\folder\project";
		const string screen = "argv[3] = \"C:\\\\Temp\\\\folder\\\\project\"";

		Assert.Equal(
			"argv[3] = \"<PROJECT_ROOT>\"",
			TerminalScreenSnapshot.ReplacePathForSnapshot(
				screen,
				path,
				"<PROJECT_ROOT>",
				isMacOs: false));
	}

	[Theory]
	[InlineData(
		"Path: ...f4edda1e6ad83e9f53abfxxxxxxxxxxxxxxxxxxxxx/project",
		"/tmp/d1a9390ae3bf4edda1e6ad83e9f53abfxxxxxxxxxxxxxxxxxxxxx/project",
		"<PROJECT_ROOT>",
		"Path: <PROJECT_ROOT>")]
	[InlineData(
		"Path: ...cbefdc7a4d228111ef1accc2d35cxxxxxxxxxxxxxxxxxxxxx/project/AlphaProject",
		"/tmp/7297cbefdc7a4d228111ef1accc2d35cxxxxxxxxxxxxxxxxxxxxx/project",
		"<PROJECTS_ROOT>",
		"Path: <PROJECTS_ROOT>/AlphaProject")]
	[InlineData(
		"Source: file:///...evProjex.Tests.Terminal/e92fa456903c4997a865c53f11eccd66/CombatRepository",
		"/tmp/DevProjex.Tests.Terminal/e92fa456903c4997a865c53f11eccd66",
		"<ORIGIN_ROOT>",
		"Source: file:///<ORIGIN_ROOT>/CombatRepository")]
	public void ReplacePathForSnapshot_NormalizesClippedShallowUnixPath(
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
				isMacOs: false));
	}

	[Fact]
	public void NormalizePathPlaceholderSeparators_CanonicalizesJsonEscapedSeparator()
	{
		Assert.Equal(
			"\"<TEMP_ROOT>/DevProjex\"",
			TerminalScreenSnapshot.NormalizePathPlaceholderSeparators(
				"\"<TEMP_ROOT>\\\\DevProjex\""));
	}

	[Fact]
	public void NormalizePathPlaceholderSeparators_PreservesLiteralDoubleForwardSeparator()
	{
		Assert.Equal(
			"<PROJECT_ROOT>//src",
			TerminalScreenSnapshot.NormalizePathPlaceholderSeparators(
				"<PROJECT_ROOT>//src"));
	}

	[Fact]
	public void Normalize_DoesNotTreatHexadecimalLeafPrefixAsIdentifier()
	{
		Assert.Equal(
			"\"<TEMP_ROOT>/DevProjex\"",
			TerminalScreenSnapshot.Normalize(
				"\"<TEMP_ROOT>/DevProjex\"",
				[]));
	}

	[Fact]
	public void Normalize_StillRecognizesUniqueTruncatedProjectIdentifier()
	{
		Assert.Equal(
			"Path: <PROJECT_ROOT>",
			TerminalScreenSnapshot.Normalize(
				"Path: <TEMP_ROOT>/deadbeef",
				[]));
	}

	[Fact]
	public void Normalize_ReplacesClippedSystemTemporaryPathAfterFieldLabels()
	{
		var temporaryPrefix = Path.GetTempPath();

		var normalized = TerminalScreenSnapshot.Normalize(
			$"│ Destination {temporaryPrefix}clipped-project│",
			[]);

		Assert.Equal("│ Destination <SYSTEM_TEMP>/clipped-project│", normalized);
		Assert.DoesNotContain(temporaryPrefix, normalized, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Normalize_DistinguishesClippedProjectRootFromOtherSystemTemporaryPaths()
	{
		var temporaryPrefix = Path.GetTempPath();
		var normalized = TerminalScreenSnapshot.Normalize(
			$"│Root: {temporaryPrefix}DevProjex.Tests.Termin▲│{Environment.NewLine}" +
			$"│Destination {temporaryPrefix}clipped-project│",
			[]);

		Assert.Contains("│Root: <PROJECT_ROOT>▲│", normalized, StringComparison.Ordinal);
		Assert.Contains(
			"│Destination <SYSTEM_TEMP>/clipped-project│",
			normalized,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("│> [1] /tmp/session/Alpha Project│", "│> [1] <RECENT_PATH>│")]
	[InlineData("│  [2] C:\\Temp\\Beta Project│", "│  [2] <RECENT_PATH>│")]
	public void Normalize_ReplacesInlineRecentPaths(string screen, string expected)
	{
		Assert.Equal(expected, TerminalScreenSnapshot.Normalize(screen, []));
	}
}
