namespace DevProjex.Infrastructure.ResourceStore;

public sealed class HelpContentProvider
{
    private readonly IReadOnlyDictionary<AppLanguage, Lazy<string>> _cache = CreateCache();

    public string GetHelpBody(AppLanguage language)
    {
        var resource = _cache.TryGetValue(language, out var localizedResource)
            ? localizedResource
            : _cache[AppLanguage.En];
        return resource.Value;
    }

    public static string ToPlainText(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            return string.Empty;

        var lines = rawBody.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(rawBody.Length);

        foreach (var line in lines)
        {
            builder.AppendLine(FormatPlainTextLine(line));
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyDictionary<AppLanguage, Lazy<string>> CreateCache()
    {
        var assembly = typeof(Marker).Assembly;
        return new Dictionary<AppLanguage, Lazy<string>>
        {
            [AppLanguage.Ru] = CreateResource(assembly, "ru"),
            [AppLanguage.En] = CreateResource(assembly, "en"),
            [AppLanguage.Uz] = CreateResource(assembly, "uz"),
            [AppLanguage.Tg] = CreateResource(assembly, "tg"),
            [AppLanguage.Kk] = CreateResource(assembly, "kk"),
            [AppLanguage.Fr] = CreateResource(assembly, "fr"),
            [AppLanguage.De] = CreateResource(assembly, "de"),
            [AppLanguage.It] = CreateResource(assembly, "it"),
            [AppLanguage.Es] = CreateResource(assembly, "es"),
            [AppLanguage.Pt] = CreateResource(assembly, "pt"),
            [AppLanguage.PtPt] = CreateResource(assembly, "pt-pt")
        };
    }

    private static Lazy<string> CreateResource(Assembly assembly, string code) =>
        new(() => Load(assembly, code), LazyThreadSafetyMode.ExecutionAndPublication);

    private static string Load(Assembly assembly, string code)
    {
        var resourceName = $"DevProjex.Assets.HelpContent.help.{code}.txt";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            var fallbackName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith($".HelpContent.help.{code}.txt", StringComparison.OrdinalIgnoreCase));

            stream = fallbackName is null
                ? null
                : assembly.GetManifestResourceStream(fallbackName);
        }

        if (stream is null)
            throw new InvalidOperationException($"Help content resource not found: {resourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string FormatPlainTextLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var trimmed = line.Trim();

        if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            return StripInlineMarkers(trimmed[3..]);

        if (trimmed.StartsWith("### ", StringComparison.Ordinal))
            return StripInlineMarkers(trimmed[4..]);

        if (trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            var indent = line.Length - line.TrimStart().Length;
            var plainIndent = indent >= 2 ? "  " : string.Empty;
            return $"{plainIndent}- {StripInlineMarkers(trimmed[2..])}";
        }

        if (IsNumberedListItem(trimmed))
            return StripInlineMarkers(trimmed);

        return StripInlineMarkers(trimmed);
    }

    private static bool IsNumberedListItem(string line)
    {
        var dotIndex = line.IndexOf(')');
        return dotIndex > 0 &&
               dotIndex <= 4 &&
               char.IsDigit(line[0]) &&
               dotIndex + 1 < line.Length &&
               line[dotIndex + 1] == ' ';
    }

    // Help files use light markdown-like markers for rendering. Clipboard output should stay plain and readable.
    private static string StripInlineMarkers(string text) => text.Replace("`", string.Empty);
}
