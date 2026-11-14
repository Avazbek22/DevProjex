using System.Text.Json;

namespace DevProjex.Application.Dependencies;

public sealed record PackageExport(string Manifest, string Key, string Target, long EstimatedTokens = 0);

public static partial class PackageExportMap
{
	public static IReadOnlyList<PackageExport> Find(
		IReadOnlyDictionary<string, string> documents,
		IReadOnlySet<string> visibleFiles,
		string? projectRoot = null)
	{
		var result = new List<PackageExport>();
		foreach (var entry in documents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			if (!visibleFiles.Contains(entry.Key) || Path.GetFileName(entry.Key) is not ("package.json" or "jsr.json" or "deno.json"))
				continue;
			var directory = Path.GetDirectoryName(entry.Key)?.Replace('\\', '/') ?? "";
			var compiled = CompiledTargets(directory, documents, visibleFiles, projectRoot);
			try
			{
				using var document = JsonDocument.Parse(entry.Value);
				if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("exports", out var exports))
					continue;
				if (exports.ValueKind == JsonValueKind.Object && exports.EnumerateObject().Any(property => property.Name.StartsWith('.')))
				{
					foreach (var property in exports.EnumerateObject())
						AddTargets(property.Name, property.Value);
				}
				else
					AddTargets(".", exports);
			}
			catch (JsonException)
			{
				// Invalid or protected manifests cannot prove an export.
			}

			void AddTargets(string key, JsonElement target)
			{
				if (target.ValueKind == JsonValueKind.Object)
				{
					foreach (var condition in target.EnumerateObject())
						AddTargets(key, condition.Value);
					return;
				}
				if (target.ValueKind == JsonValueKind.Array)
				{
					foreach (var fallback in target.EnumerateArray())
						AddTargets(key, fallback);
					return;
				}
				if (target.ValueKind != JsonValueKind.String || target.GetString() is not { } value || !value.StartsWith("./", StringComparison.Ordinal))
					return;
				foreach (var source in visibleFiles.Where(IsSource).Order(StringComparer.Ordinal))
				{
					var relative = directory.Length == 0 ? source : source.StartsWith(directory + "/", StringComparison.Ordinal) ? source[(directory.Length + 1)..] : null;
					if (relative is null)
						continue;
					if (Match(value[2..], relative, out var wildcard))
						result.Add(new PackageExport(entry.Key, key.Replace("*", wildcard, StringComparison.Ordinal), source, (entry.Value.Length + 3L) / 4));
				}
				foreach (var output in compiled)
					if (Match(value[2..], output.Key, out var wildcard))
						result.Add(new PackageExport(entry.Key, key.Replace("*", wildcard, StringComparison.Ordinal), output.Value, (entry.Value.Length + 3L) / 4));
			}
		}
		return result.Distinct().OrderBy(item => item.Manifest, StringComparer.Ordinal).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
	}

	private static bool IsSource(string path) => Path.GetExtension(path) is ".ts" or ".tsx" or ".mts" or ".cts" or ".js" or ".jsx" or ".mjs" or ".cjs";


	private static string[] OutputExtensions(string path) => Path.GetExtension(path) switch
	{
		".mts" or ".mjs" => [".mjs", ".d.mts"],
		".cts" or ".cjs" => [".cjs", ".d.cts"],
		_ => [".js", ".d.ts"]
	};

	private static bool Match(string pattern, string path, out string wildcard)
	{
		wildcard = "";
		var star = pattern.IndexOf('*');
		if (star < 0)
			return pattern == path;
		if (pattern.LastIndexOf('*') != star || path.Length < pattern.Length - 1 ||
			!path.StartsWith(pattern[..star], StringComparison.Ordinal) || !path.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal))
			return false;
		wildcard = path.Substring(star, path.Length - pattern.Length + 1);
		return true;
	}
}
