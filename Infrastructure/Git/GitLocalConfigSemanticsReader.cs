using System.Buffers;
using System.Globalization;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Git;

internal static class GitLocalConfigSemanticsReader
{
	private const int MaximumConfigLengthBytes = 1024 * 1024;
	private const int MaximumGitFileLengthBytes = 64 * 1024;

	public static bool TryRead(
		string repositoryRoot,
		string gitMetadataPath,
		out GitPathComparisonSemantics semantics)
	{
		semantics = default;
		try
		{
			if (!TryResolveGitDirectory(repositoryRoot, gitMetadataPath, out var gitDirectory) ||
			    !TryResolveCommonDirectory(gitDirectory, out var commonDirectory) ||
			    !HasStandardRepositoryStructure(gitDirectory, commonDirectory) ||
			    !TryReadOptionalStableText(
				    Path.Combine(commonDirectory, "config"),
				    out var configExists,
				    out var configText) ||
			    !configExists)
			{
				return false;
			}

			var values = new ConfigValues();
			if (!TryParse(configText, requireRepositoryFormat: true, ref values))
				return false;

			if (values.WorktreeConfig == true)
			{
				if (!TryReadOptionalStableText(
					    Path.Combine(gitDirectory, "config.worktree"),
					    out var worktreeConfigExists,
					    out var worktreeConfigText))
				{
					return false;
				}

				if (worktreeConfigExists &&
				    !TryParse(worktreeConfigText, requireRepositoryFormat: false, ref values))
				{
					return false;
				}
			}

			// System and global scopes have lower precedence than an explicit local
			// value. Ambiguous configurations fall back to native Git resolution.
			if (!values.IgnoreCase.HasValue ||
			    OperatingSystem.IsMacOS() && !values.PrecomposeUnicode.HasValue)
			{
				return false;
			}

			semantics = new GitPathComparisonSemantics(
				values.IgnoreCase.GetValueOrDefault(),
				OperatingSystem.IsMacOS() && values.PrecomposeUnicode.GetValueOrDefault());
			return true;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return false;
		}
	}

	private static bool HasStandardRepositoryStructure(
		string gitDirectory,
		string commonDirectory)
	{
		if (!TryReadOptionalStableText(
			    Path.Combine(gitDirectory, "HEAD"),
			    out var headExists,
			    out var headText,
			    MaximumGitFileLengthBytes) ||
		    !headExists ||
		    !IsValidHead(headText))
		{
			return false;
		}

		return IsPhysicalDirectory(Path.Combine(commonDirectory, "objects")) &&
		       IsPhysicalDirectory(Path.Combine(commonDirectory, "refs"));
	}

	private static bool IsValidHead(string headText)
	{
		var firstLineEnd = headText.IndexOfAny(['\r', '\n']);
		var value = (firstLineEnd < 0 ? headText : headText[..firstLineEnd]).Trim();
		const string symbolicReferencePrefix = "ref: ";
		if (value.StartsWith(symbolicReferencePrefix, StringComparison.Ordinal))
		{
			var reference = value[symbolicReferencePrefix.Length..];
			return reference.StartsWith("refs/", StringComparison.Ordinal) &&
			       reference.Length > "refs/".Length &&
			       reference.All(static character =>
				       !char.IsControl(character) &&
				       !char.IsWhiteSpace(character) &&
				       character != '\\');
		}

		return value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
	}

