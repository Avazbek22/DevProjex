using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class MarkedSecretsMatcherMemoryTests(ITestOutputHelper output)
{
	private const string MarkedValue = "persistent-secret-value";

	[Fact(Timeout = 15_000)]
	public void NewlineDenseMaximumFile_UsesBoundedPositionIndexMemory()
	{
		var content = new string('\n', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes));
		var matcher = CreateMatcher();
		var legacyAllocated = MeasureLegacyPositionIndexAllocations(content);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var findings = matcher.Match(
			"config.env",
			content,
			TestContext.Current.CancellationToken);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		output.WriteLine(
			"16 MiB newline index: legacy={0:N0} B, bitsets+match={1:N0} B",
			legacyAllocated,
			allocated);

		Assert.Empty(findings);
		Assert.InRange(allocated, 1, 8L * 1024 * 1024);
		Assert.True(allocated * 10 < legacyAllocated);
	}

	[Fact]
	public void PositionIndex_PreservesBoundaryAndLineBreakMatching()
	{
		var content = $"prefix {MarkedValue}\r\n{MarkedValue}\n{MarkedValue}\r{MarkedValue} suffix";
		var matcher = CreateMatcher();

		var findings = matcher.Match(
			"config.env",
			content,
			TestContext.Current.CancellationToken);

		Assert.Equal(4, findings.Count);
		Assert.All(findings, finding => Assert.Equal(MarkedValue, finding.Value));
		var expectedStarts = new List<int>();
		for (var searchStart = 0;;)
		{
			var start = content.IndexOf(MarkedValue, searchStart, StringComparison.Ordinal);
			if (start < 0)
				break;
			expectedStarts.Add(start);
			searchStart = start + MarkedValue.Length;
		}
		Assert.Equal(
			expectedStarts,
			findings.Select(static finding => finding.Start));
	}

	private static MarkedSecretsMatcher CreateMatcher() =>
		new(
		[
			new MarkedSecretProfileEntry(
				MarkedSecretValueNormalizer.ComputeHash(MarkedValue),
				"TOKEN",
				MarkedValue.Length)
		],
		[]);

	private static long MeasureLegacyPositionIndexAllocations(string content)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var boundaryBits = new ulong[((content.Length + 1) + 63) >> 6];
		var lineBreakPositions = new List<int>();
		for (var position = 0; position <= content.Length; position++)
		{
			if (SecretTokenBoundary.IsBoundary(content, position))
				boundaryBits[position >> 6] |= 1UL << (position & 63);
			if (position < content.Length && content[position] is '\r' or '\n')
				lineBreakPositions.Add(position);
		}
		var positions = lineBreakPositions.ToArray();
		GC.KeepAlive(boundaryBits);
		GC.KeepAlive(positions);
		return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
	}
}
