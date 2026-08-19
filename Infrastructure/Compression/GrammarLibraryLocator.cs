using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DevProjex.Infrastructure.Persistence;

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
public sealed class EmbeddedGrammarLibraryLocator : IGrammarLibraryLocator, IDisposable
{
	private const string LeaseFileName = ".devprojex.lease";
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
	private readonly Dictionary<string, FileStream> _leases = new(PathComparer);
	private readonly object _inUseSync = new();
	private bool _disposed;

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
			UserDataPathResolver.GetLocalDataRoot(),
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

		try
		{
			// The hash is checked BEFORE loading, not only after writing: a copy can be truncated by a
			// crash mid-write, quarantined by antivirus, or damaged on disk long after it was created.
			// Nothing is ever patched in place either - macOS library validation requires the bytes to
			// stay bit-identical to the signed original.
			if (HasExpectedHash(target, expected))
			{
				MarkInUse(Path.TrimEndingDirectorySeparator(RootDirectory));
				return target;
			}

			MarkInUse(Path.TrimEndingDirectorySeparator(RootDirectory));
			return Materialize(source, RootDirectory, fileName, expected);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A damaged or valid shared copy can be unavailable while another process publishes,
			// scans, or maps it. Never trust unreadable bytes; materialize the verified embedded copy
			// into a process-local directory instead.
			source.Position = 0;
			var repairDirectory = Path.Combine(RootDirectory, $"repair-{Environment.ProcessId}");
			Directory.CreateDirectory(repairDirectory);
			MarkInUse(repairDirectory);
			return Materialize(source, repairDirectory, fileName, expected);
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
		if (TryResolveManagedLayout(currentRid, out var currentVersion, out var grammarsRoot))
		{
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
		}

		foreach (var candidate in SafeEnumerate(RootDirectory))
		{
			if (!Path.GetFileName(candidate).StartsWith("repair-", StringComparison.Ordinal))
				continue;
			if (IsInUse(candidate))
				continue;
			TryRemove(candidate, removed);
		}

		return removed;
	}

	private static bool TryResolveManagedLayout(
		string currentRid,
		out string currentVersion,
		out string grammarsRoot)
	{
		currentVersion = Path.GetDirectoryName(currentRid) ?? string.Empty;
		grammarsRoot = currentVersion.Length == 0
			? string.Empty
			: Path.GetDirectoryName(currentVersion) ?? string.Empty;
		var productRoot = grammarsRoot.Length == 0
			? string.Empty
			: Path.GetDirectoryName(grammarsRoot) ?? string.Empty;
		return currentVersion.Length > 0 &&
		       grammarsRoot.Length > 0 &&
		       productRoot.Length > 0 &&
		       PathComparer.Equals(Path.GetFileName(currentRid), GrammarPlatform.RuntimeIdentifier) &&
		       Path.GetFileName(currentVersion).StartsWith("tree-sitter-", StringComparison.Ordinal) &&
		       PathComparer.Equals(Path.GetFileName(grammarsRoot), "grammars") &&
		       PathComparer.Equals(Path.GetFileName(productRoot), "DevProjex");
	}

	private void MarkInUse(string directory)
	{
		lock (_inUseSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_leases.ContainsKey(directory))
				return;

			Directory.CreateDirectory(directory);
			var leasePath = Path.Combine(directory, LeaseFileName);
			// Cleanup requests write access to the marker. Active locators grant shared reads only,
			// which makes that request fail on every supported OS without relying on unlink semantics.
			var lease = new FileStream(
				leasePath,
				FileMode.OpenOrCreate,
				FileAccess.Read,
				FileShare.Read);
			_leases.Add(directory, lease);
		}
	}

	private bool IsInUse(string directory)
	{
		lock (_inUseSync)
			return _leases.ContainsKey(directory);
	}

	private static string Materialize(
		Stream source,
		string directory,
		string fileName,
		ReadOnlySpan<byte> expectedHash)
	{
		var target = Path.Combine(directory, fileName);
		var staging = Path.Combine(directory, $"{fileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
		try
		{
			using (var output = File.Create(staging))
				source.CopyTo(output);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(staging, UnixExecutableMode);
			try
			{
				// Never replace a library another process may already have mapped. Exactly one writer
				// publishes the cold copy; losing writers accept it only after verifying its bytes.
				File.Move(staging, target, overwrite: false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				if (!HasExpectedHash(target, expectedHash))
					throw;
			}
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

	private static bool HasExpectedHash(string path, ReadOnlySpan<byte> expectedHash)
	{
		if (!File.Exists(path))
			return false;

		using var existing = File.OpenRead(path);
		return SHA256.HashData(existing).AsSpan().SequenceEqual(expectedHash);
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
		List<FileStream>? cleanupLeases = null;
		try
		{
			if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow - AbandonedAfter)
				return;
			if (!TryAcquireCleanupLeases(directory, out cleanupLeases))
				return;
			Directory.Delete(directory, recursive: true);
			removed.Add(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A grammar still mapped by another process cannot be deleted on Windows. Leaving it
			// costs disk; deleting it out from under a running instance would cost correctness.
		}
		finally
		{
			if (cleanupLeases is not null)
			{
				foreach (var lease in cleanupLeases)
					lease.Dispose();
			}
		}
	}

	private static bool TryAcquireCleanupLeases(string directory, out List<FileStream> leases)
	{
		leases = [];
		try
		{
			var leasePaths = Directory
				.EnumerateFiles(directory, LeaseFileName, SearchOption.AllDirectories)
				.Append(Path.Combine(directory, LeaseFileName))
				.Distinct(PathComparer)
				.OrderBy(static path => path, PathComparer)
				.ToArray();
			foreach (var leasePath in leasePaths)
			{
				leases.Add(new FileStream(
					leasePath,
					FileMode.OpenOrCreate,
					FileAccess.ReadWrite,
					FileShare.Delete));
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			foreach (var lease in leases)
				lease.Dispose();
			leases.Clear();
			return false;
		}
	}

	public void Dispose()
	{
		lock (_inUseSync)
		{
			if (_disposed)
				return;
			_disposed = true;
			foreach (var lease in _leases.Values)
				lease.Dispose();
			_leases.Clear();
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
