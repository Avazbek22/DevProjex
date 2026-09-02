using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

[Trait("Category", "LocalPerformance")]
public sealed class PersistentSecretIdentityConcurrencyBenchmarkTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_SECRET_HMAC_BENCHMARK";
	private const int FileCount = 8;
	private const int CandidateLength = 64;
	private const int MeasuredRuns = 3;

	[Fact(Timeout = 900_000)]
	public async Task SharedProvider_CharacterizesDigestLockContention()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
			Assert.Skip($"Set {EnabledVariable}=1 to run the HMAC contention benchmark.");

		using var workspace = new TemporaryDirectory();
		using var provider = new PersistentSecretIdentityProvider(() => workspace.CreateFolder("app-data"));
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await provider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		using var monitorBaseline = new MonitorDigest();

		foreach (var fileSizeMiB in new[] { 1, 4, 16 })
		{
			var content = new string('x', fileSizeMiB * 1024 * 1024);
			foreach (var markCount in new[] { 1, 100, 1_000 })
			{
				var callsPerFile = Math.Max(2_048, checked(fileSizeMiB * markCount * 4));
				foreach (var parallelism in new[] { 1, 8 })
				{
					var locked = Measure(
						monitorBaseline.TryComputeDigest,
						content,
						callsPerFile,
						parallelism);
					var optimized = Measure(
						(candidate, digest) => provider.TryComputeDigest(candidate, digest),
						content,
						callsPerFile,
						parallelism);
					var improvement = locked <= 0 ? 0 : (locked - optimized) / locked * 100;
					output.WriteLine(
						$"files={FileCount}, size={fileSizeMiB}MiB, marks={markCount}, " +
						$"parallel={parallelism}, calls/file={callsPerFile}: " +
						$"monitor={locked:F3}ms, reader-gate={optimized:F3}ms, delta={improvement:+0.00;-0.00;0.00}%");
				}
			}
		}
	}

	private static double Measure(
		Digest digest,
		string content,
		int callsPerFile,
		int parallelism)
	{
		var samples = new double[MeasuredRuns];
		for (var run = 0; run < MeasuredRuns; run++)
		{
			var started = Stopwatch.GetTimestamp();
			if (parallelism == 1)
			{
				for (var file = 0; file < FileCount; file++)
					RunFile(digest, content, callsPerFile, file);
			}
			else
			{
				Parallel.For(
					0,
					FileCount,
					new ParallelOptions { MaxDegreeOfParallelism = parallelism },
					file => RunFile(digest, content, callsPerFile, file));
			}
			samples[run] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		}
		Array.Sort(samples);
		return samples[samples.Length / 2];
	}

	private static void RunFile(Digest digest, string content, int calls, int fileIndex)
	{
		Span<byte> destination = stackalloc byte[PersistentSecretIdentity.V2DigestByteLength];
		var maximumStart = content.Length - CandidateLength;
		for (var call = 0; call < calls; call++)
		{
			var start = (int)(((long)call * 997 + fileIndex * 101) % maximumStart);
			if (!digest(content.AsSpan(start, CandidateLength), destination))
				throw new InvalidOperationException("The benchmark digest provider became unavailable.");
		}
	}

	private delegate bool Digest(ReadOnlySpan<char> value, Span<byte> destination);

	private sealed class MonitorDigest : IDisposable
	{
		private readonly object _sync = new();
		private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

		public bool TryComputeDigest(ReadOnlySpan<char> value, Span<byte> destination)
		{
			lock (_sync)
			{
				var maximumByteCount = Encoding.UTF8.GetMaxByteCount(value.Length);
				byte[]? rented = null;
				Span<byte> utf8 = maximumByteCount <= 2 * 1024
					? stackalloc byte[maximumByteCount]
					: (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
				try
				{
					var byteCount = Encoding.UTF8.GetBytes(value, utf8);
					HMACSHA256.HashData(_key, utf8[..byteCount], destination);
					CryptographicOperations.ZeroMemory(utf8[..byteCount]);
					return true;
				}
				finally
				{
					if (rented is not null)
						ArrayPool<byte>.Shared.Return(rented, clearArray: true);
				}
			}
		}

		public void Dispose()
		{
			lock (_sync)
				CryptographicOperations.ZeroMemory(_key);
		}
	}
}
