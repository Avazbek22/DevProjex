namespace DevProjex.Tests.Terminal;

public sealed class ExactOutputPathSyntaxTests
{
	[Theory]
	[InlineData(null)]
	[InlineData("-")]
	[InlineData("out.md")]
	[InlineData("../out.md")]
	[InlineData("dir/out.md")]
	[InlineData("dir\\out.md")]
	[InlineData("C:\\work\\out.md")]
	[InlineData("C:out.md")]
	[InlineData("\\\\server\\share\\out.md")]
	[InlineData("//server/share/out.md")]
	[InlineData("\\\\?\\C:\\work\\out.md")]
	[InlineData("\\\\?\\UNC\\server\\share\\out.md")]
	[InlineData("@scope/out.md")]
	[InlineData(" leading-space.md")]
	[InlineData("NULL.md")]
	[InlineData("console.log")]
	[InlineData("COM10.txt")]
	[InlineData("LPT0")]
	[InlineData("nul/out.md")]
	[InlineData("C:\\work")]
	[InlineData(".")]
	public void WindowsAcceptsFilePathsAndLeavesFolderSpellingWithoutSeparatorToTheWriter(string? path)
	{
		Assert.Equal(ExactOutputPathProblem.None, ExactOutputPathSyntax.Check(path, isWindows: true));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("   ")]
	[InlineData("\t")]
	public void WhitespaceOnlyPathIsEmptyOnEveryPlatform(string path)
	{
		Assert.Equal(ExactOutputPathProblem.Empty, ExactOutputPathSyntax.Check(path, isWindows: true));
		Assert.Equal(ExactOutputPathProblem.Empty, ExactOutputPathSyntax.Check(path, isWindows: false));
	}

	[Theory]
	[InlineData("out/")]
	[InlineData("out\\")]
	[InlineData("C:\\")]
	[InlineData("C:/")]
	[InlineData("/")]
	[InlineData("\\\\server\\share\\")]
	[InlineData("..\\")]
	[InlineData("dir/sub\\")]
	public void WindowsRejectsBothTrailingSeparators(string path)
	{
		Assert.Equal(
			ExactOutputPathProblem.TrailingSeparator,
			ExactOutputPathSyntax.Check(path, isWindows: true));
	}

	[Theory]
	[InlineData("out/")]
	[InlineData("/")]
	[InlineData("/tmp/")]
	[InlineData("dir\\name/")]
	public void UnixRejectsATrailingSlash(string path)
	{
		Assert.Equal(
			ExactOutputPathProblem.TrailingSeparator,
			ExactOutputPathSyntax.Check(path, isWindows: false));
	}

	[Theory]
	[InlineData("out\\")]
	[InlineData("name\\")]
	public void UnixKeepsATrailingBackslashAsPartOfTheFileName(string path)
	{
		Assert.Equal(ExactOutputPathProblem.None, ExactOutputPathSyntax.Check(path, isWindows: false));
	}

	[Theory]
	[InlineData("out|file.md")]
	[InlineData("out<file>.md")]
	[InlineData("out\"file.md")]
	[InlineData("out*.md")]
	[InlineData("out?.md")]
	[InlineData("dir/out:stream.md")]
	[InlineData("C:\\work\\out:stream")]
	[InlineData("dir*/out.md")]
	[InlineData("out\u0001.md")]
	[InlineData("out\0.md")]
	public void WindowsRejectsInvalidNameCharacters(string path)
	{
		Assert.Equal(
			ExactOutputPathProblem.InvalidSyntax,
			ExactOutputPathSyntax.Check(path, isWindows: true));
	}

	[Theory]
	[InlineData("out|file.md")]
	[InlineData("out*.md")]
	[InlineData("dir/out:stream.md")]
	[InlineData("out\u0001.md")]
	public void UnixAcceptsCharactersThatOnlyWindowsReserves(string path)
	{
		Assert.Equal(ExactOutputPathProblem.None, ExactOutputPathSyntax.Check(path, isWindows: false));
	}

	[Fact]
	public void UnixRejectsANulCharacter()
	{
		Assert.Equal(
			ExactOutputPathProblem.InvalidSyntax,
			ExactOutputPathSyntax.Check("out\0.md", isWindows: false));
	}

	[Theory]
	[InlineData("NUL")]
	[InlineData("nul")]
	[InlineData("Nul")]
	[InlineData("NUL.txt")]
	[InlineData("nul.tar.gz")]
	[InlineData("NUL .txt")]
	[InlineData("CON")]
	[InlineData("con.md")]
	[InlineData("PRN")]
	[InlineData("aux.json")]
	[InlineData("COM1")]
	[InlineData("com9.zip")]
	[InlineData("LPT1")]
	[InlineData("lpt9.md")]
	[InlineData("dir/NUL")]
	[InlineData("C:\\work\\con.txt")]
	[InlineData("\\\\server\\share\\NUL.md")]
	[InlineData("\\\\.\\NUL")]
	[InlineData("\\\\.\\pipe\\name")]
	[InlineData("//./COM1")]
	public void WindowsRejectsReservedDeviceNamesWithOrWithoutExtension(string path)
	{
		Assert.Equal(
			ExactOutputPathProblem.DeviceName,
			ExactOutputPathSyntax.Check(path, isWindows: true));
	}

	[Theory]
	[InlineData("NUL")]
	[InlineData("nul.txt")]
	[InlineData("CON")]
	[InlineData("com1.md")]
	[InlineData("LPT9")]
	[InlineData("aux.json")]
	public void UnixTreatsWindowsDeviceNamesAsOrdinaryFileNames(string path)
	{
		Assert.Equal(ExactOutputPathProblem.None, ExactOutputPathSyntax.Check(path, isWindows: false));
	}

	[Fact]
	public void HostPlatformCheckMatchesTheExplicitPlatformBranch()
	{
		Assert.Equal(
			ExactOutputPathSyntax.Check("NUL.txt", OperatingSystem.IsWindows()),
			ExactOutputPathSyntax.Check("NUL.txt"));
		Assert.Equal(
			ExactOutputPathSyntax.Check("out\\", OperatingSystem.IsWindows()),
			ExactOutputPathSyntax.Check("out\\"));
	}
}
