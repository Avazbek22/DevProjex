namespace DevProjex.Application.Services;

public sealed class RepositoryWebPathPresentationService
{
    public static string NormalizeForDisplay(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return string.Empty;

        return NormalizeRepositoryUrl(repositoryUrl);
    }

    public ExportPathPresentation? TryCreate(string localRootPath, string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(localRootPath) || string.IsNullOrWhiteSpace(repositoryUrl))
            return null;

        var normalizedRootPath = Path.GetFullPath(localRootPath);
        if (!Uri.TryCreate(NormalizeForDisplay(repositoryUrl), UriKind.Absolute, out var repoUri))
            return null;

        if (!repoUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !repoUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootWebPath = repoUri.ToString().TrimEnd('/');
        var displayRootName = ExtractRepositoryName(repoUri);

        return new ExportPathPresentation(
            displayRootPath: rootWebPath,
            mapFilePath: filePath => MapToRepositoryPath(filePath, normalizedRootPath, rootWebPath),
            displayRootName: displayRootName);
    }

    public Func<string, string>? TryCreatePathMapper(string localRootPath, string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(localRootPath))
            return null;

        var displayRootPath = NormalizeForDisplay(repositoryUrl);
        if (displayRootPath.Length == 0)
            return null;

        string normalizedRootPath;
        try
        {
            normalizedRootPath = Path.GetFullPath(localRootPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return filePath => MapToRepositoryPath(filePath, normalizedRootPath, displayRootPath);
    }

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var normalized = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl).TrimEnd('/');
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private static string MapToRepositoryPath(string fullPath, string localRootPath, string rootDisplayPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return fullPath;

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(localRootPath, fullPath);
        }
        catch
        {
            return fullPath;
        }

        if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            return rootDisplayPath;

        if (PathUtility.IsRelativePathOutsideRoot(relativePath))
            return fullPath;

        var relativeUnixPath = PathUtility.NormalizeSeparators(relativePath);
        var encodedRelativePath = EncodePathSegments(relativeUnixPath);

        return $"{rootDisplayPath}/{encodedRelativePath}";
    }

    private static string? ExtractRepositoryName(Uri repositoryUri)
    {
        var path = repositoryUri.AbsolutePath.Trim('/');
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var repositoryName = Uri.UnescapeDataString(segments[^1]);
        return string.IsNullOrWhiteSpace(repositoryName) ? null : repositoryName;
    }

    private static string EncodePathSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var span = path.AsSpan();
        var sb = new StringBuilder(path.Length + 8);
        var segmentStart = 0;

        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '/')
                continue;

            if (i > segmentStart)
            {
                var segment = span[segmentStart..i];
                if (IsUriUnreserved(segment))
                    sb.Append(segment);
                else
                    sb.Append(Uri.EscapeDataString(segment.ToString()));
            }

            if (i < span.Length)
                sb.Append('/');

            segmentStart = i + 1;
        }

        return sb.ToString();
    }

    private static bool IsUriUnreserved(ReadOnlySpan<char> segment)
    {
        foreach (var ch in segment)
        {
            var isAlphaNum = (ch is >= 'A' and <= 'Z') || (ch is >= 'a' and <= 'z') || (ch is >= '0' and <= '9');
            if (isAlphaNum)
                continue;

            if (ch is '-' or '.' or '_' or '~')
                continue;

            return false;
        }

        return true;
    }
}
