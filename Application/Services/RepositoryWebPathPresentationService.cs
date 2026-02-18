namespace DevProjex.Application.Services;

public sealed class RepositoryWebPathPresentationService
{
    public ExportPathPresentation? TryCreate(string localRootPath, string repositoryUrl, string? branchName)
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

        return new ExportPathPresentation(
            displayRootPath: rootWebPath,
            mapFilePath: filePath => MapToFileWebPath(filePath, normalizedRootPath, rootWebPath));
    }

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var normalized = repositoryUrl.Trim().TrimEnd('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return normalized;

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

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

    private static string EncodePathSegments(string path)
    {
        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString);
        return string.Join("/", segments);
    }
}
