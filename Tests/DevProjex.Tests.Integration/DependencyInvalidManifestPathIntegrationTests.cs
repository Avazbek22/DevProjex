using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyInvalidManifestPathIntegrationTests
{
	[Theory]
	[InlineData("[lib]\npath = \"bad\\u0000path\"\n")]
	[InlineData("[dependencies]\nlocal = { path = \"bad\\u0000path\" }\n")]
	public async Task InvalidCargoPathReportsUnsupportedConfiguration(string content)
	{
		using var fixture = new TemporaryDirectory();
		var manifest = fixture.CreateFile("Cargo.toml", content);

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[manifest],
			TestContext.Current.CancellationToken);

		var scope = Assert.Single(result.Scopes, scope => scope.ScopeId == "rust:Cargo.toml");
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, scope.ConfigurationState);
		var diagnostic = Assert.Single(result.ConfigurationDiagnostics);
		Assert.Equal("Cargo.toml", diagnostic.Path);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal(FileDependencyConfigurationProvider.CargoInvalidPathReason, diagnostic.Reason);
	}

	[Fact]
	public async Task InvalidGemfilePathReportsUnsupportedConfiguration()
	{
		using var fixture = new TemporaryDirectory();
		var manifest = fixture.CreateFile("Gemfile", "gem 'local', path: 'bad\0path'\n");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[manifest],
			TestContext.Current.CancellationToken);

		var scope = Assert.Single(result.Scopes, scope => scope.ScopeId == "ruby:.");
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, scope.ConfigurationState);
		var diagnostic = Assert.Single(result.ConfigurationDiagnostics);
		Assert.Equal("Gemfile", diagnostic.Path);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, diagnostic.State);
		Assert.Equal(FileDependencyConfigurationProvider.RubyInvalidPathReason, diagnostic.Reason);
	}
}
