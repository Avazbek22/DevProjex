using System.Text;

namespace DevProjex.Application.Services;

public sealed class RepositoryWebPathPresentationService
{
    public ExportPathPresentation? TryCreate(string localRootPath, string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(localRootPath) || string.IsNullOrWhiteSpace(repositoryUrl))
            return null;

        var normalizedRootPath = Path.GetFullPath(localRootPath);
        if (!Uri.TryCreate(NormalizeRepositoryUrl(repositoryUrl), UriKind.Absolute, out var repoUri))
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
            mapFilePath: filePath => MapToFileWebPath(filePath, normalizedRootPath, rootWebPath),
            displayRootName: displayRootName);
    }

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var normalized = repositoryUrl.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return normalized.TrimEnd('/');

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        var normalizedPath = builder.Path.TrimEnd('/');
        if (normalizedPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalizedPath = normalizedPath[..^4];
        builder.Path = normalizedPath;

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string MapToFileWebPath(string fullPath, string localRootPath, string rootWebPath)
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

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
            return rootWebPath;

        if (relativePath.StartsWith("..", StringComparison.Ordinal))
            return fullPath;

        var relativeUnixPath = relativePath.Replace('\\', '/');
        var encodedRelativePath = EncodePathSegments(relativeUnixPath);

        return $"{rootWebPath}/{encodedRelativePath}";
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
        if (string.IsNullOrWhiteSpace(path))
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
