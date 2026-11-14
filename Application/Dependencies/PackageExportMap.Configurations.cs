using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevProjex.Application.Dependencies;

public static partial class PackageExportMap
{
	private static readonly JsonDocumentOptions ConfigurationJsonOptions = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};

	public static IReadOnlyList<string> ConfigurationDependencies(string path, string content, bool references,
		string? projectRoot = null, bool includeInheritance = true)
	{
		try
		{
			using var json = JsonDocument.Parse(content, ConfigurationJsonOptions);
			if (json.RootElement.ValueKind != JsonValueKind.Object)
				return [];
			var result = new List<string>();
			if (includeInheritance && json.RootElement.TryGetProperty("extends", out var parent) && parent.ValueKind == JsonValueKind.String &&
				ResolveConfigurationPath(path, parent.GetString(), reference: false, projectRoot) is { } extended)
				result.Add(extended);
			if (references && json.RootElement.TryGetProperty("references", out var links) && links.ValueKind == JsonValueKind.Array)
				foreach (var link in links.EnumerateArray().Take(16))
					if (link.ValueKind == JsonValueKind.Object && link.TryGetProperty("path", out var target) &&
						target.ValueKind == JsonValueKind.String && ResolveConfigurationPath(path, target.GetString(), reference: true, projectRoot) is { } referenced)
						result.Add(referenced);
			return result.Distinct(StringComparer.Ordinal).ToArray();
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static string? ResolveConfigurationPath(string owner, string? value, bool reference, string? projectRoot)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;
		value = value.Replace('\\', '/');
		if (!reference && !value.StartsWith("./", StringComparison.Ordinal) && !value.StartsWith("../", StringComparison.Ordinal))
			return null;
		var resolved = reference && Path.IsPathFullyQualified(value)
			? ResolveAbsoluteConfigurationPath(projectRoot, value)
			: ResolveRelativePath(DirectoryOf(owner), value);
		if (resolved is null)
			return null;
		return resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
			? resolved
			: reference ? (resolved.Length == 0 ? "" : resolved.TrimEnd('/') + "/") + "tsconfig.json" : resolved + ".json";
	}

	private static string? ResolveAbsoluteConfigurationPath(string? projectRoot, string path)
	{
		if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot))
			return null;
		try
		{
			var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
			return ResolveRelativePath(string.Empty, relative);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private static string DirectoryOf(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";

	private static string? ResolveRelativePath(string directory, string value)
	{
		value = value.Replace('\\', '/');
		if (value.StartsWith('/') || value.Contains(':') || value.Contains('\0') || value.Contains("${", StringComparison.Ordinal))
			return null;
		var parts = directory.Length == 0 ? new List<string>() : directory.Split('/').ToList();
		foreach (var part in value.Split('/'))
		{
			if (part is "" or ".") continue;
			if (part == "..")
			{
				if (parts.Count == 0) return null;
				parts.RemoveAt(parts.Count - 1);
			}
			else parts.Add(part);
		}
		return string.Join('/', parts);
	}

	private static IReadOnlyDictionary<string, string> CompiledTargets(
		string directory, IReadOnlyDictionary<string, string> documents, IReadOnlySet<string> visibleFiles, string? projectRoot)
	{
		var configPath = directory.Length == 0 ? "tsconfig.json" : directory + "/tsconfig.json";
		if (!visibleFiles.Contains(configPath) || !documents.ContainsKey(configPath))
			return new Dictionary<string, string>();
		var candidates = new List<string> { configPath };
		if (documents.TryGetValue(configPath, out var rootContent))
		{
			try
			{
				using var json = JsonDocument.Parse(rootContent, ConfigurationJsonOptions);
				if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("references", out var references) &&
					references.ValueKind == JsonValueKind.Array)
					foreach (var reference in references.EnumerateArray().Take(16))
						if (reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("path", out var target) &&
							target.ValueKind == JsonValueKind.String && ResolveConfigurationPath(configPath, target.GetString(), reference: true, projectRoot) is { } candidate)
							candidates.Add(candidate);
			}
			catch (Exception exception) when (exception is JsonException or RegexMatchTimeoutException or NotSupportedException) { }
		}
		var outputs = new List<(string Output, string Source)>();
		var configurationNames = documents.Keys.Where(visibleFiles.Contains).ToArray();
		foreach (var requested in candidates.Distinct(StringComparer.Ordinal))
		{
			if (!ProjectTreePathIdentity.TryResolveAvailableName(configurationNames, requested, out var candidate) ||
				!documents.TryGetValue(candidate, out var content)) continue;
			try
			{
				using var config = JsonDocument.Parse(content, ConfigurationJsonOptions);
				var own = config.RootElement;
				if (own.ValueKind != JsonValueKind.Object || !SafeInheritance(candidate, documents, configurationNames, new HashSet<string>(StringComparer.Ordinal), 0, projectRoot) ||
					!own.TryGetProperty("compilerOptions", out var options) || options.ValueKind != JsonValueKind.Object ||
					!options.TryGetProperty("rootDir", out var root) || root.ValueKind != JsonValueKind.String ||
					!options.TryGetProperty("outDir", out var output) || output.ValueKind != JsonValueKind.String) continue;
				var rootDir = root.GetString()!;
				var outDir = output.GetString()!;
				if (rootDir.Contains("..", StringComparison.Ordinal) || outDir.Contains("..", StringComparison.Ordinal)) continue;
				var configDirectory = DirectoryOf(candidate);
				var sourceRoot = ResolveRelativePath(configDirectory, rootDir);
				var outputRoot = ResolveRelativePath(configDirectory, outDir);
				if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(outputRoot)) continue;
				var hasFiles = own.TryGetProperty("files", out var files);
				var hasInclude = own.TryGetProperty("include", out var include);
				if (!hasFiles && !hasInclude && own.TryGetProperty("extends", out _)) continue;
				var fileNames = hasFiles ? ReadStrings(files) : [];
				var includes = hasInclude ? ReadStrings(include) : [];
				var excludes = own.TryGetProperty("exclude", out var exclude) ? ReadStrings(exclude) : [];
				if (fileNames is null || includes is null || excludes is null) continue;
				var exact = fileNames.Select(value => ResolveRelativePath(configDirectory, value)).ToHashSet(StringComparer.Ordinal);
				if (exact.Contains(null)) continue;
				var includePatterns = Patterns(includes, configDirectory, visibleFiles);
				var excludePatterns = Patterns(excludes, configDirectory, visibleFiles);
				if (includePatterns is null || excludePatterns is null) continue;
				foreach (var source in visibleFiles.Where(IsSource).Order(StringComparer.Ordinal))
				{
					if (!source.StartsWith(sourceRoot + "/", StringComparison.Ordinal) ||
						(hasFiles || hasInclude) && !exact.Contains(source) && !includePatterns.Any(pattern => pattern.IsMatch(source)) ||
						excludePatterns.Any(pattern => pattern.IsMatch(source))) continue;
					foreach (var extension in OutputExtensions(source))
					{
						var emitted = outputRoot + "/" + Path.ChangeExtension(source[(sourceRoot.Length + 1)..], null) + extension;
						if (directory.Length > 0 && !emitted.StartsWith(directory + "/", StringComparison.Ordinal)) continue;
						outputs.Add((directory.Length == 0 ? emitted : emitted[(directory.Length + 1)..], source));
					}
				}
			}
			catch (Exception exception) when (exception is JsonException or RegexMatchTimeoutException or NotSupportedException) { }
		}
		return outputs.GroupBy(static pair => pair.Output, StringComparer.Ordinal)
			.Where(group => group.Select(static pair => pair.Source).Distinct(StringComparer.Ordinal).Count() == 1)
			.Where(group => Path.GetExtension(group.First().Source) is ".ts" or ".mts" or ".cts")
			.Where(group => !visibleFiles.Contains((directory.Length == 0 ? "" : directory + "/") + group.Key))
			.ToDictionary(static group => group.Key, static group => group.First().Source, StringComparer.Ordinal);
	}

	private static bool SafeInheritance(string path, IReadOnlyDictionary<string, string> documents,
		IReadOnlyList<string> configurationNames, HashSet<string> visited, int depth, string? projectRoot)
	{
		if (depth > 4 || !ProjectTreePathIdentity.TryResolveAvailableName(configurationNames, path, out var resolved) ||
			!visited.Add(resolved) || !documents.TryGetValue(resolved, out var content)) return false;
		path = resolved;
		using var json = JsonDocument.Parse(content, ConfigurationJsonOptions);
		var config = json.RootElement;
		if (config.ValueKind != JsonValueKind.Object) return false;
		if (config.TryGetProperty("compilerOptions", out var options))
		{
			if (options.ValueKind != JsonValueKind.Object || options.TryGetProperty("outFile", out _) || options.TryGetProperty("declarationDir", out _) ||
				options.TryGetProperty("noEmit", out var noEmit) && noEmit.ValueKind != JsonValueKind.False) return false;
		}
		if (!config.TryGetProperty("extends", out var parent)) return true;
		return parent.ValueKind == JsonValueKind.String && ResolveConfigurationPath(path, parent.GetString(), reference: false, projectRoot) is { } next &&
			SafeInheritance(next, documents, configurationNames, visited, depth + 1, projectRoot);
	}

	private static string[]? ReadStrings(JsonElement array) => array.ValueKind == JsonValueKind.Array &&
		array.EnumerateArray().All(static value => value.ValueKind == JsonValueKind.String)
		? array.EnumerateArray().Select(static value => value.GetString()!).ToArray() : null;

	private static IReadOnlyList<Regex>? Patterns(IEnumerable<string> patterns, string directory, IReadOnlySet<string> files)
	{
		var result = new List<Regex>();
		foreach (var value in patterns)
		{
			var pattern = ResolveRelativePath(directory, value);
			if (pattern is null || pattern.Length > 4096 || pattern.IndexOfAny(['[', ']', '{', '}', '!', '$']) >= 0 ||
				pattern.Split('/').Any(static segment => segment.Contains("**", StringComparison.Ordinal) && segment != "**")) return null;
			if (!pattern.Contains('*') && !pattern.Contains('?') &&
				(!Path.HasExtension(pattern) || files.Any(file => file.StartsWith(pattern + "/", StringComparison.Ordinal))))
				pattern = pattern.Length == 0 ? "**" : pattern + "/**";
			var expression = new System.Text.StringBuilder("\\A");
			for (var index = 0; index < pattern.Length; index++)
			{
				if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
				{
					index++;
					if (index + 1 < pattern.Length && pattern[index + 1] == '/') { expression.Append("(?:.*/)?"); index++; }
					else expression.Append(".*");
				}
				else expression.Append(pattern[index] switch { '*' => "[^/]*", '?' => "[^/]", _ => Regex.Escape(pattern[index].ToString()) });
			}
			result.Add(new Regex(expression.Append("\\z").ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)));
		}
		return result;
	}
}
