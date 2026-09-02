using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DevProjex.Application.Context;
using DevProjex.Application.Selection;

namespace DevProjex.Tests.Unit;

public sealed class ProjectContextProjectionPerformanceTests(ITestOutputHelper output)
{
	private static readonly byte[] FingerprintSeparator = [0];

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void CompleteTreeProjectionBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		var root = CreateTree(directoryCount: 100, filesPerDirectory: 999);
		var selection = new HashSet<string>(PathComparer.Default);
		var knownFilePaths = EnumerateNodes(root)
			.Where(static node => !node.IsDirectory)
			.Select(static node => node.FullPath)
			.Reverse()
			.ToArray();
		var fingerprintSelection = ProjectSelectionSpec.Standard with
		{
			Roots = ["src"],
			Extensions = [".cs"],
			HideSecrets = true
		};
		_ = BuildLegacyProjection(root, selection, fingerprintSelection);
		_ = BuildOptimizedProjection(root, selection, knownFilePaths, fingerprintSelection);

		var legacyAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var legacyStopwatch = Stopwatch.StartNew();
		var legacy = BuildLegacyProjection(root, selection, fingerprintSelection);
		legacyStopwatch.Stop();
		var legacyAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - legacyAllocatedBefore;

		var optimizedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var optimizedStopwatch = Stopwatch.StartNew();
		var optimized = BuildOptimizedProjection(root, selection, knownFilePaths, fingerprintSelection);
		optimizedStopwatch.Stop();
		var optimizedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - optimizedAllocatedBefore;

