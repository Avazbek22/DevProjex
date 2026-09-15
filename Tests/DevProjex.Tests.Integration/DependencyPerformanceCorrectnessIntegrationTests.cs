using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed class DependencyPerformanceCorrectnessIntegrationTests
{
	[Fact]
	public async Task GuardedSourceReader_RejectsAParentReplacedByAnExternalDirectoryLink()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("workspace/Source.cs", "public sealed class SafeSource { }");
		var externalDirectory = Directory.CreateDirectory(Path.Combine(outside.Path, "workspace")).FullName;
		File.WriteAllText(Path.Combine(externalDirectory, "Source.cs"), "public sealed class LeakedSource { }");
		var registry = new McpRootRegistry([fixture.Path]);
		var jail = new McpRootJailFileStreamOpener(registry);
		var swapped = false;
		FileStream OpenGuarded(string path, int bufferSize, FileShare share, bool asynchronous)
		{
			if (!swapped && Path.GetFullPath(path) == Path.GetFullPath(source))
			{
				ReplaceWithExternalDirectoryLinkOrSkip(Path.GetDirectoryName(source)!, externalDirectory);
				swapped = true;
			}
			return jail.OpenRead(path, bufferSize, share, asynchronous);
		}
		using var extractor = new TreeSitterDependencyFactExtractor(OpenGuarded);
		using var engine = new DependencyFactsEngine(
			extractor,
			new FileDependencyConfigurationProvider(OpenGuarded));

		await Assert.ThrowsAsync<McpToolException>(() => engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken));

		Assert.True(swapped);
		Assert.Equal(0, extractor.ParseCount);
	}

	[Fact]
	public async Task GuardedControlReader_RejectsAParentReplacedByAnExternalDirectoryLink()
	{
		using var fixture = new TemporaryDirectory();
		using var outside = new TemporaryDirectory();
		var config = fixture.CreateFile("workspace/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var source = fixture.CreateFile("workspace/consumer.ts", "import '@target';");
		var target = fixture.CreateFile("workspace/target.ts", "export const local = 1;");
		var externalDirectory = Directory.CreateDirectory(Path.Combine(outside.Path, "workspace")).FullName;
		File.WriteAllText(Path.Combine(externalDirectory, "tsconfig.json"),
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"@target\":[\"target.ts\"]}}}");
		File.WriteAllText(Path.Combine(externalDirectory, "consumer.ts"), "import '@target';");
		File.WriteAllText(Path.Combine(externalDirectory, "target.ts"), "export const external = 1;");
		var jail = new McpRootJailFileStreamOpener(new McpRootRegistry([fixture.Path]));
		var swapped = false;
		FileStream OpenGuarded(string path, int bufferSize, FileShare share, bool asynchronous)
		{
			if (!swapped && Path.GetFullPath(path) == Path.GetFullPath(config))
			{
				ReplaceWithExternalDirectoryLinkOrSkip(Path.GetDirectoryName(config)!, externalDirectory);
				swapped = true;
			}
			return jail.OpenRead(path, bufferSize, share, asynchronous);
		}
		using var extractor = new TreeSitterDependencyFactExtractor(OpenGuarded);
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider(OpenGuarded));

		await Assert.ThrowsAsync<McpToolException>(() => engine.IndexAsync(fixture.Path, [config, source, target],
			cancellationToken: TestContext.Current.CancellationToken));

		Assert.True(swapped);
		Assert.Equal(0, extractor.ParseCount);
	}

	[Fact]
	public async Task ResolverWorkBudget_CountsCandidateFanOutAndKeepsOrdinaryResolution()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var files = new List<string> { project };
		for (var index = 0; index < 32; index++)
			files.Add(fixture.CreateFile($"N{index}/User.cs", $"namespace N{index}; public sealed class User {{ }}"));
		files.Add(fixture.CreateFile("Fanout.cs",
			string.Join(' ', Enumerable.Range(0, 32).Select(index => $"using N{index};")) +
			" public sealed class Fanout { User Value; }"));
		files.Add(fixture.CreateFile("Single.cs", "namespace SingleNamespace; public sealed class Only { }"));
		files.Add(fixture.CreateFile("Ordinary.cs", "using SingleNamespace; public sealed class Ordinary { Only Value; }"));
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumWorkPerIndex: 16));

		var result = await engine.IndexAsync(fixture.Path, files,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "Fanout.cs" &&
			edge.Reference == "<limit>" && edge.Reasons.Contains("index work limit exceeded"));
		var ordinary = Assert.Single(result.Edges,
			edge => edge.Source == "Ordinary.cs" && edge.Reference == "Only");
		Assert.Equal(ResolutionStatus.Resolved, ordinary.Status);
		Assert.Equal("Single.cs", ordinary.Target);
	}

	[Fact]
	public async Task TypeScriptModuleSuffixes_UseConfiguredOrderAndRetainEmptyFallback()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"moduleSuffixes\":[\".ios\",\"\"]}}");
		var consumer = fixture.CreateFile("consumer.ts", "import './v';");
		var ios = fixture.CreateFile("v.ios.ts", "export const platform = 'ios';");
		var portable = fixture.CreateFile("v.ts", "export const platform = 'portable';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, consumer, ios, portable],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate => candidate.Source == "consumer.ts");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("v.ios.ts", edge.Target);
	}

	[Fact]
	public async Task NativeParseCancellation_RemainsBoundedByTheExistingFileLimit()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Large.cs", "public sealed class Large { " +
			string.Concat(Enumerable.Repeat("int Value; ", 120_000)) + "}");
		using var engine = CreateEngine();
		using var cancellation = new CancellationTokenSource();
		var stopwatch = Stopwatch.StartNew();
		var operation = engine.IndexAsync(fixture.Path, [project, source], cancellationToken: cancellation.Token);
		await Task.Delay(10, TestContext.Current.CancellationToken);
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {stopwatch.Elapsed}.");
	}

	[Fact]
	public async Task IncrementalManifestHash_RemainsByteIdenticalToLengthPrefixedUtf8Contract()
	{
		using var fixture = new TemporaryDirectory();
		var first = fixture.CreateFile("α.cs", "public sealed class Alpha { }");
		var second = fixture.CreateFile("nested/β.ts", "export const beta = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [second, first],
			cancellationToken: TestContext.Current.CancellationToken);
		var identities = result.Files.OrderBy(static file => file.Path, StringComparer.Ordinal)
			.Select(file => $"{file.Path}\0{file.ContentFingerprint}\0{file.LanguageId}");

		Assert.Equal(ReferenceHash(identities), result.ManifestGeneration);
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());

	private static string ReferenceHash(IEnumerable<string> values)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Span<byte> prefix = stackalloc byte[sizeof(int)];
		foreach (var value in values)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
			hash.AppendData(prefix);
			hash.AppendData(bytes);
		}
		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	private static void ReplaceWithExternalDirectoryLinkOrSkip(string linkPath, string targetPath)
	{
		try
		{
			Directory.Delete(linkPath, recursive: true);
			if (!OperatingSystem.IsWindows())
			{
				Directory.CreateSymbolicLink(linkPath, targetPath);
				return;
			}
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = "cmd.exe",
				UseShellExecute = false,
				CreateNoWindow = true,
				ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath }
			});
			process!.WaitForExit();
			if (process.ExitCode != 0 || !Directory.Exists(linkPath))
				Assert.Skip("Windows directory junctions are unavailable.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Skip($"Directory links are unavailable: {exception.GetType().Name}.");
		}
	}
}
