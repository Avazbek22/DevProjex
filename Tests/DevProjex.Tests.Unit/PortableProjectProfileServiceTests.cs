using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class PortableProjectProfileServiceTests
{
	[Fact]
	public async Task SaveAsyncMapsSharedDestinationConflictToProfileContract()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var profileDirectory = workspace.CreateFolder("profiles");
		var destination = Path.Combine(profileDirectory, "portable.json");
		const string competingContent = "created by another process";
		File.WriteAllText(destination, competingContent);
		var service = new PortableProjectProfileService();

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() =>
			service.SaveAsync(
				sourceRoot,
				destination,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: []),
				overwrite: false,
				TestContext.Current.CancellationToken));

		Assert.Equal("DPX-PROFILE-DESTINATION-EXISTS", exception.Code);
		Assert.Equal(competingContent, await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken));
		Assert.Empty(Directory.EnumerateFiles(
			profileDirectory,
			".portable.json.*.tmp",
			SearchOption.TopDirectoryOnly));
	}

	[Fact]
	public async Task SaveAsyncReportsStableRequestedAliasAfterPhysicalCommit()
	{
		using var workspace = new TemporaryDirectory();
		var sourceRoot = workspace.CreateFolder("project");
		var profileDirectory = workspace.CreateFolder("profiles");
		var alias = Path.Combine(workspace.Path, "profile-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, profileDirectory);
		}
		catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       PlatformNotSupportedException)
		{
			Assert.Skip(
				$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		try
		{
			var requestedPath = Path.Combine(alias, "portable.json");
			var service = new PortableProjectProfileService();

			var writtenPath = await service.SaveAsync(
				sourceRoot,
				requestedPath,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: []),
				overwrite: false,
				TestContext.Current.CancellationToken);

			Assert.Equal(Path.GetFullPath(requestedPath), writtenPath);
			Assert.True(File.Exists(Path.Combine(profileDirectory, "portable.json")));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}
}
