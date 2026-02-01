using System;
using System.Linq;
using Xunit;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for Git URL validation logic.
/// These tests verify that URL validation correctly identifies valid and invalid Git repository URLs.
///
/// Test categories:
/// - Valid Git URLs (GitHub, GitLab, Bitbucket, etc.)
/// - Invalid URLs (non-Git services, malformed URLs)
/// - Edge cases (with/without .git extension, custom domains)
/// </summary>
public class GitUrlValidationTests
{
    /// <summary>
    /// Simplified version of MainWindow's IsValidGitRepositoryUrl for testing.
    /// This validates that URL looks like a valid Git repository URL.
    /// </summary>
    private static bool IsValidGitRepositoryUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            // Try to parse as URI
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            // Must be HTTP or HTTPS
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return false;

            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            // Check for common Git hosting services
            var validHosts = new[]
            {
                "github.com",
                "gitlab.com",
                "bitbucket.org",
                "gitea.com",
                "codeberg.org",
                "sourceforge.net",
                "git.sr.ht"
            };

            // Allow subdomains (e.g., gitlab.mycompany.com)
            var isKnownHost = validHosts.Any(h => host == h || host.EndsWith("." + h));

            // Or URL ends with .git extension
            var hasGitExtension = path.EndsWith(".git");

            // Or contains /git/ in path (common for self-hosted instances)
            var hasGitInPath = path.Contains("/git/");

            return isKnownHost || hasGitExtension || hasGitInPath;
        }
        catch
        {
            return false;
        }
    }

    #region Valid URLs

    [Theory]
    [InlineData("https://github.com/user/repo")]
    [InlineData("https://github.com/user/repo.git")]
    [InlineData("http://github.com/user/repo")]
    [InlineData("https://www.github.com/user/repo")]
    public void IsValidGitRepositoryUrl_ReturnsTrue_ForGitHubUrls(string url)
    {
        // GitHub URLs should be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.True(result, $"URL should be valid: {url}");
    }

    [Theory]
    [InlineData("https://gitlab.com/user/repo")]
    [InlineData("https://gitlab.com/user/repo.git")]
    [InlineData("https://gitlab.mycompany.com/user/repo.git")]
    public void IsValidGitRepositoryUrl_ReturnsTrue_ForGitLabUrls(string url)
    {
        // GitLab URLs should be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.True(result, $"URL should be valid: {url}");
    }

    [Theory]
    [InlineData("https://bitbucket.org/user/repo")]
    [InlineData("https://bitbucket.org/user/repo.git")]
    public void IsValidGitRepositoryUrl_ReturnsTrue_ForBitbucketUrls(string url)
    {
        // Bitbucket URLs should be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.True(result, $"URL should be valid: {url}");
    }

    [Theory]
    [InlineData("https://example.com/project.git")]
    [InlineData("https://my-server.com/repos/myrepo.git")]
    public void IsValidGitRepositoryUrl_ReturnsTrue_ForUrlsWithGitExtension(string url)
    {
        // URLs ending with .git should be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.True(result, $"URL should be valid: {url}");
    }

    [Theory]
    [InlineData("https://example.com/git/repo")]
    [InlineData("https://server.com/projects/git/myproject")]
    public void IsValidGitRepositoryUrl_ReturnsTrue_ForUrlsWithGitInPath(string url)
    {
        // URLs containing /git/ in path should be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.True(result, $"URL should be valid: {url}");
    }

    #endregion

    #region Invalid URLs

    [Theory]
    [InlineData("https://t.me/addstickers/SomeSticker")]
    [InlineData("https://telegram.org/something")]
    public void IsValidGitRepositoryUrl_ReturnsFalse_ForTelegramUrls(string url)
    {
        // Telegram URLs should NOT be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.False(result, $"URL should NOT be valid: {url}");
    }

    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("https://google.com")]
    [InlineData("https://stackoverflow.com/questions/123")]
    public void IsValidGitRepositoryUrl_ReturnsFalse_ForNonGitUrls(string url)
    {
        // Regular web URLs should NOT be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.False(result, $"URL should NOT be valid: {url}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidGitRepositoryUrl_ReturnsFalse_ForEmptyOrNullUrls(string? url)
    {
        // Empty/null URLs should NOT be valid
        var result = IsValidGitRepositoryUrl(url!);
        Assert.False(result, $"URL should NOT be valid: {url}");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/repo")]
    [InlineData("file:///C:/repos/myrepo")]
    public void IsValidGitRepositoryUrl_ReturnsFalse_ForMalformedUrls(string url)
    {
        // Malformed or non-HTTP URLs should NOT be valid
        var result = IsValidGitRepositoryUrl(url);
        Assert.False(result, $"URL should NOT be valid: {url}");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void IsValidGitRepositoryUrl_ReturnsFalse_ForUrlWithoutScheme()
    {
        // URL without http:// or https:// should NOT be valid
        var result = IsValidGitRepositoryUrl("github.com/user/repo");
        Assert.False(result);
    }

    [Fact]
    public void IsValidGitRepositoryUrl_HandlesUrlsWithQueryParameters()
    {
        // URLs with query parameters should be validated by host/path, not query
        var result = IsValidGitRepositoryUrl("https://github.com/user/repo?branch=main");
        Assert.True(result);
    }

    [Fact]
    public void IsValidGitRepositoryUrl_HandlesUrlsWithFragments()
    {
        // URLs with fragments should be validated by host/path, not fragment
        var result = IsValidGitRepositoryUrl("https://github.com/user/repo#readme");
        Assert.True(result);
    }

    #endregion
}
