using System.Collections;
using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class DependencyFactsCancellationTests(ITestOutputHelper output)
{
	[Fact]
	public async Task AlreadyCanceledColdRequest_DoesNotEnumerateManifestOrReadConfiguration()
	{
		using var fixture = new TemporaryDirectory();
		using var cancellation = new CancellationTokenSource();
		var manifest = LargeManifest(fixture.Path);
		var configuration = new ConfigurationProvider();
		var extractor = new SyntheticExtractor();
		using var engine = new DependencyFactsEngine(extractor, configuration);
		cancellation.Cancel();

		var exception = await Record.ExceptionAsync(() => engine.IndexAsync(
			fixture.Path, manifest, cancellationToken: cancellation.Token));

		output.WriteLine($"Manifest reads={manifest.ReadCount}, configuration reads={configuration.ReadCount}");
		Assert.Equal(0, manifest.ReadCount);
		Assert.Equal(0, configuration.ReadCount);
		Assert.Equal(0, extractor.ParseCount);
		Assert.IsAssignableFrom<OperationCanceledException>(exception);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AlreadyCanceledWarmRequest_DoesNotReturnCachedSuccess(bool emptyManifest)
	{
		using var fixture = new TemporaryDirectory();
		using var cancellation = new CancellationTokenSource();
		var manifest = new CountingPaths(emptyManifest ? [] : [fixture.CreateFile("Source.cs", string.Empty)]);
		var configuration = new ConfigurationProvider();
		using var engine = new DependencyFactsEngine(new SyntheticExtractor(), configuration);
		await engine.IndexAsync(fixture.Path, manifest, cancellationToken: TestContext.Current.CancellationToken);
		manifest.ReadCount = 0;
		cancellation.Cancel();

		var exception = await Record.ExceptionAsync(() => engine.IndexAsync(
			fixture.Path, manifest, cancellationToken: cancellation.Token));

		Assert.IsAssignableFrom<OperationCanceledException>(exception);
		Assert.Equal(0, manifest.ReadCount);
		Assert.Equal(1, configuration.ReadCount);
	}

	[Fact]
	public async Task CancellationDuringManifestEnumeration_StopsBeforeProcessingRemainingPaths()
	{
		using var fixture = new TemporaryDirectory();
		using var cancellation = new CancellationTokenSource();
		var manifest = LargeManifest(fixture.Path);
		manifest.OnRead = cancellation.Cancel;
		var configuration = new ConfigurationProvider();
		using var engine = new DependencyFactsEngine(new SyntheticExtractor(), configuration);

		var exception = await Record.ExceptionAsync(() => engine.IndexAsync(
			fixture.Path, manifest, cancellationToken: cancellation.Token));

		output.WriteLine($"Manifest reads after cancellation={manifest.ReadCount}");
		Assert.Equal(1, manifest.ReadCount);
		Assert.Equal(0, configuration.ReadCount);
		Assert.IsAssignableFrom<OperationCanceledException>(exception);
	}

	[Fact]
	public async Task CancellationDuringWarmControlFileValidation_StopsBeforeRemainingProbes()
	{
		using var fixture = new TemporaryDirectory();
		using var cancellation = new CancellationTokenSource();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		var controls = new CountingPaths(Enumerable.Range(0, 100)
			.Select(index => Path.Combine(fixture.Path, $"absent-{index}.json")).ToArray());
		var configuration = new ConfigurationProvider { AbsentControlFiles = controls };
		using var engine = new DependencyFactsEngine(new SyntheticExtractor(), configuration);
		await engine.IndexAsync(fixture.Path, [file], cancellationToken: TestContext.Current.CancellationToken);
		controls.ReadCount = 0;
		controls.OnRead = cancellation.Cancel;

		var exception = await Record.ExceptionAsync(() => engine.IndexAsync(
			fixture.Path, [file], cancellationToken: cancellation.Token));

		output.WriteLine($"Control file candidates after cancellation={controls.ReadCount}");
		Assert.Equal(1, controls.ReadCount);
		Assert.Equal(1, configuration.ReadCount);
		Assert.IsAssignableFrom<OperationCanceledException>(exception);
	}

	[Fact]
	public async Task WarmProgressCancellation_IsObservedBeforeReturningSnapshot()
	{
		using var fixture = new TemporaryDirectory();
		using var cancellation = new CancellationTokenSource();
		var file = fixture.CreateFile("Source.cs", string.Empty);
		using var engine = new DependencyFactsEngine(new SyntheticExtractor(), new ConfigurationProvider());
		await engine.IndexAsync(fixture.Path, [file], cancellationToken: TestContext.Current.CancellationToken);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.IndexAsync(
			fixture.Path, [file], new InlineProgress(cancellation.Cancel), cancellation.Token));
	}

	private static CountingPaths LargeManifest(string root) => new(Enumerable.Range(0, 10_000)
		.Select(index => Path.Combine(root, $"File{index:D5}.cs")).ToArray());

	private sealed class CountingPaths(string[] paths) : IReadOnlyList<string>
	{
		public int Count => paths.Length;
		public int ReadCount { get; set; }
		public Action? OnRead { get; set; }
		public string this[int index]
		{
			get
			{
				ReadCount++;
				OnRead?.Invoke();
				return paths[index];
			}
		}
		public IEnumerator<string> GetEnumerator()
		{
			for (var index = 0; index < paths.Length; index++)
				yield return this[index];
		}
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class InlineProgress(Action report) : IProgress<DependencyIndexProgress>
	{
		public void Report(DependencyIndexProgress value) => report();
	}

	private sealed class SyntheticExtractor : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;
		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot, string fullPath, DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits, CancellationToken cancellationToken, string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath, relative, "fixture", LanguageId.CSharp, relative, "synthetic:v1", string.Empty));
		}
		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			return new FileFacts(source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint,
				0, DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), [], [], [], [],
				new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}
		public void Dispose() { }
	}

	private sealed class ConfigurationProvider : IDependencyConfigurationProvider
	{
		public int ReadCount { get; private set; }
		public IReadOnlyList<string> AbsentControlFiles { get; init; } = [];
		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot, IReadOnlyList<string> manifestFiles, CancellationToken cancellationToken)
		{
			ReadCount++;
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(new DependencyResolverConfiguration("fixture", [],
				new Dictionary<string, PackageMapDescriptor>(), new HashSet<string>(),
				new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>())
			{ AbsentControlFiles = AbsentControlFiles });
		}
	}
}
