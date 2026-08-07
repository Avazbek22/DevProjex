using System.IO.Compression;
using DevProjex.Application.Compression;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Integration;

/// <summary>
/// The wiring contract, not the engine: whatever the compressor decides, every non-preview output
/// has to carry the same bytes, and a copy that was changed has to say so. The engine's own
/// behaviour is covered by the unit fixtures.
/// </summary>
public sealed class CodeCompressionOutputContractIntegrationTests
{
	private const string CompressibleSource = """
		namespace Sample;

		public sealed class Widget
		{
			public int Compute(int left, int right)
			{
				var total = left + right;
				for (var index = 0; index < 8; index++)
					total += index * left - right;
				return total;
			}

			public string Describe(int value)
			{
				var text = value.ToString();
				return string.Concat(text, "-", text, "-", text, "-", text);
			}
		}
		""";

	[Fact]
	public async Task ContextDocument_WithCompression_ShrinksBodiesAndKeepsSignatures()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);

		var plain = await workspace.BuildContextAsync(compress: false);
		var compressed = await workspace.BuildContextAsync(compress: true);

		Assert.Contains("public int Compute(int left, int right)", compressed);
		Assert.Contains("public string Describe(int value)", compressed);
		Assert.DoesNotContain("total += index * left - right", compressed);
		Assert.True(
			compressed.Length < plain.Length,
			$"compressed document was not smaller ({compressed.Length} >= {plain.Length})");
	}

	[Fact]
	public async Task ContextDocument_WithoutCompression_IsUnchanged()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);

		var document = await workspace.BuildContextAsync(compress: false);

		Assert.Contains("total += index * left - right", document);
	}

	[Fact]
	public async Task FolderCopy_WithCompression_WritesCompressedFilesAndOneNotice()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);

		var result = await workspace.ExportFolderAsync(compress: true);

		var copied = await File.ReadAllTextAsync(
			Path.Combine(result.DestinationPath, "Widget.cs"),
			TestContext.Current.CancellationToken);
		Assert.Contains("public int Compute(int left, int right)", copied);
		Assert.DoesNotContain("total += index * left - right", copied);

		var noticePath = Path.Combine(
			result.DestinationPath,
			ProjectCopyExportService.TransformationNoticeFileName);
		Assert.True(File.Exists(noticePath), "a transformed copy must carry a notice in its root");
		Assert.False(
			string.IsNullOrWhiteSpace(
				await File.ReadAllTextAsync(noticePath, TestContext.Current.CancellationToken)));
	}

	[Fact]
	public async Task FolderCopy_WithoutCompression_IsByteForByteAndCarriesNoNotice()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);

		var result = await workspace.ExportFolderAsync(compress: false);

		Assert.Equal(
			await File.ReadAllBytesAsync(workspace.SourceFile, TestContext.Current.CancellationToken),
			await File.ReadAllBytesAsync(
				Path.Combine(result.DestinationPath, "Widget.cs"),
				TestContext.Current.CancellationToken));
		Assert.False(File.Exists(Path.Combine(
			result.DestinationPath,
			ProjectCopyExportService.TransformationNoticeFileName)));
	}

	[Fact]
	public async Task ZipCopy_WithCompression_CarriesTheSameBytesAsTheFolderCopy()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);
		var folder = await workspace.ExportFolderAsync(compress: true);
		var zipPath = Path.Combine(workspace.DestinationParent, "copy.zip");

		await workspace.ExportZipAsync(zipPath, compress: true);

		using var archive = ZipFile.OpenRead(zipPath);
		var entry = archive.Entries.Single(entry =>
			entry.FullName.EndsWith("Widget.cs", StringComparison.Ordinal));
		using var reader = new StreamReader(entry.Open());
		Assert.Equal(
			await File.ReadAllTextAsync(
				Path.Combine(folder.DestinationPath, "Widget.cs"),
				TestContext.Current.CancellationToken),
			await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
		Assert.Contains(
			archive.Entries,
			candidate => candidate.FullName.EndsWith(
				ProjectCopyExportService.TransformationNoticeFileName,
				StringComparison.Ordinal));
	}

	private sealed class CompressionWorkspace : IDisposable
	{
		private CompressionWorkspace(string root, string sourceRoot, string destinationParent, string sourceFile)
		{
			Root = root;
			SourceRoot = sourceRoot;
			DestinationParent = destinationParent;
			SourceFile = sourceFile;
		}

		public string Root { get; }

		public string SourceRoot { get; }

		public string DestinationParent { get; }

		public string SourceFile { get; }

		public static CompressionWorkspace Create(string source)
		{
			var root = Directory.CreateTempSubdirectory("DevProjex-Compression-").FullName;
			var sourceRoot = Path.Combine(root, "Sample");
			var destinationParent = Path.Combine(root, "out");
			Directory.CreateDirectory(sourceRoot);
			Directory.CreateDirectory(destinationParent);
			var sourceFile = Path.Combine(sourceRoot, "Widget.cs");
			File.WriteAllText(sourceFile, source);
			return new CompressionWorkspace(root, sourceRoot, destinationParent, sourceFile);
		}

		public async Task<string> BuildContextAsync(bool compress)
		{
			var analyzer = new FileContentAnalyzer();
			using var session = CodeCompressionFactory.CreateSession();
			var context = compress
				? new ContentTransformationContext(new CodeCompressionContext(SourceRoot, session), null)
				: null;
			var builder = new PreviewDocumentBuilder(analyzer);
			var document = await builder.BuildContentDocumentAsync(
				[SourceFile],
				TestContext.Current.CancellationToken,
				displayPathMapper: null,
				includeOmissionMarkers: false,
				transformationContext: context);
			Assert.NotNull(document);
			using (document)
				return document.GetFullText();
		}

		public Task<ProjectCopyExportResult> ExportFolderAsync(bool compress) =>
			ExportAsync(
				Path.Combine(DestinationParent, compress ? "compressed" : "plain"),
				ProjectCopyExportFormat.Folder,
				compress);

		public Task<ProjectCopyExportResult> ExportZipAsync(string destination, bool compress) =>
			ExportAsync(destination, ProjectCopyExportFormat.Zip, compress);

		private async Task<ProjectCopyExportResult> ExportAsync(
			string destination,
			ProjectCopyExportFormat format,
			bool compress)
		{
			using var session = CodeCompressionFactory.CreateSession();
			var service = new ProjectCopyExportService(
				new ProjectCopyExportPlanBuilder(),
				new FileContentAnalyzer(),
				secretRedactionSession: null,
				codeCompressionSession: session);
			return await service.ExportAsync(
				new ProjectCopyExportRequest(
					SourceRoot,
					"Sample",
					BuildTree(),
					new HashSet<string>(PathComparer.Default),
					destination,
					format,
					ProjectCopyDestinationMode.Exact,
					ProjectCopyConflictPolicy.Fail,
					RedactSecrets: false,
					CompressCode: compress,
					NoticeText: new ProjectCopyNoticeText("redaction notice", "compression notice")),
				progress: null,
				TestContext.Current.CancellationToken);
		}

		private TreeNodeDescriptor BuildTree() =>
			new("Sample", SourceRoot, true, false, "folder",
				[new TreeNodeDescriptor("Widget.cs", SourceFile, false, false, "file", [])]);

		public void Dispose()
		{
			try
			{
				Directory.Delete(Root, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Temp cleanup is best effort; a held handle must not fail a passing assertion.
			}
		}
	}
}
