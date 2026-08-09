namespace DevProjex.Infrastructure.Compression;

/// <summary>
/// One language, expressed as data. Adding a language is a manifest, a set of .scm queries and
/// fixtures — no C# changes for the common case. That is not a promise that it is ALWAYS enough:
/// Python already needs its own docstring handling, so the pack carries optional queries and the
/// compressor keeps a small amount of per-language behaviour behind them.
/// </summary>
internal sealed record CompressionLanguagePack(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Extensions,
	string Library,
	string Export,
	int QueryVersion,
	string BlockPlaceholder,
	IReadOnlySet<string> ContainerNodeTypes,
	IReadOnlySet<string> ExecutableOwnerKinds,
	string BodiesQuery,
	string DeclarationsQuery,
	string? DocstringsQuery,
	string? PreservesQuery)
{
	/// <summary>
	/// Goes into the cache key. A grammar or query change must change this string, or plans built
	/// under the old rules are served against the new ones.
	/// </summary>
	public string Identity => $"{Id}:v{QueryVersion}";

	private sealed record Manifest(
		string Id,
		string DisplayName,
		string[] Extensions,
		string Library,
		string Export,
		int QueryVersion,
		string BlockPlaceholder,
		string[] ContainerNodeTypes,
		string[] ExecutableOwnerKinds);

	private static readonly JsonSerializerOptions ManifestOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	public static IReadOnlyList<CompressionLanguagePack> LoadAll()
	{
		var assembly = typeof(CompressionLanguagePack).Assembly;
		const string prefix = "DevProjex.Infrastructure.Compression.Languages.";
		var packs = new List<CompressionLanguagePack>();

		foreach (var resource in assembly.GetManifestResourceNames())
		{
			if (!resource.StartsWith(prefix, StringComparison.Ordinal) ||
			    !resource.EndsWith(".language.json", StringComparison.Ordinal))
			{
				continue;
			}

			var directory = resource[..^"language.json".Length];
			var manifest = JsonSerializer.Deserialize<Manifest>(ReadText(assembly, resource), ManifestOptions)
				?? throw new InvalidOperationException($"Language manifest '{resource}' is empty.");

			packs.Add(new CompressionLanguagePack(
				manifest.Id,
				manifest.DisplayName,
				manifest.Extensions,
				manifest.Library,
				manifest.Export,
				manifest.QueryVersion,
				manifest.BlockPlaceholder,
				manifest.ContainerNodeTypes.ToHashSet(StringComparer.Ordinal),
				manifest.ExecutableOwnerKinds.ToHashSet(StringComparer.Ordinal),
				ReadText(assembly, directory + "bodies.scm"),
				ReadText(assembly, directory + "declarations.scm"),
				TryReadText(assembly, directory + "docstrings.scm"),
				TryReadText(assembly, directory + "preserve.scm")));
		}

		return packs.OrderBy(static pack => pack.Id, StringComparer.Ordinal).ToArray();
	}

	private static string ReadText(Assembly assembly, string resource) =>
		TryReadText(assembly, resource)
			?? throw new InvalidOperationException($"Language pack resource '{resource}' is missing from the assembly.");

	private static string? TryReadText(Assembly assembly, string resource)
	{
		using var stream = assembly.GetManifestResourceStream(resource);
		if (stream is null)
			return null;
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return reader.ReadToEnd();
	}
}
