namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceTextFittingTests
{
	[Theory]
	[InlineData(
		"file:///Users/runner/work/_temp/session/CombatRepository",
		40,
		"file:///",
		"/CombatRepository")]
	[InlineData(
		"https://github.example.com/organization/projects/DevProjex",
		45,
		"https://github.example.com/",
		"/DevProjex")]
	public void FitPathToWidth_PreservesRepositorySourceIdentity(
		string value,
		int width,
		string expectedPrefix,
		string expectedSuffix)
	{
		var result = TerminalWorkspaceSession.FitPathToWidth(value, width);

		Assert.StartsWith(expectedPrefix, result, StringComparison.Ordinal);
		Assert.EndsWith(expectedSuffix, result, StringComparison.Ordinal);
		Assert.Contains("...", result, StringComparison.Ordinal);
		Assert.Equal(width, result.Length);
	}

	[Fact]
	public void FitPathToWidth_DoesNotPresentLocalWindowsPathAsFileUri()
	{
		const string value =
			@"C:\Users\developer\RiderProjects\organization\DevProjex";

		var result = TerminalWorkspaceSession.FitPathToWidth(value, 32);

		Assert.StartsWith("...", result, StringComparison.Ordinal);
		Assert.EndsWith(@"\DevProjex", result, StringComparison.Ordinal);
		Assert.DoesNotContain("file:///", result, StringComparison.Ordinal);
		Assert.Equal(32, result.Length);
	}
}