	private static bool IsPhysicalDirectory(string path)
	{
		try
		{
			var attributes = File.GetAttributes(path);
			return attributes.HasFlag(FileAttributes.Directory) &&
			       !attributes.HasFlag(FileAttributes.ReparsePoint);
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}

	private static bool TryResolveGitDirectory(
		string repositoryRoot,
		string gitMetadataPath,
		out string gitDirectory)
	{
		gitDirectory = string.Empty;
		var attributes = File.GetAttributes(gitMetadataPath);
		if (attributes.HasFlag(FileAttributes.ReparsePoint))
			return false;

		if (attributes.HasFlag(FileAttributes.Directory))
		{
			gitDirectory = PathUtility.Normalize(gitMetadataPath);
			return true;
		}

		if (!TryReadOptionalStableText(
			    gitMetadataPath,
			    out var exists,
			    out var gitFileText,
			    MaximumGitFileLengthBytes) ||
		    !exists)
		{
			return false;
		}

		var firstLineEnd = gitFileText.IndexOfAny(['\r', '\n']);
		var firstLine = firstLineEnd < 0 ? gitFileText : gitFileText[..firstLineEnd];
		const string prefix = "gitdir:";
		if (!firstLine.StartsWith(prefix, StringComparison.Ordinal))
			return false;

		var target = firstLine[prefix.Length..].Trim();
		if (target.Length == 0)
			return false;

		gitDirectory = PathUtility.Normalize(
			Path.IsPathRooted(target)
				? target
				: Path.Combine(repositoryRoot, target));
		return Directory.Exists(gitDirectory);
	}

	private static bool TryResolveCommonDirectory(
		string gitDirectory,
		out string commonDirectory)
	{
		commonDirectory = gitDirectory;
		if (!TryReadOptionalStableText(
			    Path.Combine(gitDirectory, "commondir"),
			    out var exists,
			    out var commonDirectoryText,
			    MaximumGitFileLengthBytes))
		{
			return false;
		}

		if (!exists)
			return true;

		var firstLineEnd = commonDirectoryText.IndexOfAny(['\r', '\n']);
		var target = (firstLineEnd < 0
				? commonDirectoryText
				: commonDirectoryText[..firstLineEnd])
			.Trim();
		if (target.Length == 0)
			return false;

		commonDirectory = PathUtility.Normalize(
			Path.IsPathRooted(target)
				? target
				: Path.Combine(gitDirectory, target));
		return Directory.Exists(commonDirectory);
	}

	private static bool TryReadOptionalStableText(
		string path,
		out bool exists,
		out string text,
		int maximumLengthBytes = MaximumConfigLengthBytes)
	{
		exists = false;
		text = string.Empty;
		for (var attempt = 0; attempt < 2; attempt++)
		{
			var fileInfo = new FileInfo(path);
			fileInfo.Refresh();
			if (!fileInfo.Exists)
				return true;
			if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
			    !string.IsNullOrEmpty(fileInfo.LinkTarget) ||
			    !UnixFileTypeInspector.IsRegularFile(path) ||
			    fileInfo.Length > maximumLengthBytes)
			{
				return false;
			}

			var lastWriteTicks = fileInfo.LastWriteTimeUtc.Ticks;
			var lengthBytes = fileInfo.Length;
			using var stream = new FileStream(
				fileInfo.FullName,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read | FileShare.Delete,
				bufferSize: 4096,
				FileOptions.SequentialScan);
			if (stream.Length > maximumLengthBytes)
				return false;
			if (!TryReadBoundedText(stream, maximumLengthBytes, out var observedText))
				return false;

			fileInfo.Refresh();
			if (fileInfo.Exists &&
			    fileInfo.LastWriteTimeUtc.Ticks == lastWriteTicks &&
			    fileInfo.Length == lengthBytes)
			{
				exists = true;
				text = observedText;
				return true;
			}
		}

		return false;
	}

	internal static bool TryReadBoundedText(
		Stream stream,
		int maximumBytes,
		out string text)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
		text = string.Empty;
		if (stream.Length > maximumBytes)
			return false;

		var initialCapacity = (int)Math.Min(stream.Length, Math.Min(maximumBytes, 4096));
		using var content = new MemoryStream(initialCapacity);
		var buffer = ArrayPool<byte>.Shared.Rent(4096);
		try
		{
			var total = 0;
			while (true)
			{
				var remaining = maximumBytes - total;
				var readSize = remaining >= buffer.Length ? buffer.Length : remaining + 1;
				var read = stream.Read(buffer, 0, readSize);
				if (read == 0)
					break;
				if (read > remaining)
					return false;
				content.Write(buffer, 0, read);
				total += read;
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}

		content.Position = 0;
		using var reader = new StreamReader(
			content,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true,
			bufferSize: 1024,
			leaveOpen: true);
		text = reader.ReadToEnd();
		return true;
	}

