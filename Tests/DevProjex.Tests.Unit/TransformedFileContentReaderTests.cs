using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Unit;

public sealed class TransformedFileContentReaderTests
{
	private const string Secret = "secret-value-123456";

	[Fact]
	public async Task ReadAsync_WithoutTransformations_ReturnsOriginalContent()
	{
		using var temporary = new TemporaryDirectory();
		var path = temporary.CreateFile("config.txt", $"token={Secret}");
		var analyzer = new FileContentAnalyzer();
		var reader = new TransformedFileContentReader(
			analyzer,
			new SecretRedactionOutputPreparer(analyzer));

		var result = await reader.ReadAsync(
			path,
			transformationContext: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Text, result.Classification);
		Assert.Equal($"token={Secret}", result.Content);
	}

	[Fact]
	public async Task ReadAsync_WithSecretRedaction_NeverReturnsRawSecret()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var path = temporary.CreateFile("project/config.txt", $"token={Secret}");
		var analyzer = new FileContentAnalyzer();
		using var session = new SecretRedactionSession(new ExactValueDetector());
		var reader = new TransformedFileContentReader(
			analyzer,
			new SecretRedactionOutputPreparer(analyzer));

		var result = await reader.ReadAsync(
			path,
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(root, session)),
			TestContext.Current.CancellationToken);

		Assert.True(result.HasText);
		Assert.DoesNotContain(Secret, result.Content, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[test-secret#1]", result.Content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReadAsync_WithCompression_ReturnsThePreparedCompressedText()
	{
		const string source = """
			public sealed class Sample
			{

			    public int Value => 1;

			}
			""";
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("project");
		var path = temporary.CreateFile("project/Sample.cs", source);
		var analyzer = new FileContentAnalyzer();
		using var compression = CodeCompressionFactory.CreateSession();
		var reader = new TransformedFileContentReader(
			analyzer,
			new SecretRedactionOutputPreparer(analyzer));

		var result = await reader.ReadAsync(
			path,
			new ContentTransformationContext(
				new CodeCompressionContext(root, compression, CodeTransformKinds.BlankLines),
				Redaction: null),
			TestContext.Current.CancellationToken);

		Assert.True(result.HasText);
		Assert.DoesNotContain($"{{{Environment.NewLine}{Environment.NewLine}", result.Content, StringComparison.Ordinal);
		Assert.DoesNotContain($";{Environment.NewLine}{Environment.NewLine}}}", result.Content, StringComparison.Ordinal);
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
				: [new DetectedSecret("test-secret", index, Secret.Length, Secret, 0)];
		}
	}
}
