using System.Diagnostics;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Unit;

public sealed class AgentJournalStartupMeasurementTests(ITestOutputHelper output)
{
	[Fact]
	public void MeasureConstructorSweepWithIsolatedEmptyAndPopulatedJournals()
	{
		Assert.SkipWhen(Environment.GetEnvironmentVariable("DEVPROJEX_JOURNAL_STARTUP_BENCHMARK") != "1",
			"Set DEVPROJEX_JOURNAL_STARTUP_BENCHMARK=1 to measure isolated journal startup.");
		var samples = new List<ConstructorSample>();
		foreach (var headerCount in new[] { 0, 200, 512 })
		{
			using var fixture = new TemporaryDirectory();
			var journalDirectory = fixture.CreateFolder("agent-journal");
			var headers = CreateHeaders(fixture.Path, headerCount);
			var headerBytes = headers.Sum(header => (long)Encoding.UTF8.GetByteCount(header.Json));
			for (var iteration = 0; iteration < 4; iteration++)
			{
				TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
				// Restore the same workload outside timing; default retention removes excess sessions.
				foreach (var header in headers)
					fixture.CreateFile(Path.Combine("agent-journal", header.FileName), header.Json);
				var filesBefore = Directory.GetFiles(journalDirectory, "*.jsonl").Length;
				var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
				var started = Stopwatch.GetTimestamp();
				using var store = new AgentJournalStore(() => fixture.Path);
				var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
				var filesAfter = Directory.GetFiles(journalDirectory, "*.jsonl").Length;
				Assert.Equal(headerCount, filesBefore);
				Assert.Equal(Math.Min(headerCount, AgentJournalRetentionPolicy.Default.MaximumSessions), filesAfter);
				Assert.Equal(AgentJournalRetentionPolicy.Default, store.Retention);
				samples.Add(new ConstructorSample(
					headerCount, headerBytes, iteration, elapsed, allocated, filesBefore, filesAfter));
			}
		}
		output.WriteLine("JOURNAL_STARTUP_SAMPLES=" + JsonSerializer.Serialize(samples));
	}

	private static Header[] CreateHeaders(string root, int count)
	{
		var startedUtc = DateTimeOffset.UtcNow;
		return Enumerable.Range(0, count).Select(index =>
		{
			var started = startedUtc.AddSeconds(-index);
			var pid = 100_000 + index;
			var session = new AgentJournalSession(
				AgentJournalStore.CreateSessionId(started, pid), started, null, pid, started.AddMinutes(-1),
				"measurement-client", "1.0", AgentJournalMode.Standard,
				[new AgentJournalRoot(root, "fixture")], AgentJournalToolSet.Full, "5.2.0", false,
				AgentJournalTotals.Empty, false);
			var json = JsonSerializer.Serialize(new AgentJournalLine("session", Session: session),
				AgentJournalJsonSerializerContext.Default.AgentJournalLine) + Environment.NewLine;
			return new Header(session.Id + ".jsonl", json);
		}).ToArray();
	}

	private sealed record Header(string FileName, string Json);
	private sealed record ConstructorSample(
		int ValidHeaderFilesPresented,
		long FixtureHeaderBytes,
		int Iteration,
		double ConstructorMilliseconds,
		long ConstructorAllocatedBytes,
		int FilesBefore,
		int FilesAfter);
}
