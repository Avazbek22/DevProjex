using System.Xml.Linq;
using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class ProjectContextDocumentUnicodeIntegrationTests
{
	private const string Secret = "fixture-secret-value";
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly UnicodeEncoding Utf16LittleEndian =
		new(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);

	[Fact]
	public async Task StructuredDocuments_PreserveSurrogatePairsAcrossConsolidatedSnapshotChunks()
	{
		using var temporary = new TemporaryDirectory();
		var separateRoot = CreateProject(temporary, "separate", 255);
		var consolidatedRoot = CreateProject(temporary, "consolidated", 256);
		var separatePlan = await BuildPlanAsync(separateRoot);
		var consolidatedPlan = await BuildPlanAsync(consolidatedRoot);
		var analyzer = new FileContentAnalyzer();

		await using var separatePrepared = await PrepareAsync(analyzer, separatePlan);
		await using var consolidatedPrepared = await PrepareAsync(analyzer, consolidatedPlan);

		foreach (var format in new[] { ProjectContextDocumentFormat.Json, ProjectContextDocumentFormat.Xml })
		{
			var separate = await WritePreparedAsync(analyzer, separatePlan, separatePrepared, format);
			var consolidated = await WritePreparedAsync(analyzer, consolidatedPlan, consolidatedPrepared, format);
			foreach (var path in TargetPaths())
			{
				var separateContent = ExtractContent(separate, format, path);
				var consolidatedContent = ExtractContent(consolidated, format, path);
				Assert.Equal(Utf8WithoutBom.GetBytes(separateContent), Utf8WithoutBom.GetBytes(consolidatedContent));
				Assert.DoesNotContain('\uFFFD', consolidatedContent);
				Assert.Contains("😀", consolidatedContent, StringComparison.Ordinal);
			}
		}
	}

	private static string CreateProject(TemporaryDirectory temporary, string name, int fileCount)
	{
		var root = temporary.CreateDirectory(name);
		foreach (var (path, encoding, prefixLength) in TargetFiles())
		{
			var fullPath = Path.Combine(root, path);
			var content = string.Concat(new string('x', prefixLength), "😀\n", Secret, "\n");
			File.WriteAllText(fullPath, content, encoding);
		}

		for (var index = TargetFiles().Count; index < fileCount; index++)
			File.WriteAllText(Path.Combine(root, $"dummy-{index:D3}.txt"), $"{Secret}\n", Utf8WithoutBom);
		return root;
	}

	private static IReadOnlyList<(string Path, Encoding Encoding, int PrefixLength)> TargetFiles() =>
	[
		("utf8-8191.txt", Utf8WithoutBom, 8190),
		("utf8-8192.txt", Utf8WithoutBom, 8191),
		("utf8-8193.txt", Utf8WithoutBom, 8192),
		("utf16-8191.txt", Utf16LittleEndian, 8190),
		("utf16-8192.txt", Utf16LittleEndian, 8191),
		("utf16-8193.txt", Utf16LittleEndian, 8192)
	];

	private static IEnumerable<string> TargetPaths() => TargetFiles().Select(static file => file.Path);

	private static async Task<ProjectContextPlan> BuildPlanAsync(string projectRoot)
	{
		var analysisService = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		return await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				projectRoot,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: [],
					HideSecrets: true)),
			TestContext.Current.CancellationToken);
	}

	private static async Task<PreparedSecretRedactionOutput> PrepareAsync(
		IFileContentAnalyzer analyzer,
		ProjectContextPlan plan)
	{
		using var session = new SecretRedactionSession(new ExactValueDetector());
		return await new SecretRedactionOutputPreparer(analyzer).PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(plan.SourceRoot, session)),
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
	}

	private static async Task<string> WritePreparedAsync(
		IFileContentAnalyzer analyzer,
		ProjectContextPlan plan,
		PreparedSecretRedactionOutput prepared,
		ProjectContextDocumentFormat format)
	{
		using var destination = new MemoryStream();
		await new ProjectContextDocumentService(new TreeExportService(), analyzer)
			.WritePreparedCompleteAsync(
				plan,
				ProjectContextView.Content,
				format,
				destination,
				prepared,
				TestContext.Current.CancellationToken);
		return Utf8WithoutBom.GetString(destination.ToArray());
	}

	private static string ExtractContent(
		string document,
		ProjectContextDocumentFormat format,
		string path)
	{
		if (format == ProjectContextDocumentFormat.Json)
		{
			using var json = JsonDocument.Parse(document);
			return json.RootElement.GetProperty("files")
				.EnumerateArray()
				.Single(file => string.Equals(file.GetProperty("path").GetString(), path, StringComparison.Ordinal))
				.GetProperty("content")
				.GetString()!;
		}

		var xml = XDocument.Parse(document);
		return xml.Root!.Element("files")!
			.Elements("file")
			.Single(file => string.Equals(file.Attribute("path")?.Value, path, StringComparison.Ordinal))
			.Element("content")!
			.Value;
	}

	private sealed class ExactValueDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var index = content.IndexOf(Secret, StringComparison.Ordinal);
			return index < 0
				? []
				: [new DetectedSecret("fixture-secret", index, Secret.Length, Secret, RuleOrder: 0)];
		}
	}
}
