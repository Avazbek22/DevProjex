using DevProjex.Tests.Shared.StoreListing;

namespace DevProjex.Tests.Integration;

public sealed class StoreListingValidationScriptIntegrationTests
{
    private static readonly Lazy<string> RepoRoot = new(StoreListingPaths.FindRepositoryRoot);

    [Fact]
    public void ValidateStoreListingScript_Passes_ForCurrentRepositoryState()
    {
        // The PowerShell validator is part of the release gate, not just a helper.
        // Testing it directly ensures the script itself stays executable and correct.
        var result = RunPowerShellScript(
            Path.Combine(RepoRoot.Value, "Scripts", "validate-store-listing.ps1"),
            ["-RepositoryRoot", RepoRoot.Value]);

        Assert.True(
            result.ExitCode == 0,
            $"validate-store-listing.ps1 failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
    }

    [Fact]
    public void ReleaseAll_ValidateConfigOnly_Passes_WithStoreListingValidationEnabled()
    {
        var result = RunPowerShellScript(
            Path.Combine(RepoRoot.Value, "Scripts", "release-all.ps1"),
            ["-ValidateConfigOnly"]);

        Assert.True(
            result.ExitCode == 0,
            $"release-all.ps1 -ValidateConfigOnly failed.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
    }

    [Fact]
    public void ValidateStoreListingScript_Fails_WhenKeywordBudgetIsExceeded()
    {
        using var fixture = CreateStoreListingFixture();

        // Intentionally exceed the total word budget without crossing the per-term 40-char limit.
        // This keeps the failure focused on the late-import regression we actually saw in production.
        MutateImportCsv(
            fixture.ImportCsvPath,
            row => row.Field is "SearchTerm1" or "SearchTerm2" or "SearchTerm3",
            values =>
            {
                switch (values["Field"])
                {
                    case "SearchTerm1":
                        values["en-us"] = "context for ai chat export";
                        break;
                    case "SearchTerm2":
                        values["en-us"] = "project tree for code review";
                        break;
                    case "SearchTerm3":
                        values["en-us"] = "copy code to ai chat";
                        break;
                }
            });

        var result = RunPowerShellScript(
            Path.Combine(fixture.RepositoryRoot, "Scripts", "validate-store-listing.ps1"),
            ["-RepositoryRoot", fixture.RepositoryRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SLP011", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStoreListingScript_Fails_WhenScreenshotCoverageDriftsBetweenLocales()
    {
        using var fixture = CreateStoreListingFixture();

        // One locale silently losing a screenshot is exactly the kind of drift that should fail
        // before someone discovers it manually in the Partner Center UI.
        MutateImportCsv(
            fixture.ImportCsvPath,
            row => row.Field == "DesktopScreenshot5",
            values => values["fr-fr"] = string.Empty);

        var result = RunPowerShellScript(
            Path.Combine(fixture.RepositoryRoot, "Scripts", "validate-store-listing.ps1"),
            ["-RepositoryRoot", fixture.RepositoryRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SLP022", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStoreListingScript_Fails_WhenFieldRowIsDuplicated()
    {
        using var fixture = CreateStoreListingFixture();

        var document = StoreListingCsvDocument.Load(fixture.ImportCsvPath);
        var duplicatedTitleRow = document.Rows.First(row => row.Field == "Title");
        var rows = document.Rows.Concat([duplicatedTitleRow]).ToArray();
        StoreListingCsvWriter.Save(fixture.ImportCsvPath, document.Headers, rows, utf8Bom: false);

        var result = RunPowerShellScript(
            Path.Combine(fixture.RepositoryRoot, "Scripts", "validate-store-listing.ps1"),
            ["-RepositoryRoot", fixture.RepositoryRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SLP025", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStoreListingScript_Fails_WhenCriticalValueHasTrailingWhitespace()
    {
        using var fixture = CreateStoreListingFixture();
        MutateImportCsv(
            fixture.ImportCsvPath,
            row => row.Field == "SearchTerm1",
            values => values["en-us"] = values["en-us"] + " ");

        var result = RunPowerShellScript(
            Path.Combine(fixture.RepositoryRoot, "Scripts", "validate-store-listing.ps1"),
            ["-RepositoryRoot", fixture.RepositoryRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("SLP026", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunPowerShellScript(
        string scriptPath,
        IReadOnlyList<string> arguments)
    {
        var shellPath = ResolvePowerShellExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = shellPath,
            WorkingDirectory = RepoRoot.Value,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start PowerShell script: {scriptPath}");

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput, standardError);
    }

    private static string ResolvePowerShellExecutable()
    {
        // CI runners normally expose pwsh, while some local Windows environments may
        // still only have Windows PowerShell available in PATH. The test supports both
        // so the validation layer stays runnable everywhere the repository is tested.
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("$PSVersionTable.PSVersion.ToString()");

                using var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                process.WaitForExit();
                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        throw new InvalidOperationException("Neither 'pwsh' nor 'powershell' is available in PATH.");
    }

    private static StoreListingScriptFixture CreateStoreListingFixture()
    {
        var tempDirectory = new TemporaryDirectory();
        var repositoryRoot = tempDirectory.Path;

        CopyFileIntoFixture("Scripts/validate-store-listing.ps1", repositoryRoot);
        CopyFileIntoFixture("Scripts/release-helpers.ps1", repositoryRoot);

        var storeListingRoot = Path.Combine(repositoryRoot, "Packaging", "Windows", "StoreListing");
        Directory.CreateDirectory(storeListingRoot);

        var latestTemplatePath = StoreListingPaths.FindLatestExportTemplateCsv(RepoRoot.Value);
        File.Copy(latestTemplatePath, Path.Combine(storeListingRoot, Path.GetFileName(latestTemplatePath)), overwrite: true);

        CopyDirectory(
            Path.Combine(RepoRoot.Value, "Packaging", "Windows", "StoreListing", "ImportFolder"),
            Path.Combine(storeListingRoot, "ImportFolder"));

        return new StoreListingScriptFixture(
            tempDirectory,
            repositoryRoot,
            Path.Combine(storeListingRoot, "ImportFolder", "listingData.csv"));
    }

    private static void MutateImportCsv(
        string importCsvPath,
        Func<StoreListingCsvRow, bool> rowPredicate,
        Action<Dictionary<string, string>> mutate)
    {
        // The mutation helper rewrites the CSV through the shared writer so negative tests
        // stay close to the real file structure instead of relying on fragile string replaces.
        var document = StoreListingCsvDocument.Load(importCsvPath);
        var rows = document.Rows
            .Select(row =>
            {
                var values = row.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (rowPredicate(row))
                {
                    mutate(values);
                }

                return new StoreListingCsvRow(values);
            })
            .ToArray();

        StoreListingCsvWriter.Save(importCsvPath, document.Headers, rows, utf8Bom: false);
    }

    private static void CopyFileIntoFixture(string relativePath, string fixtureRoot)
    {
        var sourcePath = StoreListingPaths.CombineRelativePath(RepoRoot.Value, relativePath);
        var targetPath = StoreListingPaths.CombineRelativePath(fixtureRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        Directory.CreateDirectory(targetPath);

        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativeDirectory = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(targetPath, relativeDirectory));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relativeFile = Path.GetRelativePath(sourcePath, file);
            var targetFile = Path.Combine(targetPath, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private sealed record StoreListingScriptFixture(
        TemporaryDirectory TempDirectory,
        string RepositoryRoot,
        string ImportCsvPath) : IDisposable
    {
        public void Dispose()
        {
            TempDirectory.Dispose();
        }
    }
}
