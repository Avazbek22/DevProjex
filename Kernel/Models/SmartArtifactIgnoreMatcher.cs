using System.Collections.Frozen;
using System.IO.Enumeration;

namespace DevProjex.Kernel.Models;

// Smart artifact ignore is the generic half of the hybrid ignore model. Stack-specific
// smart rules are still preferred when a project marker proves the technology, but real
// workspaces often contain generated folders outside a clean project root: copied build
// artifacts, package caches, temporary publish folders, or dependency stores. This matcher
// catches those cases without requiring a deep "guess the project type" scan.
//
// The contract is deliberately conservative:
// 1. A cheap name/prefix check only marks a directory as suspicious.
// 2. A bounded top-level signature probe must prove generated-tool output.
// 3. Source-looking folders with names like build, vendor, cache, pkg, or Library stay
//    visible unless their own contents contain a strong artifact signature.
//
// Do not replace this with "ignore every known folder name". That would be faster but
// would hide real source trees in large mixed workspaces and home-directory opens.
public sealed class SmartArtifactIgnoreMatcher
{
	private const int MaxEnumeratedSignatureEntries = 1024;
	private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
	private static readonly FrozenSet<string> EmptyNameSet =
		Array.Empty<string>().ToFrozenSet(NameComparer);
	private static readonly FrozenDictionary<string, SmartArtifactDirectoryRule[]> EmptyRuleIndex =
		new Dictionary<string, SmartArtifactDirectoryRule[]>(NameComparer).ToFrozenDictionary(NameComparer);
	private static readonly string[] EmptyNames = [];

	public static SmartArtifactIgnoreMatcher Empty { get; } = new([]);

	public static SmartArtifactIgnoreMatcher Default { get; } = new(
		CreateDefaultRules(),
		CreateDefaultIgnoredFileSuffixes());

	private readonly SmartArtifactDirectoryRule[] _rules;
	private readonly FrozenDictionary<string, SmartArtifactDirectoryRule[]> _exactRulesByName;
	private readonly SmartArtifactDirectoryRule[] _prefixRules;
	private readonly string[] _ignoredFileSuffixes;
	private readonly string[] _ignoredFileTerminalSuffixes;

	public SmartArtifactIgnoreMatcher(
		IEnumerable<SmartArtifactDirectoryRule> rules,
		IEnumerable<string>? ignoredFileSuffixes = null)
	{
		_rules = rules.ToArray();
		_ignoredFileSuffixes = ignoredFileSuffixes?
			.Where(static suffix => !string.IsNullOrWhiteSpace(suffix))
			.Distinct(NameComparer)
			.ToArray() ?? EmptyNames;
		_ignoredFileTerminalSuffixes = _ignoredFileSuffixes
			.Select(static suffix =>
			{
				var extension = Path.GetExtension(suffix);
				return string.IsNullOrEmpty(extension) ? suffix : extension;
			})
			.Distinct(NameComparer)
			.ToArray();
		if (_rules.Length == 0)
		{
			_exactRulesByName = EmptyRuleIndex;
			_prefixRules = [];
			return;
		}

		_exactRulesByName = _rules
			.Where(static rule => rule.MatchKind == SmartArtifactNameMatchKind.Exact)
			.GroupBy(static rule => rule.NamePattern, NameComparer)
			.ToFrozenDictionary(
				static group => group.Key,
				static group => group.ToArray(),
				NameComparer);
		_prefixRules = _rules
			.Where(static rule => rule.MatchKind == SmartArtifactNameMatchKind.Prefix)
			.ToArray();
	}

	public bool HasRules => _rules.Length > 0 || _ignoredFileSuffixes.Length > 0;

	public bool IsCandidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (_exactRulesByName.ContainsKey(name))
			return true;

