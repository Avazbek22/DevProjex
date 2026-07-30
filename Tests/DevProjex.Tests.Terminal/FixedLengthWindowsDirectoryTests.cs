namespace DevProjex.Tests.Terminal;

public sealed class FixedLengthWindowsDirectoryTests
{
	[Fact]
	public void BuilderKeepsTotalPathAndLeafStableAcrossDifferentTemporaryRoots()
	{
		var shortRoot = Path.Combine(Path.GetTempPath(), "u");
		var longRoot = Path.Combine(Path.GetTempPath(), "runneradmin");
		const string projectName = "0123456789abcdef0123456789abcdef";
		const int totalPathLength = 160;

		var shortPath = FixedLengthWindowsDirectory.BuildPath(
			shortRoot,
			totalPathLength,
			projectName,
			new string('a', 32));
		var longPath = FixedLengthWindowsDirectory.BuildPath(
			longRoot,
			totalPathLength,
			projectName,
			new string('b', 32));

		Assert.Equal(totalPathLength, shortPath.Path.Length);
		Assert.Equal(totalPathLength, longPath.Path.Length);
		Assert.Equal(projectName, Path.GetFileName(shortPath.Path));
		Assert.Equal(projectName, Path.GetFileName(longPath.Path));
		Assert.Equal(shortPath.OwnedRoot.Length, longPath.OwnedRoot.Length);
		Assert.NotEqual(
			Path.GetFileName(shortPath.OwnedRoot).Length,
			Path.GetFileName(longPath.OwnedRoot).Length);
	}
}
