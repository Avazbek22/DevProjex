namespace DevProjex.Tests.Terminal;

public sealed class TerminalPtyHarnessContractTests
{
	[Fact]
	public void DataRootKeepsGitObjectFilesBelowTheLegacyWindowsPathLimit()
	{
		const int legacyWindowsPathLimit = 260;
		const string hostedRunnerTemp =
			@"C:\Users\runneradmin\AppData\Local\Temp";
		var invocationId = new string('a', 32);
		var repositoryDirectory =
			new string('r', 32) + "_" + new string('A', 29);
		var repositoryPath = string.Join(
			'\\',
			hostedRunnerTemp,
			TerminalPtyHarness.DataRootDirectoryName,
			invocationId,
			"devprojex",
			"RepoCache",
			".staging",
			repositoryDirectory);
		var looseObjectPath = string.Join(
			'\\',
			repositoryPath,
			".git",
			"objects",
			"ab",
			new string('0', 38));
		var packFileName = "pack-" + new string('0', 40) + ".pack";
		var packPath = string.Join(
			'\\',
			repositoryPath,
			".git",
			"objects",
			"pack",
			packFileName);

		AssertBelowLegacyLimit(looseObjectPath);
		AssertBelowLegacyLimit(packPath);

		void AssertBelowLegacyLimit(string path)
		{
			Assert.True(
				path.Length < legacyWindowsPathLimit,
				$"The PTY fixture Git object path is {path.Length} characters: {path}");
		}
	}
}
