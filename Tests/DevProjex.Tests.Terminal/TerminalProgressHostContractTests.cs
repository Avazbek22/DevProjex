namespace DevProjex.Tests.Terminal;

public sealed class TerminalProgressHostContractTests
{
	[Fact]
	public void ProductionTerminalAssemblyContainsNoCheckpointProtocolOrBlockingImplementation()
	{
		var assembly = typeof(TerminalApplication).Assembly;
		var binaryText = Encoding.UTF8.GetString(
			File.ReadAllBytes(assembly.Location));

		Assert.Null(assembly.GetType(
			"DevProjex.Terminal.Tui.TerminalProgressTestCheckpoint",
			throwOnError: false));
		Assert.DoesNotContain(
			TerminalProgressCheckpointProtocol.CheckpointsVariable,
			binaryText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			TerminalProgressCheckpointProtocol.PhasesVariable,
			binaryText,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			TerminalProgressCheckpointProtocol.DirectoryName,
			binaryText,
			StringComparison.Ordinal);
	}

	[Fact]
	public void ProgressCheckpointHostIsASeparateTestArtifact()
	{
		var host = PublishedApplicationLocator
			.FindProgressCheckpointHostExecutable();
		var repositoryRoot = PublishedApplicationLocator.FindRepositoryRoot();
		var testHostRoot = Path.Combine(
			repositoryRoot,
			"Tests",
			"DevProjex.Tests.Terminal.ProgressHost");

		Assert.True(PathUtility.IsPathInside(host, testHostRoot));
		Assert.False(PathUtility.IsPathInside(
			host,
			Path.Combine(
				repositoryRoot,
				"Apps",
				"Avalonia",
				"DevProjex.Avalonia")));
	}
}
