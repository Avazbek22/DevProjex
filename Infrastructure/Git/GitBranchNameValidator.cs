using System.Buffers;

namespace DevProjex.Infrastructure.Git;

internal static class GitBranchNameValidator
{
	private static readonly SearchValues<char> ForbiddenCharacters =
		SearchValues.Create(" ~^:?*[\\");

	public static bool IsValid(string? branchName)
	{
		if (string.IsNullOrEmpty(branchName) ||
		    branchName[0] == '-' ||
		    branchName is "@" ||
		    branchName[^1] is '/' or '.' ||
		    branchName.Contains("..", StringComparison.Ordinal) ||
		    branchName.Contains("@{", StringComparison.Ordinal) ||
		    branchName.Contains("//", StringComparison.Ordinal) ||
		    branchName.AsSpan().IndexOfAny(ForbiddenCharacters) >= 0)
		{
			return false;
		}

		foreach (var component in branchName.Split('/'))
		{
			if (component.Length == 0 ||
			    component[0] == '.' ||
			    component.EndsWith(".lock", StringComparison.Ordinal))
			{
				return false;
			}
		}

		foreach (var character in branchName)
		{
			if (character < ' ' || character == '\u007F')
				return false;
		}

		return true;
	}

	public static string ValidateAndNormalize(string branchName)
	{
		ArgumentNullException.ThrowIfNull(branchName);
		if (!IsValid(branchName))
			throw new ArgumentException("The Git branch name is invalid.", nameof(branchName));
		return branchName;
	}
}
