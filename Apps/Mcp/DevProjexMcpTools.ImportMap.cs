using System.Text;
using DevProjex.Kernel;
using DevProjex.Application.Context;
using DevProjex.Application.Dependencies;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	// A constant: neither the requested path nor the file read reaches it, so it stays trusted.
	internal const string ModulePathResolvedNotice =
		"[Path resolved] the requested path does not exist; the module file it names was read, and its path is in the File line.";
	private const int MaximumNamedImports = 12;

	// A constant: neither the path nor the file reaches it, so it stays trusted.
	internal const string LargeFileOutlineNotice =
		"[Large file] only the start and the structure are shown; read any part with start_line and end_line, " +
		"or the whole file with start_line=1 and end_line set to its last line.";

	// A constant: neither the path nor the file reaches it, so it stays trusted.
	internal const string LargeFileStructureOnceNotice =
		"[Large file] the structure of this long file follows the requested lines; it is listed once per file version.";

	// Physical path to the fingerprint of the protected text whose structure this session has
	// already received, so a long file read in pieces carries its structure once.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> outlinedFiles =
		new(PathComparer.Default);

	private static string OutlineFingerprint(string content) =>
		ContentFingerprint.Compute(content.AsSpan()).ToHexString();

	private bool IsFirstOutline(string physicalPath, string content) =>
		!outlinedFiles.TryGetValue(physicalPath, out var fingerprint) ||
		!string.Equals(fingerprint, OutlineFingerprint(content), StringComparison.Ordinal);

	private void RecordOutline(string physicalPath, string content) =>
		outlinedFiles[physicalPath] = OutlineFingerprint(content);

	private static string FormatOutline(McpLargeFileOutline outline) =>
		"Structure:\n" + outline.Map.TrimEnd('\n');

	private static readonly string[] ScriptExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];
	private static readonly string[] ScriptEntryExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs"];

	/// <summary>
	/// The "Imports:" line for a read of <paramref name="physicalPath"/>: each import statement on the
	/// returned lines with the project file it resolves to. The statement must appear on its line of
	/// <paramref name="pageText"/>, the protected text being returned, so a reference the protection
	/// changed is left out. Null when the language has no module imports, no import is on the
	/// returned lines, or the resolution fails; the line is a hint and never fails the read.
	/// </summary>
	private async Task<string?> FindImportsLineAsync(
		McpJsonArguments arguments,
		string physicalPath,
		string pageText,
		int startLine,
		CancellationToken cancellationToken)
	{
		var imports = await FindImportsAsync(arguments, [physicalPath], cancellationToken).ConfigureAwait(false);
		return imports.TryGetValue(physicalPath, out var resolved) ? FormatImportsLine(resolved, pageText, startLine) : null;
	}

	/// <summary>
	/// The resolved imports of each of <paramref name="physicalPaths"/> that has module imports,
	/// keyed by physical path, resolved against the readable project. A file whose resolution fails
	/// is left out; the result is a hint and never fails the read.
	/// </summary>
	private async Task<IReadOnlyDictionary<string, IReadOnlyList<DependencyFileImport>>> FindImportsAsync(
		McpJsonArguments arguments,
		IReadOnlyList<string> physicalPaths,
		CancellationToken cancellationToken)
	{
		var result = new Dictionary<string, IReadOnlyList<DependencyFileImport>>(PathComparer.Default);
		if (physicalPaths.Count == 0)
			return result;
		ProjectContextPlan readable;
		try
		{
			readable = await Projects.BuildReadableProjectPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				arguments.OptionalString("profile"),
				ParseExclusionsArgument(arguments),
				cancellationToken).ConfigureAwait(false);
		}
		catch (McpToolException)
		{
			return result;
		}
		foreach (var physicalPath in physicalPaths.Distinct(PathComparer.Default))
		{
			try
			{
				var imports = await Projects.DependencyFactsEngine.ResolveFileImportsAsync(
					readable.SourceRoot,
					readable.IncludedFiles,
					physicalPath,
					readable.IncludedFiles,
					cancellationToken).ConfigureAwait(false);
				if (imports is { Count: > 0 } &&
					WithoutRedactedImports(readable, physicalPath, imports, cancellationToken) is { Count: > 0 } shown)
					result[physicalPath] = shown;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
												  InvalidOperationException or NotSupportedException or
												  DependencyTraversalLimitException)
			{
				// One file that cannot be resolved does not take the hint away from the others.
			}
		}
		return result;
	}

	// References and target paths are synthetic text built from project names; an import any part
	// of which the protection would change is left out rather than shown masked.
	private IReadOnlyList<DependencyFileImport> WithoutRedactedImports(
		ProjectContextPlan plan,
		string physicalPath,
		IReadOnlyList<DependencyFileImport> imports,
		CancellationToken cancellationToken)
	{
		var shown = new List<DependencyFileImport>(imports.Count);
		foreach (var import in imports)
		{
			var text = string.Join('\n', [import.Reference, .. import.Targets]);
			if (Projects.RedactSyntheticText(plan, physicalPath, text, cancellationToken).Text == text)
				shown.Add(import);
		}
		return shown;
	}

	internal static string? FormatImportsLine(IReadOnlyList<DependencyFileImport> imports, string pageText, int startLine)
	{
		var lines = pageText.Split('\n');
		var entries = new List<string>();
		var external = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var import in imports)
		{
			var index = import.Line - startLine;
			if (index < 0 || index >= lines.Length)
				continue;
			var reference = DisplayedImportReference(import.Reference);
			if (reference.Length == 0 || !lines[index].Contains(reference, StringComparison.Ordinal))
				continue;
			var escaped = McpTextEscaping.EscapeSingleLine(reference);
			if (!seen.Add(escaped))
				continue;
			// Project files are what the line is for; packages and standard modules are summarized
			// after them so they never push a project path out of the line.
			if (import.Status == ResolutionStatus.External)
				external.Add(escaped);
			else
				entries.Add(escaped + " → " + FormatImportTargets(import));
		}
		if (entries.Count == 0 && external.Count == 0)
			return null;
		var line = new StringBuilder("Imports: ");
		line.Append(string.Join(" · ", entries.Take(MaximumNamedImports)));
		if (entries.Count > MaximumNamedImports)
			line.Append(" (+").Append((entries.Count - MaximumNamedImports).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" more)");
		if (external.Count > 0)
		{
			if (entries.Count > 0)
				line.Append(" · ");
			line.Append("external: ").Append(string.Join(", ", external.Take(4)));
			if (external.Count > 4)
				line.Append(" (+").Append((external.Count - 4).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" more)");
		}
		return line.ToString();
	}

	// A wildcard import is recorded as "module.*"; the statement writes the module alone.
	private static string DisplayedImportReference(string reference) =>
		reference.EndsWith(".*", StringComparison.Ordinal) ? reference[..^2] :
		reference.EndsWith('*') ? reference[..^1] :
		reference;

	private static string FormatImportTargets(DependencyFileImport import) => import.Status switch
	{
		ResolutionStatus.Resolved when import.Targets.Count == 1 && import.Targets[0].StartsWith("namespace:", StringComparison.Ordinal) =>
			"namespace package",
		ResolutionStatus.Resolved when import.Targets.Count > 0 =>
			string.Join(" | ", import.Targets.Select(McpTextEscaping.EscapeSingleLine)),
		ResolutionStatus.Ambiguous when import.Targets.Count > 0 =>
			"ambiguous: " + string.Join(" | ", import.Targets.Take(4).Select(McpTextEscaping.EscapeSingleLine)),
		ResolutionStatus.External => "external",
		_ => "unresolved"
	};

	private static string AppendSectionLine(string section, string line) =>
		section + (section.EndsWith('\n') ? string.Empty : "\n") + line;

	/// <summary>
	/// The readable file a missing path most likely names as a module, or null. See
	/// <see cref="FindModuleFileForMissingPath"/>; candidates come from the readable project only.
	/// </summary>
	private async Task<string?> FindReadableModuleFileAsync(
		McpJsonArguments arguments,
		string requestedPath,
		CancellationToken cancellationToken)
	{
		ProjectContextPlan readable;
		try
		{
			readable = await Projects.BuildReadableProjectPlanAsync(
				arguments.OptionalString("project"),
				arguments.OptionalString("branch"),
				arguments.OptionalString("profile"),
				ParseExclusionsArgument(arguments),
				cancellationToken).ConfigureAwait(false);
		}
		catch (McpToolException)
		{
			return null;
		}
		var readableFiles = readable.IncludedFiles
			.Select(path => McpProjectService.ToRelative(readable.SourceRoot, path))
			.ToHashSet(StringComparer.Ordinal);
		return FindModuleFileForMissingPath(readableFiles, requestedPath);
	}

	/// <summary>
	/// The one readable file a path that names a module rather than a file stands for: "x.ts" read as
	/// "x/index.ts", "x.js" as "x.ts" the way TypeScript resolves ESM imports, and "pkg/mod" as
	/// "pkg/mod.py" or "pkg/mod/__init__.py". Null unless exactly one candidate is readable.
	/// </summary>
	internal static string? FindModuleFileForMissingPath(IReadOnlySet<string> readableFiles, string requestedPath)
	{
		var path = requestedPath.Replace('\\', '/').Trim().TrimStart('/');
		while (path.StartsWith("./", StringComparison.Ordinal))
			path = path[2..];
		if (path.Length == 0 || path.EndsWith('/'))
			return null;
		var extension = Path.GetExtension(path);
		var candidates = new List<string>();
		if (extension.Length == 0)
		{
			candidates.AddRange(ScriptExtensions.Select(item => path + item));
			candidates.Add(path + ".py");
			candidates.AddRange(ScriptEntryExtensions.Select(item => path + "/index" + item));
			candidates.Add(path + "/__init__.py");
		}
		else if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
		{
			var stem = path[..^extension.Length];
			candidates.AddRange(ScriptExtensions
				.Where(item => !item.Equals(extension, StringComparison.OrdinalIgnoreCase))
				.Select(item => stem + item));
			candidates.AddRange(ScriptEntryExtensions.Select(item => stem + "/index" + item));
		}
		else if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
			candidates.Add(path[..^extension.Length] + "/__init__.py");
		else
			return null;

		var existing = candidates.Where(readableFiles.Contains).Distinct(StringComparer.Ordinal).ToArray();
		return existing.Length == 1 ? existing[0] : null;
	}
}