		foreach (var rule in _prefixRules)
			if (name.StartsWith(rule.NamePattern, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	public bool HasCandidateDirectory(ProjectRootFacts rootFacts)
	{
		if (!rootFacts.Exists || !rootFacts.IsAccessible)
			return false;

		foreach (var directory in rootFacts.Directories)
		{
			if (!directory.IsReparsePoint && IsCandidateName(directory.Name))
				return true;
		}

		return false;
	}

	public bool HasConfirmedArtifactDirectory(ProjectRootFacts rootFacts)
	{
		if (!rootFacts.Exists || !rootFacts.IsAccessible)
			return false;

		foreach (var directory in rootFacts.Directories)
		{
			if (!directory.IsReparsePoint && IsIgnoredDirectory(directory.FullPath, directory.Name))
				return true;
		}

		return false;
	}

	public bool IsIgnoredFile(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		// Most files fail this short terminal check. Full long-suffix comparisons are
		// reserved for the rare matching extension, keeping the scanner hot path cheap.
		var hasMatchingTerminalSuffix = false;
		foreach (var terminalSuffix in _ignoredFileTerminalSuffixes)
		{
			if (!name.EndsWith(terminalSuffix, StringComparison.OrdinalIgnoreCase))
				continue;

			hasMatchingTerminalSuffix = true;
			break;
		}

		if (!hasMatchingTerminalSuffix)
			return false;

		foreach (var suffix in _ignoredFileSuffixes)
			if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	public bool IsIgnoredDirectory(string fullPath, string name)
	{
		if (_rules.Length == 0 || string.IsNullOrWhiteSpace(fullPath) || !IsCandidateName(name))
			return false;

		// This matcher is intentionally two-stage: cheap candidate-name filtering first,
		// then a bounded artifact signature probe only for suspicious directories.
		// Generic names like "build" or "Library" stay visible unless their own
		// contents prove that they are generated tool output.
		return IsIgnoredDirectoryCore(fullPath, name, portableOnly: false);
	}

	public bool IsPortableIgnoredDirectory(string fullPath, string name)
	{
		if (_rules.Length == 0 || string.IsNullOrWhiteSpace(fullPath) || !IsCandidateName(name))
			return false;

		return IsIgnoredDirectoryCore(fullPath, name, portableOnly: true);
	}

	private bool IsIgnoredDirectoryCore(string fullPath, string name, bool portableOnly)
	{
		if (_exactRulesByName.TryGetValue(name, out var exactRules) &&
		    HasMatchingStrongSignature(exactRules, fullPath, portableOnly))
		{
			return true;
		}

		return HasMatchingStrongSignature(_prefixRules, fullPath, portableOnly, name);
	}

	private static bool HasMatchingStrongSignature(
		SmartArtifactDirectoryRule[] rules,
		string fullPath,
		bool portableOnly,
		string? candidateName = null)
	{
		for (var index = 0; index < rules.Length; index++)
		{
			var rule = rules[index];
			if (portableOnly && !rule.ApplyOutsideProjectScopes)
				continue;

			if (candidateName is not null && !rule.MatchesName(candidateName))
				continue;

			if (rule.HasStrongSignature(fullPath))
				return true;
		}

		return false;
	}

	private static SmartArtifactDirectoryRule[] CreateDefaultRules() =>
	[
		// Rules intentionally describe portable artifact signatures rather than project
		// identity. The same directory name can be source in one repo and generated output
		// in another, so every broad name below needs at least one strong local marker.
		SmartArtifactDirectoryRule.Exact(
			"obj",
			files:
			[
				"project.assets.json",
				"project.nuget.cache",
				"project.packagespec.json",
				"rider.project.model.nuget.info",
				"rider.project.restore.info"
			],
			directories: ["Debug", "Release", "ref", "refint"],
			fileSuffixes:
			[
				".csproj.nuget.g.props",
				".csproj.nuget.g.targets",
				".csproj.nuget.dgspec.json",
				".fsproj.nuget.g.props",
				".fsproj.nuget.g.targets",
				".vbproj.nuget.g.props",
				".vbproj.nuget.g.targets",
				".GeneratedMSBuildEditorConfig.editorconfig",
				".AssemblyInfoInputs.cache",
				".CoreCompileInputs.cache",
				".FileListAbsolute.txt",
				".GlobalUsings.g.cs",
				".sourcelink.json",
				".genruntimeconfig.cache"
			]),
		SmartArtifactDirectoryRule.Exact(
			"bin",
			directories: ["Debug", "Release", "x64", "x86", "AnyCPU", "net"],
			fileExtensions: [".dll", ".exe", ".pdb", ".deps.json", ".runtimeconfig.json"]),
		SmartArtifactDirectoryRule.Exact(
			"node_modules",
			files: [".package-lock.json", "package-lock.json"],
			directories: [".bin", "@types", "@babel", "@vite", "@angular"]),
		SmartArtifactDirectoryRule.Exact(
			"bower_components",
			directories: [".bin"],
			childFiles: ["bower.json", "package.json"]),
		SmartArtifactDirectoryRule.Exact(
			"jspm_packages",
			files: ["package.json"],
			directories: ["npm", "github"]),
		SmartArtifactDirectoryRule.Exact(
			"packages",
			files: ["repositories.config"],
			pathSuffixSegments: [".nuget", "packages"],
			repeatedChildSignature: new RepeatedChildArtifactSignature(
				selfNamedFileSuffix: ".nupkg",
				layoutDirectories:
				[
					"lib",
					"ref",
					"runtimes",
					"tools",
					"analyzers",
					"build",
					"content",
					"contentFiles"
				],
				minimumMatches: 2,
				maxEntries: 48),
			applyOutsideProjectScopes: true),
		SmartArtifactDirectoryRule.Exact(
			"repository",
			pathSuffixSegments: [".m2", "repository"],
			applyOutsideProjectScopes: true),
		SmartArtifactDirectoryRule.Exact(
			"registry",
			pathSuffixSegments: [".cargo", "registry"],
			applyOutsideProjectScopes: true),
		SmartArtifactDirectoryRule.Exact(
			"_cacache",
			directories: ["content-v2", "index-v5"],
			applyOutsideProjectScopes: true),
		SmartArtifactDirectoryRule.Exact(
			"modules-2",
			directories: ["files-2.1"],
			applyOutsideProjectScopes: true),
		SmartArtifactDirectoryRule.Exact(
			"__pycache__",
			fileExtensions: [".pyc", ".pyo"]),
		SmartArtifactDirectoryRule.Exact(
			".venv",
			files: ["pyvenv.cfg"]),
		SmartArtifactDirectoryRule.Exact(
			"venv",
			files: ["pyvenv.cfg"]),
		SmartArtifactDirectoryRule.Exact(
			"env",
			files: ["pyvenv.cfg"]),
		SmartArtifactDirectoryRule.Exact(
			".pytest_cache",
			files: ["CACHEDIR.TAG", "README.md"],
			directories: ["v", "d"]),
		SmartArtifactDirectoryRule.Exact(
			".mypy_cache",
			files: ["CACHEDIR.TAG"],
			directories: ["3.10", "3.11", "3.12", "3.13"]),
		SmartArtifactDirectoryRule.Exact(
			".ruff_cache",
			files: ["CACHEDIR.TAG"],
			directories: ["content", "0.1", "0.2", "0.3", "0.4", "0.5", "0.6"]),
		SmartArtifactDirectoryRule.Exact(
			".tox",
			files: ["CACHEDIR.TAG"],
			directories: ["py", "py310", "py311", "py312"]),
		SmartArtifactDirectoryRule.Exact(
			".nox",
			directories: ["py", "tests"]),
		SmartArtifactDirectoryRule.Exact(
			".hypothesis",
			files: ["CACHEDIR.TAG"],
			directories: ["examples"]),
		SmartArtifactDirectoryRule.Exact(
			".ipynb_checkpoints",
			fileExtensions: [".ipynb"]),
		SmartArtifactDirectoryRule.Exact(
			".pyre",
			directories: ["cache"]),
		SmartArtifactDirectoryRule.Exact(
			".gradle",
			directories: ["caches", "daemon", "wrapper", "buildOutputCleanup", "vcs-1"]),
		SmartArtifactDirectoryRule.Exact(
			"target",
			directories:
			[
				"debug",
				"release",
				"deps",
				"classes",
				"generated-sources",
				"generated-test-sources",
				"maven-status",
				"surefire-reports"
			],
			files: [".rustc_info.json"]),
		SmartArtifactDirectoryRule.Exact(
			"build",
			files: ["build.ninja", "compile_commands.json", "CMakeCache.txt"],
			directories:
			[
				"CMakeFiles",
				"classes",
				"generated",
				"intermediates",
				"kotlin",
				"libs",
				"outputs",
				"reports",
				"tmp"
			]),
		SmartArtifactDirectoryRule.Exact(
			"dist",
			files: ["stats.json", "manifest.json", "asset-manifest.json"],
			directories: ["assets", "static", "_next"]),
		SmartArtifactDirectoryRule.Exact(
			"out",
			files: ["build.ninja", "compile_commands.json"],
			directories: ["production", "test", "classes", "artifacts"]),
		SmartArtifactDirectoryRule.Exact(
			"coverage",
			files: ["lcov.info", "coverage-final.json", "clover.xml", "cobertura-coverage.xml", "index.html"]),
		SmartArtifactDirectoryRule.Exact(
			".next",
			files: ["BUILD_ID", "routes-manifest.json", "build-manifest.json"],
			directories: ["cache", "server", "static"]),
		SmartArtifactDirectoryRule.Exact(
			".nuxt",
			files: ["nuxt.json", "tsconfig.json"],
			directories: ["dist", "types"]),
		SmartArtifactDirectoryRule.Exact(
			".turbo",
			files: ["cookies", "daemon"],
			directories: ["cache", "runs"]),
		SmartArtifactDirectoryRule.Exact(
			".vite",
			directories: ["deps", "deps_temp"]),
		SmartArtifactDirectoryRule.Exact(
			".parcel-cache",
			directories: ["data", "fs", "lmdb"]),
		SmartArtifactDirectoryRule.Exact(
			".svelte-kit",
			files: ["tsconfig.json"],
			directories: ["generated", "output", "types"]),
		SmartArtifactDirectoryRule.Exact(
			".angular",
			directories: ["cache"]),
		SmartArtifactDirectoryRule.Exact(
			".astro",
			files: ["types.d.ts"],
			directories: ["content-assets.mjs"]),
		SmartArtifactDirectoryRule.Exact(
			".output",
			files: ["nitro.json"],
			directories: ["public", "server"]),
		SmartArtifactDirectoryRule.Exact(
			"storybook-static",
			files: ["index.html"],
			directories: ["assets"]),
		SmartArtifactDirectoryRule.Exact(
			".nyc_output",
			files: ["processinfo"],
			fileExtensions: [".json"]),
		SmartArtifactDirectoryRule.Exact(
			"htmlcov",
			files: ["index.html", "status.json"]),
		SmartArtifactDirectoryRule.Exact(
			".dart_tool",
			files: ["package_config.json", "version"],
			directories: ["build", "flutter_build", "pub"]),
		SmartArtifactDirectoryRule.Exact(
			".build",
			files: ["workspace-state.json"],
			directories: ["checkouts", "debug", "release", "repositories", "artifacts"]),
		SmartArtifactDirectoryRule.Exact(
			"DerivedData",
			directories: ["Build", "Index.noindex", "Logs", "ModuleCache.noindex", "SourcePackages"]),
		SmartArtifactDirectoryRule.Exact(
			"CMakeFiles",
			files: ["CMakeOutput.log", "CMakeError.log", "TargetDirectories.txt", "cmake.check_cache"],
			directories: ["pkgRedirects"]),
		SmartArtifactDirectoryRule.Exact(
			".cxx",
			files: ["CMakeCache.txt"],
			directories: ["Debug", "Release", "RelWithDebInfo", "cmake"]),
		SmartArtifactDirectoryRule.Prefix(
			"cmake-build-",
			files: ["CMakeCache.txt", "build.ninja", "compile_commands.json"],
			directories: ["CMakeFiles"]),
		SmartArtifactDirectoryRule.Exact(
			"vendor",
			files: ["autoload.php", "modules.txt"],
			directories: ["composer", "bin"]),
		SmartArtifactDirectoryRule.Exact(
			".bundle",
			files: ["config"],
			directories: ["cache", "ruby"]),
		SmartArtifactDirectoryRule.Exact(
			"pkg",
			directories: ["mod", "sumdb"]),
		SmartArtifactDirectoryRule.Exact(
			"_build",
			files: [".mix"],
			directories: ["dev", "prod", "test"]),
		SmartArtifactDirectoryRule.Exact(
			".stack-work",
			directories: ["dist", "install"]),
		SmartArtifactDirectoryRule.Exact(
			".terraform",
			files: ["environment"],
			directories: ["modules", "providers"]),
		SmartArtifactDirectoryRule.Exact(
			".serverless",
			files: ["cloudformation-template-update-stack.json"],
			fileExtensions: [".zip"]),
		SmartArtifactDirectoryRule.Exact(
			".zig-cache",
			directories: ["o", "h", "tmp"]),
		SmartArtifactDirectoryRule.Exact(
			"zig-cache",
			directories: ["o", "h", "tmp"]),
		SmartArtifactDirectoryRule.Exact(
			".cache",
			files: ["CACHEDIR.TAG"]),
		SmartArtifactDirectoryRule.Exact(
			"cache",
			files: ["CACHEDIR.TAG"]),
		SmartArtifactDirectoryRule.Exact(
			"tmp",
			files: ["CACHEDIR.TAG"]),
		SmartArtifactDirectoryRule.Exact(
			"temp",
			files: ["CACHEDIR.TAG"]),
		SmartArtifactDirectoryRule.Exact(
			"Library",
			files: ["ArtifactDB", "SourceAssetDB"],
			directories: ["Bee", "PackageCache", "ScriptAssemblies", "ShaderCache"]),
		SmartArtifactDirectoryRule.Exact(
			"Intermediate",
			directories: ["Build", "ProjectFiles", "Source", "Cache"]),
		SmartArtifactDirectoryRule.Exact(
			"Saved",
			directories: ["Autosaves", "Cooked", "Crashes", "Logs", "StagedBuilds"]),
		SmartArtifactDirectoryRule.Exact(
			"Binaries",
			directories: ["Android", "Linux", "Mac", "Win64", "Win32"]),
		SmartArtifactDirectoryRule.Exact(
			"xcuserdata",
			fileSuffixes: [".xcuserstate"])
	];

	private static string[] CreateDefaultIgnoredFileSuffixes() =>
	[
		// These files contain machine/user-specific IDE state. Shared equivalents such as
		// .sln.DotSettings deliberately remain visible.
		".sln.DotSettings.user",
		".csproj.user",
		".fsproj.user",
		".vbproj.user"
	];

	public sealed class SmartArtifactDirectoryRule
	{
		private readonly FrozenSet<string> _files;
		private readonly FrozenSet<string> _directories;
		private readonly string[] _fileSuffixes;
		private readonly string[] _fileExtensions;
		private readonly FrozenSet<string> _childFiles;
		private readonly string[] _pathSuffixSegments;
		private readonly RepeatedChildArtifactSignature? _repeatedChildSignature;

		private SmartArtifactDirectoryRule(
			string namePattern,
			SmartArtifactNameMatchKind matchKind,
			IReadOnlyCollection<string> files,
			IReadOnlyCollection<string> directories,
			IReadOnlyCollection<string> fileSuffixes,
			IReadOnlyCollection<string> fileExtensions,
			IReadOnlyCollection<string> childFiles,
			IReadOnlyCollection<string> pathSuffixSegments,
			RepeatedChildArtifactSignature? repeatedChildSignature,
			bool applyOutsideProjectScopes)
		{
			NamePattern = namePattern;
			MatchKind = matchKind;
			_files = ToNameSet(files);
			_directories = ToNameSet(directories);
			_fileSuffixes = fileSuffixes.Count == 0 ? EmptyNames : fileSuffixes.ToArray();
			_fileExtensions = fileExtensions.Count == 0 ? EmptyNames : fileExtensions.ToArray();
			_childFiles = ToNameSet(childFiles);
			_pathSuffixSegments = pathSuffixSegments.Count == 0 ? EmptyNames : pathSuffixSegments.ToArray();
			_repeatedChildSignature = repeatedChildSignature;
			ApplyOutsideProjectScopes = applyOutsideProjectScopes;
		}

		public string NamePattern { get; }

		public SmartArtifactNameMatchKind MatchKind { get; }

		public bool ApplyOutsideProjectScopes { get; }

		public static SmartArtifactDirectoryRule Exact(
			string name,
			IReadOnlyCollection<string>? files = null,
			IReadOnlyCollection<string>? directories = null,
			IReadOnlyCollection<string>? fileSuffixes = null,
			IReadOnlyCollection<string>? fileExtensions = null,
			IReadOnlyCollection<string>? childFiles = null,
			IReadOnlyCollection<string>? pathSuffixSegments = null,
			RepeatedChildArtifactSignature? repeatedChildSignature = null,
			bool applyOutsideProjectScopes = false) =>
			new(
				name,
				SmartArtifactNameMatchKind.Exact,
				files ?? [],
				directories ?? [],
				fileSuffixes ?? [],
				fileExtensions ?? [],
				childFiles ?? [],
				pathSuffixSegments ?? [],
				repeatedChildSignature,
				applyOutsideProjectScopes);

		public static SmartArtifactDirectoryRule Prefix(
			string prefix,
			IReadOnlyCollection<string>? files = null,
			IReadOnlyCollection<string>? directories = null,
			IReadOnlyCollection<string>? fileSuffixes = null,
			IReadOnlyCollection<string>? fileExtensions = null,
			IReadOnlyCollection<string>? childFiles = null,
			IReadOnlyCollection<string>? pathSuffixSegments = null,
			RepeatedChildArtifactSignature? repeatedChildSignature = null,
			bool applyOutsideProjectScopes = false) =>
			new(
				prefix,
				SmartArtifactNameMatchKind.Prefix,
				files ?? [],
				directories ?? [],
				fileSuffixes ?? [],
				fileExtensions ?? [],
				childFiles ?? [],
				pathSuffixSegments ?? [],
				repeatedChildSignature,
				applyOutsideProjectScopes);

		public bool MatchesName(string name) =>
			MatchKind switch
			{
				SmartArtifactNameMatchKind.Exact => string.Equals(name, NamePattern, StringComparison.OrdinalIgnoreCase),
				SmartArtifactNameMatchKind.Prefix => name.StartsWith(NamePattern, StringComparison.OrdinalIgnoreCase),
				_ => false
			};

		public bool HasStrongSignature(string directoryPath)
		{
			try
			{
				if (MatchesPathSuffix(directoryPath))
					return true;

				foreach (var file in _files)
				{
					if (File.Exists(Path.Combine(directoryPath, file)))
						return true;
				}

				foreach (var directory in _directories)
				{
					if (Directory.Exists(Path.Combine(directoryPath, directory)))
						return true;
				}

				if (_repeatedChildSignature?.Matches(directoryPath) == true)
					return true;

				if (_fileSuffixes.Length > 0 || _fileExtensions.Length > 0 || _childFiles.Count > 0)
					return HasEnumeratedSignature(directoryPath);
			}
			catch (Exception exception) when (exception is
			       UnauthorizedAccessException or
			       IOException or
			       ArgumentException or
			       NotSupportedException or
			       System.Security.SecurityException)
			{
				return false;
			}

			return false;
		}

		private bool MatchesPathSuffix(string directoryPath)
		{
			if (_pathSuffixSegments.Length == 0)
				return false;

			var currentPath = directoryPath.TrimEnd(
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);
			for (var index = _pathSuffixSegments.Length - 1; index >= 0; index--)
			{
				var actualSegment = Path.GetFileName(currentPath);
				if (!string.Equals(actualSegment, _pathSuffixSegments[index], StringComparison.OrdinalIgnoreCase))
					return false;

				if (index == 0)
					return true;

				var parentPath = Path.GetDirectoryName(currentPath);
				if (string.IsNullOrWhiteSpace(parentPath))
					return false;

				currentPath = parentPath;
			}

			return false;
		}

		private bool HasEnumeratedSignature(string directoryPath)
		{
			var inspectedEntries = 0;
			foreach (var entry in EnumerateTopLevelEntries(directoryPath))
			{
				if (++inspectedEntries > MaxEnumeratedSignatureEntries)
					return false;

				if (entry.IsDirectory)
				{
					if (_childFiles.Count == 0)
						continue;

					foreach (var childFile in _childFiles)
						if (File.Exists(Path.Combine(entry.FullPath, childFile)))
							return true;

					continue;
				}

				if (HasMatchingFileSignature(entry.Name))
					return true;
			}

			return false;
		}

		private bool HasMatchingFileSignature(string fileName)
		{
			foreach (var suffix in _fileSuffixes)
				if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					return true;

			if (_fileExtensions.Length == 0)
				return false;

			var extension = Path.GetExtension(fileName);
			if (string.IsNullOrWhiteSpace(extension))
				return false;

			foreach (var candidate in _fileExtensions)
				if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
					return true;

			foreach (var candidate in _fileExtensions)
				if (fileName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		private static FrozenSet<string> ToNameSet(IReadOnlyCollection<string> values) =>
			values.Count == 0
				? EmptyNameSet
				: values.ToFrozenSet(NameComparer);
	}

	public sealed class RepeatedChildArtifactSignature
	{
		private readonly FrozenSet<string> _layoutDirectories;

		public RepeatedChildArtifactSignature(
			string selfNamedFileSuffix,
			IReadOnlyCollection<string> layoutDirectories,
			int minimumMatches,
			int maxEntries)
		{
			if (string.IsNullOrWhiteSpace(selfNamedFileSuffix))
				throw new ArgumentException("A self-named file suffix is required.", nameof(selfNamedFileSuffix));
			if (minimumMatches <= 0)
				throw new ArgumentOutOfRangeException(nameof(minimumMatches));
			if (maxEntries < minimumMatches)
				throw new ArgumentOutOfRangeException(nameof(maxEntries));

			SelfNamedFileSuffix = selfNamedFileSuffix;
			_layoutDirectories = layoutDirectories.Count == 0
				? EmptyNameSet
				: layoutDirectories.ToFrozenSet(NameComparer);
			MinimumMatches = minimumMatches;
			MaxEntries = maxEntries;
		}

		public string SelfNamedFileSuffix { get; }

		public int MinimumMatches { get; }

		public int MaxEntries { get; }

		internal bool Matches(string directoryPath)
		{
			var inspectedEntries = 0;
			var matchingChildren = 0;
			foreach (var entry in EnumerateTopLevelEntries(directoryPath))
			{
				if (++inspectedEntries > MaxEntries)
					return false;
				if (!entry.IsDirectory)
					continue;

				var selfNamedArtifactPath = Path.Combine(
					entry.FullPath,
					entry.Name + SelfNamedFileSuffix);
				if (!File.Exists(selfNamedArtifactPath) || !HasKnownLayoutDirectory(entry.FullPath))
					continue;

				if (++matchingChildren >= MinimumMatches)
					return true;
			}

			return false;
		}

		private bool HasKnownLayoutDirectory(string childPath)
		{
			if (_layoutDirectories.Count == 0)
				return true;

			foreach (var directory in _layoutDirectories)
				if (Directory.Exists(Path.Combine(childPath, directory)))
					return true;

			return false;
		}
	}

	private static IEnumerable<SmartArtifactEntry> EnumerateTopLevelEntries(string directoryPath)
	{
		var enumerable = new FileSystemEnumerable<SmartArtifactEntry>(
			directoryPath,
			static (ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new SmartArtifactEntry(
					name,
					entry.ToSpecifiedFullPath(),
					entry.IsDirectory);
			},
			new EnumerationOptions
			{
				RecurseSubdirectories = false,
				ReturnSpecialDirectories = false,
				// Signature probes must not follow links into caches outside the opened tree.
				AttributesToSkip = FileAttributes.ReparsePoint,
				IgnoreInaccessible = true
			});
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) => true;
		return enumerable;
	}

	private readonly record struct SmartArtifactEntry(
		string Name,
		string FullPath,
		bool IsDirectory);
}

public enum SmartArtifactNameMatchKind
{
	Exact = 0,
	Prefix = 1
}
