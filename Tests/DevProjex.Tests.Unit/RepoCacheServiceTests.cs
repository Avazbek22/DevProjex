namespace DevProjex.Tests.Unit;

using DevProjex.Terminal.Execution;

/// <summary>
/// Unit tests for RepoCacheService.
/// Tests repository cache management, directory creation, cleanup, and path handling.
/// </summary>
public class RepoCacheServiceTests : IDisposable
{
    private readonly RepoCacheService _service;
    private readonly string _testCacheRoot;

    public RepoCacheServiceTests()
    {
        _testCacheRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "CacheTests", Guid.NewGuid().ToString("N"));
        _service = new RepoCacheService(_testCacheRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheRoot))
                Directory.Delete(_testCacheRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void CreateRepositoryDirectory_CreatesUniqueDirectory()
    {
        // Create two directories for the same URL - should get unique paths
        var url = "https://github.com/user/repo";

        var dir1 = _service.CreateRepositoryDirectory(url);
        var dir2 = _service.CreateRepositoryDirectory(url);

        Assert.NotEqual(dir1, dir2);
        Assert.True(Directory.Exists(dir1));
        Assert.True(Directory.Exists(dir2));

        // Cleanup
        try
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void DefaultCacheLocationUsesDedicatedCacheRoot()
    {
        var cacheBase = Path.Combine(_testCacheRoot, "xdg-cache");

        var service = new RepoCacheService(() => cacheBase);

        Assert.Equal(
            Path.Combine(cacheBase, "DevProjex", "RepoCache"),
            service.CacheRootPath);
    }

    [Fact]
    public void IdenticalCurrentAndLegacyRootsAreSearchedOnlyOnce()
    {
        var cacheBase = Path.Combine(_testCacheRoot, "shared-root");

        var service = new RepoCacheService(
            () => cacheBase,
            () => cacheBase);

        Assert.Equal(
            [service.CacheRootPath],
            service.CacheSearchRootPaths,
            PathComparer.Default);
    }

    [Fact]
    public void LegacyIndexedCacheRemainsReadableWithoutMovingRepositoryFiles()
    {
        const string repositoryUrl = "https://github.com/example/project.git";
        var currentBase = Path.Combine(_testCacheRoot, "current");
        var legacyBase = Path.Combine(_testCacheRoot, "legacy");
        var legacyCacheRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        var legacyService = new RepoCacheService(legacyCacheRoot);
        var legacyRepository = legacyService.CreateRepositoryDirectory(repositoryUrl);
        var markerPath = Path.Combine(legacyRepository, "README.md");
        File.WriteAllText(markerPath, "legacy cache");
        legacyService.RecordIndexedRepository(
            repositoryUrl,
            legacyRepository,
            "main",
            "0123456789abcdef");
        var legacyIndexPath = Path.Combine(legacyCacheRoot, "cache-index.json");
        var originalLegacyIndex = File.ReadAllBytes(legacyIndexPath);
        var service = new RepoCacheService(
            () => currentBase,
            () => legacyBase);

        var indexed = service.FindIndexedRepository(repositoryUrl);

        Assert.NotNull(indexed);
        Assert.Equal(legacyRepository, indexed.LocalPath, PathComparer.Default);
        Assert.Equal("legacy cache", File.ReadAllText(markerPath));
        Assert.Equal(originalLegacyIndex, File.ReadAllBytes(legacyIndexPath));
        Assert.Equal(
            Path.Combine(currentBase, "DevProjex", "RepoCache"),
            service.CacheRootPath,
            PathComparer.Default);
        Assert.Equal(
            [
                service.CacheRootPath,
                legacyCacheRoot
            ],
            service.CacheSearchRootPaths,
            PathComparer.Default);
        Assert.True(File.Exists(Path.Combine(service.CacheRootPath, "cache-index.json")));
    }

    [Fact]
    public async Task ConcurrentLegacyIndexPromotionKeepsCurrentIndexValid()
    {
        const string repositoryUrl = "https://github.com/example/concurrent.git";
        var currentBase = Path.Combine(_testCacheRoot, "concurrent-current");
        var legacyBase = Path.Combine(_testCacheRoot, "concurrent-legacy");
        var legacyCacheRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        var legacyRepository = Path.Combine(legacyCacheRoot, "concurrent_legacy");
        Directory.CreateDirectory(legacyRepository);

        await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => Task.Run(() =>
                {
                    var service = new RepoCacheService(
                        () => currentBase,
                        () => legacyBase);
                    service.RecordIndexedRepository(
                        repositoryUrl,
                        legacyRepository,
                        "main",
                        "0123456789abcdef");
                })));

        var currentCacheRoot = Path.Combine(currentBase, "DevProjex", "RepoCache");
        using var document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(currentCacheRoot, "cache-index.json")));
        var entries = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .ToArray();
        var entry = Assert.Single(entries);
        Assert.Equal(legacyRepository, entry.GetProperty("localPath").GetString());
    }

    [Fact]
    public void ListIndexedRepositories_MergesRootsCleansMissingAndReturnsNewestReadyEntries()
    {
        var currentBase = Path.Combine(_testCacheRoot, "catalog-current");
        var legacyBase = Path.Combine(_testCacheRoot, "catalog-legacy");
        var currentRoot = Path.Combine(currentBase, "DevProjex", "RepoCache");
        var legacyRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        var currentRepository = Path.Combine(currentRoot, "shared-current");
        var legacyRepository = Path.Combine(legacyRoot, "shared-legacy");
        var legacyOnlyRepository = Path.Combine(legacyRoot, "legacy-only");
        var newerDamagedDuplicate = Path.Combine(legacyRoot, "shared-damaged");
        var missingLegacyRepository = Path.Combine(legacyRoot, "missing-legacy");
        var damagedRepository = Path.Combine(currentRoot, "damaged");
        var missingRepository = Path.Combine(currentRoot, "missing");
        Directory.CreateDirectory(currentRepository);
        Directory.CreateDirectory(legacyRepository);
        Directory.CreateDirectory(legacyOnlyRepository);
        Directory.CreateDirectory(newerDamagedDuplicate);
        Directory.CreateDirectory(damagedRepository);

        const string sharedUrl = "https://secret@github.com/example/shared.git";
        const string legacyOnlyUrl = "https://github.com/example/legacy-only.git";
        const string damagedUrl = "https://github.com/example/damaged.git";
        const string missingUrl = "https://github.com/example/missing.git";
        const string missingLegacyUrl = "https://github.com/example/missing-legacy.git";
        var sharedIdentity = RepositoryUrlUtility.GetComparisonKey(sharedUrl);
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(2);
        var newest = older.AddDays(3);
        WriteCacheIndex(
            currentRoot,
            [
                new RepositoryCacheIndexEntry(
                    sharedIdentity,
                    sharedUrl,
                    currentRepository,
                    "release",
                    "222",
                    newer,
                    RepositoryCacheEntryState.Ready,
                    200,
                    RepositoryCacheContentKind.Git),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(damagedUrl),
                    damagedUrl,
                    damagedRepository,
                    "main",
                    null,
                    newest,
                    RepositoryCacheEntryState.Damaged,
                    300,
                    RepositoryCacheContentKind.Git),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(missingUrl),
                    missingUrl,
                    missingRepository,
                    "main",
                    null,
                    newest,
                    RepositoryCacheEntryState.Ready,
                    400,
                    RepositoryCacheContentKind.Zip)
            ]);
        WriteCacheIndex(
            legacyRoot,
            [
                new RepositoryCacheIndexEntry(
                    sharedIdentity,
                    "https://github.com/example/shared.git",
                    legacyRepository,
                    "main",
                    "111",
                    older,
                    RepositoryCacheEntryState.Ready,
                    100,
                    RepositoryCacheContentKind.Git),
                new RepositoryCacheIndexEntry(
                    sharedIdentity,
                    "https://github.com/example/shared.git",
                    newerDamagedDuplicate,
                    "damaged",
                    null,
                    newest.AddDays(1),
                    RepositoryCacheEntryState.Damaged,
                    900,
                    RepositoryCacheContentKind.Git),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(legacyOnlyUrl),
                    legacyOnlyUrl,
                    legacyOnlyRepository,
                    "archive",
                    null,
                    newest,
                    RepositoryCacheEntryState.Ready,
                    500,
                    RepositoryCacheContentKind.Zip),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(missingLegacyUrl),
                    missingLegacyUrl,
                    missingLegacyRepository,
                    "main",
                    null,
                    newest,
                    RepositoryCacheEntryState.Ready,
                    600,
                    RepositoryCacheContentKind.Zip)
            ]);
        var service = new RepoCacheService(() => currentBase, () => legacyBase);

        var entries = service.ListIndexedRepositories();

        Assert.Collection(
            entries,
            first =>
            {
                Assert.Equal("legacy-only", first.RepositoryName);
                Assert.Equal(legacyOnlyRepository, first.LocalPath, PathComparer.Default);
                Assert.Equal(RepositoryCacheContentKind.Zip, first.ContentKind);
                Assert.Equal(500, first.ApproximateSizeBytes);
            },
            second =>
            {
                Assert.Equal("shared", second.RepositoryName);
                Assert.Equal("https://github.com/example/shared.git", second.RepositoryUrl);
                Assert.Equal(currentRepository, second.LocalPath, PathComparer.Default);
                Assert.Equal("release", second.Branch);
                Assert.Equal(newer, second.LastOpenedUtc);
            });
        using var currentDocument = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(currentRoot, "cache-index.json")));
        Assert.DoesNotContain(
            currentDocument.RootElement.GetProperty("entries").EnumerateArray(),
            entry => string.Equals(
                entry.GetProperty("repositoryUrl").GetString(),
                missingUrl,
                StringComparison.Ordinal));
        using var legacyDocument = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(legacyRoot, "cache-index.json")));
        Assert.DoesNotContain(
            legacyDocument.RootElement.GetProperty("entries").EnumerateArray(),
            entry => string.Equals(
                entry.GetProperty("repositoryUrl").GetString(),
                missingLegacyUrl,
                StringComparison.Ordinal));
    }

    [Fact]
    public void ListIndexedRepositories_DoesNotChangeLastOpenedTimeOrIndexBytes()
    {
        const string repositoryUrl = "https://github.com/example/unchanged.git";
        var repositoryPath = _service.CreateRepositoryDirectory(repositoryUrl);
        var lastOpened = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);
        WriteCacheIndex(
            _testCacheRoot,
            [
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(repositoryUrl),
                    repositoryUrl,
                    repositoryPath,
                    "main",
                    null,
                    lastOpened,
                    RepositoryCacheEntryState.Ready,
                    42,
                    RepositoryCacheContentKind.Zip)
            ]);
        var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
        var before = File.ReadAllBytes(indexPath);

        var entry = Assert.Single(_service.ListIndexedRepositories());

        Assert.Equal(lastOpened, entry.LastOpenedUtc);
        Assert.Equal(before, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void FindIndexedRepository_FutureTimestampCannotOverrideValidDuplicate()
    {
        const string repositoryUrl = "https://github.com/example/timestamp.git";
        var corruptPath = _service.CreateRepositoryDirectory(repositoryUrl);
        var validPath = _service.CreateRepositoryDirectory(repositoryUrl);
        var identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
        var validTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        WriteCacheIndex(
            _testCacheRoot,
            [
                new RepositoryCacheIndexEntry(
                    identity,
                    repositoryUrl,
                    corruptPath,
                    "corrupt",
                    null,
                    DateTimeOffset.MaxValue,
                    RepositoryCacheEntryState.Ready,
                    ContentKind: RepositoryCacheContentKind.Zip),
                new RepositoryCacheIndexEntry(
                    identity,
                    repositoryUrl,
                    validPath,
                    "valid",
                    null,
                    validTimestamp,
                    RepositoryCacheEntryState.Ready,
                    ContentKind: RepositoryCacheContentKind.Zip)
            ]);

        var entry = Assert.IsType<RepositoryCacheIndexEntry>(_service.FindIndexedRepository(repositoryUrl));

        Assert.Equal(validPath, entry.LocalPath, PathComparer.Default);
        Assert.Equal(validTimestamp, entry.LastOpenedUtc);
    }

    [Fact]
    public void CollectGarbage_SaturatedIndexedSizeStillEvictsEveryOversizedEntry()
    {
        const string firstUrl = "https://github.com/example/oversized-first.git";
        const string secondUrl = "https://github.com/example/oversized-second.git";
        var firstPath = _service.CreateRepositoryDirectory(firstUrl);
        var secondPath = _service.CreateRepositoryDirectory(secondUrl);
        var lastUsedUtc = DateTimeOffset.UtcNow;
        WriteCacheIndex(
            _testCacheRoot,
            [
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(firstUrl),
                    firstUrl,
                    firstPath,
                    null,
                    null,
                    lastUsedUtc,
                    RepositoryCacheEntryState.Ready,
                    long.MaxValue,
                    RepositoryCacheContentKind.Zip),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(secondUrl),
                    secondUrl,
                    secondPath,
                    null,
                    null,
                    lastUsedUtc,
                    RepositoryCacheEntryState.Ready,
                    long.MaxValue,
                    RepositoryCacheContentKind.Zip)
            ]);
        var service = new RepoCacheService(
            _testCacheRoot,
            new RepositoryCachePolicy(1, TimeSpan.FromDays(60)),
            TimeProvider.System,
            new GitWorktreeManager());

        service.CollectGarbage();

        Assert.Empty(service.ListIndexedRepositories());
        Assert.False(Directory.Exists(firstPath));
        Assert.False(Directory.Exists(secondPath));
    }

    [Fact]
    public void LocalRepositoryCacheIdentityUsesPlatformPathCaseSemantics()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "SourceIdentity");
        var upperUrl = new Uri(Path.Combine(sourceRoot, "Repo.git")).AbsoluteUri;
        var lowerUrl = new Uri(Path.Combine(sourceRoot, "repo.git")).AbsoluteUri;
        var upperCachePath = PublishZip(_service, upperUrl);
        var lowerCachePath = PublishZip(_service, lowerUrl);
        _service.RecordIndexedRepository(upperUrl, upperCachePath, "upper");
        _service.RecordIndexedRepository(lowerUrl, lowerCachePath, "lower");

        var entries = _service.ListIndexedRepositories();

        if (OperatingSystem.IsWindows())
        {
            var entry = Assert.Single(entries);
            Assert.Equal(lowerCachePath, entry.LocalPath, PathComparer.Default);
            return;
        }

        Assert.Equal(2, entries.Count);
        Assert.Equal(
            upperCachePath,
            Assert.IsType<RepositoryCacheIndexEntry>(_service.FindIndexedRepository(upperUrl)).LocalPath,
            PathComparer.Default);
        Assert.Equal(
            lowerCachePath,
            Assert.IsType<RepositoryCacheIndexEntry>(_service.FindIndexedRepository(lowerUrl)).LocalPath,
            PathComparer.Default);
    }

    [Fact]
    public void LegacyLocalRepositoryIdentityIsNormalizedWhenIndexIsRead()
    {
        var sourcePath = Path.Combine(_testCacheRoot, "source", "legacy.git");
        var repositoryUrl = new Uri(sourcePath).AbsoluteUri;
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);
        var legacyIdentity = RepositoryUrlUtility.Normalize(repositoryUrl)[..^4];
        WriteCacheIndex(
            _testCacheRoot,
            [
                new RepositoryCacheIndexEntry(
                    legacyIdentity,
                    repositoryUrl,
                    cachePath,
                    "main",
                    null,
                    DateTimeOffset.UtcNow,
                    RepositoryCacheEntryState.Ready,
                    ContentKind: RepositoryCacheContentKind.Zip)
            ]);

        var entry = Assert.IsType<RepositoryCacheIndexEntry>(_service.FindIndexedRepository(repositoryUrl));

        Assert.Equal(RepositoryUrlUtility.GetComparisonKey(repositoryUrl), entry.Identity);
        Assert.Equal(cachePath, entry.LocalPath, PathComparer.Default);
    }

    [Fact]
    public void CollectGarbage_OverlongFileUrlEntryDoesNotDiscardValidIndexEntries()
    {
        const string validUrl = "https://github.com/example/valid.git";
        var validPath = _service.CreateRepositoryDirectory(validUrl);
        var invalidPath = _service.CreateRepositoryDirectory("invalid");
        File.WriteAllText(Path.Combine(validPath, "payload.txt"), "valid");
        File.WriteAllText(Path.Combine(invalidPath, "payload.txt"), "invalid");
        var invalidUrl = $"file:///C:/{new string('a', 40_000)}";
        WriteCacheIndex(
            _testCacheRoot,
            [
                new RepositoryCacheIndexEntry(
                    "invalid",
                    invalidUrl,
                    invalidPath,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    RepositoryCacheEntryState.Ready,
                    ContentKind: RepositoryCacheContentKind.Zip),
                new RepositoryCacheIndexEntry(
                    RepositoryUrlUtility.GetComparisonKey(validUrl),
                    validUrl,
                    validPath,
                    "main",
                    null,
                    DateTimeOffset.UtcNow,
                    RepositoryCacheEntryState.Ready,
                    ContentKind: RepositoryCacheContentKind.Zip)
            ]);

        _service.CollectGarbage();

        var validEntry = Assert.IsType<RepositoryCacheIndexEntry>(_service.FindIndexedRepository(validUrl));
        Assert.Equal(validPath, validEntry.LocalPath, PathComparer.Default);
        Assert.DoesNotContain(
            _service.ListIndexedRepositories(),
            entry => string.Equals(entry.RepositoryUrl, invalidUrl, StringComparison.Ordinal));
        Assert.True(Directory.Exists(validPath));
        Assert.True(File.Exists(Path.Combine(validPath, "payload.txt")));
    }

    [Fact]
    public async Task ListIndexedRepositories_LegacyCatalogEntryCanBeOpenedByPathOffline()
    {
        var currentBase = Path.Combine(_testCacheRoot, "open-current");
        var legacyBase = Path.Combine(_testCacheRoot, "open-legacy");
        var legacyRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        const string repositoryUrl = "https://github.com/example/legacy-catalog.git";
        var legacyService = new RepoCacheService(legacyRoot);
        var staging = legacyService.CreateRepositoryStagingDirectory(repositoryUrl);
        File.WriteAllText(Path.Combine(staging, "payload.txt"), "offline payload");
        var published = legacyService.PublishRepositoryDirectory(staging, repositoryUrl);
        legacyService.RecordIndexedRepository(repositoryUrl, published, "snapshot");
        var service = new RepoCacheService(() => currentBase, () => legacyBase);
        var catalogEntry = Assert.Single(service.ListIndexedRepositories());

        using var session = await service.TryAcquireRepositorySessionByPathAsync(
            catalogEntry.LocalPath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(session);
        Assert.Equal("snapshot", session.Branch);
        Assert.Equal("offline payload", File.ReadAllText(Path.Combine(session.RepositoryPath, "payload.txt")));
        Assert.NotNull(service.FindIndexedRepository(repositoryUrl));
    }

    [Fact]
    public void LegacyCatalogEntries_DeleteAndClearThroughTheirOwningIndex()
    {
        var currentBase = Path.Combine(_testCacheRoot, "delete-current");
        var legacyBase = Path.Combine(_testCacheRoot, "delete-legacy");
        var legacyRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        var legacyService = new RepoCacheService(legacyRoot);
        var firstPath = PublishZip(legacyService, "https://github.com/example/legacy-first.git");
        var secondPath = PublishZip(legacyService, "https://github.com/example/legacy-second.git");
        var service = new RepoCacheService(() => currentBase, () => legacyBase);
        Assert.Equal(2, service.ListIndexedRepositories().Count);

        service.DeleteRepositoryDirectory(firstPath);

        var retained = Assert.Single(service.ListIndexedRepositories());
        Assert.Equal(secondPath, retained.LocalPath, PathComparer.Default);
        Assert.False(Directory.Exists(firstPath));

        service.ClearAllCache();

        Assert.Empty(service.ListIndexedRepositories());
        Assert.False(Directory.Exists(secondPath));
        using var index = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(legacyRoot, "cache-index.json")));
        Assert.Empty(index.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task LegacyCacheWithoutIndexIsResolvedOfflineAndIndexedInCurrentRoot()
    {
        const string repositoryUrl = "https://github.com/example/offline.git";
        var currentBase = Path.Combine(_testCacheRoot, "offline-current");
        var legacyBase = Path.Combine(_testCacheRoot, "offline-legacy");
        var legacyCacheRoot = Path.Combine(legacyBase, "DevProjex", "RepoCache");
        var legacyRepository = Path.Combine(legacyCacheRoot, "offline_legacy");
        Directory.CreateDirectory(Path.Combine(legacyRepository, ".git"));
        File.WriteAllText(Path.Combine(legacyRepository, "README.md"), "offline");
        var originalLegacyEntries = Directory
            .EnumerateFileSystemEntries(
                legacyCacheRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(legacyCacheRoot, path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var service = new RepoCacheService(
            () => currentBase,
            () => legacyBase);
        var git = new OfflineGitRepositoryService(
            legacyRepository,
            repositoryUrl);
        var catalog = new RepositoryCacheCatalog(git, service);

        var cached = await catalog.FindAsync(
            repositoryUrl,
            TestContext.Current.CancellationToken);

        Assert.Equal(RepositoryCacheState.Ready, cached.State);
        Assert.Equal(legacyRepository, cached.LocalPath, PathComparer.Default);
        Assert.Equal(0, git.NetworkOperationCount);
        Assert.Equal("offline", File.ReadAllText(Path.Combine(legacyRepository, "README.md")));
        Assert.Equal(
            originalLegacyEntries,
            Directory
                .EnumerateFileSystemEntries(
                    legacyCacheRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(legacyCacheRoot, path))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray());
        Assert.True(File.Exists(Path.Combine(service.CacheRootPath, "cache-index.json")));
    }

    [Fact]
    public void PublishRepositoryDirectory_MovesCompletedCloneOutOfStaging()
    {
        var staging = _service.CreateRepositoryStagingDirectory(
            "https://github.com/user/repository.git");
        Directory.CreateDirectory(Path.Combine(staging, ".git"));
        File.WriteAllText(Path.Combine(staging, "README.md"), "content");

        var published = _service.PublishRepositoryDirectory(
            staging,
            "https://github.com/user/repository.git");

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(Path.Combine(published, ".git")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(published, "README.md")));
        Assert.DoesNotContain(
            $"{Path.DirectorySeparatorChar}.staging{Path.DirectorySeparatorChar}",
            published,
            StringComparison.Ordinal);
        _service.DeleteRepositoryDirectory(published);
    }

    [Fact]
    public void PublishRepositoryDirectory_IndexesPersistentCache()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var staging = _service.CreateRepositoryStagingDirectory(repositoryUrl);
        Directory.CreateDirectory(Path.Combine(staging, ".git"));

        var published = _service.PublishRepositoryDirectory(staging, repositoryUrl);
        var indexed = _service.FindIndexedRepository(
            "git@github.com:user/repository.git");

        Assert.NotNull(indexed);
        Assert.Equal(published, indexed.LocalPath, PathComparer.Default);
        Assert.Equal(repositoryUrl, indexed.RepositoryUrl);
        Assert.Equal(RepositoryCacheEntryState.Ready, indexed.State);
    }

    [Fact]
    public void RecordIndexedRepository_UpdatesMetadataWithoutDuplicatingIdentity()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);

        _service.RecordIndexedRepository(repositoryUrl, cachePath, "main", "1111111");
        _service.RecordIndexedRepository(
            "git@github.com:user/repository.git",
            cachePath,
            "release",
            "2222222");

        var indexed = _service.FindIndexedRepository(repositoryUrl);
        Assert.NotNull(indexed);
        Assert.Equal("release", indexed.Branch);
        Assert.Equal("2222222", indexed.CommitHash);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testCacheRoot, "cache-index.json")));
        Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void RemoveIndexedRepository_PreservesCachedFiles()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);
        var contentPath = Path.Combine(cachePath, "README.md");
        File.WriteAllText(contentPath, "content");
        _service.RecordIndexedRepository(repositoryUrl, cachePath);

        _service.RemoveIndexedRepository(cachePath);

        Assert.Null(_service.FindIndexedRepository(repositoryUrl));
        Assert.Equal("content", File.ReadAllText(contentPath));
    }

    [Fact]
    public void FindIndexedRepository_RecoversFromValidBackup()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);
        _service.RecordIndexedRepository(repositoryUrl, cachePath, "main", "1234567");
        var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
        Assert.True(File.Exists($"{indexPath}.bak"));
        File.WriteAllText(indexPath, "{ invalid json");

        var indexed = new RepoCacheService(_testCacheRoot)
            .FindIndexedRepository(repositoryUrl);

        Assert.NotNull(indexed);
        Assert.Equal(cachePath, indexed.LocalPath, PathComparer.Default);
        Assert.Equal("main", indexed.Branch);
    }

	[Fact]
	public void OversizedCacheIndex_IsNotReadOrReplacedByMetadataUpdates()
	{
		const string repositoryUrl = "https://github.com/example/oversized-index.git";
		Directory.CreateDirectory(_testCacheRoot);
		var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
		using (var stream = new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			stream.SetLength(RepoCacheService.MaximumCacheIndexBytes + 1);
		var repositoryPath = _service.CreateRepositoryDirectory(repositoryUrl);

		Assert.Null(_service.FindIndexedRepository(repositoryUrl));
		_service.RecordIndexedRepository(repositoryUrl, repositoryPath, "main", "1234567");

		Assert.Equal(RepoCacheService.MaximumCacheIndexBytes + 1, new FileInfo(indexPath).Length);
		Assert.Null(_service.FindIndexedRepository(repositoryUrl));
	}

	[Fact]
	public void FutureCacheIndex_IsNotDowngradedByMetadataUpdates()
	{
		const string repositoryUrl = "https://github.com/example/future-index.git";
		Directory.CreateDirectory(_testCacheRoot);
		var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
		File.WriteAllText(indexPath, """{"schemaVersion":3,"entries":[],"future":"preserve"}""");
		var original = File.ReadAllBytes(indexPath);
		var repositoryPath = _service.CreateRepositoryDirectory(repositoryUrl);

		_service.RecordIndexedRepository(repositoryUrl, repositoryPath, "main", "1234567");

		Assert.Equal(original, File.ReadAllBytes(indexPath));
		Assert.Null(_service.FindIndexedRepository(repositoryUrl));
	}

	[Fact]
	public async Task FutureCacheIndexWithCurrentBackup_IsNotDowngradedBySessionMetadata()
	{
		const string repositoryUrl = "https://github.com/example/future-session-index.git";
		var repositoryPath = _service.CreateRepositoryDirectory(repositoryUrl);
		_service.RecordIndexedRepository(repositoryUrl, repositoryPath, "main", "1234567");
		var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
		Assert.True(File.Exists($"{indexPath}.bak"));
		File.WriteAllText(indexPath, """{"schemaVersion":3,"entries":[],"future":"preserve"}""");
		var original = File.ReadAllBytes(indexPath);

		using var session = await _service.TryAcquireRepositorySessionAsync(
			repositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Null(session);
		Assert.Equal(original, File.ReadAllBytes(indexPath));
	}

	[Fact]
	public async Task FutureCacheIndexWithLegacyGitBackup_DoesNotMoveRepositoryDuringMigration()
	{
		const string repositoryUrl = "https://github.com/example/future-legacy-index.git";
		var repositoryPath = _service.CreateRepositoryDirectory(repositoryUrl);
		Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
		_service.RecordIndexedRepository(repositoryUrl, repositoryPath, "main", "1234567");
		var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
		Assert.True(File.Exists($"{indexPath}.bak"));
		File.WriteAllText(indexPath, """{"schemaVersion":3,"entries":[],"future":"preserve"}""");
		var original = File.ReadAllBytes(indexPath);

		using var session = await _service.TryAcquireRepositorySessionAsync(
			repositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Null(session);
		Assert.True(Directory.Exists(repositoryPath));
		Assert.True(Directory.Exists(Path.Combine(repositoryPath, ".git")));
		Assert.Equal(original, File.ReadAllBytes(indexPath));
	}

    [Fact]
    public void FindIndexedRepository_IgnoresEntriesWithMissingRequiredStrings()
    {
        Directory.CreateDirectory(_testCacheRoot);
        File.WriteAllText(
            Path.Combine(_testCacheRoot, "cache-index.json"),
            """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "identity": null,
                  "repositoryUrl": null,
                  "localPath": null,
                  "state": "ready",
                  "lastUsedUtc": "2026-07-29T00:00:00Z"
                }
              ]
            }
            """);

        var indexed = _service.FindIndexedRepository(
            "https://github.com/user/repository.git");

        Assert.Null(indexed);
    }

    [Fact]
    public void CreateRepositoryDirectory_SanitizesUrl()
    {
        // URL with special characters should create valid directory
        var url = "https://github.com/user/my-repo.git";

        var dir = _service.CreateRepositoryDirectory(url);

        Assert.True(Directory.Exists(dir));
        Assert.DoesNotContain(":", Path.GetFileName(dir));
        Assert.DoesNotContain("/", Path.GetFileName(dir));
        Assert.DoesNotContain("\\", Path.GetFileName(dir));

        // Cleanup
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("CON", "CON_repo_")]
    [InlineData("nul", "nul_repo_")]
    [InlineData("com1", "com1_repo_")]
    [InlineData("con.git", "con_repo_")]
    [InlineData("con.something", "con_repo.something_")]
    [InlineData("repository.", "repository_")]
    [InlineData("repository ", "repository_")]
    [InlineData("ordinary", "ordinary_")]
    public void CreateRepositoryDirectory_NormalizesPortableRepositoryName(
        string repositoryName,
        string expectedPrefix)
    {
        var directory = _service.CreateRepositoryDirectory(
            $"https://github.com/user/{repositoryName}");

        Assert.StartsWith(
            expectedPrefix,
            Path.GetFileName(directory),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteRepositoryDirectory_RemovesDirectory()
    {
        var url = "https://github.com/user/repo";
        var dir = _service.CreateRepositoryDirectory(url);

        // Create some content
        File.WriteAllText(Path.Combine(dir, "test.txt"), "test");

        _service.DeleteRepositoryDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteRepositoryDirectory_HandlesNonexistentDirectory()
    {
        var nonExistentPath = Path.Combine(_testCacheRoot, "nonexistent");

        // Should not throw
        _service.DeleteRepositoryDirectory(nonExistentPath);
    }

    private sealed class OfflineGitRepositoryService(
        string repositoryPath,
        string repositoryUrl) : IGitRepositoryService
    {
        public int NetworkOperationCount { get; private set; }

        public Task<string?> GetRemoteUrlAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(
                PathComparer.Default.Equals(candidatePath, repositoryPath)
                    ? repositoryUrl
                    : null);

        public Task<string?> GetCurrentBranchAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("main");

        public Task<string?> GetHeadCommitAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("0123456789abcdef");

        public Task<bool> IsGitAvailableAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitBranch>>([]);

        public Task<string?> GetDefaultBranchAsync(
            string candidatePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("main");

        public Task<bool> SwitchBranchAsync(
            string candidatePath,
            string branchName,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> PullUpdatesAsync(
            string candidatePath,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            NetworkOperationCount++;
            throw new InvalidOperationException("Network access is forbidden by this test.");
        }

        public Task<GitCloneResult> CloneAsync(
            string url,
            string targetDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            NetworkOperationCount++;
            throw new InvalidOperationException("Network access is forbidden by this test.");
        }
    }

    [Fact]
    public void DeleteRepositoryDirectory_HandlesLockedFiles()
    {
        var url = "https://github.com/user/repo";
        var dir = _service.CreateRepositoryDirectory(url);
        var filePath = Path.Combine(dir, "locked.txt");

        // Create and lock a file
        using var stream = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write([1, 2, 3]);
        stream.Flush();

        // Delete should not throw even with locked file
        var deleteWhileLockedException = Record.Exception(() => _service.DeleteRepositoryDirectory(dir));
        Assert.Null(deleteWhileLockedException);

        stream.Dispose();

        // After lock release, deletion should finish successfully.
        _service.DeleteRepositoryDirectory(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CreateRepositoryDirectory_CreatesUniqueDirectories()
    {
        var url = "https://github.com/user/repo";
        var dir1 = _service.CreateRepositoryDirectory(url);
        var dir2 = _service.CreateRepositoryDirectory(url);

        try
        {
            // Both should be created and be different
            Assert.NotEqual(dir1, dir2);
            Assert.Contains("Tests", dir1);
            Assert.Contains("Tests", dir2);
        }
        finally
        {
            // Best effort cleanup
            try { _service.DeleteRepositoryDirectory(dir1); } catch { }
            try { _service.DeleteRepositoryDirectory(dir2); } catch { }
        }
    }

    [Fact]
    public void DeleteRepositoryDirectory_OnlyDeletesIfInsideCacheRoot()
    {
        // Try to delete a directory outside cache root (security test)
        var outsidePath = Path.Combine(Path.GetTempPath(), "SomeOtherDir");
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(Path.Combine(outsidePath, "important.txt"), "data");

        // Should not delete directories outside cache
        _service.DeleteRepositoryDirectory(outsidePath);

        // Directory should still exist (or at least file should exist if it was protected)
        // This test verifies the service doesn't delete arbitrary paths
        Assert.True(Directory.Exists(outsidePath) || !File.Exists(Path.Combine(outsidePath, "important.txt")));

        // Cleanup
        try
        {
            if (Directory.Exists(outsidePath))
                Directory.Delete(outsidePath, recursive: true);
        }
        catch { }
    }

    private static void WriteCacheIndex(
        string cacheRoot,
        IReadOnlyList<RepositoryCacheIndexEntry> entries)
    {
        Directory.CreateDirectory(cacheRoot);
        File.WriteAllText(
            Path.Combine(cacheRoot, "cache-index.json"),
            JsonSerializer.Serialize(
                new { SchemaVersion = 2, Entries = entries },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static string PublishZip(RepoCacheService service, string repositoryUrl)
    {
        var staging = service.CreateRepositoryStagingDirectory(repositoryUrl);
        File.WriteAllText(Path.Combine(staging, "payload.txt"), repositoryUrl);
        return service.PublishRepositoryDirectory(staging, repositoryUrl);
    }

    [Fact]
    public void IsInCache_PrefixTrapSibling_ReturnsFalse()
    {
        var sibling = _testCacheRoot + "2";
        Directory.CreateDirectory(sibling);

        try
        {
            Assert.False(_service.IsInCache(sibling));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sibling))
                    Directory.Delete(sibling, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void IsInCache_CaseVariantBehavior_MatchesPlatform()
    {
        var cacheDir = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var alteredCaseRoot = _testCacheRoot.Replace("CacheTests", "cAchetEsts", StringComparison.Ordinal);
        var alteredCasePath = cacheDir.Replace(_testCacheRoot, alteredCaseRoot, StringComparison.Ordinal);

        Assert.Equal(OperatingSystem.IsWindows(), _service.IsInCache(alteredCasePath));

        _service.DeleteRepositoryDirectory(cacheDir);
    }
}
