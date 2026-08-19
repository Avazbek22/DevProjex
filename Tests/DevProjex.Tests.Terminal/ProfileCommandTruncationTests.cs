using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

public sealed class ProfileCommandTruncationTests
{
	private const int SelectionItemLimit = 100_000;

	[Fact]
	public async Task ImportApply_WhenSelectionExceedsStorageLimit_ReturnsExplicitFailure()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var profilePath = WriteProfile(
			workspace,
			SelectionItemLimit + 1);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var handler = new ProfileCommandHandler(services, new TestTerminalEnvironment());

		var exception = await Assert.ThrowsAsync<PortableProjectProfileException>(() => handler.ImportAsync(
			profilePath,
			projectPath,
			apply: true,
			TestContext.Current.CancellationToken));

		Assert.Equal("DPX-CLI-PROFILE-SELECTION-TOO-LARGE", exception.Code);
		Assert.Contains("100,000", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ImportApply_WhenSelectionMatchesStorageLimit_Succeeds()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}\n");
		var profilePath = WriteProfile(
			workspace,
			SelectionItemLimit);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var handler = new ProfileCommandHandler(services, new TestTerminalEnvironment());

		var exitCode = await handler.ImportAsync(
			profilePath,
			projectPath,
			apply: true,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.True(services.LocalProfileStore.TryLoadProfile(projectPath, out var profile));
		Assert.Equal(SelectionItemLimit, profile.SelectedExtensions.Count);
	}

	private static string WriteProfile(TemporaryDirectory workspace, int extensionCount)
	{
		var extensions = Enumerable.Range(0, extensionCount)
			.Select(static index => index == 0 ? ".cs" : $".x{index:D6}")
			.ToArray();
		var json = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			selection = new
			{
				roots = (string[]?)null,
				extensions,
				selectedPaths = Array.Empty<string>(),
				gitMode = "none",
				exclusions = Array.Empty<string>()
			}
		});
		return workspace.WriteFile("profile.json", json);
	}
}
