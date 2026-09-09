using System.Security.Cryptography;
using System.Text;

namespace DevProjex.Kernel.Models;

public static class RepositoryUrlUtility
{
	private const string ComparisonIdentityVersionPrefix = "v2:";
	private const string SourceCacheIdentityVersionPrefix = "v3:";
	private const string TestFileTransportPolicyVariable =
		"DEVPROJEX_INTERNAL_TEST_ALLOW_FILE_GIT";
	private static readonly HashSet<string> CaseInsensitiveRepositoryPathHosts = new(
		StringComparer.OrdinalIgnoreCase)
	{
		"github.com",
		"gitlab.com",
		"bitbucket.org"
	};

	public static bool TryNormalize(string? repositoryUrl, out string normalizedUrl)
	{
		normalizedUrl = Normalize(repositoryUrl);
		return normalizedUrl.Length > 0;
	}

	public static string Normalize(string? repositoryUrl) =>
		Normalize(repositoryUrl, preserveHttpUserName: false);

	public static string ToSafeSourceIdentity(string? repositoryUrl) =>
		Normalize(repositoryUrl, preserveHttpUserName: true);

	private static string Normalize(string? repositoryUrl, bool preserveHttpUserName)
	{
		if (string.IsNullOrWhiteSpace(repositoryUrl))
			return string.Empty;

		var trimmed = repositoryUrl.Trim();
		if (ContainsUnsafeCharacters(trimmed))
			return string.Empty;

		if (TryParseScpSyntax(trimmed, out var scp))
		{
			var safePath = RemoveQueryAndFragment(scp.Path);
			return safePath.Length == 0
				? string.Empty
				: $"{scp.UserPrefix}{scp.Host.ToLowerInvariant()}:{NormalizePath(safePath)}";
		}

		if (!Uri.TryCreate(trimmed.Replace('\\', '/'), UriKind.Absolute, out var uri))
			return trimmed.Contains("://", StringComparison.Ordinal)
				? string.Empty
				: trimmed.Replace('\\', '/').TrimEnd('/');
		if (uri.Host.Length == 0 &&
		    !uri.IsFile &&
		    trimmed.Contains('@', StringComparison.Ordinal))
		{
			return string.Empty;
		}

		try
		{
			var builder = new UriBuilder(uri)
			{
				Fragment = string.Empty,
				Query = string.Empty,
				Password = string.Empty,
				Host = uri.Host.ToLowerInvariant()
			};
			if (!preserveHttpUserName && uri.Scheme is "http" or "https")
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
			return BuildVersionedHostPathKey(scp.Host, -1, scp.Path);

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
		    uri.Scheme is "http" or "https" or "ssh" or "git")
		{
			return BuildVersionedHostPathKey(
				uri.Host,
				uri.IsDefaultPort ? -1 : uri.Port,
				uri.AbsolutePath);
		}
		if (uri?.IsFile == true)
			return BuildVersionedFileSystemKey(TrimGitSuffix(uri.LocalPath));

		try
		{
			if (Path.IsPathFullyQualified(normalized))
				return BuildVersionedFileSystemKey(TrimGitSuffix(normalized));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return string.Empty;
		}

		return VersionIdentity(TrimGitSuffix(normalized));
	}

	public static string GetSourceCacheKey(string? repositoryUrl)
	{
		var normalized = ToSafeSourceIdentity(repositoryUrl);
		if (normalized.Length == 0)
			return string.Empty;

		if (TryParseScpSyntax(normalized, out var scp))
		{
			return BuildSourceCacheHostPathKey(
				"ssh",
				scp.UserPrefix.TrimEnd('@'),
				scp.Host,
				22,
				scp.Path);
		}

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
		    uri.Scheme is "http" or "https" or "ssh" or "git")
		{
			return BuildSourceCacheHostPathKey(
				uri.Scheme.ToLowerInvariant(),
				GetUserName(uri),
				uri.Host,
				GetEffectivePort(uri),
				uri.AbsolutePath);
		}
		if (uri?.IsFile == true)
			return BuildSourceCacheFileSystemKey(uri.LocalPath);

		try
		{
			if (Path.IsPathFullyQualified(normalized))
				return BuildSourceCacheFileSystemKey(normalized);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return string.Empty;
		}

