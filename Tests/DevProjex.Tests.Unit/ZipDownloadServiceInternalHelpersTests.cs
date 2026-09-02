namespace DevProjex.Tests.Unit;

public sealed class ZipDownloadServiceInternalHelpersTests
{
	[Theory]
	[InlineData("nul", true)]
	[InlineData("CON.txt", true)]
	[InlineData("com1.tar.gz", true)]
	[InlineData("COM\u00B9.txt", true)]
	[InlineData("lpt\u00B2", true)]
	[InlineData("AUX", true)]
	[InlineData("COM10", false)]
	[InlineData("COM\u2074", false)]
	[InlineData("console.log", false)]
	[InlineData("nullable.cs", false)]
	public void IsWindowsReservedDeviceName_ClassifiesBaseComponent(string component, bool expected)
	{
		Assert.Equal(expected, ZipDownloadService.IsWindowsReservedDeviceName(component));
	}

    [Theory]
    [InlineData("repo-main/", "repo-main")]
    [InlineData("repo-main/src/file.txt", "repo-main")]
    [InlineData("repo-main", null)]
    [InlineData("", null)]
    public void TryGetTopLevelFolder_ReturnsExpectedValue(string entryPath, string? expected)
    {
        var method = GetPrivateStaticMethod("TryGetTopLevelFolder");

        var actual = (string?)method.Invoke(null, [entryPath]);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("repo-main/src/file.txt", "repo-main", true)]
    [InlineData("repo-main/", "repo-main", true)]
    [InlineData("repo-main", "repo-main", false)]
    [InlineData("repo-mainx/src/file.txt", "repo-main", false)]
    [InlineData("other-main/src/file.txt", "repo-main", false)]
    public void StartsWithFolderPrefix_ReturnsExpectedValue(string value, string folderName, bool expected)
    {
        var method = GetPrivateStaticMethod("StartsWithFolderPrefix");

        var actual = (bool)method.Invoke(null, [value, folderName])!;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveSafeDestinationPath_AllowsNestedEntryInsideTarget()
    {
        using var temp = new TemporaryDirectory();

        var actual = ZipDownloadService.ResolveSafeDestinationPath(temp.Path, "repo-main/src/app.cs");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(temp.Path, "repo-main", "src", "app.cs")),
            actual);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData("repo-main/../../outside.txt")]
    [InlineData(@"repo-main\..\..\outside.txt")]
    [InlineData("repo-main/src/../../../outside.txt")]
    public void ResolveSafeDestinationPath_RejectsTraversalOutsideTarget(string entryPath)
    {
        using var temp = new TemporaryDirectory();

        Assert.Throws<InvalidDataException>(() =>
            ZipDownloadService.ResolveSafeDestinationPath(temp.Path, entryPath));
    }

    [Fact]
    public void ResolveSafeDestinationPath_DoesNotConfuseSiblingPrefixWithTarget()
    {
        using var temp = new TemporaryDirectory();
        var siblingPrefix = temp.Path + "-sibling";
        var entryPath = Path.Combine("..", Path.GetFileName(siblingPrefix), "payload.txt");

        Assert.Throws<InvalidDataException>(() =>
            ZipDownloadService.ResolveSafeDestinationPath(temp.Path, entryPath));
    }

    private static MethodInfo GetPrivateStaticMethod(string name)
    {
        var method = typeof(ZipDownloadService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!;
    }
}

