namespace DevProjex.Tests.Integration;

/// <summary>
/// Serializes Git integration tests that share local bare repositories and cache roots.
/// Git for Windows can keep pack/index files locked briefly, so running these fixtures
/// as unrelated xUnit collections makes the suite flaky on CI even when production code is fine.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GitNetworkTestCollection
{
    public const string Name = "GitNetworkTests";
}
