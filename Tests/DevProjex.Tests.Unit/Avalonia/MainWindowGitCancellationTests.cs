using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Avalonia;
using Xunit;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowGitCancellationTests
{
    [Fact]
    public async Task CheckInternetConnectionAsync_PreCanceledToken_ThrowsOperationCanceled()
    {
        var method = typeof(MainWindow).GetMethod(
            "CheckInternetConnectionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = (Task<bool>)method!.Invoke(null, [cts.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }
}
