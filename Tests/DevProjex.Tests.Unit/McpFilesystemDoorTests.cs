using System.Text.RegularExpressions;

namespace DevProjex.Tests.Unit;

/// <summary>
/// The offline guarantee is checked by counting the probes a resolution makes, which is only as
/// good as the set of calls that report themselves. This reads the two files that decide a
/// client-supplied project and insists that every filesystem call in them is preceded by a
/// recording call in the same member.
/// </summary>
/// <remarks>
/// Without this, the measurement rots in the easiest possible way: someone adds a perfectly
/// reasonable <c>DirectoryInfo.Exists</c> or <c>ResolveLinkTarget</c>, the count stays zero because
/// the new call reports nothing, and the tests that assert nothing was opened keep passing while
/// something is. A call that genuinely does not need recording is listed below with the reason,
/// which makes adding one a decision rather than an oversight.
/// </remarks>
public sealed class McpFilesystemDoorTests
{
	private static readonly string[] ResolutionSources =
	[
		"Apps/Mcp/McpProjectSourceResolver.cs",
		"Apps/Mcp/McpRootRegistry.cs"
	];

	/// <summary>
	/// Calls that reach the filesystem, written as they appear in source.
	/// </summary>
	private static readonly Regex FilesystemCall = new(
		@"\b(Directory|File)\.(Exists|Open|OpenRead|ReadAll\w+|GetAttributes|Enumerate\w+|Get\w+)\b" +
		@"|\bnew\s+(DirectoryInfo|FileInfo|FileStream)\b" +
		@"|\.(ResolveLinkTarget|EnumerateFileSystemInfos|EnumerateFiles|EnumerateDirectories)\b" +
		@"|\bEnsureRegularFile\b",
		RegexOptions.Compiled);

	/// <summary>
	/// Calls that are allowed to go unrecorded, each with the reason it cannot be a probe of a
	/// client-supplied project string.
	/// </summary>
	private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
	{
		["Apps/Mcp/McpProjectSourceResolver.cs:Directory.Exists(clone.LocalPath)"] =
			"The checkout directory the cache produced, reached only after remote access was granted."
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
			foreach (var (line, index) in lines.Select(static (line, index) => (line, index)))
			{
				if (StartsMember(line))
					recordedInMember = false;
				if (line.Contains("McpProjectPathProbe.Record();", StringComparison.Ordinal))
					recordedInMember = true;
				if (recordedInMember || IsComment(line))
					continue;
				var match = FilesystemCall.Match(line);
				if (!match.Success)
					continue;
				if (Exempt.Keys.Any(key =>
					    key.StartsWith(relative + ":", StringComparison.Ordinal) &&
					    line.Contains(key[(relative.Length + 1)..], StringComparison.Ordinal)))
				{
					continue;
				}
				unrecorded.Add($"{relative}:{index + 1}: {line.Trim()}");
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
	/// A member declaration in these files sits at exactly one tab and carries a parameter list.
	/// Crossing one ends the reach of whatever recorded before it.
	/// </summary>
	private static bool StartsMember(string line) =>
		line.StartsWith('\t') &&
		!line.StartsWith("\t\t", StringComparison.Ordinal) &&
		line.Contains('(', StringComparison.Ordinal) &&
		!IsComment(line) &&
		!line.TrimStart().StartsWith('[');

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