		Assert.Same(root, legacy.ProjectedTree);
		Assert.Same(root, optimized.ProjectedTree);
		Assert.Equal(legacy.IncludedFiles, optimized.IncludedFiles);
		Assert.Equal(legacy.IncludedFolders, optimized.IncludedFolders);
		Assert.Equal(legacy.Fingerprint, optimized.Fingerprint);
		Assert.True(
			optimizedAllocatedBytes < legacyAllocatedBytes,
			$"Optimized selection allocated {optimizedAllocatedBytes:N0} bytes versus " +
			$"{legacyAllocatedBytes:N0} legacy bytes.");
		output.WriteLine(
			$"Legacy complete selection: {legacyStopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{legacyAllocatedBytes:N0} B for {knownFilePaths.Length:N0} files.");
		output.WriteLine(
			$"Optimized complete selection: {optimizedStopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{optimizedAllocatedBytes:N0} B for {knownFilePaths.Length:N0} files.");
	}

	[Fact]
	public void ResolveSelectionProjection_WholeTreeReusesRootAndCanonicalizesKnownFiles()
	{
		var root = CreateTree(directoryCount: 3, filesPerDirectory: 4);
		var knownFilePaths = EnumerateNodes(root)
			.Where(static node => !node.IsDirectory)
			.Select(static node => node.FullPath)
			.Reverse()
			.Append(root.Children[0].Children[0].FullPath)
			.ToArray();

		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			root,
			new HashSet<string>(PathComparer.Default),
			selectsNoEffectivePaths: false,
			knownFilePaths,
			TestContext.Current.CancellationToken);

		Assert.Same(root, projection.ProjectedTree);
		Assert.Equal(
			knownFilePaths.Distinct(PathComparer.Default).OrderBy(static path => path, PathComparer.Default),
			projection.IncludedFiles);
		Assert.Equal(
			EnumerateNodes(root)
				.Where(static node => node.IsDirectory)
				.Select(static node => node.FullPath)
				.OrderBy(static path => path, PathComparer.Default),
			projection.IncludedFolders);
	}

	[Fact]
	public void ResolveSelectionProjection_PreservesEntriesThatDifferOnlyByCase()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "dpx-case-plan");
		var upperPath = Path.Combine(rootPath, "Foo.cs");
		var lowerPath = Path.Combine(rootPath, "foo.cs");
		var root = new TreeNodeDescriptor(
			"dpx-case-plan",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			[
				new TreeNodeDescriptor("Foo.cs", upperPath, false, false, "csharp", []),
				new TreeNodeDescriptor("foo.cs", lowerPath, false, false, "csharp", [])
			]);

		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			root,
			new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer),
			selectsNoEffectivePaths: false,
			knownFullTreeFilePaths: [lowerPath, upperPath],
			TestContext.Current.CancellationToken);

		Assert.Equal([upperPath, lowerPath], projection.IncludedFiles);
		Assert.Equal(2, projection.ProjectedTree.Children.Count);
	}

	[Fact]
	public void ResolveSelectedPaths_PrefersExactCaseAndRejectsAmbiguousPlatformAlias()
	{
		var rootPath = Path.Combine(Path.GetTempPath(), "dpx-case-selection");
		var upperPath = Path.Combine(rootPath, "Foo.cs");
		var lowerPath = Path.Combine(rootPath, "foo.cs");
		var root = new TreeNodeDescriptor(
			"dpx-case-selection",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			[
				new TreeNodeDescriptor("Foo.cs", upperPath, false, false, "csharp", []),
				new TreeNodeDescriptor("foo.cs", lowerPath, false, false, "csharp", [])
			]);
		var diagnostics = new List<ContextDiagnostic>();

		var exact = ProjectContextPlanner.ResolveSelectedPaths(
			root,
			rootPath,
			["Foo.cs", "foo.cs"],
			diagnostics,
			TestContext.Current.CancellationToken,
			out var exactHadMatch);

		Assert.True(exactHadMatch);
		Assert.Equal(2, exact.Count);
		Assert.Contains(upperPath, exact, ProjectTreePathIdentity.CanonicalComparer);
		Assert.Contains(lowerPath, exact, ProjectTreePathIdentity.CanonicalComparer);
		Assert.Empty(diagnostics);

		if (!OperatingSystem.IsWindows())
			return;

		var ambiguousDiagnostics = new List<ContextDiagnostic>();
		var ambiguous = ProjectContextPlanner.ResolveSelectedPaths(
			root,
			rootPath,
			["fOo.cs"],
			ambiguousDiagnostics,
			TestContext.Current.CancellationToken,
			out var ambiguousHadMatch);

		Assert.False(ambiguousHadMatch);
		Assert.Empty(ambiguous);
		Assert.Equal("DPX-SELECTION-PATH-MISSING", Assert.Single(ambiguousDiagnostics).Code);

		var uniqueRoot = root with { Children = [root.Children[0]] };
		var alias = ProjectContextPlanner.ResolveSelectedPaths(
			uniqueRoot,
			rootPath,
			["fOo.cs"],
			[],
			TestContext.Current.CancellationToken,
			out var aliasHadMatch);

		Assert.True(aliasHadMatch);
		Assert.Equal(upperPath, Assert.Single(alias), ProjectTreePathIdentity.CanonicalComparer);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OrderedPathFingerprint_MatchesLegacyNodeFingerprint(bool sparseSelection)
	{
		var root = CreateTree(directoryCount: 3, filesPerDirectory: 4);
		var selectedPaths = sparseSelection
			? new HashSet<string>([root.Children[1].FullPath], PathComparer.Default)
			: new HashSet<string>(PathComparer.Default);
		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodes(root, selectedPaths);
		var includedFiles = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
			root,
			selectedPaths,
			ensureExists: false);
		var folderPaths = new List<string>();
		foreach (var node in includedNodes)
		{
			if (node.IsDirectory)
				folderPaths.Add(node.FullPath);
		}
		folderPaths.Sort(PathComparer.Default);
		var includedFolders = folderPaths.ToArray();
		var selection = ProjectSelectionSpec.Standard with
		{
			Roots = ["src"],
			Extensions = [".cs"],
			HideSecrets = true,
			HidePrivateData = true,
			StripComments = true
		};

		var expected = BuildLegacyFingerprint(root.FullPath, selection, includedNodes);
		var actual = ProjectContextPlanner.BuildFingerprint(
			root.FullPath,
			selection,
			includedFiles,
			includedFolders,
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void OrderedPathFingerprint_DistinguishesDiffRanges()
	{
		var root = CreateTree(directoryCount: 1, filesPerDirectory: 1);
		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			root,
			new HashSet<string>(PathComparer.Default),
			selectsNoEffectivePaths: false,
			knownFullTreeFilePaths: null,
			TestContext.Current.CancellationToken);
		var selection = ProjectSelectionSpec.Standard with { GitMode = GitFilteringMode.Diff };

		var first = ProjectContextPlanner.BuildFingerprint(
			root.FullPath,
			selection with { GitDiffRange = "main..feature-a" },
			projection.IncludedFiles,
			projection.IncludedFolders,
			TestContext.Current.CancellationToken);
		var second = ProjectContextPlanner.BuildFingerprint(
			root.FullPath,
			selection with { GitDiffRange = "main..feature-b" },
			projection.IncludedFiles,
			projection.IncludedFolders,
			TestContext.Current.CancellationToken);

		Assert.NotEqual(first, second);
	}

	[Fact]
	public void ResolveProjectedTree_DistinguishesCompleteAndMissingExplicitSelections()
	{
		var root = CreateTree(directoryCount: 1, filesPerDirectory: 1);
		var fullSelection = new HashSet<string>(PathComparer.Default);
		var included = ProjectTreeSelectionProjection.BuildIncludedNodes(root, fullSelection);

		var complete = ProjectContextPlanner.ResolveProjectedTree(
			root,
			fullSelection,
			included,
			selectsNoEffectivePaths: false,
			TestContext.Current.CancellationToken);
		var missing = ProjectContextPlanner.ResolveProjectedTree(
			root,
			fullSelection,
			[],
			selectsNoEffectivePaths: true,
			TestContext.Current.CancellationToken);

		Assert.Same(root, complete);
		Assert.Empty(missing.Children);
		Assert.NotSame(root, missing);
	}

	private static TreeNodeDescriptor CreateTree(int directoryCount, int filesPerDirectory)
	{
		const string rootPath = "/benchmark/project";
		var directories = new List<TreeNodeDescriptor>(directoryCount);
		for (var directoryIndex = 0; directoryIndex < directoryCount; directoryIndex++)
		{
			var directoryPath = $"{rootPath}/dir-{directoryIndex:D3}";
			var files = new List<TreeNodeDescriptor>(filesPerDirectory);
			for (var fileIndex = 0; fileIndex < filesPerDirectory; fileIndex++)
			{
				files.Add(new TreeNodeDescriptor(
					$"file-{fileIndex:D4}.cs",
					$"{directoryPath}/file-{fileIndex:D4}.cs",
					IsDirectory: false,
					IsAccessDenied: false,
					"csharp",
					[]));
			}

			directories.Add(new TreeNodeDescriptor(
				$"dir-{directoryIndex:D3}",
				directoryPath,
				IsDirectory: true,
				IsAccessDenied: false,
				"folder",
				files));
		}

		return new TreeNodeDescriptor(
			"project",
			rootPath,
			IsDirectory: true,
			IsAccessDenied: false,
			"folder",
			directories);
	}

	private static IEnumerable<TreeNodeDescriptor> EnumerateNodes(TreeNodeDescriptor root)
	{
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			yield return node;
			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}
	}

	private static (
		TreeNodeDescriptor ProjectedTree,
		IReadOnlyList<string> IncludedFiles,
		IReadOnlyList<string> IncludedFolders,
		string Fingerprint) BuildLegacyProjection(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		ProjectSelectionSpec selection)
	{
		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodes(root, selectedPaths);
		var projectedTree = ProjectContextPlanner.ResolveProjectedTree(
			root,
			selectedPaths,
			includedNodes,
			selectsNoEffectivePaths: false,
			CancellationToken.None);
		var includedFiles = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
			root,
			selectedPaths,
			ensureExists: false);
		var folderPaths = new List<string>();
		foreach (var node in includedNodes)
		{
			if (node.IsDirectory)
				folderPaths.Add(node.FullPath);
		}
		folderPaths.Sort(PathComparer.Default);
		var includedFolders = folderPaths.ToArray();
		return (
			projectedTree,
			includedFiles,
			includedFolders,
			BuildLegacyFingerprint(root.FullPath, selection, includedNodes));
	}

	private static (
		TreeNodeDescriptor ProjectedTree,
		IReadOnlyList<string> IncludedFiles,
		IReadOnlyList<string> IncludedFolders,
		string Fingerprint) BuildOptimizedProjection(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		IReadOnlyList<string> knownFilePaths,
		ProjectSelectionSpec selection)
	{
		var projection = ProjectContextPlanner.ResolveSelectionProjection(
			root,
			selectedPaths,
			selectsNoEffectivePaths: false,
			knownFilePaths,
			CancellationToken.None);
		return (
			projection.ProjectedTree,
			projection.IncludedFiles,
			projection.IncludedFolders,
			ProjectContextPlanner.BuildFingerprint(
				root.FullPath,
				selection,
				projection.IncludedFiles,
				projection.IncludedFolders));
	}

	private static string BuildLegacyFingerprint(
		string sourceRoot,
		ProjectSelectionSpec selection,
		IReadOnlyList<TreeNodeDescriptor> includedNodes)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(selection.GitMode!.Value.ToString());
		foreach (var exclusion in selection.Exclusions!.OrderBy(static value => value))
			Append(exclusion.ToString());
		Append($"hide-secrets:{selection.HideSecrets == true}");
		Append($"hide-private-data:{selection.HidePrivateData == true}");
		Append($"compress-code:{selection.CompressCode == true}");
		Append($"strip-comments:{selection.StripComments == true}");
		Append($"strip-blank-lines:{selection.StripBlankLines == true}");
		foreach (var selectedRoot in selection.Roots ?? [])
			Append("r:" + selectedRoot);
		foreach (var extension in selection.Extensions ?? [])
			Append("e:" + extension);
		var orderedNodes = new List<TreeNodeDescriptor>(includedNodes);
		orderedNodes.Sort(static (left, right) => PathComparer.Default.Compare(left.FullPath, right.FullPath));
		foreach (var node in orderedNodes)
		{
			var relativePath = PathUtility.NormalizeSeparators(
				Path.GetRelativePath(sourceRoot, node.FullPath));
			Append((node.IsDirectory ? "d:" : "f:") + relativePath);
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

		void Append(string value)
		{
			var byteCount = Encoding.UTF8.GetByteCount(value);
			if (byteCount <= 512)
			{
				Span<byte> bytes = stackalloc byte[byteCount];
				Encoding.UTF8.GetBytes(value, bytes);
				hash.AppendData(bytes);
			}
			else
			{
				var rented = ArrayPool<byte>.Shared.Rent(byteCount);
				try
				{
					var written = Encoding.UTF8.GetBytes(value, rented);
					hash.AppendData(rented.AsSpan(0, written));
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(rented, clearArray: true);
				}
			}

			hash.AppendData(FingerprintSeparator);
		}
	}
}
