namespace DevProjex.Tests.Terminal;

public sealed class PublishedApplicationLocatorTests
{
	[Theory]
	[InlineData("bin", "Debug", "net10.0", "Debug")]
	[InlineData("bin", "Release", "net10.0", "Release")]
	[InlineData("artifacts", "bin", "Tests", "debug", "Debug")]
	[InlineData("release", "artifacts", "bin", "Tests", "debug", "Debug")]
	public void ResolveBuildConfigurationUsesTheNearestConfigurationSegment(
		params string[] segments)
	{
		var expected = segments[^1];
		var baseDirectory = Path.Combine(segments[..^1]);

		Assert.Equal(
			expected,
			PublishedApplicationLocator.ResolveBuildConfiguration(baseDirectory));
	}

	[Fact]
	public void ResolveBuildConfigurationDefaultsToDebugWithoutAConfigurationSegment()
	{
		Assert.Equal(
			"Debug",
			PublishedApplicationLocator.ResolveBuildConfiguration(
				Path.Combine("artifacts", "bin", "Tests")));
	}
}
