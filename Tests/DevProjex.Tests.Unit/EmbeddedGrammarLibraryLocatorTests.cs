using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class EmbeddedGrammarLibraryLocatorTests
{
	[Fact]
	public void Resolve_LockedSharedLibrary_UsesVerifiedProcessRepairOnWindows()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temporary = new TemporaryDirectory();
		var locator = CreateLocator(temporary.Path);
		var library = locator.EnumerateLibraries()[0];
		var sharedPath = locator.Resolve(library);

		using var sharedLock = new FileStream(
			sharedPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);

		var resolvedPath = CreateLocator(temporary.Path).Resolve(library);

		Assert.False(PathComparer.Default.Equals(sharedPath, resolvedPath));
		Assert.StartsWith(
			Path.Combine(temporary.Path, $"repair-{Environment.ProcessId}") +
			Path.DirectorySeparatorChar,
			resolvedPath,
			StringComparison.OrdinalIgnoreCase);
		Assert.Equal(
			locator.GetEmbeddedHash(library),
			System.Security.Cryptography.SHA256.HashData(
				File.ReadAllBytes(resolvedPath)));
	}

	[Fact]
	public void PruneAbandonedDirectories_CustomRootNeverDeletesAncestorSiblings()
	{
		using var temporary = new TemporaryDirectory();
		var customParent = Directory.CreateDirectory(
			Path.Combine(temporary.Path, "custom-parent")).FullName;
		var root = Directory.CreateDirectory(
			Path.Combine(customParent, "custom-root")).FullName;
		var unrelated = Directory.CreateDirectory(
			Path.Combine(temporary.Path, "unrelated-application-data")).FullName;
		File.WriteAllText(Path.Combine(unrelated, "must-survive.txt"), "sentinel");
		Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(2));
		var localRepair = Directory.CreateDirectory(
			Path.Combine(root, "repair-1234")).FullName;
		Directory.SetLastWriteTimeUtc(localRepair, DateTime.UtcNow - TimeSpan.FromDays(2));
		var locator = CreateLocator(root);

		var removed = locator.PruneAbandonedDirectories();

		Assert.True(Directory.Exists(unrelated));
		Assert.Equal("sentinel", File.ReadAllText(Path.Combine(unrelated, "must-survive.txt")));
		Assert.False(Directory.Exists(localRepair));
		Assert.Contains(localRepair, removed, PathComparer.Default);
		Assert.DoesNotContain(unrelated, removed, PathComparer.Default);
	}

	[Fact]
	public void PruneAbandonedDirectories_ManagedLayoutRemovesOnlyStaleManagedSiblings()
	{
		using var temporary = new TemporaryDirectory();
		var grammars = Path.Combine(temporary.Path, "DevProjex", "grammars");
		var currentVersion = Path.Combine(grammars, "tree-sitter-current");
		var currentRid = Directory.CreateDirectory(
			Path.Combine(currentVersion, GrammarPlatform.RuntimeIdentifier)).FullName;
		var staleVersion = Directory.CreateDirectory(
			Path.Combine(grammars, "tree-sitter-stale")).FullName;
		var staleRid = Directory.CreateDirectory(
			Path.Combine(currentVersion, "obsolete-rid")).FullName;
		Directory.SetLastWriteTimeUtc(staleVersion, DateTime.UtcNow - TimeSpan.FromDays(2));
		Directory.SetLastWriteTimeUtc(staleRid, DateTime.UtcNow - TimeSpan.FromDays(2));
		var locator = CreateLocator(currentRid);

		var removed = locator.PruneAbandonedDirectories();

		Assert.True(Directory.Exists(currentRid));
		Assert.False(Directory.Exists(staleVersion));
		Assert.False(Directory.Exists(staleRid));
		Assert.Contains(staleVersion, removed, PathComparer.Default);
		Assert.Contains(staleRid, removed, PathComparer.Default);
	}

	private static EmbeddedGrammarLibraryLocator CreateLocator(string root) =>
		new(
			typeof(TreeSitterCodeCompressor).Assembly,
			CodeCompressionFactory.EmbeddedResourcePrefix,
			root);
}
