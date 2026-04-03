namespace DevProjex.Tests.Shared.StoreListing;

internal static class StoreListingCsvWriter
{
    internal static void Save(
        string csvPath,
        IReadOnlyList<string> headers,
        IReadOnlyList<StoreListingCsvRow> rows,
        bool utf8Bom = false,
        string lineEnding = "\r\n")
    {
        // This writer is intentionally small and deterministic.
        // It is not a general-purpose CSV serializer; it exists so tests can mutate
        // real Partner Center artifacts without changing field order, line endings,
        // or other details that matter to the store import pipeline.
        var builder = new StringBuilder();
        builder.Append(string.Join(",", headers.Select(EscapeCsv)));
        builder.Append(lineEnding);

        foreach (var row in rows)
        {
            var values = headers
                .Select(header => row.Values.TryGetValue(header, out var value) ? value : string.Empty)
                .Select(EscapeCsv);

            builder.Append(string.Join(",", values));
            builder.Append(lineEnding);
        }

        File.WriteAllText(csvPath, builder.ToString(), new UTF8Encoding(utf8Bom));
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
