using DevProjex.Application.UseCases;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class ScanOptionsUseCaseRobustnessTests
{
    private static IgnoreRules CreateRules() => new(
        IgnoreHiddenFolders: false,
        IgnoreHiddenFiles: false,
        IgnoreDotFolders: false,
        IgnoreDotFiles: false,
        SmartIgnoredFolders: new HashSet<string>(),
        SmartIgnoredFiles: new HashSet<string>());

    [Fact]
    public void Execute_Throws_WhenExtensionsScannerFails()
    {
        var scanner = new StubFileSystemScanner
        {
            GetExtensionsHandler = (_, _) => throw new InvalidOperationException("extensions failed"),
            GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(new List<string> { "src" }, false, false)
        };

        var useCase = new ScanOptionsUseCase(scanner);

        var ex = Assert.ThrowsAny<Exception>(() =>
            useCase.Execute(new ScanOptionsRequest("/root", CreateRules())));
        AssertContainsInnerError(ex, "extensions failed");
    }

    [Fact]
    public void Execute_Throws_WhenRootFoldersScannerFails()
    {
        var scanner = new StubFileSystemScanner
        {
            GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(new HashSet<string> { ".cs" }, false, false),
            GetRootFolderNamesHandler = (_, _) => throw new InvalidOperationException("roots failed")
        };

        var useCase = new ScanOptionsUseCase(scanner);

        var ex = Assert.ThrowsAny<Exception>(() =>
            useCase.Execute(new ScanOptionsRequest("/root", CreateRules())));
        AssertContainsInnerError(ex, "roots failed");
    }

    [Fact]
    public void Execute_RespectsCanceledToken_BeforeWork()
    {
        var scanner = new StubFileSystemScanner();
        var useCase = new ScanOptionsUseCase(scanner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            useCase.Execute(new ScanOptionsRequest("/root", CreateRules()), cts.Token));
    }

    [Fact]
    public void GetExtensionsForRootFolders_RespectsCanceledToken_BeforeWork()
    {
        var scanner = new StubFileSystemScanner();
        var useCase = new ScanOptionsUseCase(scanner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            useCase.GetExtensionsForRootFolders("/root", new[] { "src" }, CreateRules(), cts.Token));
    }

    [Fact]
    public void GetExtensionsForRootFolders_ReturnsOnlyUniqueValues_FromManyFolders()
    {
        var scanner = new StubFileSystemScanner
        {
            GetRootFileExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".CS", ".json" }, false, false),
            GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".JSON", ".md" }, false, false)
        };

        var useCase = new ScanOptionsUseCase(scanner);

        var result = useCase.GetExtensionsForRootFolders(
            "/root",
            new[] { "src", "tests", "docs", "tools" },
            CreateRules());

        Assert.Equal(3, result.Value.Count);
        Assert.Contains(".cs", result.Value);
        Assert.Contains(".json", result.Value);
        Assert.Contains(".md", result.Value);
    }

    [Fact]
    public void Execute_SortsCaseInsensitive_ForMixedCaseInput()
    {
        var scanner = new StubFileSystemScanner
        {
            GetExtensionsHandler = (_, _) => new ScanResult<HashSet<string>>(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".z", ".A", ".m", ".B" }, false, false),
            GetRootFolderNamesHandler = (_, _) => new ScanResult<List<string>>(
                new List<string> { "zeta", "Alpha", "beta", "Gamma" }, false, false)
        };

        var useCase = new ScanOptionsUseCase(scanner);
        var result = useCase.Execute(new ScanOptionsRequest("/root", CreateRules()));

        Assert.Equal(new[] { ".A", ".B", ".m", ".z" }, result.Extensions);
        Assert.Equal(new[] { "Alpha", "beta", "Gamma", "zeta" }, result.RootFolders);
    }

    private static void AssertContainsInnerError(Exception ex, string expectedMessagePart)
    {
        if (ex is AggregateException aggregate)
        {
            Assert.Contains(aggregate.InnerExceptions, inner => inner.Message.Contains(expectedMessagePart, StringComparison.OrdinalIgnoreCase));
            return;
        }

        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
