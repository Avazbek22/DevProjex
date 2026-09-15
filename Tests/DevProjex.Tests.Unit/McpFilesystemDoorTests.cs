using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

/// <summary>
/// The offline guarantee is checked by counting the probes a resolution makes, which is only as
/// good as the set of calls that report themselves. This reads the files that decide a
/// client-supplied project and insists that every filesystem call in them is preceded by a
/// recording call in the same member.
/// </summary>
/// <remarks>
/// Without this, the measurement rots in the easiest possible way: someone adds a perfectly
/// reasonable <c>Path.Exists</c> or <c>ResolveLinkTarget</c>, the count stays zero because the new
/// call reports nothing, and the tests that assert nothing was opened keep passing while something
/// is. Which calls count is decided by listing the members that are known to be lexical rather than
/// the ones that touch a disk, for the same reason the path classifier lists what it accepts: the
/// framework keeps adding ways to open a file, and a list of those cannot be finished.
/// </remarks>
public sealed class McpFilesystemDoorTests
{
	/// <summary>
	/// Every file that reads a client-supplied project string before it has been cleared, including
	/// the classifier, which is evaluated first of all and claims to touch nothing.
	/// </summary>
	private static readonly string[] ResolutionSources =
	[
		"Apps/Mcp/McpProjectSourceResolver.cs",
		"Apps/Mcp/McpRootRegistry.cs",
		"Apps/Mcp/McpRemoteProviderPath.cs",
		"Apps/Mcp/McpProjectPathProbe.cs"
	];

	/// <summary>
	/// Anything that names one of the filesystem types, plus the instance members that reach a disk
	/// through a handle already in hand.
	/// </summary>
	private static readonly Regex FilesystemCall = new(
		@"\b(Directory|File|Path)\.(?<member>\w+)" +
		@"|\bnew\s+(?<constructed>DirectoryInfo|FileInfo|FileStream|DriveInfo)\b" +
		@"|\.(?<instance>ResolveLinkTarget|Refresh|EnumerateFileSystemInfos|EnumerateFiles|EnumerateDirectories)\b" +
		@"|\b(?<helper>EnsureRegularFile)\b",
		RegexOptions.Compiled);

	/// <summary>
	/// Members of those types that only rewrite a string. Everything else on them has to report
	/// itself, whether or not this list has heard of it.
	/// </summary>
	private static readonly HashSet<string> LexicalMembers = new(StringComparer.Ordinal)
	{
		"Combine",
		"GetFullPath",
		"GetFileName",
		"GetFileNameWithoutExtension",
		"GetDirectoryName",
		"GetExtension",
		"GetPathRoot",
		"GetRelativePath",
		"IsPathFullyQualified",
		"IsPathRooted",
		"TrimEndingDirectorySeparator",
		"EndsInDirectorySeparator",
		"DirectorySeparatorChar",
		"AltDirectorySeparatorChar",
		"PathSeparator",
		"VolumeSeparatorChar",
		"GetInvalidFileNameChars",
		"GetInvalidPathChars"
	};

	/// <summary>
	/// Calls allowed to go unrecorded, keyed by file and by the call exactly as the scan reads it,
	/// each with the reason it cannot be a probe of a client-supplied project string.
	/// </summary>
	private static readonly Dictionary<(string File, string Call), string> Exempt = new()
	{
		[("Apps/Mcp/McpProjectSourceResolver.cs", "Directory.Exists")] =
			"Only on clone.LocalPath, the checkout the cache produced, reached after remote access " +
			"was granted. Guarded by the single-call rule below, so it cannot cover a second call."
	};

	[Fact]
	public void EveryFilesystemCallOnTheProjectResolutionPathReportsItself()
	{
		var root = FindRepositoryRoot();
		var unrecorded = new List<string>();

		foreach (var relative in ResolutionSources)
		{
			var lines = File.ReadAllLines(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
			var recordedInMember = false;
			for (var index = 0; index < lines.Length; index++)
			{
				var line = lines[index];
				if (StartsMember(line))
					recordedInMember = false;
				if (line.Contains("McpProjectPathProbe.Record();", StringComparison.Ordinal))
					recordedInMember = true;
				if (recordedInMember || IsComment(line))
					continue;
				foreach (var call in FilesystemCalls(line))
				{
					if (IsExempt(relative, call, line))
						continue;
					unrecorded.Add($"{relative}:{index + 1}: {call} — {line.Trim()}");
				}
			}
		}

		Assert.True(
			unrecorded.Count == 0,
			"These calls reach the filesystem without reporting themselves to McpProjectPathProbe, so a " +
			"test that asserts nothing was opened would not see them. Put McpProjectPathProbe.Record() " +
			"immediately before each, or list it in this file with the reason it cannot be a probe of a " +
			"client-supplied project:" +
			Environment.NewLine + string.Join(Environment.NewLine, unrecorded));
	}

	/// <summary>
	/// An exemption covers one call, not the line it sits on: a second call sharing that line would
	/// otherwise ride along on the first one's reason.
	/// </summary>
	private static bool IsExempt(string relative, string call, string line) =>
		Exempt.ContainsKey((relative, call)) && FilesystemCalls(line).Count == 1;

	private static List<string> FilesystemCalls(string line)
	{
		var calls = new List<string>();
		foreach (Match match in FilesystemCall.Matches(line))
		{
			var member = match.Groups["member"];
			if (member.Success)
			{
				if (LexicalMembers.Contains(member.Value))
					continue;
				calls.Add(match.Value);
				continue;
			}
			calls.Add(match.Value.Trim());
		}
		return calls;
	}

	/// <summary>
	/// A member in these files sits at exactly one tab. Anything at that depth ends the reach of
	/// whatever recorded before it, including a property or a field, which carry no parameter list
	/// and would otherwise inherit a recording from the member above.
	/// </summary>
	private static bool StartsMember(string line)
	{
		if (!line.StartsWith('\t') || line.StartsWith("\t\t", StringComparison.Ordinal))
			return false;
		var trimmed = line.TrimStart();
		return trimmed.Length > 0 &&
		       !IsComment(line) &&
		       !trimmed.StartsWith('[') &&
		       !trimmed.StartsWith('{') &&
		       !trimmed.StartsWith('}');
	}

	private static bool IsComment(string line)
	{
		var trimmed = line.TrimStart();
		return trimmed.StartsWith("//", StringComparison.Ordinal) ||
		       trimmed.StartsWith('*') ||
		       trimmed.StartsWith("/*", StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
			directory = directory.Parent;
		return directory?.FullName ??
		       throw new InvalidOperationException("The repository root could not be located.");
	}
}
