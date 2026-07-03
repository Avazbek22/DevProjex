namespace DevProjex.Tests.Integration.Helpers;

internal static class ProgressAssertions
{
    public static void AssertCompletedZipDownload(IReadOnlyList<string> reports)
    {
        Assert.NotEmpty(reports);

        var extractionMarkerIndex = IndexOf(reports, "::EXTRACTING::");
        Assert.True(extractionMarkerIndex >= 0, "ZIP progress must report extraction phase transition.");

        var downloadReports = reports.Take(extractionMarkerIndex).ToArray();
        var extractionReports = reports.Skip(extractionMarkerIndex + 1).ToArray();

        Assert.Contains("0%", downloadReports);
        Assert.Contains("100%", downloadReports);
        Assert.Contains("0%", extractionReports);
        Assert.Contains("100%", extractionReports);

        Assert.All(reports, report =>
            Assert.True(
                report == "::EXTRACTING::" || IsPercentReport(report),
                $"Unexpected progress report: '{report}'."));
    }

    private static int IndexOf(IReadOnlyList<string> reports, string value)
    {
        for (var index = 0; index < reports.Count; index++)
        {
            if (string.Equals(reports[index], value, StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static bool IsPercentReport(string report)
    {
        if (!report.EndsWith('%'))
            return false;

        return int.TryParse(report.AsSpan(0, report.Length - 1), out var percent) &&
               percent is >= 0 and <= 100;
    }
}
