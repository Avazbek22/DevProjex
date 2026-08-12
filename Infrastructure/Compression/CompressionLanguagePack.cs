using DevProjex.Application.Compression;

namespace DevProjex.Infrastructure.Compression;

internal enum ExpressionBodyStyle
{
	None,
	Inline,
	Declaration
}

internal enum BlockBodyStyle
{
	Inline,
	IndentedStatement,
	RemoveCompleteLines
}

/// <summary>
/// One language, expressed as data. Adding a language is a manifest, a set of .scm queries and
/// fixtures — no C# changes for the common case. Language-specific syntax exceptions are explicit
/// manifest capabilities rather than language-name checks in the compressor.
/// </summary>
internal sealed record CompressionLanguagePack(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Extensions,
	string Library,
	string Export,
	int QueryVersion,
	CodeTransformKinds TransformCapabilities,
	string BlockPlaceholder,
	BlockBodyStyle BlockBodyStyle,
	bool PreserveLeadingDocstring,
	ExpressionBodyStyle ExpressionBodyStyle,
	IReadOnlySet<string> ContainerNodeTypes,
	IReadOnlySet<string> ExecutableOwnerKinds,
	string? BodiesQuery,
	string DeclarationsQuery,
	string? PreservesQuery,
	string? CommentsQuery)
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
		string[]? TransformCapabilities,
		string BlockPlaceholder,
		string? BlockBodyStyle,
		bool PreserveLeadingDocstring,
		string? ExpressionBodyStyle,
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
			var capabilities = ParseTransformCapabilities(manifest.TransformCapabilities, resource);
			var bodiesQuery = TryReadText(assembly, directory + "bodies.scm");
			var commentsQuery = TryReadText(assembly, directory + "comments.scm");
			if ((capabilities & CodeTransformKinds.Bodies) != 0 && bodiesQuery is null)
				throw new InvalidOperationException($"Body-capable language pack '{resource}' has no bodies.scm.");
			if ((capabilities & CodeTransformKinds.Comments) != 0 && commentsQuery is null)
				throw new InvalidOperationException($"Comment-capable language pack '{resource}' has no comments.scm.");

			packs.Add(new CompressionLanguagePack(
				manifest.Id,
				manifest.DisplayName,
				manifest.Extensions,
				manifest.Library,
				manifest.Export,
				manifest.QueryVersion,
				capabilities,
				manifest.BlockPlaceholder,
				ParseBlockBodyStyle(manifest.BlockBodyStyle, resource),
				manifest.PreserveLeadingDocstring,
				ParseExpressionBodyStyle(manifest.ExpressionBodyStyle, resource),
				manifest.ContainerNodeTypes.ToHashSet(StringComparer.Ordinal),
				manifest.ExecutableOwnerKinds.ToHashSet(StringComparer.Ordinal),
				bodiesQuery,
				ReadText(assembly, directory + "declarations.scm"),
				TryReadText(assembly, directory + "preserve.scm"),
				commentsQuery));
		}

		return packs.OrderBy(static pack => pack.Id, StringComparer.Ordinal).ToArray();
	}

	private static CodeTransformKinds ParseTransformCapabilities(
		IReadOnlyList<string>? values,
		string resource)
	{
		if (values is null)
			return CodeTransformKinds.Bodies | CodeTransformKinds.Comments;
		if (values.Count == 0)
			throw new InvalidOperationException($"Language manifest '{resource}' has no transform capabilities.");

		var capabilities = CodeTransformKinds.None;
		foreach (var value in values)
		{
			capabilities |= value switch
			{
				"bodies" => CodeTransformKinds.Bodies,
				"comments" => CodeTransformKinds.Comments,
				"blankLines" => CodeTransformKinds.BlankLines,
				_ => throw new InvalidOperationException(
					$"Language manifest '{resource}' has unsupported transform capability '{value}'.")
			};
		}

		return capabilities;
	}

	private static BlockBodyStyle ParseBlockBodyStyle(string? value, string resource) =>
		value switch
		{
			null or "" or "inline" => BlockBodyStyle.Inline,
			"indented-statement" => BlockBodyStyle.IndentedStatement,
			"remove-complete-lines" => BlockBodyStyle.RemoveCompleteLines,
			_ => throw new InvalidOperationException(
				$"Language manifest '{resource}' has unsupported blockBodyStyle '{value}'.")
		};

	private static ExpressionBodyStyle ParseExpressionBodyStyle(string? value, string resource) =>
		value switch
		{
			null or "" => ExpressionBodyStyle.None,
			"inline" => ExpressionBodyStyle.Inline,
			"declaration" => ExpressionBodyStyle.Declaration,
			_ => throw new InvalidOperationException(
				$"Language manifest '{resource}' has unsupported expressionBodyStyle '{value}'.")
		};

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