		return SourceCacheIdentity(normalized);
	}

	public static bool AreEquivalent(string? left, string? right)
	{
		var leftKey = GetComparisonKey(left);
		var rightKey = GetComparisonKey(right);
		return leftKey.Length > 0 &&
		       string.Equals(leftKey, rightKey, StringComparison.Ordinal);
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

	public static bool IsNetworkCloneSource(string? repositoryUrl)
	{
		if (!TryNormalize(repositoryUrl, out var normalized))
			return false;

		if (TryParseScpSyntax(normalized, out _))
			return true;

		return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
		       uri.Scheme is "http" or "https" or "ssh" or "git";
	}

	public static bool IsSupportedCloneSource(string? repositoryUrl)
	{
		try
		{
			var localCandidate = repositoryUrl?.Trim();
			if (!string.IsNullOrEmpty(localCandidate) &&
			    !localCandidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
			    Path.IsPathFullyQualified(localCandidate) &&
			    Directory.Exists(localCandidate))
			{
				return true;
			}
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
		}

		if (!TryNormalize(repositoryUrl, out var normalized) ||
		    normalized.StartsWith("-", StringComparison.Ordinal))
		{
			return false;
		}

		if (TryParseScpSyntax(normalized, out _))
			return true;

		if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
		{
			return uri.Scheme is "https" or "ssh" ||
			       uri.IsFile && string.Equals(
				       Environment.GetEnvironmentVariable(TestFileTransportPolicyVariable),
				       "1",
				       StringComparison.Ordinal);
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

	public static bool IsScpStyleSource(string? repositoryUrl)
	{
		if (!TryNormalize(repositoryUrl, out var normalized))
			return false;

		return TryParseScpSyntax(normalized, out _);
	}

	private static string BuildVersionedHostPathKey(string host, int port, string path)
	{
		var normalizedHost = host.Trim().ToLowerInvariant();
		var caseInsensitivePath = CaseInsensitiveRepositoryPathHosts.Contains(normalizedHost);
		var normalizedPath = TrimGitSuffix(
			NormalizePath(path),
			caseInsensitivePath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		if (caseInsensitivePath)
			normalizedPath = normalizedPath.ToLowerInvariant();
		var portSuffix = port > 0 ? $":{port}" : string.Empty;
		return VersionIdentity($"{normalizedHost}{portSuffix}/{normalizedPath.TrimStart('/')}");
	}

	private static string BuildSourceCacheHostPathKey(
		string scheme,
		string user,
		string host,
		int port,
		string path)
	{
		var normalizedHost = host.Trim().ToLowerInvariant();
		var caseInsensitivePath = CaseInsensitiveRepositoryPathHosts.Contains(normalizedHost);
		var normalizedPath = TrimGitSuffix(
			NormalizePath(path),
			StringComparison.OrdinalIgnoreCase);
		if (caseInsensitivePath)
			normalizedPath = normalizedPath.ToLowerInvariant();
		return SourceCacheIdentity(
			$"{scheme.ToLowerInvariant()}://{user}@{normalizedHost}:{port}/{normalizedPath.TrimStart('/')}");
	}

	private static string BuildVersionedFileSystemKey(string path)
	{
		var normalizedPath = PathUtility.NormalizeForCacheKey(path);
		return VersionIdentity(
			$"file/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))}");
	}

	private static string BuildSourceCacheFileSystemKey(string path)
	{
		var normalizedPath = PathUtility.NormalizeForCacheKey(ResolveLocalIdentityPath(path));
		return SourceCacheIdentity(
			$"file/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))}");
	}

	private static string ResolveLocalIdentityPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		try
		{
			var info = new DirectoryInfo(fullPath);
			if (info.Exists && info.LinkTarget is not null)
				return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
		}
		return fullPath;
	}

	private static string GetUserName(Uri uri)
	{
		if (string.IsNullOrEmpty(uri.UserInfo))
			return string.Empty;
		var separator = uri.UserInfo.IndexOf(':');
		var encoded = separator >= 0 ? uri.UserInfo[..separator] : uri.UserInfo;
		return Uri.UnescapeDataString(encoded);
	}

	private static int GetEffectivePort(Uri uri)
	{
		if (!uri.IsDefaultPort)
			return uri.Port;
		return uri.Scheme.ToLowerInvariant() switch
		{
			"http" => 80,
			"https" => 443,
			"ssh" => 22,
			"git" => 9418,
			_ => -1
		};
	}

	private static string VersionIdentity(string identity) =>
		$"{ComparisonIdentityVersionPrefix}{identity}";

	private static string SourceCacheIdentity(string identity) =>
		$"{SourceCacheIdentityVersionPrefix}{identity}";

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

	private static string RemoveQueryAndFragment(string value)
	{
		var separator = value.AsSpan().IndexOfAny('?', '#');
		return separator < 0 ? value : value[..separator];
	}

	private static string TrimGitSuffix(
		string value,
		StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
		value.EndsWith(".git", comparison)
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
