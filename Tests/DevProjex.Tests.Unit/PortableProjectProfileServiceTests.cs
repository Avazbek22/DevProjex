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
}
