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
	private static readonly string[] EmptyNames = [];

	public static SmartArtifactIgnoreMatcher Empty { get; } = new([]);

	public static SmartArtifactIgnoreMatcher Default { get; } = new(CreateDefaultRules());

	private readonly SmartArtifactDirectoryRule[] _rules;
	private readonly FrozenSet<string> _exactCandidateNames;
	private readonly string[] _candidatePrefixes;

	public SmartArtifactIgnoreMatcher(IEnumerable<SmartArtifactDirectoryRule> rules)
	{
		_rules = rules.ToArray();
		if (_rules.Length == 0)
		{
			_exactCandidateNames = EmptyNameSet;
			_candidatePrefixes = EmptyNames;
			return;
		}

		_exactCandidateNames = _rules
			.Where(static rule => rule.MatchKind == SmartArtifactNameMatchKind.Exact)
			.Select(static rule => rule.NamePattern)
			.ToFrozenSet(NameComparer);
		_candidatePrefixes = _rules
			.Where(static rule => rule.MatchKind == SmartArtifactNameMatchKind.Prefix)
			.Select(static rule => rule.NamePattern)
			.Distinct(NameComparer)
			.ToArray();
	}

	public bool HasRules => _rules.Length > 0;

	public bool IsCandidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return false;

		if (_exactCandidateNames.Contains(name))
			return true;

		foreach (var prefix in _candidatePrefixes)
			if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

	public bool IsIgnoredDirectory(string fullPath, string name)
	{
		if (_rules.Length == 0 || string.IsNullOrWhiteSpace(fullPath) || !IsCandidateName(name))
			return false;

		// This matcher is intentionally two-stage: cheap candidate-name filtering first,
		// then a bounded artifact signature probe only for suspicious directories.
		// Generic names like "build" or "Library" stay visible unless their own
		// contents prove that they are generated tool output.
		return IsIgnoredDirectoryCore(fullPath, name);
	}

	private bool IsIgnoredDirectoryCore(string fullPath, string name)
	{
		foreach (var rule in _rules)
		{
			if (!rule.MatchesName(name))
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

	public sealed class SmartArtifactDirectoryRule
	{
		private readonly FrozenSet<string> _files;
		private readonly FrozenSet<string> _directories;
		private readonly string[] _fileSuffixes;
		private readonly string[] _fileExtensions;
		private readonly FrozenSet<string> _childFiles;

		private SmartArtifactDirectoryRule(
			string namePattern,
			SmartArtifactNameMatchKind matchKind,
			IReadOnlyCollection<string> files,
			IReadOnlyCollection<string> directories,
			IReadOnlyCollection<string> fileSuffixes,
			IReadOnlyCollection<string> fileExtensions,
			IReadOnlyCollection<string> childFiles)
		{
			NamePattern = namePattern;
			MatchKind = matchKind;
			_files = ToNameSet(files);
			_directories = ToNameSet(directories);
			_fileSuffixes = fileSuffixes.Count == 0 ? EmptyNames : fileSuffixes.ToArray();
			_fileExtensions = fileExtensions.Count == 0 ? EmptyNames : fileExtensions.ToArray();
			_childFiles = ToNameSet(childFiles);
		}

		public string NamePattern { get; }

		public SmartArtifactNameMatchKind MatchKind { get; }

		public static SmartArtifactDirectoryRule Exact(
			string name,
			IReadOnlyCollection<string>? files = null,
			IReadOnlyCollection<string>? directories = null,
			IReadOnlyCollection<string>? fileSuffixes = null,
			IReadOnlyCollection<string>? fileExtensions = null,
			IReadOnlyCollection<string>? childFiles = null) =>
			new(
				name,
				SmartArtifactNameMatchKind.Exact,
				files ?? [],
				directories ?? [],
				fileSuffixes ?? [],
				fileExtensions ?? [],
				childFiles ?? []);

		public static SmartArtifactDirectoryRule Prefix(
			string prefix,
			IReadOnlyCollection<string>? files = null,
			IReadOnlyCollection<string>? directories = null,
			IReadOnlyCollection<string>? fileSuffixes = null,
			IReadOnlyCollection<string>? fileExtensions = null,
			IReadOnlyCollection<string>? childFiles = null) =>
			new(
				prefix,
				SmartArtifactNameMatchKind.Prefix,
				files ?? [],
				directories ?? [],
				fileSuffixes ?? [],
				fileExtensions ?? [],
				childFiles ?? []);

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

				if (_fileSuffixes.Length > 0 || _fileExtensions.Length > 0 || _childFiles.Count > 0)
					return HasEnumeratedSignature(directoryPath);
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
			catch (IOException)
			{
				return false;
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
				AttributesToSkip = 0,
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
