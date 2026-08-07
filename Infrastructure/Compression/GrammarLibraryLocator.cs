using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DevProjex.Infrastructure.Compression;

/// <summary>
/// Resolves an absolute path to a native grammar library.
///
/// Absolute paths, never bare names: under PublishSingleFile the runtime extracts native assets to
/// a directory that is on NATIVE_DLL_SEARCH_DIRECTORIES but not on the OS loader's search path, so
/// TreeSitter's Language(id) constructor throws DllNotFoundException on every RID. The core
/// tree-sitter library still resolves, because it is a plain DllImport - which is why it is the one
/// native left in the publish output.
/// </summary>
public interface IGrammarLibraryLocator
{
	string StrategyName { get; }

	/// <summary>
	/// The grammar libraries this build can actually serve, derived from the delivery source itself
	/// rather than a parallel list that can drift from what shipped.
	/// </summary>
	IReadOnlyList<string> EnumerateLibraries();

	string Resolve(string libraryBaseName);
}

/// <summary>
/// Portable builds: grammars travel inside the single file and are materialized on first use.
///
/// Not used by the MSIX build. A library written at runtime is not covered by the package
/// signature, which is what Smart App Control and S mode check, so packaged builds ship grammars as
/// ordinary package content instead - see <see cref="ContentGrammarLibraryLocator"/>.
/// </summary>
public sealed class EmbeddedGrammarLibraryLocator : IGrammarLibraryLocator
{
	// Matches what the .NET single-file extractor applies to the native libraries it writes.
	private const UnixFileMode UnixExecutableMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
		UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
		UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	/// <summary>
	/// A sibling process may have created a repair directory moments ago and not loaded from it
	/// yet. Only directories left untouched for longer than this are considered abandoned.
	/// </summary>
	private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(24);

	private static readonly StringComparer PathComparer =
		OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

	private readonly Assembly _assembly;
	private readonly string _resourcePrefix;
	private readonly HashSet<string> _inUse = new(PathComparer);

	public EmbeddedGrammarLibraryLocator(Assembly assembly, string resourcePrefix, string rootDirectory)
	{
		_assembly = assembly;
		_resourcePrefix = resourcePrefix;
		RootDirectory = rootDirectory;
	}

	public string StrategyName => "embedded";

	public string RootDirectory { get; }

