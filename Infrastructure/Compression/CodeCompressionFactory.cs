using DevProjex.Application.Compression;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Infrastructure.Compression;

/// <summary>
/// Builds the compression session both application hosts use, so the delivery strategy is decided
/// once. Nothing here loads a grammar: the locator is only consulted when a supported file is
/// actually compressed, which keeps a session that never enables the option at zero cost.
/// </summary>
public static class CodeCompressionFactory
{
	public const string EmbeddedResourcePrefix = "DevProjex.Grammars/";

	public static CodeCompressionSession CreateSession() =>
		new(new TreeSitterCodeCompressor(CreateLocator()));

	public static IGrammarLibraryLocator CreateLocator()
	{
		// Packaged builds must not load native code they wrote themselves: a runtime-materialized
		// library is outside the package signature, which is exactly what Smart App Control and S
		// mode verify. They ship grammars as ordinary package content instead.
		if (OperatingSystem.IsWindows() && WindowsPackageIdentityProbe.IsPackagedApp())
			return new ContentGrammarLibraryLocator();

		return new EmbeddedGrammarLibraryLocator(
			typeof(CodeCompressionFactory).Assembly,
			EmbeddedResourcePrefix,
			EmbeddedGrammarLibraryLocator.DefaultRootDirectory(GrammarPlatform.BindingVersion));
	}
}