	private static bool TryParse(
		string configText,
		bool requireRepositoryFormat,
		ref ConfigValues values)
	{
		var section = ConfigSection.None;
		var foundRepositoryFormat = false;
		foreach (var rawLine in configText.Split(['\r', '\n']))
		{
			var line = rawLine.Trim();
			if (line.Length == 0 || line[0] is '#' or ';')
				continue;
			if (HasLineContinuation(line))
				return false;

			if (line[0] == '[')
			{
				if (!TryReadSection(line, out section))
					return false;
				if (section == ConfigSection.Include)
					return false;
				continue;
			}

			var separatorIndex = line.IndexOf('=');
			var key = (separatorIndex < 0 ? line : line[..separatorIndex]).Trim();
			if (key.Length == 0)
				return false;
			var rawValue = separatorIndex < 0 ? null : line[(separatorIndex + 1)..];
			if (!IsValidKey(key) || !HasSimpleValueSyntax(rawValue))
				return false;

			if (section == ConfigSection.None &&
			    key.StartsWith("include.", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (IsKey(section, key, ConfigSection.Core, "repositoryformatversion"))
			{
				if (!TryReadInteger(rawValue, out _))
					return false;
				foundRepositoryFormat = true;
			}
			else if (IsKey(section, key, ConfigSection.Core, "ignorecase"))
			{
				if (!TryReadBoolean(rawValue, out var value))
					return false;
				values.IgnoreCase = value;
			}
			else if (IsKey(section, key, ConfigSection.Core, "precomposeunicode"))
			{
				if (!TryReadBoolean(rawValue, out var value))
					return false;
				values.PrecomposeUnicode = value;
			}
			else if (IsKey(section, key, ConfigSection.Extensions, "worktreeconfig"))
			{
				if (!TryReadBoolean(rawValue, out var value))
					return false;
				values.WorktreeConfig = value;
			}
		}

		return !requireRepositoryFormat || foundRepositoryFormat;
	}

	private static bool TryReadSection(string line, out ConfigSection section)
	{
		section = ConfigSection.Other;
		var closingIndex = line.IndexOf(']');
		if (closingIndex <= 1)
			return false;

		var suffix = line[(closingIndex + 1)..].TrimStart();
		if (suffix.Length > 0 && suffix[0] is not '#' and not ';')
			return false;

		var header = line[1..closingIndex].Trim();
		var nameEnd = header.IndexOfAny([' ', '\t', '"']);
		var name = nameEnd < 0 ? header : header[..nameEnd];
		if (!IsValidKey(name) || !HasSimpleSubsectionSyntax(header, nameEnd))
			return false;
		if (name.Equals("include", StringComparison.OrdinalIgnoreCase) ||
		    name.Equals("includeif", StringComparison.OrdinalIgnoreCase))
		{
			section = ConfigSection.Include;
		}
		else if (name.Equals("core", StringComparison.OrdinalIgnoreCase) && nameEnd < 0)
			section = ConfigSection.Core;
		else if (name.Equals("extensions", StringComparison.OrdinalIgnoreCase) && nameEnd < 0)
			section = ConfigSection.Extensions;
		else
			section = ConfigSection.Other;

		return name.Length > 0;
	}

	private static bool IsValidKey(string value)
	{
		if (value.Length == 0)
			return false;

		var segmentStart = true;
		foreach (var character in value)
		{
			if (character == '.')
			{
				if (segmentStart)
					return false;
				segmentStart = true;
				continue;
			}

			if (segmentStart)
			{
				if (!char.IsAsciiLetter(character))
					return false;
				segmentStart = false;
				continue;
			}

			if (!char.IsAsciiLetterOrDigit(character) && character != '-')
				return false;
		}

		return !segmentStart;
	}

	private static bool HasSimpleSubsectionSyntax(string header, int nameEnd)
	{
		if (nameEnd < 0)
			return true;

		var subsection = header[nameEnd..].Trim();
		return subsection.Length >= 2 &&
		       subsection[0] == '"' &&
		       subsection[^1] == '"' &&
		       subsection[1..^1].IndexOfAny(['"', '\\']) < 0;
	}

	private static bool HasSimpleValueSyntax(string? rawValue)
	{
		if (rawValue is null)
			return true;

		var value = rawValue.Trim();
		if (value.Length == 0)
			return true;
		if (value[0] == '"')
		{
			return value.Length >= 2 &&
			       value[^1] == '"' &&
			       value[1..^1].IndexOfAny(['"', '\\']) < 0;
		}

		return value.IndexOfAny(['"', '\\']) < 0;
	}

	private static bool IsKey(
		ConfigSection section,
		string key,
		ConfigSection expectedSection,
		string expectedKey) =>
		section == expectedSection && key.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) ||
		section == ConfigSection.None && key.Equals(
			$"{expectedSection.ToString().ToLowerInvariant()}.{expectedKey}",
			StringComparison.OrdinalIgnoreCase);

	private static bool TryReadBoolean(string? rawValue, out bool value)
	{
		if (rawValue is null)
		{
			value = true;
			return true;
		}

		if (!TryNormalizeValue(rawValue, out var normalized) || normalized.Length == 0)
		{
			value = false;
			return false;
		}

		switch (normalized.ToLowerInvariant())
		{
			case "true":
			case "yes":
			case "on":
			case "1":
				value = true;
				return true;
			case "false":
			case "no":
			case "off":
			case "0":
				value = false;
				return true;
			default:
				value = false;
				return false;
		}
	}

	private static bool TryReadInteger(string? rawValue, out int value)
	{
		value = 0;
		return rawValue is not null &&
		       TryNormalizeValue(rawValue, out var normalized) &&
		       int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
		       value >= 0;
	}

	private static bool TryNormalizeValue(string rawValue, out string normalized)
	{
		var value = rawValue.Trim();
		if (value.Length == 0)
		{
			normalized = string.Empty;
			return true;
		}

		if (value[0] == '"')
		{
			if (value.Length < 2 || value[^1] != '"')
			{
				normalized = string.Empty;
				return false;
			}

			normalized = value[1..^1];
			return normalized.IndexOfAny(['"', '\\']) < 0;
		}

		var commentIndex = value.IndexOfAny(['#', ';']);
		normalized = (commentIndex < 0 ? value : value[..commentIndex]).TrimEnd();
		return true;
	}

	private static bool HasLineContinuation(string line)
	{
		var slashCount = 0;
		for (var index = line.Length - 1; index >= 0 && line[index] == '\\'; index--)
			slashCount++;
		return slashCount % 2 != 0;
	}

	private struct ConfigValues
	{
		public bool? IgnoreCase { get; set; }
		public bool? PrecomposeUnicode { get; set; }
		public bool? WorktreeConfig { get; set; }
	}

	private enum ConfigSection
	{
		None,
		Core,
		Extensions,
		Include,
		Other
	}
}
