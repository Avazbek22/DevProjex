namespace DevProjex.Infrastructure.Git;

internal sealed record GitCloneAuthentication(
	string RepositoryUrl,
	string UserName,
	string Password)
{
	public static bool TryResolveCloneUrl(
		string repositoryUrl,
		out string cloneUrl,
		out GitCloneAuthentication? authentication)
	{
		authentication = TryCreate(repositoryUrl);
		if (authentication is not null)
		{
			cloneUrl = authentication.RepositoryUrl;
			return true;
		}

		if (ContainsPotentialHttpUserInfo(repositoryUrl))
		{
			cloneUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
			return false;
		}

		cloneUrl = repositoryUrl;
		return true;
	}

	public static GitCloneAuthentication? TryCreate(string repositoryUrl)
	{
		if (!Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out var uri) ||
		    uri.Scheme is not ("http" or "https") ||
		    string.IsNullOrEmpty(uri.UserInfo))
		{
			return null;
		}

		var separator = uri.UserInfo.IndexOf(':');
		var encodedUserName = separator >= 0 ? uri.UserInfo[..separator] : uri.UserInfo;
		var encodedPassword = separator >= 0 ? uri.UserInfo[(separator + 1)..] : string.Empty;
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		if (safeUrl.Length == 0)
			return null;

		try
		{
			return new GitCloneAuthentication(
				safeUrl,
				Uri.UnescapeDataString(encodedUserName),
				Uri.UnescapeDataString(encodedPassword));
		}
		catch (UriFormatException)
		{
			return null;
		}
	}

	private static bool ContainsPotentialHttpUserInfo(string repositoryUrl)
	{
		if (string.IsNullOrWhiteSpace(repositoryUrl))
			return false;

		var value = repositoryUrl.Trim();
		var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
		if (schemeSeparator <= 0)
			return false;

		var scheme = value[..schemeSeparator];
		if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
		    !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var authorityStart = schemeSeparator + 3;
		var authorityEnd = value.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
		var authority = authorityEnd < 0
			? value.AsSpan(authorityStart)
			: value.AsSpan(authorityStart, authorityEnd);
		return authority.Contains('@');
	}
}
