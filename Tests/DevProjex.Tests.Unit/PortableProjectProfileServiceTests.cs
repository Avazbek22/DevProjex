using DevProjex.Application.Context;

namespace DevProjex.Tests.Unit;

public sealed class PortableProjectProfileServiceTests
{
	[Fact]
	public async Task SaveAsyncClassifiesMoveTimeDestinationRaceAsConflict()
	{
		using var workspace = new TemporaryDirectory();
		var profileDirectory = Path.Combine(workspace.Path, "profiles");
		var destination = Path.Combine(profileDirectory, "portable.json");
		const string competingContent = "created by another process";
		var service = new PortableProjectProfileService(
			(sourcePath, destinationPath, overwrite) =>
			{
				Assert.False(overwrite);
				File.WriteAllText(destinationPath, competingContent);
				File.Move(sourcePath, destinationPath, overwrite);
			});

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() =>
			service.SaveAsync(
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
