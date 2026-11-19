using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class CacheRemoveArgumentOrderTests
{
	[Fact]
	public async Task CacheRemoveWithoutUrlReportsTheMissingArgumentFirst()
	{
		using var data = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(["cache", "remove", "--language", "en"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.DoesNotContain(
			localization["Terminal.Validation.CacheForceRequired"],
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Contains("URL", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CacheRemoveWithUrlStillRequiresConfirmation()
	{
		using var data = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => data.Path))
			.RunAsync(
				["cache", "remove", "https://github.com/example/repo", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(
			localization["Terminal.Validation.CacheForceRequired"],
			environment.StandardError,
			StringComparison.Ordinal);
	}
}
