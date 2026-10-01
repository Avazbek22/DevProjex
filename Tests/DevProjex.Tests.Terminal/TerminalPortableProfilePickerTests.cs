using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalPortableProfilePickerTests
{
	[Fact]
	public void PickedProfileFileThatDoesNotExistIsNotFound()
	{
		using var workspace = new TemporaryDirectory();

		var failure = TerminalWorkspaceSession.ClassifyPickedPortableProfile(
			Path.Combine(workspace.Path, "missing.json"));

		Assert.NotNull(failure);
		Assert.Equal("DPX-CLI-PROFILE-NOT-FOUND", failure.Value.Code);
		Assert.Equal("Terminal.Error.ProfileUnresolved", failure.Value.MessageKey);
	}

	[Fact]
	public void PickedProfileFileThatExistsIsLeftToTheLoader()
	{
		using var workspace = new TemporaryDirectory();
		var malformed = workspace.WriteFile("malformed.json", "{ not json");

		Assert.Null(TerminalWorkspaceSession.ClassifyPickedPortableProfile(malformed));
	}

	[Theory]
	[InlineData("DPX-CLI-PROFILE-NOT-FOUND", "Terminal.Error.ProfileUnresolved")]
	[InlineData("DPX-CLI-PROFILE-INVALID", "Terminal.Error.ProfileInvalid")]
	public void LoadFailureMessageMatchesItsCode(string code, string expectedKey)
	{
		Assert.Equal(expectedKey, TerminalWorkspaceSession.PortableProfileFailureMessageKey(code));
	}

	[Fact]
	public async Task MalformedExistingProfileStillFailsAsInvalidWhenLoaded()
	{
		using var workspace = new TemporaryDirectory();
		var malformed = workspace.WriteFile("malformed.json", "{ not json");

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() =>
			new PortableProjectProfileService().LoadAsync(malformed, TestContext.Current.CancellationToken));

		Assert.Equal("DPX-CLI-PROFILE-INVALID", exception.Code);
		Assert.Equal(
			"Terminal.Error.ProfileInvalid",
			TerminalWorkspaceSession.PortableProfileFailureMessageKey(exception.Code));
	}
}
