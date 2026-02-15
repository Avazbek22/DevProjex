using DevProjex.Avalonia.Services;
using Xunit;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ProjectLoadCancellationFallbackResolverTests
{
    [Theory]
    [InlineData(true, ProjectLoadCancellationFallback.RestorePreviousProject)]
    [InlineData(false, ProjectLoadCancellationFallback.ResetToInitialState)]
    public void Resolve_ReturnsExpectedFallback(bool hadLoadedProjectBefore, ProjectLoadCancellationFallback expected)
    {
        var actual = ProjectLoadCancellationFallbackResolver.Resolve(hadLoadedProjectBefore);

        Assert.Equal(expected, actual);
    }
}
