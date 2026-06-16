using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.Unit;

public sealed class ReportPathResolverTests
{
	[Fact]
	public void Resolve_ExplicitPath_ReturnsFullPath()
	{
		using var temp = new TemporaryDirectory();
		var explicitPath = Path.Combine(temp.Path, "reports", "custom.json");
		var resolver = new ReportPathResolver();

		var resolved = resolver.Resolve(new StartupReportOptions(true, explicitPath, StartupReportFormat.Json));

		Assert.Equal(Path.GetFullPath(explicitPath), resolved);
	}

	[Fact]
	public void Resolve_DefaultPath_UsesDocumentsDevProjexReportsFolder()
	{
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? "/home/user/Documents" : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));

		var resolved = resolver.Resolve(new StartupReportOptions(true, null, StartupReportFormat.Json));

		Assert.Equal(
			Path.Combine("/home/user/Documents", "DevProjex", "reports", "devprojex-report-2026-06-16_10-11-12.json"),
			resolved);
	}

	[Fact]
	public void Resolve_DefaultPath_FallsBackToUserProfileThenTemp()
	{
		var userProfileResolver = new ReportPathResolver(
			specialFolderPathProvider: _ => string.Empty,
			userProfilePathProvider: () => "/home/user",
			tempPathProvider: () => "/tmp",
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));
		var tempResolver = new ReportPathResolver(
			specialFolderPathProvider: _ => string.Empty,
			userProfilePathProvider: () => string.Empty,
			tempPathProvider: () => "/tmp",
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));

		Assert.StartsWith(Path.Combine("/home/user", "DevProjex", "reports"), userProfileResolver.Resolve(StartupReportOptions.Disabled));
		Assert.StartsWith(Path.Combine("/tmp", "DevProjex", "reports"), tempResolver.Resolve(StartupReportOptions.Disabled));
	}
}
