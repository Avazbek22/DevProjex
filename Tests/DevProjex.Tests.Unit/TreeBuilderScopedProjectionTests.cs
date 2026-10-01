using System.Diagnostics;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class TreeBuilderScopedProjectionTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData(true, false, false, false)]
	[InlineData(true, true, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(true, true, true, false)]
	[InlineData(false, false, false, false)]
	[InlineData(true, false, false, true)]
	public void InventoryProjection_MatchesGlobalContextAcrossRepositoryAndRuleBoundaries(
		bool useGitIgnore,
		bool trackedOnly,
		bool reverseScopes,
		bool filterNames)
	{
		var root = Path.Combine(Path.GetTempPath(), "DevProjexScopedProjection", "workspace");
		var scopes = new[]
		{
			Matcher(Path.GetDirectoryName(root)!, "", ["ancestor-drop.cs"]),
			Matcher(root, "", ["*.cache", "src/blocked/"]),
			Matcher(root, "src", ["secret.cs"]),
			Matcher(root, "other", ["other-drop.cs"]),
			Matcher(root, "opaque", [], repositoryBoundary: true, opaque: true),
			Matcher(root, "missing", [], repositoryBoundary: true),
			Matcher(root, "vendored", ["*.cs"], repositoryBoundary: true),
			Matcher(root, "src/nested", ["!keep.cache"]),
			Matcher(root, "src/blocked", ["!keep.cs"])
		}.OrderBy(static scope => scope.ScopeRootPath.Length).ToArray();
		if (reverseScopes)
			Array.Reverse(scopes);
		var trackedIndexes = new[]
		{
			new GitTrackedPathIndex(root, ["src/drop.cache", "missing/hidden.cache", "vendored/drop.cs"]),
			new GitTrackedPathIndex(Path.Combine(root, "vendored"), ["keep.cache"]),
			GitTrackedPathIndex.Unavailable(Path.Combine(root, "missing"))
		};
		var inventory = CreateInventory(root,
		[
			"rootfile.cs", "src/keep.cs", "src/drop.cache", "src/ancestor-drop.cs",
			"src/blocked/drop.cs", "src/blocked/keep.cs", "src/nested/keep.cache",
			"src/nested/drop.cache", "src/nested/secret.cs", "other/drop.cache",
			"other/keep.cs", "other/other-drop.cs", "vendored/keep.cache", "vendored/drop.cs",
			"opaque/hidden.cs", "missing/hidden.cache", ".git/config", "dot/.env"
		], scopes, trackedIndexes);
		var options = Options(inventory, useGitIgnore, trackedOnly) with
		{
			NameFilter = filterNames ? "keep" : null
		};

		foreach (var preloadScopes in new[] { false, true })
		{
			var projectionOptions = options with
			{
				IgnoreRules = options.IgnoreRules with { ScopedGitIgnoreMatchers = preloadScopes ? scopes : [] }
			};
			var expected = BuildWithGlobalContext(inventory, projectionOptions);
			var actual = new TreeBuilder().Build(inventory, projectionOptions, TestContext.Current.CancellationToken);

			Assert.Equal(Flatten(expected.Root), Flatten(actual.Root));
			Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
			Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
			Assert.Equal(expected.HadScanFailure, actual.HadScanFailure);
		}
	}

	[Fact]
	public void InventoryProjection_NoncanonicalScopeIdentityPreservesGlobalPathSemantics()
	{
		var root = Path.Combine(Path.GetTempPath(), "DevProjexScopedProjection", "aliases");
		var nestedScope = Matcher(root, "src/nested", ["!keep.cache"]);
		var scopes = new[]
		{
			Matcher(root, "", ["*.cache"]),
			Matcher(root, "src", ["*.cs"]),
			nestedScope with { ScopeRootPath = nestedScope.ScopeRootPath + Path.DirectorySeparatorChar }
		};
		var inventory = CreateInventory(root,
			["src/keep.cs", "src/drop.cache", "src/nested/keep.cache", "other/keep.cs"], scopes, []);
		var options = Options(inventory, useGitIgnore: true, trackedOnly: false);

		Assert.Equal(Flatten(BuildWithGlobalContext(inventory, options).Root),
			Flatten(new TreeBuilder().Build(inventory, options, TestContext.Current.CancellationToken).Root));
	}

	[Theory]
	[InlineData(10_000)]
	[InlineData(100_000)]
	[Trait("Category", "LocalPerformance")]
	public void ManySiblingScopes_ProjectsLargeInventoryWithoutChangingOrdering(int fileCount)
	{
		if (Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS") != "1")
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");

		const int scopeCount = 1_000;
		var root = Path.Combine(Path.GetTempPath(), "DevProjexScopedProjection", "scale");
		var paths = Enumerable.Range(0, fileCount)
			.Select(index => $"scope-{index % scopeCount:D4}/file-{index:D6}.{(index / scopeCount % 2 == 0 ? "cs" : "cache")}")
			.ToArray();
		var scopes = Enumerable.Range(0, scopeCount)
			.Select(index => Matcher(root, $"scope-{index:D4}", ["*.cache"]))
			.ToArray();
		var inventory = CreateInventory(root, paths, scopes, []);
		var options = Options(inventory, useGitIgnore: true, trackedOnly: false);
		var builder = new TreeBuilder();
		_ = builder.Build(inventory, options, TestContext.Current.CancellationToken);
		var elapsed = new double[3];
		var allocations = new long[3];
		for (var attempt = 0; attempt < elapsed.Length; attempt++)
		{
			var before = GC.GetTotalAllocatedBytes(precise: true);
			var stopwatch = Stopwatch.StartNew();
			var result = builder.Build(inventory, options, TestContext.Current.CancellationToken);
			stopwatch.Stop();
			allocations[attempt] = GC.GetTotalAllocatedBytes(precise: true) - before;
			elapsed[attempt] = stopwatch.Elapsed.TotalMilliseconds;
			Assert.Equal(scopeCount, result.Root.Children.Count);
			Assert.Equal(fileCount / 2, result.Root.Children.Sum(static directory => directory.Children.Count));
			Assert.All(result.Root.Children, directory =>
			{
				Assert.All(directory.Children, file => Assert.EndsWith(".cs", file.Name));
				Assert.Equal(directory.Children.Select(static child => child.Name).Order(StringComparer.Ordinal),
					directory.Children.Select(static child => child.Name));
			});
		}
		Array.Sort(elapsed);
		Array.Sort(allocations);
		output.WriteLine($"Scoped inventory projection: {fileCount:N0} files / {scopeCount:N0} scopes; " +
						 $"median {elapsed[1]:F3} ms, range {elapsed[0]:F3}–{elapsed[2]:F3} ms; " +
						 $"median {allocations[1]:N0} B.");
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void InventoryProjection_CaseDistinctAndDuplicateScopesRetainOriginalPrecedence(
		bool reverseIndexes, bool includeCaseAlias)
	{
		var root = Path.Combine(Path.GetTempPath(), "DevProjexScopedProjection", "identities");
		var scopes = new List<ScopedGitIgnoreMatcher>
		{
			Matcher(root, "Foo", ["*.cache"]),
			Matcher(root, "foo", ["*.cs"]),
			Matcher(root, "Foo", ["keep.cs"], repositoryBoundary: true),
			Matcher(root, "Foo", ["*.cs"])
		};
		if (includeCaseAlias)
			scopes.Add(Matcher(root, "FOO", ["*"]));
		var indexes = new[]
		{
			new GitTrackedPathIndex(root, ["Foo/drop.cache", "foo/keep.cs", "foo/nested/keep.cs"]),
			new GitTrackedPathIndex(Path.Combine(root, "Foo"), ["keep.cs"]),
			new GitTrackedPathIndex(Path.Combine(root, "Foo"), ["drop.cache"]),
			new GitTrackedPathIndex(Path.Combine(root, "foo", "nested"), ["drop.cache"]),
			new GitTrackedPathIndex(Path.Combine(root, "foo"), ["keep.cs", "nested/keep.cs"])
		};
		if (reverseIndexes)
			Array.Reverse(indexes);
		var inventory = CreateInventory(root,
			["Foo/keep.cs", "Foo/drop.cache", "foo/keep.cs", "foo/drop.cache", "foo/nested/keep.cs", "foo/nested/drop.cache"],
			scopes, indexes);
		foreach (var trackedOnly in new[] { false, true })
		{
			var options = Options(inventory, useGitIgnore: true, trackedOnly);
			Assert.Equal(Flatten(BuildWithGlobalContext(inventory, options).Root),
				Flatten(new TreeBuilder().Build(inventory, options, TestContext.Current.CancellationToken).Root));
		}
	}

	private static ScopedGitIgnoreMatcher Matcher(
		string root, string relativePath, string[] patterns, bool repositoryBoundary = false, bool opaque = false)
	{
		var scope = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		return new ScopedGitIgnoreMatcher(scope, GitIgnoreMatcher.Build(scope, patterns))
		{
			IsRepositoryBoundary = repositoryBoundary,
			IsOpaqueRepository = opaque
		};
	}

	private static TreeFilterOptions Options(ProjectTreeInventorySnapshot inventory, bool useGitIgnore, bool trackedOnly) =>
		new(new HashSet<string>([".cs", ".cache"], StringComparer.OrdinalIgnoreCase),
			inventory.GetChildren(0).ToArray().Where(static entry => entry.IsDirectory)
				.Select(static entry => entry.Name).ToHashSet(ProjectTreePathIdentity.CanonicalComparer),
			new IgnoreRules(false, false, false, false, new HashSet<string>(), new HashSet<string>())
			{
				UseGitIgnore = useGitIgnore,
				UseTrackedGitFilesOnly = trackedOnly,
				IgnoreEmptyFolders = true
			});

	private static ProjectTreeInventorySnapshot CreateInventory(
		string root,
		IReadOnlyList<string> paths,
		IReadOnlyList<ScopedGitIgnoreMatcher> scopes,
		IReadOnlyList<GitTrackedPathIndex> trackedIndexes)
	{
		var directories = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal)
		{
			[string.Empty] = new(StringComparer.Ordinal)
		};
		foreach (var path in paths)
		{
			var parent = string.Empty;
			var segments = path.Split('/');
			for (var index = 0; index < segments.Length; index++)
			{
				var child = parent.Length == 0 ? segments[index] : $"{parent}/{segments[index]}";
				directories[parent].Add(child);
				if (index < segments.Length - 1)
					directories.TryAdd(child, new SortedSet<string>(StringComparer.Ordinal));
				parent = child;
			}
		}
		var entries = new List<ProjectTreeInventoryEntry>(paths.Count + directories.Count)
		{
			new(Path.GetFileName(root), root, string.Empty, -1, true, false, 0)
		};
		Populate(string.Empty, 0);
		return new ProjectTreeInventorySnapshot(entries, false, false, scopes, trackedIndexes);

		void Populate(string relativePath, int parentIndex)
		{
			var children = directories[relativePath].OrderByDescending(directories.ContainsKey)
				.ThenBy(static path => path, StringComparer.Ordinal).ToArray();
			var pending = new List<(string Path, int Index)>();
			var parent = entries[parentIndex];
			parent.FirstChildIndex = entries.Count;
			parent.ChildCount = children.Length;
			entries[parentIndex] = parent;
			foreach (var child in children)
			{
				var directory = directories.ContainsKey(child);
				if (directory)
					pending.Add((child, entries.Count));
				entries.Add(new ProjectTreeInventoryEntry(child[(child.LastIndexOf('/') + 1)..],
					Path.Combine(root, child.Replace('/', Path.DirectorySeparatorChar)), child,
					parentIndex, directory, false, directory ? 0 : 1));
			}
			foreach (var directory in pending)
				Populate(directory.Path, directory.Index);
		}
	}

	private static TreeBuildResult BuildWithGlobalContext(ProjectTreeInventorySnapshot inventory, TreeFilterOptions options)
	{
		var root = inventory.GetEntry(0);
		var context = options.IgnoreRules.CreateGitIgnoreScanContext(root.FullPath,
			inventory.DiscoveredGitIgnoreMatchers, inventory.DiscoveredGitTrackedPathIndexes);
		return new TreeBuildResult(Project(0)!, inventory.RootAccessDenied, inventory.HadAccessDenied, inventory.HadScanFailure);

		FileSystemNode? Project(int index)
		{
			var entry = inventory.GetEntry(index);
			var evaluation = context.Evaluate(entry.FullPath, entry.RelativePath, entry.IsDirectory, entry.Name);
			var rules = options.IgnoreRules;
			if (entry.IsDirectory)
			{
				if (index != 0 && (entry.ParentIndex == 0 && !options.AllowedRootFolders.Contains(entry.Name) ||
					IgnoreDecisionEngine.EvaluateDirectory(entry.FullPath, entry.Name, entry.IsHidden, rules, evaluation).IsIgnored))
					return null;
				var children = new List<FileSystemNode>();
				if (!entry.IsAccessDenied)
				{
					for (var offset = 0; offset < entry.ChildCount; offset++)
						if (Project(entry.FirstChildIndex + offset) is { } child)
							children.Add(child);
				}
				if (index != 0 && children.Count == 0 && !entry.IsAccessDenied &&
					(rules.IgnoreEmptyFolders || evaluation.IsIgnored && evaluation.ShouldTraverseIgnoredDirectory))
					return null;
				if (index != 0 && !string.IsNullOrWhiteSpace(options.NameFilter) && children.Count == 0 &&
					!entry.Name.Contains(options.NameFilter, StringComparison.OrdinalIgnoreCase))
					return null;
				return new FileSystemNode(entry.Name, entry.FullPath, true, entry.IsAccessDenied, children);
			}
			if (IgnoreDecisionEngine.EvaluateFile(entry.FullPath, entry.Name, entry.IsHidden, entry.Length,
					rules, rules.ShouldApplySmartIgnore(inventory.GetEntry(entry.ParentIndex).FullPath, true), evaluation).IsIgnored ||
				!IgnoreRuleSemantics.IsExtensionlessFileName(entry.Name) && !options.AllowedExtensions.Contains(Path.GetExtension(entry.Name)) ||
				!string.IsNullOrWhiteSpace(options.NameFilter) && !entry.Name.Contains(options.NameFilter, StringComparison.OrdinalIgnoreCase))
				return null;
			return new FileSystemNode(entry.Name, entry.FullPath, false, false, FileSystemNode.EmptyChildren);
		}
	}

	private static IReadOnlyList<string> Flatten(FileSystemNode root)
	{
		var result = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.TryPop(out var node))
		{
			result.Add($"{node.FullPath}|{node.IsDirectory}|{node.IsAccessDenied}");
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}
		return result;
	}
}
