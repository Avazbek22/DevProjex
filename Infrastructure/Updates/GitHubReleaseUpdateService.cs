using DevProjex.Application.Updates;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Updates;

public sealed class GitHubReleaseUpdateService : IApplicationUpdateService, IDisposable
{
    private static readonly Uri LatestReleaseEndpoint = new(
        "https://api.github.com/repos/Avazbek22/DevProjex/releases/latest");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService()
        : this(new HttpClient())
    {
    }

    internal GitHubReleaseUpdateService(HttpMessageHandler handler)
        : this(new HttpClient(handler, disposeHandler: true))
    {
    }

    private GitHubReleaseUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ApplicationUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!ApplicationReleaseVersion.TryParse(currentVersion, out var current))
            return Failed(currentVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("DevProjex-UpdateCheck");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Failed(current.ToString());

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            var release = await JsonSerializer.DeserializeAsync(
                content,
                InfrastructureJsonSerializerContext.Default.GitHubLatestReleaseResponse,
                timeout.Token);
            if (!ApplicationReleaseVersion.TryParse(release?.TagName, out var latest))
                return Failed(current.ToString());

            var comparison = current.CompareTo(latest);
            var availability = comparison switch
            {
                < 0 => ApplicationUpdateAvailability.UpdateAvailable,
                > 0 => ApplicationUpdateAvailability.CurrentVersionNewer,
                _ => ApplicationUpdateAvailability.UpToDate
            };
            return new ApplicationUpdateCheckResult(
                availability,
                current.ToString(),
                latest.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                                           HttpRequestException or
                                           OperationCanceledException or
                                           IOException or
                                           JsonException)
        {
            return Failed(current.ToString());
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static ApplicationUpdateCheckResult Failed(string currentVersion)
        => new(ApplicationUpdateAvailability.CheckFailed, currentVersion);
}

internal sealed record GitHubLatestReleaseResponse(
    [property: JsonPropertyName("tag_name")] string? TagName);
