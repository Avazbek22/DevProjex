using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public static class OutputRootPathPresentation
{
	private const string LocalUserPlaceholder = "[local-user-1]";

	public static string Resolve(
		string rootPath,
		ExportPathPresentation? pathPresentation,
		ContentTransformationContext? transformationContext) =>
		Resolve(
			rootPath,
			pathPresentation?.DisplayRootPath,
			transformationContext?.Redaction?.Features.HasFlag(SecretRedactionFeatures.PrivateData) == true);

	public static string Resolve(
		string rootPath,
		string? displayRootPath,
		bool hidePrivateData)
	{
		var effectivePath = string.IsNullOrWhiteSpace(displayRootPath)
			? rootPath
			: displayRootPath;
		return hidePrivateData ? MaskLocalUserSegment(effectivePath) : effectivePath;
	}

	public static string MaskLocalUserSegment(string path)
	{
		if (string.IsNullOrEmpty(path) || !TryFindLocalUserSegment(path.AsSpan(), out var start, out var length))
			return path;

		return string.Concat(path.AsSpan(0, start), LocalUserPlaceholder, path.AsSpan(start + length));
	}

	private static bool TryFindLocalUserSegment(
		ReadOnlySpan<char> path,
		out int start,
		out int length)
	{
		start = 0;
		length = 0;
		if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
		{
			var position = 3;
			if (!path[position..].StartsWith("Users", StringComparison.OrdinalIgnoreCase))
				return false;
			position += "Users".Length;
			if (position >= path.Length || !IsSeparator(path[position]))
				return false;
			start = position + 1;
		}
		else if (path.StartsWith("/home/", StringComparison.Ordinal))
		{
			start = "/home/".Length;
		}
		else if (path.StartsWith("/Users/", StringComparison.Ordinal))
		{
			start = "/Users/".Length;
		}
		else
		{
			return false;
		}

		var end = start;
		while (end < path.Length && !IsSeparator(path[end]))
			end++;
		var segment = path[start..end];
		if (end == start || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
			return false;

		length = end - start;
		return true;
	}

	private static bool IsSeparator(char value) => value is '/' or '\\';
}
