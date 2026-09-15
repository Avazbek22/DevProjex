using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionFactoryTests
{
	[Fact]
	public void CreateLocator_ContentDirectoryOnNonWindows_UsesContentDelivery()
	{
		using var temporary = new TemporaryDirectory();
		Directory.CreateDirectory(Path.Combine(temporary.Path, "grammars"));

		var locator = CodeCompressionFactory.CreateLocator(
			temporary.Path,
			isWindows: false,
			isWindowsPackagedApp: static () => false);

		var content = Assert.IsType<ContentGrammarLibraryLocator>(locator);
		Assert.Throws<FileNotFoundException>(() => content.Resolve("tree-sitter-deliberately-missing"));
	}

	[Fact]
	public void CreateLocator_WithoutContentDirectory_UsesEmbeddedDelivery()
	{
		using var temporary = new TemporaryDirectory();

		var locator = CodeCompressionFactory.CreateLocator(
			temporary.Path,
			isWindows: false,
			isWindowsPackagedApp: static () => false);

		Assert.IsType<EmbeddedGrammarLibraryLocator>(locator).Dispose();
	}

	[Fact]
	public void CreateLocator_WindowsPackagedApp_UsesContentDeliveryWithoutDirectory()
	{
		using var temporary = new TemporaryDirectory();

		var locator = CodeCompressionFactory.CreateLocator(
			temporary.Path,
			isWindows: true,
			isWindowsPackagedApp: static () => true);

		Assert.IsType<ContentGrammarLibraryLocator>(locator);
	}
}
