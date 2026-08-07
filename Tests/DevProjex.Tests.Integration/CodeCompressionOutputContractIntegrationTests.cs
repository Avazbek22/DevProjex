using System.IO.Compression;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
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

	private const string MarkedConstantValue = "SessionMarkedConstantValue";
	private const string MarkedBodyValue = "SessionMarkedBodyValue";

	// The constant sits AFTER the body compression removes, so its line number genuinely moves.
	// A fixture with the constant first would pass without any translation at all.
	private const string MarkableSource = """
		namespace Sample;

		public sealed class Widget
		{
			public int Compute(int left, int right)
			{
				var embedded = "SessionMarkedBodyValue";
				var total = left + right;
				for (var index = 0; index < 8; index++)
					total += index * left - right;
				return total + embedded.Length;
			}

			public const string ApiKey = "SessionMarkedConstantValue";
		}
		""";

	/// <summary>
	/// A mark made while looking at the uncompressed file has to keep working once compression is
	/// switched on. Its coordinates describe the source, the text being scanned is the compressed
	/// output, and the transform map is what carries the anchor from one to the other.
	///
	/// The two values are deliberately on opposite sides of that map: one lives in a constant that
	/// survives compression and must stay hidden, the other lives in a method body that compression
	/// removes and must leave with it rather than land on whatever now sits at those coordinates.
	/// </summary>
	[Fact]
	public async Task SessionMarkMadeBeforeCompression_StillHidesTheValueThatSurvivesIt()
	{
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());
		MarkInSource(secrets, MarkableSource, MarkedConstantValue);
		MarkInSource(secrets, MarkableSource, MarkedBodyValue);

		var compressed = await workspace.BuildContentAsync(secrets, compress: true);

		Assert.Contains("public int Compute(int left, int right)", compressed.Text);
		// Guards the fixture itself: the constant has to be further from the signature in the source
		// than in the output, otherwise nothing moved and translating the anchor is not what makes
		// this pass. Measured as a distance so the document's own header lines cannot flatter it.
		Assert.True(
			LinesBetweenSignatureAndConstant(MarkableSource) >
			LinesBetweenSignatureAndConstant(compressed.Text),
			"compression did not move the marked constant, so this test proves nothing");
		Assert.DoesNotContain(MarkedConstantValue, compressed.Text);
		Assert.DoesNotContain(MarkedBodyValue, compressed.Text);
		// One span, not two: the body value was never in the scanned text to redact.
		Assert.Equal(1, compressed.RedactionCount);
	}

	/// <summary>
	/// Compression leaves plenty of files whole - unsupported languages, and supported ones with no
	/// executable body. Their map is the identity, so a mark keeps applying exactly as captured. The
	/// mark here is stamped with no transform while the run has one, which is the combination a user
	/// produces by marking a value and then ticking the checkbox.
	/// </summary>
	[Fact]
	public async Task SessionMarkOnAFileCompressionLeavesWhole_StillApplies()
	{
		// Deliberately not one of the values in the C# fixture, so a hit here can only come from
		// the file compression left whole.
		const string notesValue = "SessionMarkedNotesValue";
		const string plainText = $"api_token = {notesValue}\n";
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		var notesPath = workspace.CreateExtraFile("notes.txt", plainText);
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());
		Assert.True(MarkedSecretValueNormalizer.TryCreate(notesValue, out var marked, out _));
		Assert.True(secrets.AddSessionMarkedSecret(
			"notes.txt",
			0,
			plainText.IndexOf(notesValue, StringComparison.Ordinal),
			marked));

		var compressed = await workspace.BuildContentAsync(secrets, compress: true, extraFile: notesPath);

		Assert.Contains("api_token = ", compressed.Text);
		Assert.DoesNotContain(notesValue, compressed.Text);
	}

	/// <summary>
	/// The clipboard and the text-file export are the surfaces the desktop app drives through
	/// <see cref="SelectedContentExportService"/>, not through the preview document. They have to
	/// carry the same bytes as everything else: the transformed text, with redaction applied at the
	/// offsets that describe it.
	/// </summary>
	[Fact]
	public async Task ClipboardExport_CarriesCompressedTextAndRedactsAtItsOffsets()
	{
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());
		MarkInSource(secrets, MarkableSource, MarkedConstantValue);

		var clipboard = await workspace.BuildClipboardAsync(secrets, compress: true);

		Assert.Contains("public int Compute(int left, int right)", clipboard);
		Assert.DoesNotContain("total += index * left - right", clipboard);
		// The mark sits after the removed body, so a plan applied to the untransformed text would
		// redact the wrong characters and leave this value visible.
		Assert.DoesNotContain(MarkedConstantValue, clipboard);
		Assert.Contains("public const string ApiKey", clipboard);
	}

	[Fact]
	public async Task ClipboardExport_WithoutCompression_KeepsTheOriginalText()
	{
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());

		var clipboard = await workspace.BuildClipboardAsync(secrets, compress: false);

		Assert.Contains("total += index * left - right", clipboard);
	}

	[Fact]
	public async Task SessionMarkMadeBeforeCompression_HidesBothValuesWhileCompressionIsOff()
	{
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());
		MarkInSource(secrets, MarkableSource, MarkedConstantValue);
		MarkInSource(secrets, MarkableSource, MarkedBodyValue);

		var plain = await workspace.BuildContentAsync(secrets, compress: false);

		Assert.DoesNotContain(MarkedConstantValue, plain.Text);
		Assert.DoesNotContain(MarkedBodyValue, plain.Text);
		Assert.Equal(2, plain.RedactionCount);
	}

	private static int LinesBetweenSignatureAndConstant(string text)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');
		var signature = Array.FindIndex(
			lines,
			line => line.Contains("public int Compute", StringComparison.Ordinal));
		var constant = Array.FindIndex(
			lines,
			line => line.Contains("public const string ApiKey", StringComparison.Ordinal));
		Assert.True(signature >= 0 && constant > signature, "the fixture landmarks are missing");
		return constant - signature;
	}

	/// <summary>Marks the value where it sits in the file, exactly as a click on the preview would.</summary>
	private static void MarkInSource(SecretRedactionSession session, string source, string value)
	{
		var lines = source.Replace("\r\n", "\n").Split('\n');
		var lineIndex = Array.FindIndex(lines, line => line.Contains(value, StringComparison.Ordinal));
		Assert.True(lineIndex >= 0, $"'{value}' is not in the fixture source");
		Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var marked, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"Widget.cs",
			lineIndex,
			lines[lineIndex].IndexOf(value, StringComparison.Ordinal),
			marked));
	}

	private sealed class NoFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private readonly record struct TransformedContent(string Text, int RedactionCount);

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

		/// <summary>The clipboard and text-file export path, which does not use the preview document.</summary>
		public async Task<string> BuildClipboardAsync(SecretRedactionSession secrets, bool compress)
		{
			using var session = CodeCompressionFactory.CreateSession();
			return await new SelectedContentExportService(new FileContentAnalyzer()).BuildAsync(
				[SourceFile],
				TestContext.Current.CancellationToken,
				displayPathMapper: null,
				new ContentTransformationContext(
					compress ? new CodeCompressionContext(SourceRoot, session) : null,
					new SecretRedactionContext(SourceRoot, secrets)));
		}

		public string CreateExtraFile(string name, string content)
		{
			var path = Path.Combine(SourceRoot, name);
			File.WriteAllText(path, content);
			return path;
		}

		/// <summary>Builds the preview document - the single source of truth for every export.</summary>
		public async Task<TransformedContent> BuildContentAsync(
			SecretRedactionSession secrets,
			bool compress,
			string? extraFile = null)
		{
			using var session = CodeCompressionFactory.CreateSession();
			var context = new ContentTransformationContext(
				compress ? new CodeCompressionContext(SourceRoot, session) : null,
				new SecretRedactionContext(SourceRoot, secrets));
			var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
				.BuildContentDocumentAsync(
					extraFile is null ? [SourceFile] : [SourceFile, extraFile],
					TestContext.Current.CancellationToken,
					displayPathMapper: null,
					includeOmissionMarkers: false,
					transformationContext: context);
			Assert.NotNull(document);
			using (document)
				return new TransformedContent(document.GetFullText(), document.Redactions.Count);
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
