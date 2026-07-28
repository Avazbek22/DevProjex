namespace DevProjex.Kernel.Models;

public static class RepositoryUrlUtility
{
	public static bool TryNormalize(string? repositoryUrl, out string normalizedUrl)
	{
		normalizedUrl = Normalize(repositoryUrl);
		return normalizedUrl.Length > 0;
	}

	public static string Normalize(string? repositoryUrl)
	{
		if (string.IsNullOrWhiteSpace(repositoryUrl))
			return string.Empty;

		var trimmed = repositoryUrl.Trim();
		if (ContainsUnsafeCharacters(trimmed))
			return string.Empty;

		if (TryParseScpSyntax(trimmed, out var scp))
			return $"{scp.UserPrefix}{scp.Host.ToLowerInvariant()}:{NormalizePath(scp.Path)}";

		if (!Uri.TryCreate(trimmed.Replace('\\', '/'), UriKind.Absolute, out var uri))
			return trimmed.Replace('\\', '/').TrimEnd('/');

		try
		{
			var builder = new UriBuilder(uri)
			{
				Fragment = string.Empty,
				Query = string.Empty,
				Password = string.Empty,
				Host = uri.Host.ToLowerInvariant()
			};
			if (uri.Scheme is "http" or "https")
				builder.UserName = string.Empty;

			var sanitizedUri = builder.Uri;
			return sanitizedUri.IsFile
				? sanitizedUri.AbsoluteUri.TrimEnd('/')
				: sanitizedUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
		}
		catch
		{
			return string.Empty;
		}
	}

	public static string GetComparisonKey(string? repositoryUrl)
	{
		var normalized = Normalize(repositoryUrl);
		if (normalized.Length == 0)
			return string.Empty;

		if (TryParseScpSyntax(normalized, out var scp))
			return BuildHostPathKey(scp.Host, -1, scp.Path);

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
		    uri.Scheme is "http" or "https" or "ssh" or "git")
		{
			return BuildHostPathKey(
				uri.Host,
				uri.IsDefaultPort ? -1 : uri.Port,
				uri.AbsolutePath);
		}

		return TrimGitSuffix(normalized);
	}

	public static bool AreEquivalent(string? left, string? right)
	{
		var leftKey = GetComparisonKey(left);
		var rightKey = GetComparisonKey(right);
		return leftKey.Length > 0 &&
		       string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
	}

	public static string GetRepositoryName(string? repositoryUrl)
	{
		var normalized = Normalize(repositoryUrl);
		if (normalized.Length == 0)
			return "repository";

		string candidate;
		if (TryParseScpSyntax(normalized, out var scp))
		{
			candidate = GetLastPathSegment(scp.Path);
		}
		else if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
		{
			candidate = GetLastPathSegment(uri.AbsolutePath);
		}
		else
		{
			candidate = GetLastPathSegment(normalized);
		}

		candidate = TrimGitSuffix(candidate);
		try
		{
			candidate = Uri.UnescapeDataString(candidate);
		}
		catch
		{
			// Keep the encoded path segment when percent encoding is malformed.
		}

		var safeName = RemoveControlCharacters(candidate).Trim();
		return safeName.Length > 0 ? safeName : "repository";
	}

	public static string ToSafeDisplay(string? repositoryUrl) => Normalize(repositoryUrl);

	public static bool IsSupportedCloneSource(string? repositoryUrl)
	{
		if (!TryNormalize(repositoryUrl, out var normalized) ||
		    normalized.StartsWith("-", StringComparison.Ordinal))
		{
			return false;
		}

		if (TryParseScpSyntax(normalized, out _))
			return true;

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
		{
			return uri.Scheme is "http" or "https" or "ssh" or "git" or "file";
		}

		try
		{
			return Path.IsPathFullyQualified(normalized) && Directory.Exists(normalized);
		}
		catch
		{
			return false;
		}
	}

	private static string BuildHostPathKey(string host, int port, string path)
	{
		var normalizedHost = host.Trim().ToLowerInvariant();
		var normalizedPath = TrimGitSuffix(NormalizePath(path));
		var portSuffix = port > 0 ? $":{port}" : string.Empty;
		return $"{normalizedHost}{portSuffix}/{normalizedPath.TrimStart('/')}";
	}

	private static string GetLastPathSegment(string value)
	{
		var withoutSuffix = value.TrimEnd('/');
		var separatorIndex = withoutSuffix.LastIndexOf('/');
		return separatorIndex >= 0
			? withoutSuffix[(separatorIndex + 1)..]
			: withoutSuffix;
	}

	private static string NormalizePath(string value) =>
		value.Replace('\\', '/').Trim().TrimEnd('/');

	private static string TrimGitSuffix(string value) =>
		value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? value[..^4]
			: value;

	private static bool ContainsUnsafeCharacters(string value)
	{
		foreach (var character in value)
		{
			if (character == '"' || char.IsControl(character))
				return true;
		}

		return false;
	}

	private static string RemoveControlCharacters(string value)
	{
		if (!value.Any(char.IsControl))
			return value;

		return string.Concat(value.Where(static character => !char.IsControl(character)));
	}

	private static bool TryParseScpSyntax(string value, out ScpRepositoryUrl scp)
	{
		scp = default;
		if (value.Contains("://", StringComparison.Ordinal))
			return false;

		var colonIndex = value.IndexOf(':');
		if (colonIndex <= 0 || colonIndex == value.Length - 1)
			return false;

		var authority = value[..colonIndex];
		if (!authority.Contains('@') && !authority.Contains('.'))
			return false;

		var atIndex = authority.LastIndexOf('@');
		var userPrefix = atIndex >= 0 ? authority[..(atIndex + 1)] : string.Empty;
		var host = atIndex >= 0 ? authority[(atIndex + 1)..] : authority;
		if (host.Length == 0)
			return false;

		var path = value[(colonIndex + 1)..].TrimStart('/');
		if (path.Length == 0)
			return false;

		scp = new ScpRepositoryUrl(userPrefix, host, path);
		return true;
	}

	private readonly record struct ScpRepositoryUrl(
		string UserPrefix,
		string Host,
		string Path);
}
