using System.Collections;
using System.IO.Compression;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class NavigationCachePayloadTests
{
	[Fact]
	public void PayloadPreservesEveryDeclarationFieldAndUtf16CodeUnit()
	{
		var declarations = Enum.GetValues<NavigationSymbolKind>().Select((kind, index) =>
		 new NavigationDeclaration("Owner.名前_Ж😀\ud800\u0000\r\n" + index, kind, index % 2 == 0 ? null : "", index, index + 3, "fingerprint")
		 { StartIndex = index - 1, EndIndex = index + 17 }).ToArray();
		var actual = NavigationCachePayload.Decode(NavigationCachePayload.Encode(declarations, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
		Assert.Equal(declarations, actual);
		Assert.Empty(NavigationCachePayload.Decode(NavigationCachePayload.Encode([], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
	}

	[Fact]
	public void LargeNamesAndRepeatedOwnersRoundTripWithoutGrowingThePayload()
	{
		var owner = new string('Ж', 100_000) + "\r\n\t😀";
		var declarations = Enumerable.Range(0, 8).Select(index => new NavigationDeclaration(owner + index, NavigationSymbolKind.Method, owner, 1, 2, "fingerprint")).ToArray();
		var packed = NavigationCachePayload.Encode(declarations, TestContext.Current.CancellationToken);
		Assert.True(packed.Length < owner.Length);
		Assert.Equal(declarations, NavigationCachePayload.Decode(packed, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void EncodingObservesCancellationAfterEnumerationHasStarted()
	{
		using var cancelled = new CancellationTokenSource();
		var declaration = new NavigationDeclaration("Example", NavigationSymbolKind.Type, null, 1, 1, "fingerprint");
		var source = new CancellingList([declaration, declaration], cancelled);
		Assert.Throws<OperationCanceledException>(() => NavigationCachePayload.Encode(source, cancelled.Token));
		var packed = NavigationCachePayload.Encode([declaration], TestContext.Current.CancellationToken);
		Assert.Throws<OperationCanceledException>(() => NavigationCachePayload.Decode(packed, cancelled.Token));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(int.MaxValue)]
	public void DecoderRejectsAnInvalidStringTableBeforeAllocating(int count)
	{
		using var output = new MemoryStream();
		using (var compressed = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
		using (var writer = new BinaryWriter(compressed)) writer.Write(count);
		Assert.Throws<InvalidDataException>(() => NavigationCachePayload.Decode(output.ToArray(), TestContext.Current.CancellationToken));
	}

	[Fact]
	public void AWorkingSetOverTheRawBudgetIsReusedWithinTheSameBound()
	{
		var extractor = new NavigationExtractor(500);
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider());
		var token = TestContext.Current.CancellationToken;
		var snapshots = Enumerable.Range(0, 40).Select(index => engine.ExtractNavigationFromProtectedText($"src/{index}.cs", "source", "fingerprint", token)).ToArray();
		Assert.True(engine.NavigationCacheState.Packed > 0);
		Assert.InRange(engine.NavigationCacheState.Bytes, 1, DependencyFactsEngine.MaximumNavigationCacheBytes);
		Assert.Equal(40, extractor.ParseCount);
		for (var index = 0; index < snapshots.Length; index++)
			Assert.Equal(snapshots[index], engine.ExtractNavigationFromProtectedText($"src/{index}.cs", "source", "fingerprint", token));
		Assert.Equal(40, extractor.ParseCount);
		Assert.InRange(engine.NavigationCacheState.Bytes, 1, DependencyFactsEngine.MaximumNavigationCacheBytes);
	}

	[Fact]
	public void SmallWorkingSetsKeepTheSameCachedSnapshotAndAChangedFingerprintReparses()
	{
		var extractor = new NavigationExtractor(1);
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider());
		var token = TestContext.Current.CancellationToken;
		var first = engine.ExtractNavigationFromProtectedText("small.cs", "source", "one", token);
		Assert.Same(first, engine.ExtractNavigationFromProtectedText("small.cs", "source", "one", token));
		Assert.Equal(0, engine.NavigationCacheState.Packed);
		var changed = engine.ExtractNavigationFromProtectedText("small.cs", "source", "two", token);
		Assert.Equal("two", Assert.Single(changed).ContentFingerprint);
		Assert.Equal(2, extractor.ParseCount);
		using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
		Assert.Throws<OperationCanceledException>(() => engine.ExtractNavigationFromProtectedText("small.cs", "source", "two", cancellation.Token));
	}

	private sealed class CancellingList(IReadOnlyList<NavigationDeclaration> declarations, CancellationTokenSource cancellation) : IReadOnlyList<NavigationDeclaration>
	{
		public int Count => declarations.Count;
		public NavigationDeclaration this[int index] => declarations[index];
		public IEnumerator<NavigationDeclaration> GetEnumerator()
		{
			for (var index = 0; index < declarations.Count; index++)
			{
				if (index == 1) cancellation.Cancel();
				yield return declarations[index];
			}
		}
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class NavigationExtractor(int count) : IDependencyFactExtractor, IDependencyNavigationExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;
		public IReadOnlyList<NavigationDeclaration> ExtractNavigation(string relativePath, string source, string contentFingerprint, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested(); ParseCount++;
			return Enumerable.Range(0, count).Select(index => new NavigationDeclaration(
			 "Example." + new string('n', 120) + ".Type" + index, NavigationSymbolKind.Type,
			 "Example." + new string('n', 120), index + 1, index + 2, contentFingerprint)
			{ StartIndex = index * 3, EndIndex = index * 3 + 4 }).ToArray();
		}
		public ValueTask<PreparedDependencySource> PrepareAsync(string sourceRoot, string fullPath, DependencyResolverConfiguration configuration, DependencyFactsLimits limits, CancellationToken cancellationToken, string? contentIdentity = null) => throw new NotSupportedException();
		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits) => throw new NotSupportedException();
		public void Dispose() { }
	}
}