	public static string DefaultRootDirectory(string bindingVersion) =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"DevProjex",
			"grammars",
			$"tree-sitter-{bindingVersion}",
			GrammarPlatform.RuntimeIdentifier);

	public IReadOnlyList<string> EnumerateLibraries() =>
		_assembly.GetManifestResourceNames()
			.Where(name => name.StartsWith(_resourcePrefix, StringComparison.Ordinal))
			.Select(name => GrammarPlatform.ToBaseName(name[_resourcePrefix.Length..]))
			.OrderBy(static name => name, StringComparer.Ordinal)
			.ToArray();

	/// <summary>Hash of the library as embedded, so a caller can prove a copy on disk is identical.</summary>
	public byte[] GetEmbeddedHash(string libraryBaseName)
	{
		using var source = OpenEmbedded(GrammarPlatform.ResolveFileName(libraryBaseName));
		return SHA256.HashData(source);
	}

	public string Resolve(string libraryBaseName)
	{
		var fileName = GrammarPlatform.ResolveFileName(libraryBaseName);
		Directory.CreateDirectory(RootDirectory);
		var target = Path.Combine(RootDirectory, fileName);

		using var source = OpenEmbedded(fileName);
		var expected = SHA256.HashData(source);
		source.Position = 0;

		// The hash is checked BEFORE loading, not only after writing: a copy can be truncated by a
		// crash mid-write, quarantined by antivirus, or damaged on disk long after it was created.
		// Nothing is ever patched in place either - macOS library validation requires the bytes to
		// stay bit-identical to the signed original.
		if (File.Exists(target))
		{
			using var existing = File.OpenRead(target);
			if (SHA256.HashData(existing).AsSpan().SequenceEqual(expected))
				return target;
		}

		try
		{
			_inUse.Add(Path.TrimEndingDirectorySeparator(RootDirectory));
			return Materialize(source, RootDirectory, fileName);
		}
		catch (IOException)
		{
			// The damaged copy is already mapped into this process - the binding loads grammars and
			// never releases the module handle, so Windows refuses to replace the file. Repair into
			// a fresh directory; the stale copy is never loaded again because the hash check above
			// rejects it.
			source.Position = 0;
			var repairDirectory = Path.Combine(RootDirectory, $"repair-{Environment.ProcessId}");
			Directory.CreateDirectory(repairDirectory);
			_inUse.Add(repairDirectory);
			return Materialize(source, repairDirectory, fileName);
		}
	}

	/// <summary>
	/// Removes grammar directories left by earlier versions, other architectures, and repairs that
	/// were never cleaned up. Never throws: a directory that cannot be removed is left in place.
	/// </summary>
	public IReadOnlyList<string> PruneAbandonedDirectories()
	{
		var removed = new List<string>();
		// Layout is grammars/tree-sitter-<version>/<rid>/, so there are two levels to sweep: whole
		// binding versions left by an upgrade, and stale architectures under the current one.
		var currentRid = Path.TrimEndingDirectorySeparator(RootDirectory);
		var currentVersion = Path.GetDirectoryName(currentRid);
		var grammarsRoot = currentVersion is null ? null : Path.GetDirectoryName(currentVersion);

		foreach (var candidate in SafeEnumerate(grammarsRoot))
		{
			if (PathComparer.Equals(candidate, currentVersion))
				continue;
			TryRemove(candidate, removed);
		}

		foreach (var candidate in SafeEnumerate(currentVersion))
		{
			if (PathComparer.Equals(candidate, currentRid))
				continue;
			TryRemove(candidate, removed);
		}

		foreach (var candidate in SafeEnumerate(RootDirectory))
		{
			if (!Path.GetFileName(candidate).StartsWith("repair-", StringComparison.Ordinal))
				continue;
			if (_inUse.Contains(candidate))
				continue;
			TryRemove(candidate, removed);
		}

		return removed;
	}

	private static string Materialize(Stream source, string directory, string fileName)
	{
		var target = Path.Combine(directory, fileName);
		var staging = Path.Combine(directory, $"{fileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (var output = File.Create(staging))
				source.CopyTo(output);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(staging, UnixExecutableMode);
			File.Move(staging, target, overwrite: true);
			return target;
		}
		finally
		{
			try
			{
				File.Delete(staging);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// A failed cleanup must not hide the original materialization failure.
			}
		}
	}

	private static IEnumerable<string> SafeEnumerate(string? directory)
	{
		if (directory is null || !Directory.Exists(directory))
			return [];
		try
		{
			return Directory.EnumerateDirectories(directory).ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static void TryRemove(string directory, List<string> removed)
	{
		try
		{
			if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow - AbandonedAfter)
				return;
			Directory.Delete(directory, recursive: true);
			removed.Add(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A grammar still mapped by another process cannot be deleted on Windows. Leaving it
			// costs disk; deleting it out from under a running instance would cost correctness.
		}
	}

	private Stream OpenEmbedded(string fileName) =>
		_assembly.GetManifestResourceStream($"{_resourcePrefix}{fileName}")
			?? throw new InvalidOperationException(
				$"Embedded grammar '{fileName}' is missing. The publish filter and the embed list disagree.");
}

/// <summary>
/// MSIX builds: grammars are ordinary package files covered by the package signature, so nothing is
/// written at runtime.
/// </summary>
public sealed class ContentGrammarLibraryLocator : IGrammarLibraryLocator
{
	public string StrategyName => "content";

	public string RootDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "grammars");

	public IReadOnlyList<string> EnumerateLibraries() =>
		Directory.Exists(RootDirectory)
			? Directory.EnumerateFiles(RootDirectory)
				.Select(path => GrammarPlatform.ToBaseName(Path.GetFileName(path)))
				.OrderBy(static name => name, StringComparer.Ordinal)
				.ToArray()
			: [];

	public string Resolve(string libraryBaseName)
	{
		var path = Path.Combine(RootDirectory, GrammarPlatform.ResolveFileName(libraryBaseName));
		if (!File.Exists(path))
			throw new FileNotFoundException($"Packaged grammar '{path}' is missing from the application directory.", path);
		return path;
	}
}

/// <summary>Platform naming for native grammar libraries.</summary>
public static class GrammarPlatform
{
	/// <summary>Windows ships tree-sitter-c-sharp.dll; Unix ships libtree-sitter-c-sharp.so/.dylib.</summary>
	public static string LibraryPrefix => OperatingSystem.IsWindows() ? string.Empty : "lib";

	public static string LibraryExtension =>
		OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

	public static string ResolveFileName(string libraryBaseName) =>
		$"{LibraryPrefix}{libraryBaseName}{LibraryExtension}";

	/// <summary>
	/// Inverse of <see cref="ResolveFileName"/>. Derived from the prefix and extension rather than
	/// from ResolveFileName(string.Empty), which yields "lib.so" on Unix and silently fails to strip.
	/// </summary>
	public static string ToBaseName(string fileName)
	{
		var name = fileName;
		if (name.EndsWith(LibraryExtension, StringComparison.Ordinal))
			name = name[..^LibraryExtension.Length];
		if (LibraryPrefix.Length > 0 && name.StartsWith(LibraryPrefix, StringComparison.Ordinal))
			name = name[LibraryPrefix.Length..];
		return name;
	}

	/// <summary>
	/// Version of the tree-sitter binding, read from the assembly rather than duplicated as a
	/// constant: the grammar cache directory is keyed on it, so the two must never drift.
	/// </summary>
	public static string BindingVersion
	{
		get
		{
			var informational = typeof(TreeSitter.Language).Assembly
				.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
			if (string.IsNullOrWhiteSpace(informational))
				return typeof(TreeSitter.Language).Assembly.GetName().Version?.ToString() ?? "unknown";
			var plus = informational.IndexOf('+');
			return plus < 0 ? informational : informational[..plus];
		}
	}

	public static string RuntimeIdentifier
	{
		get
		{
			var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
			var architecture = RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.X64 => "x64",
				Architecture.Arm64 => "arm64",
				Architecture.X86 => "x86",
				Architecture.Arm => "arm",
				var other => other.ToString().ToLowerInvariant()
			};
			return $"{platform}-{architecture}";
		}
	}
}
