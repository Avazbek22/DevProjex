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
					var end = line.IndexOf(']');
					if (end < 0)
						return new(paths, true);
					var section = line[1..end].Trim();
					inSubmodule = section.StartsWith("submodule ", StringComparison.OrdinalIgnoreCase) &&
					              section[10..].Trim() is var name && name.Length >= 2 &&
					              name[0] == '"' && name[^1] == '"';
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

	private static bool TryReadPath(string value, out string path)
	{
		var result = new System.Text.StringBuilder();
		var quoted = false;
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
		}
		path = result.ToString().Trim();
		return !quoted && path.Length > 0 && !Path.IsPathRooted(path) &&
		       !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
		       path.Split('/').All(static segment => segment.Length > 0 && segment is not ("." or ".."));
	}
}
