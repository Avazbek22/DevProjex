using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed class TreeAndContentExportService(
	TreeExportService treeExport,
	SelectedContentExportService contentExport)
{
	private const string ClipboardBlankLine = "\u00A0"; // NBSP: looks empty but won't collapse on paste
	private const int MaximumCachedRelativePathMappers = 32;
	private static readonly IReadOnlySet<string> EmptySelection = new HashSet<string>(PathComparer.Default);
	private static readonly object RelativePathMapperSync = new();
	private static readonly Dictionary<string, RelativePathMapperCacheEntry> RelativePathMappers =
		new(PathComparer.Default);
	private static readonly LinkedList<string> RelativePathMapperLru = new();

	public string Build(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths)
		=> Build(rootPath, root, selectedPaths, TreeTextFormat.Ascii);

	public string Build(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format)
		=> Build(rootPath, root, selectedPaths, format, pathPresentation: null);

	public string Build(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		ExportPathPresentation? pathPresentation)
		=> BuildAsync(rootPath, root, selectedPaths, format, CancellationToken.None, pathPresentation).GetAwaiter().GetResult();

	public async Task<string> BuildAsync(string rootPath, TreeNodeDescriptor root, IReadOnlySet<string> selectedPaths, CancellationToken cancellationToken)
		=> await BuildAsync(rootPath, root, selectedPaths, TreeTextFormat.Ascii, cancellationToken).ConfigureAwait(false);

	public async Task<string> BuildAsync(
		string rootPath,
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		TreeTextFormat format,
		CancellationToken cancellationToken,
		ExportPathPresentation? pathPresentation = null,
		ContentTransformationContext? transformationContext = null,
		OutputPathRedactionDecision? outputPathRedaction = null)
	{
		outputPathRedaction ??= OutputRootPathPresentation.CaptureRedactionDecision(transformationContext);
		var displayRootPath = OutputRootPathPresentation.Resolve(
			rootPath,
			pathPresentation,
			outputPathRedaction);
		var displayRootName = pathPresentation?.DisplayRootName;
		var hasSelection = selectedPaths.Count > 0;
		string tree;
		if (hasSelection)
		{
			tree = treeExport.BuildSelectedTreeWithCancellation(
				rootPath,
				root,
				selectedPaths,
				format,
				displayRootPath,
				displayRootName,
				cancellationToken);
			if (string.IsNullOrWhiteSpace(tree))
			{
				hasSelection = false;
				tree = treeExport.BuildFullTreeWithCancellation(
					rootPath,
					root,
					format,
					displayRootPath,
					displayRootName,
					includeRootPath: true,
					cancellationToken: cancellationToken);
			}
		}
		else
		{
			tree = treeExport.BuildFullTreeWithCancellation(
				rootPath,
				root,
				format,
				displayRootPath,
				displayRootName,
				includeRootPath: true,
				cancellationToken: cancellationToken);
		}

		var files = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePathsWithCancellation(
			root,
			hasSelection ? selectedPaths : EmptySelection,
			ensureExists: hasSelection,
			cancellationToken);
		var contentPathMapper = CreateRelativeContentHeaderPathMapper(rootPath);

		var contentResult = await contentExport.BuildResultAsync(
			files,
			cancellationToken,
			contentPathMapper,
			transformationContext,
			displayRootPath: null,
			outputPathRedaction: outputPathRedaction).ConfigureAwait(false);
		var content = contentResult.Text;
		if (string.IsNullOrWhiteSpace(content))
			return tree;

		// The selected format applies only to the tree block; file content stays plain text.
		return CombineTreeAndContent(tree, content);
	}

	internal static string CombineTreeAndContent(string tree, string content)
	{
		var treeLength = TrailingLineEndingTrimming.GetTrimmedLength(tree);
		var lineEnding = Environment.NewLine;
		var resultLength = checked(treeLength + content.Length + lineEnding.Length * 3 + 2);
		return string.Create(
			resultLength,
			(Tree: tree, TreeLength: treeLength, Content: content, LineEnding: lineEnding),
			static (destination, state) =>
			{
				var offset = 0;
				state.Tree.AsSpan(0, state.TreeLength).CopyTo(destination);
				offset += state.TreeLength;
				state.LineEnding.AsSpan().CopyTo(destination[offset..]);
				offset += state.LineEnding.Length;
				destination[offset++] = ClipboardBlankLine[0];
				state.LineEnding.AsSpan().CopyTo(destination[offset..]);
				offset += state.LineEnding.Length;
				destination[offset++] = ClipboardBlankLine[0];
				state.LineEnding.AsSpan().CopyTo(destination[offset..]);
				offset += state.LineEnding.Length;
				state.Content.AsSpan().CopyTo(destination[offset..]);
			});
	}

	public static Func<string, string> CreateRelativeContentHeaderPathMapper(string rootPath)
	{
		string? normalizedRootPath;
		try
		{
			// The mapper runs once per exported file. Resolve the invariant root once instead
			// of repeating Path.GetFullPath throughout large preview and metrics traversals.
			normalizedRootPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			normalizedRootPath = null;
		}

		var cacheKey = normalizedRootPath ?? rootPath;
		lock (RelativePathMapperSync)
		{
			if (RelativePathMappers.TryGetValue(cacheKey, out var cached))
			{
				RelativePathMapperLru.Remove(cached.Node);
				RelativePathMapperLru.AddFirst(cached.Node);
				return cached.Mapper;
			}

			Func<string, string> mapper = filePath =>
				MapRelativeContentHeaderPathFromNormalizedRoot(normalizedRootPath, filePath);
			var node = RelativePathMapperLru.AddFirst(cacheKey);
			RelativePathMappers.Add(cacheKey, new RelativePathMapperCacheEntry(mapper, node));
			if (RelativePathMappers.Count > MaximumCachedRelativePathMappers &&
			    RelativePathMapperLru.Last is { } oldest)
			{
				RelativePathMappers.Remove(oldest.Value);
				RelativePathMapperLru.RemoveLast();
			}
			return mapper;
		}
	}

	public static string MapRelativeContentHeaderPath(string rootPath, string filePath)
	{
		string? normalizedRootPath;
		try
		{
			normalizedRootPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			normalizedRootPath = null;
		}

		return MapRelativeContentHeaderPathFromNormalizedRoot(normalizedRootPath, filePath);
	}

	private static string MapRelativeContentHeaderPathFromNormalizedRoot(
		string? normalizedRootPath,
		string filePath)
	{
		try
		{
			if (normalizedRootPath is null)
				return GetFallbackContentHeaderPath(filePath);

			var relativePath = Path.GetRelativePath(normalizedRootPath, filePath);
			if (!string.IsNullOrEmpty(relativePath) &&
			    relativePath != "." &&
			    !PathUtility.IsRelativePathOutsideRoot(relativePath) &&
			    !Path.IsPathRooted(relativePath))
			{
				return PathUtility.NormalizeSeparators(relativePath);
			}
		}
		catch
		{
			// Tree + Content already carries the root path in the tree block, so file
			// sections should stay short and portable even when relative path calculation fails.
		}

		return GetFallbackContentHeaderPath(filePath);
	}

	private static string GetFallbackContentHeaderPath(string filePath)
	{
		var fileName = Path.GetFileName(filePath);
		return string.IsNullOrEmpty(fileName) ? PathUtility.NormalizeSeparators(filePath) : fileName;
	}

	private sealed record RelativePathMapperCacheEntry(
		Func<string, string> Mapper,
		LinkedListNode<string> Node);
}
