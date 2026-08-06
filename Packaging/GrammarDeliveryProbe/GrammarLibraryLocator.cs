using System.Reflection;
using System.Security.Cryptography;

namespace DevProjex.Packaging.GrammarDeliveryProbe;

/// <summary>
/// Resolves an absolute path to a grammar library. Both implementations return a path rather
/// than a bare name on purpose: under PublishSingleFile the native extraction directory is not
/// on the OS loader search path, so loading by name throws DllNotFoundException on every RID.
/// </summary>
internal interface IGrammarLibraryLocator
{
	string StrategyName { get; }

	string Resolve(string libraryBaseName);
}

/// <summary>
/// Portable builds. Grammars travel inside the single file and are materialized on first use.
/// </summary>
internal sealed class EmbeddedGrammarLibraryLocator(string rootDirectory) : IGrammarLibraryLocator
{
	// Matches what the .NET single-file extractor applies to the native libraries it writes.
	private const UnixFileMode UnixExecutableMode =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
		UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
		UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	private readonly Assembly _assembly = typeof(EmbeddedGrammarLibraryLocator).Assembly;

	public string StrategyName => "embedded";

	public string RootDirectory { get; } = rootDirectory;

	public static string DefaultRootDirectory(string bindingVersion) =>
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"DevProjex",
			"grammars",
			$"tree-sitter-{bindingVersion}",
			GrammarCatalog.ResolveRuntimeIdentifier());

	/// <summary>
	/// Hash of the library as it was embedded. Exposed so a caller can prove that a materialized
	/// copy on disk is bit-identical to the original.
	/// </summary>
	public byte[] GetEmbeddedHash(string libraryBaseName)
	{
		using var source = OpenEmbedded(GrammarCatalog.ResolveFileName(libraryBaseName));
		return SHA256.HashData(source);
	}

	public string Resolve(string libraryBaseName)
	{
		var fileName = GrammarCatalog.ResolveFileName(libraryBaseName);
		Directory.CreateDirectory(RootDirectory);
		var target = Path.Combine(RootDirectory, fileName);

		using var source = OpenEmbedded(fileName);
		var expected = SHA256.HashData(source);
		source.Position = 0;

		// The hash is checked BEFORE loading, not only after writing: a copy can be truncated by
		// a crash mid-write, quarantined by antivirus, or damaged on disk long after it was
		// created. A mismatch always re-materializes rather than loading damaged code.
		// It is also why nothing is ever patched in place - macOS library validation requires the
		// bytes to stay bit-identical to the signed original.
		if (File.Exists(target))
		{
			using var existing = File.OpenRead(target);
			if (SHA256.HashData(existing).AsSpan().SequenceEqual(expected))
				return target;
		}

		try
		{
			return Materialize(source, RootDirectory, fileName);
		}
		catch (IOException)
		{
			// The damaged copy is already mapped into this process - the binding loads grammars
			// and never releases the module handle, so Windows refuses to replace the file.
			// Repair into a fresh directory instead of failing; the stale copy is never loaded
			// again because the hash check above rejects it.
			source.Position = 0;
			var repairDirectory = Path.Combine(RootDirectory, $"repair-{Environment.ProcessId}");
			Directory.CreateDirectory(repairDirectory);
			return Materialize(source, repairDirectory, fileName);
		}
	}

	private static string Materialize(Stream source, string directory, string fileName)
	{
		var target = Path.Combine(directory, fileName);
		var staging = Path.Combine(directory, $"{fileName}.{Environment.ProcessId}.tmp");
		using (var output = File.Create(staging))
			source.CopyTo(output);
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(staging, UnixExecutableMode);
		File.Move(staging, target, overwrite: true);
		return target;
	}

	private Stream OpenEmbedded(string fileName) =>
		_assembly.GetManifestResourceStream($"Grammars/{fileName}")
			?? throw new InvalidOperationException(
				$"Embedded grammar '{fileName}' is missing. The publish filter and the embed list disagree.");
}

/// <summary>
/// MSIX builds. Grammars are ordinary package files covered by the package signature, so nothing
/// is written at runtime — extracted libraries would not inherit that signature and are blocked
/// by Smart App Control and S mode.
/// </summary>
internal sealed class ContentGrammarLibraryLocator : IGrammarLibraryLocator
{
	public string StrategyName => "content";

	public string RootDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "grammars");

	public string Resolve(string libraryBaseName)
	{
		var path = Path.Combine(RootDirectory, GrammarCatalog.ResolveFileName(libraryBaseName));
		if (!File.Exists(path))
			throw new FileNotFoundException($"Packaged grammar '{path}' is missing from the application directory.", path);
		return path;
	}
}
