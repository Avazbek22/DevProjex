using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
public sealed class ReportPathResolverTests
{
	[Fact]
	public void Resolve_ExplicitPath_ReturnsFullPath()
	{
		using var temp = new TemporaryDirectory();
		var explicitPath = Path.Combine(temp.Path, "reports", "custom.json");
		var resolver = new ReportPathResolver();

		var resolved = resolver.Resolve(explicitPath);

		Assert.Equal(Path.GetFullPath(explicitPath), resolved);
	}

	[Fact]
	public void Resolve_RelativeExplicitPath_UsesCurrentProcessDirectory()
	{
		using var temp = new TemporaryDirectory();
		var workingDirectory = temp.CreateFolder("working directory");
		var relativePath = Path.Combine("reports", "relative.json");
		var resolver = new ReportPathResolver(currentDirectoryProvider: () => workingDirectory);

		var resolved = resolver.Resolve(relativePath);

		Assert.Equal(Path.GetFullPath(Path.Combine(workingDirectory, relativePath)), resolved);
	}

	[Fact]
	public void Resolve_AbsoluteExplicitPath_DoesNotReadCurrentProcessDirectory()
	{
		using var temp = new TemporaryDirectory();
		var explicitPath = Path.Combine(temp.Path, "reports", "absolute.json");
		var resolver = new ReportPathResolver(
			currentDirectoryProvider: () => throw new InvalidOperationException("Absolute report paths must not read current directory."),
			reportIdProvider: () => throw new InvalidOperationException("Explicit report paths must not create a default report identifier."));

		var resolved = resolver.Resolve(explicitPath);

		Assert.Equal(Path.GetFullPath(explicitPath), resolved);
	}

	[Fact]
	public void Resolve_DefaultPath_UsesDocumentsDevProjexReportsFolder()
	{
		var reportId = Guid.Parse("0f2f4f2a-1111-2222-3333-444444444444");
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? "/home/user/Documents" : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero),
			reportIdProvider: () => reportId);

		var resolved = resolver.Resolve();

		Assert.Equal(
			Path.Combine("/home/user/Documents", "DevProjex", "reports", "devprojex-report-2026-06-16_10-11-12-0f2f4f2a111122223333444444444444.json"),
			resolved);
	}

	[Fact]
	public void Resolve_DefaultPath_FallsBackFromWhitespaceDocumentsAndProfileToUserProfileThenTemp()
	{
		var userProfileResolver = new ReportPathResolver(
			specialFolderPathProvider: _ => "  ",
			userProfilePathProvider: () => "/home/user",
			tempPathProvider: () => "/tmp",
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));
		var tempResolver = new ReportPathResolver(
			specialFolderPathProvider: _ => "  ",
			userProfilePathProvider: () => "  ",
			tempPathProvider: () => "/tmp",
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero));

		Assert.StartsWith(Path.Combine("/home/user", "DevProjex", "reports"), userProfileResolver.Resolve());
		Assert.StartsWith(Path.Combine("/tmp", "DevProjex", "reports"), tempResolver.Resolve());
	}

	[Fact]
	public void Resolve_DefaultPath_UsesDistinctNamesWhenReportsShareTheSameSecond()
	{
		var reportIds = new Queue<Guid>(
		[
			Guid.Parse("11111111-1111-1111-1111-111111111111"),
			Guid.Parse("22222222-2222-2222-2222-222222222222")
		]);
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? "/home/user/Documents" : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 16, 10, 11, 12, TimeSpan.Zero),
			reportIdProvider: () => reportIds.Dequeue());

		var first = resolver.Resolve();
		var second = resolver.Resolve();

		Assert.NotEqual(first, second);
		Assert.EndsWith("-11111111111111111111111111111111.json", first, StringComparison.Ordinal);
		Assert.EndsWith("-22222222222222222222222222222222.json", second, StringComparison.Ordinal);
	}
}
