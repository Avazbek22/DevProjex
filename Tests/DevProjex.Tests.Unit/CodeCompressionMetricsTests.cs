using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class CodeCompressionMetricsTests
{
	public static TheoryData<string, CodeCompressionPlan> ContractCases
	{
		get
		{
			var cases = new TheoryData<string, CodeCompressionPlan>
			{
				{
			string.Empty,
			CodeCompressionPlan.Unchanged(
				"empty.cs",
				"csharp",
				CodeCompressionOutcome.UnchangedNoBenefit,
				0,
				"test")
				}
			};
			AddCase(cases, "alpha\nbeta\n", new CodeCompressionEdit(0, 5, "x"));
			AddCase(cases, "alpha\r\nbeta\r\n", new CodeCompressionEdit(7, 4, "{ }"));
			AddCase(cases, "alpha\r\nbeta\ngamma\rdelta", new CodeCompressionEdit(7, 11, "..."));
			AddCase(cases, "😀 alpha\nβeta `code`\n", new CodeCompressionEdit(3, 5, "{ }"));
			AddCase(cases, "first middle last", new CodeCompressionEdit(0, 5, "x"));
			AddCase(cases, "first middle last", new CodeCompressionEdit(13, 4, "x"));
			AddCase(cases,
			"0123456789abcdef",
			new CodeCompressionEdit(2, 4, "x"),
			new CodeCompressionEdit(6, 4, "y"));
			AddCase(cases,
			"def work():\r\n    first = 1\r\n    second = 2\r\n",
			new CodeCompressionEdit(17, 27, "...\r\n"));
			AddCase(cases,
			"void Work()\n{\n    Execute();\n}\n",
			new CodeCompressionEdit(12, 19, "{ }"));
			AddCase(cases,
			"// docs\r\nint value = 1; // trailing\r\n",
			new CodeCompressionEdit(0, 9, string.Empty) { Kinds = CodeTransformKinds.Comments },
			new CodeCompressionEdit(23, 11, string.Empty) { Kinds = CodeTransformKinds.Comments });
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(ContractCases))]
	public void MetricsFromPlan_EqualMetricsFromMaterializedOutput(
		string source,
		CodeCompressionPlan plan)
	{
		var applied = plan.Apply(source).Text;

		var expected = FileContentAnalyzer.ComputeMetrics(
			applied,
			Encoding.UTF8.GetByteCount(applied));
		var actual = FileContentAnalyzer.ComputeTransformedMetrics(source, plan);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void MetricsFromPlan_RandomNonOverlappingEditsMatchMaterializedOutput()
	{
		var random = new Random(0x5EED);
		for (var iteration = 0; iteration < 500; iteration++)
		{
			var source = CreateRandomSource(random, 64 + random.Next(192));
			var edits = CreateRandomEdits(random, source.Length);
			var plan = CodeCompressionPlan.Create(
				"random.cs",
				"csharp",
				edits,
				source.Length,
				"test");
			var applied = plan.Apply(source).Text;
			var expected = FileContentAnalyzer.ComputeMetrics(
				applied,
				Encoding.UTF8.GetByteCount(applied));

			var actual = FileContentAnalyzer.ComputeTransformedMetrics(source, plan);

			Assert.Equal(expected, actual);
		}
	}

	[Theory]
	[InlineData("page.html", "<!-- first -->\r\n<main>Ready<!-- trailing --></main>\r\n")]
	[InlineData("site.css", "/* first */\r\n.card { color: red; /* trailing */ }\r\n")]
	[InlineData("app.toml", "# first\r\nname = \"api#worker\" # trailing\r\n")]
	[InlineData("deploy.sh", "#!/usr/bin/env bash\r\n# first\r\nvalue=1 # trailing")]
	[InlineData("view.axaml", "<?xml version=\"1.0\"?>\r\n<!-- first -->\r\n<Root>Ready<!-- trailing --></Root>\r\n")]
	[InlineData("deployment.yaml", "---\r\n# first\r\nname: \"api#worker\" # trailing\r\n")]
	public void MetricsFromCommentOnlyLanguagePlan_EqualMaterializedOutput(
		string path,
		string source)
	{
		using var compressor = CodeCompressionTestHarness.CreateCompressor();
		using var scope = compressor.CreateScope(Path.GetTempPath(), CodeTransformKinds.Comments);
		var analysis = scope.Analyze(path, path, source, TestContext.Current.CancellationToken);
		var applied = analysis.GetResult(source).Text;
		var expected = FileContentAnalyzer.ComputeMetrics(
			applied,
			Encoding.UTF8.GetByteCount(applied));

		var actual = FileContentAnalyzer.ComputeTransformedMetrics(source, analysis.Plan);

		Assert.Equal(CodeCompressionOutcome.Compressed, analysis.Plan.Outcome);
		Assert.Equal(expected, actual);
	}

	private static void AddCase(
		TheoryData<string, CodeCompressionPlan> cases,
		string source,
		params CodeCompressionEdit[] edits) =>
		cases.Add(source, CodeCompressionPlan.Create(
			"sample.cs",
			"csharp",
			edits,
			source.Length,
			"test"));

	private static string CreateRandomSource(Random random, int targetLength)
	{
		var builder = new StringBuilder(targetLength + 8);
		var fragments = new[] { "a", "Z", " ", "\t", "\n", "\r", "\r\n", "`", "β", "😀" };
		while (builder.Length < targetLength)
			builder.Append(fragments[random.Next(fragments.Length)]);
		return builder.ToString();
	}

	private static IReadOnlyList<CodeCompressionEdit> CreateRandomEdits(Random random, int length)
	{
		var edits = new List<CodeCompressionEdit>();
		var cursor = random.Next(0, 4);
		while (cursor + 3 <= length && edits.Count < 12)
		{
			var sourceLength = Math.Min(3 + random.Next(10), length - cursor);
			if (sourceLength <= 2)
				break;
			edits.Add(new CodeCompressionEdit(cursor, sourceLength, random.Next(2) == 0 ? "x" : "{}"));
			cursor += sourceLength + random.Next(0, 8);
		}
		return edits;
	}
}
