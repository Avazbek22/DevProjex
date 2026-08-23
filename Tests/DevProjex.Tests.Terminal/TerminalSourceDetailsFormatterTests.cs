using System.Globalization;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalSourceDetailsFormatterTests
{
	[Fact]
	public void CachedRepositoryUsesSafeIdentityAndMetadataWithoutPhysicalPath()
	{
		const string repositoryUrl = "https://user:secret@example.com/owner/repository.git";
		const string safeUrl = "https://example.com/owner/repository.git";
		const string cachePath = "/home/alice/.cache/DevProjex/repository_0123456789AB";
		var identity = new ProjectSourceIdentity(
			"repository",
			ProjectSourceType.GitClone,
			safeUrl,
			repositoryUrl,
			"main",
			"0123456789abcdef",
			IsCachedRepository: true);
		var entry = new RepositoryCacheIndexEntry(
			"identity",
			safeUrl,
			cachePath,
			"main",
			"0123456789abcdef",
			new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.Zero),
			RepositoryCacheEntryState.Ready,
			ApproximateSizeBytes: 1_572_864);

		var result = TerminalSourceDetailsFormatter.Format(
			cachePath,
			identity,
			entry,
			Localize,
			CultureInfo.InvariantCulture);

		Assert.Contains($"Repository URL: {safeUrl}", result, StringComparison.Ordinal);
		Assert.Contains("Branch: main", result, StringComparison.Ordinal);
		Assert.Contains("Commit: 0123456789ab", result, StringComparison.Ordinal);
		Assert.Contains("Size: 1.5 MB", result, StringComparison.Ordinal);
		Assert.Contains("Last opened:", result, StringComparison.Ordinal);
		Assert.DoesNotContain(cachePath, result, StringComparison.Ordinal);
		Assert.DoesNotContain("secret", result, StringComparison.Ordinal);
		Assert.DoesNotContain("Source reference", result, StringComparison.Ordinal);
	}

	[Fact]
	public void LocalFolderKeepsItsUsefulSourcePath()
	{
		var root = Path.GetFullPath(Path.Combine("projects", "sample"));
		var identity = new ProjectSourceIdentity("sample", ProjectSourceType.LocalFolder, root);

		var result = TerminalSourceDetailsFormatter.Format(
			root,
			identity,
			cacheEntry: null,
			Localize,
			CultureInfo.InvariantCulture);

		Assert.Equal($"Project folder: {root}", result);
	}

	private static string Localize(string key) => key switch
	{
		"Terminal.Tui.RepositoryUrl" => "Repository URL:",
		"Terminal.Tui.RecentRepositories.Branch" => "Branch",
		"Terminal.Tui.Commit" => "Commit",
		"Terminal.Analysis.Size" => "Size",
		"Terminal.Tui.Recent.LastOpened" => "Last opened",
		"Terminal.Tui.SourceReference" => "Project folder",
		_ => key
	};
}
