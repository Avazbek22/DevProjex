using System.Net;
using DevProjex.Application.Updates;
using DevProjex.Infrastructure.Updates;

namespace DevProjex.Tests.Unit;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Theory]
    [InlineData("4.9.0", "v4.9.1", ApplicationUpdateAvailability.UpdateAvailable)]
    [InlineData("4.9", "v4.9.0", ApplicationUpdateAvailability.UpToDate)]
    [InlineData("4.10.0+local", "v4.9.9", ApplicationUpdateAvailability.CurrentVersionNewer)]
    public async Task CheckAsync_MapsStableGitHubReleaseUsingNumericVersionOrder(
        string currentVersion,
        string releaseTag,
        ApplicationUpdateAvailability expectedAvailability)
    {
        using var handler = new StubHttpMessageHandler(_ => JsonResponse($$"""
            { "tag_name": "{{releaseTag}}" }
            """));
        using var service = new GitHubReleaseUpdateService(handler);

        var result = await service.CheckAsync(
            currentVersion,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailability, result.Availability);
        Assert.Equal(releaseTag.TrimStart('v', 'V'), result.LatestVersion);
        Assert.Equal(HttpMethod.Get, handler.Request?.Method);
        Assert.Equal(
            "https://api.github.com/repos/Avazbek22/DevProjex/releases/latest",
            handler.Request?.RequestUri?.AbsoluteUri);
        Assert.Contains(
            handler.Request!.Headers.UserAgent,
            product => product.Product?.Name == "DevProjex-UpdateCheck");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{ \"tag_name\": \"v5.0.0\" }")]
    [InlineData(HttpStatusCode.Forbidden, "{ \"tag_name\": \"v5.0.0\" }")]
    [InlineData(HttpStatusCode.OK, "{ \"tag_name\": \"v5.0.0-preview.1\" }")]
    [InlineData(HttpStatusCode.OK, "{ invalid json")]
    public async Task CheckAsync_UnavailableOrInvalidRelease_ReturnsTypedFailure(
        HttpStatusCode statusCode,
        string content)
    {
        using var handler = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        using var service = new GitHubReleaseUpdateService(handler);

        var result = await service.CheckAsync(
            "4.9.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationUpdateAvailability.CheckFailed, result.Availability);
        Assert.Equal("4.9.0", result.CurrentVersion);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_CallerCancellation_RemainsCancellation()
    {
        using var handler = new StubHttpMessageHandler(_ => throw new OperationCanceledException());
        using var service = new GitHubReleaseUpdateService(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAsync("4.9.0", cancellation.Token));
    }

    [Fact]
    public async Task CheckAsync_InvalidCurrentVersion_FailsWithoutNetworkRequest()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new InvalidOperationException("Request must not be sent."));
        using var service = new GitHubReleaseUpdateService(handler);

        var result = await service.CheckAsync(
            "unknown",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationUpdateAvailability.CheckFailed, result.Availability);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task CheckAsync_NetworkFailure_ReturnsTypedFailureWithoutLeakingException()
    {
        using var handler = new StubHttpMessageHandler(
            _ => throw new HttpRequestException("Synthetic network failure."));
        using var service = new GitHubReleaseUpdateService(handler);

        var result = await service.CheckAsync(
            "5.0",
            TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationUpdateAvailability.CheckFailed, result.Availability);
        Assert.Equal("5.0", result.CurrentVersion);
        Assert.Null(result.LatestVersion);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
