using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.FileSystem;

internal sealed record GitSubmoduleManifest(IReadOnlySet<string> Paths, bool ReadFailed)
{
	private const int MaximumSubmoduleCount = 8_192;
	public static GitSubmoduleManifest Read(string repositoryRoot, CancellationToken cancellationToken)
	{
		var paths = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			var path = Path.Combine(repositoryRoot, ".gitmodules");
			var attributes = File.GetAttributes(path);
			if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
				return new(paths, true);
			var source = GitIgnoreFileReader.ReadWithCancellation(path, cancellationToken);
			var inSubmodule = false;
			foreach (var sourceLine in GitIgnoreFileReader.EnumerateLinesWithCancellation(source.Content, cancellationToken))
			{
				var line = sourceLine.Trim();
				if (line.Length == 0 || line[0] is '#' or ';')
					continue;
				if (line[0] == '[')
				{
					if (!TryReadSection(line, out inSubmodule))
						return new(paths, true);
					continue;
				}
				if (!inSubmodule)
					continue;
				var equals = line.IndexOf('=');
				if (equals < 0 || !line[..equals].Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!TryReadPath(line[(equals + 1)..], out var relativePath))
					return new(paths, true);
				paths.Add(relativePath);
				if (paths.Count > MaximumSubmoduleCount)
					return new(paths, true);
			}
			return new(paths, false);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return new(paths, false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
		       System.Security.SecurityException or ArgumentException or NotSupportedException)
		{
			return new(paths, true);
		}
	}

	private static bool TryReadSection(string line, out bool isSubmodule)
	{
		isSubmodule = false;
		var quoted = false;
		for (var index = 1; index < line.Length; index++)
		{
			if (line[index] == '\\' && quoted)
			{
				index++;
				continue;
			}
			if (line[index] == '"')
				quoted = !quoted;
			if (line[index] != ']' || quoted)
				continue;
			var trailing = line[(index + 1)..].TrimStart();
			if (trailing.Length > 0 && trailing[0] is not ('#' or ';'))
				return false;
			var section = line[1..index].Trim();
			var separator = section.IndexOfAny([' ', '\t']);
			if (separator < 0 || !section[..separator].Equals("submodule", StringComparison.OrdinalIgnoreCase))
				return true;
			var name = section[(separator + 1)..].Trim();
			isSubmodule = name.Length >= 2 && name[0] == '"' && name[^1] == '"';
			return isSubmodule;
		}
		return false;
	}

	private static bool TryReadPath(string value, out string path)
	{
		value = value.TrimStart();
		var result = new System.Text.StringBuilder();
		var quoted = false;
		var significantLength = 0;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			if (character == '"')
			{
				quoted = !quoted;
				continue;
			}
			if (!quoted && character is '#' or ';')
				break;
			if (character == '\\')
			{
				if (++index >= value.Length || value[index] is not ('"' or '\\'))
				{
					path = string.Empty;
					return false;
				}
				character = value[index];
			}
			result.Append(character);
			if (quoted || !char.IsWhiteSpace(character))
				significantLength = result.Length;
		}
		path = result.ToString(0, significantLength);
		return !quoted && path.Length > 0 && !Path.IsPathRooted(path) &&
		       !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
		       path.Split('/').All(static segment => segment.Length > 0 && segment is not ("." or ".."));
	}
}
