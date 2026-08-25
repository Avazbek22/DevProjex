namespace DevProjex.Infrastructure.Git;

internal sealed record GitCloneAuthentication(
	string RepositoryUrl,
	string UserName,
	string Password)
{
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
}
