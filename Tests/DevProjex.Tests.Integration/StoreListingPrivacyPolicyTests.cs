using System.Text.RegularExpressions;
using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Integration;

public sealed class StoreListingPrivacyPolicyTests
{
    private static readonly Lazy<string> RepoRoot = new(StoreListingPaths.FindRepositoryRoot);

    [Fact]
    public void PrivacyPolicyUrl_PointsToTheDefaultBranchCopyOfTheRepositoryPolicy()
    {
        // The Store listing once linked to blob/main, a branch that does not exist in this
        // repository, so the published privacy link returned 404 on every locale.
        var repositoryRoot = RepoRoot.Value;
        var listingCsvPath = Path.Combine(
            StoreListingPaths.GetStoreListingRoot(repositoryRoot),
            "listing.csv");
        var document = StoreListingCsvDocument.Load(listingCsvPath);
        var row = document.RowsByField["PrivacyPolicyUrl"];
        var localeColumns = StoreListingPaths.GetLocaleColumns(document.Headers);
        const string expectedUrl =
            "https://github.com/Avazbek22/DevProjex/blob/master/Packaging/Windows/StoreListing/privacy-policy.md";

        foreach (var locale in localeColumns)
        {
            var value = row.GetValue(locale);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            Assert.Equal(expectedUrl, value);
        }

        Assert.True(
            File.Exists(Path.Combine(
                StoreListingPaths.GetStoreListingRoot(repositoryRoot),
                "privacy-policy.md")),
            "The privacy policy file referenced by the Store listing is missing.");
    }

    [Fact]
    public void PrivacyPolicy_RendersAsStructuredMarkdownAndCoversTheMcpBoundary()
    {
        // The policy is served to Store visitors through the GitHub markdown renderer.
        // It once had every line prefixed with "# ", which rendered as a wall of h1 text.
        var policyPath = Path.Combine(
            StoreListingPaths.GetStoreListingRoot(RepoRoot.Value),
            "privacy-policy.md");
        var lines = File.ReadAllLines(policyPath);

        Assert.Single(lines, static line => line.StartsWith("# ", StringComparison.Ordinal));
        Assert.True(
            lines.Count(static line => line.StartsWith("## ", StringComparison.Ordinal)) >= 5,
            "The privacy policy lost its section structure.");

        var policy = string.Join('\n', lines);
        Assert.Contains("Model Context Protocol (MCP) server", policy, StringComparison.Ordinal);
        Assert.Contains("read-only tools", policy, StringComparison.Ordinal);
        Assert.Contains("masks detected secrets", policy, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Last updated: \d{4}-\d{2}-\d{2}"), policy);
    }
}
