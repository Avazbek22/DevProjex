using System.IO.Compression;
using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.ProjectProfiles;

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
	private const string AutomaticRetainedSecret = "AutomaticRetainedSecret123";
	private const string AutomaticRemovedSecret = "AutomaticRemovedSecret123";

	[Fact]
	public async Task CompressionOnlyPreparationPreservesSelectionOrderAndSkipsUnchangedTempCopies()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = Enumerable.Range(0, 32)
			.Select(index =>
			{
				var path = Path.Combine(projectRoot, $"notes-{index:D2}.txt");
				File.WriteAllText(path, $"unchanged-{index}");
				return path;
			})
			.Reverse()
			.ToArray();
		using var session = CodeCompressionFactory.CreateSession();
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(new CodeCompressionContext(projectRoot, session), null),
			paths,
			TestContext.Current.CancellationToken);

		var snapshot = Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot);
		Assert.Equal(0, snapshot.CompressedFiles);
		Assert.Equal(paths.Length, snapshot.UnchangedFiles);
		Assert.Equal(
			paths.Select(path => Path.GetRelativePath(projectRoot, path)),
			snapshot.Unchanged.Select(static outcome => outcome.RelativePath));
		foreach (var path in paths)
			Assert.Equal(path, prepared.GetFile(path).ContentPath);
	}

	[Fact]
	public async Task CompressionOnlyPreparationTransformsEverySupportedFileInTheParallelBatch()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("project");
		var paths = Enumerable.Range(0, 24)
			.Select(index =>
			{
				var path = Path.Combine(projectRoot, $"Widget{index:D2}.cs");
				File.WriteAllText(path, CompressibleSource.Replace("Widget", $"Widget{index}", StringComparison.Ordinal));
				return path;
			})
			.ToArray();
		using var session = CodeCompressionFactory.CreateSession();
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(new CodeCompressionContext(projectRoot, session), null),
			paths,
			TestContext.Current.CancellationToken);

		var snapshot = Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot);
		Assert.Equal(paths.Length, snapshot.CompressedFiles);
		Assert.Equal(0, snapshot.UnchangedFiles);
		foreach (var path in paths)
		{
			var transformed = await File.ReadAllTextAsync(
				prepared.GetFile(path).ContentPath,
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain("var total", transformed, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task CompressionOnlyPreparationTransformsAllTenLanguagesInOneMixedWorkspace()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("mixed-project");
		var sources = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["sample.c"] = "int add(int a, int b) { int implementation_marker = a + b; implementation_marker += 10; return implementation_marker; }",
			["sample.cpp"] = "int add(int a, int b) { int implementation_marker = a + b; implementation_marker += 10; return implementation_marker; }",
			["Sample.cs"] = "sealed class Sample { int Add(int a, int b) { var implementation_marker = a + b; implementation_marker += 10; return implementation_marker; } }",
			["sample.go"] = "package sample\nfunc add(a int, b int) int { implementation_marker := a + b; implementation_marker += 10; return implementation_marker }",
			["Sample.java"] = "final class Sample { int add(int a, int b) { int implementation_marker = a + b; implementation_marker += 10; return implementation_marker; } }",
			["sample.js"] = "export function add(a, b) { let implementation_marker = a + b; implementation_marker += 10; return implementation_marker; }",
			["sample.py"] = "def add(a, b):\n    implementation_marker = a + b\n    implementation_marker += 10\n    return implementation_marker\n",
			["sample.rs"] = "fn add(a: i32, b: i32) -> i32 { let mut implementation_marker = a + b; implementation_marker += 10; implementation_marker }",
			["sample.ts"] = "export function add(a: number, b: number): number { let implementation_marker = a + b; implementation_marker += 10; return implementation_marker; }",
			["sample.tsx"] = "export function Sample() { const implementation_marker = 42; return <section>{implementation_marker + 10}</section>; }"
		};
		var paths = sources.Select(pair =>
		{
			var path = Path.Combine(projectRoot, pair.Key);
			File.WriteAllText(path, pair.Value);
			return path;
		}).ToArray();
		using var session = CodeCompressionFactory.CreateSession();

		await using var prepared = await new SecretRedactionOutputPreparer(new FileContentAnalyzer())
			.PrepareAsync(
				new ContentTransformationContext(new CodeCompressionContext(projectRoot, session), null),
				paths,
				TestContext.Current.CancellationToken);

		var snapshot = Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot);
		Assert.Equal(10, snapshot.CompressedFiles);
		Assert.Equal(0, snapshot.UnchangedFiles);
		foreach (var path in paths)
		{
			var transformed = await File.ReadAllTextAsync(
				prepared.GetFile(path).ContentPath,
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain("implementation_marker", transformed, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task CompressionPreparationCarriesPythonAndTsxBalanceRulesIntoOutputFiles()
	{
		const string pythonSource = """"
			class Session:
			    def __init__(self, root):
			        self.root = root
			        self.items = []

			    def scan(self):
			        """Scan the configured root."""
			        python_implementation_marker = list(self.root.walk())
			        return python_implementation_marker
			"""";
		const string tsxSource = """
			const Panel = memo(forwardRef((props, ref) => {
			  const wrapped_implementation_marker = props.value;
			  return <section ref={ref}>{wrapped_implementation_marker}</section>;
			}));
			const normalize = (value) => value.trim();
			const Card = (props) => (
			  <article>
			    <h2>{props.title}</h2>
			    <p>{props.multiline_jsx_marker}</p>
			  </article>
			);
			const Pipeline = (items) =>
			  items
			    .map((item) => item.value)
			    .filter((multiline_chain_marker) => multiline_chain_marker);
			export const options = {
			  name: "Panel",
			  methods: {
			    render: function (value) {
			      const object_implementation_marker = value + 1;
			      return object_implementation_marker;
			    },
			    normalize: (value) => value + 1,
			  },
			};
			""";
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("balanced-project");
		var pythonPath = Path.Combine(projectRoot, "session.py");
		var tsxPath = Path.Combine(projectRoot, "Panel.tsx");
		File.WriteAllText(pythonPath, pythonSource);
		File.WriteAllText(tsxPath, tsxSource);
		using var session = CodeCompressionFactory.CreateSession();

		await using var prepared = await new SecretRedactionOutputPreparer(new FileContentAnalyzer())
			.PrepareAsync(
				new ContentTransformationContext(new CodeCompressionContext(projectRoot, session), null),
				[pythonPath, tsxPath],
				TestContext.Current.CancellationToken);

		var snapshot = Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot);
		Assert.Equal(2, snapshot.CompressedFiles);
		var python = await File.ReadAllTextAsync(
			prepared.GetFile(pythonPath).ContentPath,
			TestContext.Current.CancellationToken);
		var normalizedPython = python.ReplaceLineEndings("\n");
		Assert.Contains("self.root = root", python, StringComparison.Ordinal);
		Assert.Contains(
			"\"\"\"Scan the configured root.\"\"\"\n        ...",
			normalizedPython,
			StringComparison.Ordinal);
		Assert.DoesNotContain("python_implementation_marker", python, StringComparison.Ordinal);

		var tsx = await File.ReadAllTextAsync(
			prepared.GetFile(tsxPath).ContentPath,
			TestContext.Current.CancellationToken);
		var normalizedTsx = tsx.ReplaceLineEndings("\n");
		Assert.Contains("const Panel = memo(forwardRef((props, ref) => { }));", tsx, StringComparison.Ordinal);
		Assert.Contains("const normalize = (value) => value.trim();", tsx, StringComparison.Ordinal);
		Assert.Contains("const Card = (props) => { };", tsx, StringComparison.Ordinal);
		Assert.Contains("const Pipeline = (items) =>\n  { };", normalizedTsx, StringComparison.Ordinal);
		Assert.Contains("name: \"Panel\"", tsx, StringComparison.Ordinal);
		Assert.Contains("render: function (value) { }", tsx, StringComparison.Ordinal);
		Assert.Contains("normalize: (value) => value + 1", tsx, StringComparison.Ordinal);
		Assert.DoesNotContain("wrapped_implementation_marker", tsx, StringComparison.Ordinal);
		Assert.DoesNotContain("multiline_jsx_marker", tsx, StringComparison.Ordinal);
		Assert.DoesNotContain("multiline_chain_marker", tsx, StringComparison.Ordinal);
		Assert.DoesNotContain("object_implementation_marker", tsx, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CompressionWithHideSecrets_PreparesSmallFilesConcurrentlyAndCommitsInSelectionOrder()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("parallel-project");
		var paths = Enumerable.Range(0, 4)
			.Select(index =>
			{
				var path = Path.Combine(projectRoot, $"Widget{index:D2}.cs");
				File.WriteAllText(
					path,
					CompressibleSource.Replace("Widget", $"Widget{index}", StringComparison.Ordinal));
				return path;
			})
			.ToArray();
		var analyzer = new ConcurrentReadGateAnalyzer(new FileContentAnalyzer(), requiredConcurrency: 2);
		using var compression = CodeCompressionFactory.CreateSession();
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());

		await using var prepared = await new SecretRedactionOutputPreparer(analyzer).PrepareAsync(
			new ContentTransformationContext(
				new CodeCompressionContext(projectRoot, compression),
				new SecretRedactionContext(projectRoot, secrets)),
			paths,
			TestContext.Current.CancellationToken);

		Assert.True(
			analyzer.PeakConcurrentReads >= 2,
			$"Expected bounded parallel preparation, observed {analyzer.PeakConcurrentReads} concurrent read(s).");
		var compressionSnapshot = Assert.IsType<CodeCompressionSnapshot>(prepared.CompressionSnapshot);
		Assert.Equal(paths.Length, compressionSnapshot.CompressedFiles);
		Assert.Equal(0, Assert.IsType<SecretRedactionSnapshot>(prepared.Snapshot).RedactedCount);
		foreach (var path in paths)
		{
			var transformed = await File.ReadAllTextAsync(
				prepared.GetFile(path).ContentPath,
				TestContext.Current.CancellationToken);
			Assert.DoesNotContain("var total", transformed, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void LocalProfileRoundTripsCompressionAsAnOptInTransformation()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("profile-project");
		var store = new ProjectProfileStore(() => temporary.CreateDirectory("profile-data"));
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.CompressCode],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.CompressCode] = true,
				[IgnoreOptionId.SmartIgnore] = false
			});

		Assert.True(store.TrySaveProfile(projectRoot, profile));
		Assert.True(store.TryLoadProfile(projectRoot, out var loaded));
		Assert.Contains(IgnoreOptionId.CompressCode, loaded.SelectedIgnoreOptions);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.CompressCode]);
		Assert.True(ProjectSelectionAdapter
			.FromLegacyProfile(loaded, ProjectProfileReference.Local)
			.CompressCode);
	}

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

	[Fact]
	public async Task SessionMarkMadeOnCompressedPreview_RemainsHiddenWhenCompressionIsDisabled()
	{
		using var workspace = CompressionWorkspace.Create(MarkableSource);
		var compressedSource = workspace.CompressSource();
		var transformedOffset = compressedSource.Text.IndexOf(MarkedConstantValue, StringComparison.Ordinal);
		Assert.True(transformedOffset >= 0);
		var coordinates = PreviewContentCoordinateMap.Create(compressedSource.Text, compressedSource.Map);
		var (line, column) = ResolveLineAndColumn(compressedSource.Text, transformedOffset);
		Assert.True(coordinates.TryToSourceOffset(line, column, out var sourceOffset));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(MarkedConstantValue, out var marked, out _));
		using var secrets = new SecretRedactionSession(new NoFindingsDetector());
		Assert.True(secrets.AddSessionMarkedSecret("Widget.cs", sourceOffset, marked));

		var compressed = await workspace.BuildContentAsync(secrets, compress: true);
		var plain = await workspace.BuildContentAsync(secrets, compress: false);

		Assert.DoesNotContain(MarkedConstantValue, compressed.Text);
		Assert.DoesNotContain(MarkedConstantValue, plain.Text);
		Assert.Equal(1, compressed.RedactionCount);
		Assert.Equal(1, plain.RedactionCount);
	}

	[Fact]
	public async Task ManualMarkAfterMultilineRedaction_MapsThroughFinalPreviewAcrossCompressionStates()
	{
		const string privateKey = "-----BEGIN PRIVATE KEY-----\nabc123\n-----END PRIVATE KEY-----";
		const string laterSecret = "late-secret-value-123";
		var source =
			"public sealed class Secrets\n{\n" +
			"    public const string Pem = \"\"\"\n" + privateKey + "\n\"\"\";\n" +
			$"    public const string Later = \"{laterSecret}\";\n" +
			"    public void Run()\n    {\n        Console.WriteLine(Later);\n    }\n}\n";
		using var workspace = CompressionWorkspace.Create(source);
		using var secrets = new SecretRedactionSession(new ExactValueDetector(privateKey));
		using var compressionSession = CodeCompressionFactory.CreateSession();
		var context = new ContentTransformationContext(
			new CodeCompressionContext(workspace.SourceRoot, compressionSession),
			new SecretRedactionContext(workspace.SourceRoot, secrets));
		using var preview = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				[workspace.SourceFile],
				TestContext.Current.CancellationToken,
				displayPathMapper: null,
				includeOmissionMarkers: false,
				transformationContext: context,
				includeSourceCoordinateMaps: true);
		Assert.NotNull(preview);
		var section = Assert.Single(preview.Sections);
		var finalText = preview.GetFullText();
		Assert.DoesNotContain(privateKey, finalText, StringComparison.Ordinal);
		var finalOffset = finalText.IndexOf(laterSecret, StringComparison.Ordinal);
		Assert.True(finalOffset >= 0);
		var (line, column) = ResolveLineAndColumn(finalText, finalOffset);
		Assert.NotNull(section.CoordinateMap);
		Assert.True(section.CoordinateMap.TryToSourceOffset(
			line + 1 - section.ContentStartLine,
			column,
			out var sourceOffset));
		Assert.Equal(source.IndexOf(laterSecret, StringComparison.Ordinal), sourceOffset);
		Assert.True(MarkedSecretValueNormalizer.TryCreate(laterSecret, out var marked, out _));
		Assert.True(secrets.AddSessionMarkedSecret("Widget.cs", sourceOffset, marked));

		var compressed = await workspace.BuildContentAsync(secrets, compress: true);
		var plain = await workspace.BuildContentAsync(secrets, compress: false);
		var compressedAgain = await workspace.BuildContentAsync(secrets, compress: true);

		Assert.DoesNotContain(laterSecret, compressed.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(laterSecret, plain.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(laterSecret, compressedAgain.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AutomaticDetection_SeesTheCompressedOutputRatherThanRemovedImplementation()
	{
		const string source = $$"""
			public sealed class Secrets
			{
			    public const string Retained = "{{AutomaticRetainedSecret}}";

			    public string Build()
			    {
			        const string removed = "{{AutomaticRemovedSecret}}";
			        return removed + Retained;
			    }
			}
			""";
		using var workspace = CompressionWorkspace.Create(source);
		using var secrets = new SecretRedactionSession(
			new ExactValueDetector(AutomaticRetainedSecret, AutomaticRemovedSecret));

		var result = await workspace.BuildContentAsync(secrets, compress: true);

		Assert.DoesNotContain(AutomaticRetainedSecret, result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(AutomaticRemovedSecret, result.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[exact-value#1]", result.Text, StringComparison.Ordinal);
		Assert.Equal(1, result.RedactionCount);
		var snapshot = Assert.IsType<SecretRedactionSnapshot>(workspace.GetSnapshot(secrets, compress: true));
		Assert.Equal(1, snapshot.DetectedCount);
		Assert.Equal(1, snapshot.RedactedCount);
	}

	[Fact]
	public async Task TransformationAnalysis_UsesTheSameCompressedInputAsPreview()
	{
		const string source = $$"""
			public sealed class Secrets
			{
			    public const string Retained = "{{AutomaticRetainedSecret}}";

			    public string Build()
			    {
			        const string removed = "{{AutomaticRemovedSecret}}";
			        return removed + Retained;
			    }
			}
			""";
		using var workspace = CompressionWorkspace.Create(source);
		using var secrets = new SecretRedactionSession(
			new ExactValueDetector(AutomaticRetainedSecret, AutomaticRemovedSecret));
		using var compression = CodeCompressionFactory.CreateSession();
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var compressionSnapshotsPublished = 0;
		compression.SnapshotPublished += (_, _) => compressionSnapshotsPublished++;

		var transformed = await preparer.AnalyzeAsync(
			new ContentTransformationContext(
				new CodeCompressionContext(workspace.SourceRoot, compression),
				new SecretRedactionContext(workspace.SourceRoot, secrets)),
			[workspace.SourceFile],
			TestContext.Current.CancellationToken);
		var raw = await preparer.AnalyzeAsync(
			new SecretRedactionContext(workspace.SourceRoot, secrets),
			[workspace.SourceFile],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, transformed.DetectedCount);
		Assert.Equal(1, transformed.RedactedCount);
		Assert.Equal(2, raw.DetectedCount);
		Assert.Equal(2, raw.RedactedCount);
		Assert.Equal(0, compressionSnapshotsPublished);
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

	private static (int Line, int Column) ResolveLineAndColumn(string text, int offset)
	{
		var line = 0;
		var lineStart = 0;
		for (var index = 0; index < offset; index++)
		{
			if (text[index] != '\n')
				continue;
			line++;
			lineStart = index + 1;
		}

		return (line, offset - lineStart);
	}

	/// <summary>Marks the value where it sits in the file, exactly as a click on the preview would.</summary>
	private static void MarkInSource(SecretRedactionSession session, string source, string value)
	{
		var sourceOffset = source.IndexOf(value, StringComparison.Ordinal);
		Assert.True(sourceOffset >= 0, $"'{value}' is not in the fixture source");
		Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var marked, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"Widget.cs",
			sourceOffset,
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

	[Fact]
	public async Task ContextFolderAndZipKeepFieldsAndPropertiesWhileCompressingMethods()
	{
		const string source = """
			public sealed class Settings
			{
			    private readonly System.Func<int, int> _normalize = value =>
			    {
			        var preserved_field_marker = value + 1;
			        return preserved_field_marker;
			    };

			    public int Value
			    {
			        get
			        {
			            var preserved_property_marker = _normalize(41);
			            return preserved_property_marker;
			        }
			    }

			    public int Calculate(int value)
			    {
			        var removed_method_marker = value + 2;
			        removed_method_marker += 3;
			        return removed_method_marker;
			    }
			}
			""";
		using var workspace = CompressionWorkspace.Create(source);

		var context = await workspace.BuildContextAsync(compress: true);
		var folder = await workspace.ExportFolderAsync(compress: true);
		var folderText = await File.ReadAllTextAsync(
			Path.Combine(folder.DestinationPath, "Widget.cs"),
			TestContext.Current.CancellationToken);
		var zipPath = Path.Combine(workspace.DestinationParent, "preserved-members.zip");
		await workspace.ExportZipAsync(zipPath, compress: true);
		using var archive = ZipFile.OpenRead(zipPath);
		var sourceEntry = archive.Entries.Single(entry =>
			entry.FullName.EndsWith("Widget.cs", StringComparison.Ordinal));
		using var reader = new StreamReader(sourceEntry.Open());
		var zipText = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

		foreach (var output in new[] { context, folderText, zipText })
		{
			Assert.Contains("preserved_field_marker", output, StringComparison.Ordinal);
			Assert.Contains("preserved_property_marker", output, StringComparison.Ordinal);
			Assert.DoesNotContain("removed_method_marker", output, StringComparison.Ordinal);
		}
		Assert.Equal(folderText, zipText);
	}

	[Fact]
	public async Task FolderCopy_WithCompression_PreservesUtf16BigEndianEncodingAndBom()
	{
		using var workspace = CompressionWorkspace.Create(CompressibleSource);
		var encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
		await File.WriteAllTextAsync(
			workspace.SourceFile,
			CompressibleSource,
			encoding,
			TestContext.Current.CancellationToken);
		var sourceBytes = await File.ReadAllBytesAsync(
			workspace.SourceFile,
			TestContext.Current.CancellationToken);

		var result = await workspace.ExportFolderAsync(compress: true);
		var exportedPath = Path.Combine(result.DestinationPath, "Widget.cs");
		var exportedBytes = await File.ReadAllBytesAsync(
			exportedPath,
			TestContext.Current.CancellationToken);
		var exportedText = await File.ReadAllTextAsync(
			exportedPath,
			encoding,
			TestContext.Current.CancellationToken);

		Assert.True(exportedBytes.AsSpan().StartsWith(encoding.GetPreamble()));
		Assert.Contains("public int Compute(int left, int right)", exportedText, StringComparison.Ordinal);
		Assert.DoesNotContain("total += index * left - right", exportedText, StringComparison.Ordinal);
		Assert.Equal(
			sourceBytes,
			await File.ReadAllBytesAsync(
				workspace.SourceFile,
				TestContext.Current.CancellationToken));
	}

	public static TheoryData<string, bool> CompressionEncodingAndNewlineCases => new()
	{
		{ "utf8-bom", false },
		{ "utf8-bom", true },
		{ "utf16-le", false },
		{ "utf16-le", true },
		{ "utf16-be", false },
		{ "utf16-be", true },
		{ "utf32-le", false },
		{ "utf32-le", true },
		{ "utf32-be", false },
		{ "utf32-be", true }
	};

	[Theory]
	[MemberData(nameof(CompressionEncodingAndNewlineCases))]
	public async Task FolderAndZipCompression_PreserveEncodingBomAndLineEndings(
		string encodingName,
		bool useCrlf)
	{
		var encoding = CreateEncoding(encodingName);
		var normalizedSource = CompressibleSource.Replace("\r\n", "\n", StringComparison.Ordinal);
		var source = useCrlf
			? normalizedSource.Replace("\n", "\r\n", StringComparison.Ordinal)
			: normalizedSource;
		using var workspace = CompressionWorkspace.Create(source);
		await File.WriteAllTextAsync(
			workspace.SourceFile,
			source,
			encoding,
			TestContext.Current.CancellationToken);

		var folder = await workspace.ExportFolderAsync(compress: true);
		var folderBytes = await File.ReadAllBytesAsync(
			Path.Combine(folder.DestinationPath, "Widget.cs"),
			TestContext.Current.CancellationToken);
		var zip = await workspace.ExportZipAsync(
			Path.Combine(workspace.DestinationParent, "compressed.zip"),
			compress: true);
		byte[] zipBytes;
		using (var archive = ZipFile.OpenRead(zip.DestinationPath))
		{
			var entry = Assert.Single(archive.Entries, candidate =>
				candidate.FullName.EndsWith("Widget.cs", StringComparison.Ordinal));
			await using var entryStream = entry.Open();
			await using var buffer = new MemoryStream();
			await entryStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
			zipBytes = buffer.ToArray();
		}

		foreach (var outputBytes in new[] { folderBytes, zipBytes })
		{
			Assert.True(outputBytes.AsSpan().StartsWith(encoding.GetPreamble()));
			var text = encoding.GetString(outputBytes.AsSpan(encoding.GetPreamble().Length));
			Assert.DoesNotContain("total += index * left - right", text, StringComparison.Ordinal);
			if (useCrlf)
			{
				Assert.DoesNotContain(
					"\n",
					text.Replace("\r\n", string.Empty, StringComparison.Ordinal),
					StringComparison.Ordinal);
			}
			else
			{
				Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
			}
		}
	}

	private static Encoding CreateEncoding(string name) => name switch
	{
		"utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
		"utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
		"utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
		"utf32-le" => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
		"utf32-be" => new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
		_ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
	};

	private sealed class ExactValueDetector(params string[] values) : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var findings = new List<DetectedSecret>();
			foreach (var value in values)
			{
				for (var offset = 0;
				     (offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
				     offset += value.Length)
				{
					cancellationToken.ThrowIfCancellationRequested();
					findings.Add(new DetectedSecret(
						"exact-value",
						offset,
						value.Length,
						value,
						0));
				}
			}

			return findings;
		}
	}

	private sealed class ConcurrentReadGateAnalyzer(
		IFileContentAnalyzer inner,
		int requiredConcurrency) : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _gate = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		private int _enteredReads;
		private int _activeReads;
		private int _peakConcurrentReads;

		public int PeakConcurrentReads => Volatile.Read(ref _peakConcurrentReads);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		public async ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			var active = Interlocked.Increment(ref _activeReads);
			UpdateMaximum(ref _peakConcurrentReads, active);
			if (Interlocked.Increment(ref _enteredReads) >= requiredConcurrency)
				_gate.TrySetResult();
			try
			{
				await _gate.Task
					.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
					.ConfigureAwait(false);
				return await inner
					.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken)
					.ConfigureAwait(false);
			}
			finally
			{
				Interlocked.Decrement(ref _activeReads);
			}
		}

		private static void UpdateMaximum(ref int target, int candidate)
		{
			var observed = Volatile.Read(ref target);
			while (candidate > observed)
			{
				var exchanged = Interlocked.CompareExchange(ref target, candidate, observed);
				if (exchanged == observed)
					return;
				observed = exchanged;
			}
		}
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

		public CodeCompressionResult CompressSource()
		{
			using var session = CodeCompressionFactory.CreateSession();
			using var scope = session.BeginOutput(SourceRoot, [SourceFile]);
			var result = scope.Transform(
				SourceFile,
				"Widget.cs",
				File.ReadAllText(SourceFile),
				TestContext.Current.CancellationToken);
			scope.Complete();
			return result;
		}

		public SecretRedactionSnapshot? GetSnapshot(
			SecretRedactionSession secrets,
			bool compress)
		{
			using var compression = CodeCompressionFactory.CreateSession();
			return secrets.GetSnapshot(
				SourceRoot,
				[SourceFile],
				compress ? compression.TransformIdentity : string.Empty);
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
