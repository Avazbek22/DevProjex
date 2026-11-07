using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed partial class GitTrackedPathIndexTests
{
	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(7)]
	[InlineData(1024)]
	[InlineData(4095)]
	[InlineData(4096)]
	public async Task NullDelimitedReaderPreservesUtf8PathsAcrossEveryStreamBoundary(int fragmentLength)
	{
		const int seed = 62_030;
		var random = new Random(seed);
		var alphabet = new[] { "a", "é", "日本", "😀", " ", "\t", "\n", "\\", ";" };
		var paths = Enumerable.Range(0, 128).Select(index =>
			index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
			string.Concat(Enumerable.Range(0, 45).Select(_ => alphabet[random.Next(alphabet.Length)])) + ".txt").ToList();
		paths.Add(new string('x', 8191) + "😀.txt");
		var bytes = Encoding.UTF8.GetBytes(string.Join('\0', paths) + '\0');
		await using var stream = new FragmentedGitOutput(bytes, fragmentLength);
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var aborted = false;
		var actual = await GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(reader, TestContext.Current.CancellationToken,
			() => aborted = true);
		Assert.False(aborted, $"Seed={seed}; fragment length={fragmentLength}");
		Assert.Equal(paths, actual);
	}

	[Fact]
	public async Task NullDelimitedReaderCancellationStopsAnIncompletePathWithoutPublishingPartialResults()
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		await cancellation.CancelAsync();
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first.txt\0unfinished"));
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var abortCount = 0;
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			GitTrackedPathIndexCache.ReadNullDelimitedPathsAsync(reader, cancellation.Token, () => abortCount++));
		Assert.Equal(0, abortCount);
	}

	private sealed class FragmentedGitOutput(byte[] bytes, int fragmentLength) : MemoryStream(bytes, writable: false)
	{
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentLength)], cancellationToken);
	}
}
