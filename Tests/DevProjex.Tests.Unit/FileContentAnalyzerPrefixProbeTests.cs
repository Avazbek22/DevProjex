using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class FileContentAnalyzerPrefixProbeTests
{
	[Fact]
	public void PrefixProbeProvesBinaryContentFromTheLeadingBytesAlone()
	{
		using var temp = new TemporaryDirectory();
		var content = Enumerable.Repeat((byte)'A', 1024 * 1024).ToArray();
		content[100] = 0;
		var path = temp.CreateBinaryFile("model.asset", content);
		var opened = 0;
		var analyzer = new FileContentAnalyzer((filePath, bufferSize, share, asynchronous) =>
		{
			opened++;
			return new FileStream(filePath, FileMode.Open, FileAccess.Read, share, bufferSize);
		});

		Assert.Null(analyzer.ClassifyWithoutReading(path));
		Assert.Equal(FileContentClassification.Binary, analyzer.ClassifyFromPrefix(path));
		Assert.Equal(1, opened);
	}

	[Fact]
	public void PrefixProbeLeavesTextAndBomEncodedTextForTheFullRead()
	{
		using var temp = new TemporaryDirectory();
		var text = temp.CreateFile("notes.txt", "ordinary text\n");
		var utf16 = temp.CreateBinaryFile(
			"wide.txt",
			[.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide text")]);
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var analyzer = new FileContentAnalyzer();

		Assert.Null(analyzer.ClassifyFromPrefix(text));
		Assert.Null(analyzer.ClassifyFromPrefix(utf16));
		Assert.Null(analyzer.ClassifyFromPrefix(empty));
	}

	[Fact]
	public void PrefixProbeAnswersAKnownBinaryExtensionWithoutOpeningTheFile()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("logo.png", "not really an image");
		var analyzer = new FileContentAnalyzer((_, _, _, _) =>
			throw new InvalidOperationException("A known binary extension must not be opened."));

		Assert.Equal(FileContentClassification.Binary, analyzer.ClassifyFromPrefix(path));
	}

	[Fact]
	public void PrefixProbeLeavesAnUnreadableFileForTheFullReadToExplain()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("locked.txt", "text");
		var analyzer = new FileContentAnalyzer((_, _, _, _) => throw new IOException("locked"));

		Assert.Null(analyzer.ClassifyFromPrefix(path));
	}

	[Fact]
	public void OutputPreparerChargesOnlySourcesItWouldDecode()
	{
		using var temp = new TemporaryDirectory();
		var binary = new byte[128 * 1024];
		var asset = temp.CreateBinaryFile("scene.asset", binary);
		var source = temp.CreateFile("App.cs", "class App {}\n");
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		Assert.Equal(
			FileContentClassification.TooLarge,
			preparer.ClassifyBeforeDecoding(
				Path.Combine(temp.Path, "missing.txt"),
				SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1,
				probeContent: false));
		Assert.Null(preparer.ClassifyBeforeDecoding(asset, binary.Length, probeContent: false));
		Assert.Equal(
			FileContentClassification.Binary,
			preparer.ClassifyBeforeDecoding(asset, binary.Length, probeContent: true));
		Assert.Null(preparer.ClassifyBeforeDecoding(source, 13, probeContent: true));
	}
}
