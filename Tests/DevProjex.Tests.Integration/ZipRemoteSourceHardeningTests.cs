using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace DevProjex.Tests.Integration;

public sealed class ZipRemoteSourceHardeningTests
{
	[Fact]
	public async Task StalledArchiveBodyFailsWithinProgressDeadline()
	{
		using var temporary = new TemporaryDirectory();
		using var service = new ZipDownloadService(
			new StalledArchiveHandler(),
			ZipResourceLimits.Default with { FreeSpaceReserveBytes = 0 },
			TimeSpan.FromMilliseconds(150));
		using var outerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var stopwatch = Stopwatch.StartNew();

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/example/repository.git",
			Path.Combine(temporary.Path, "target"),
			cancellationToken: outerDeadline.Token);

		Assert.False(result.Success);
		Assert.Contains("ZIP archive body made no progress", result.ErrorMessage, StringComparison.Ordinal);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
		Assert.False(outerDeadline.IsCancellationRequested);
	}

	[Fact]
	public async Task MetadataTimeoutIsAnOperationFailureInsteadOfCallerCancellation()
	{
		using var temporary = new TemporaryDirectory();
		using var service = new ZipDownloadService(new MetadataTimeoutHandler());

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/example/repository.git",
			Path.Combine(temporary.Path, "target"),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result.Success);
		Assert.Equal("DPX-ZIP-METADATA-TIMEOUT: ZIP metadata request timed out.", result.ErrorMessage);
	}

	[Fact]
	public async Task MetadataFallbackReportsTheBranchThatWasActuallyDownloaded()
	{
		using var temporary = new TemporaryDirectory();
		var archive = CreateArchive(("repository-master/app.txt", "master"));
		using var service = new ZipDownloadService(new MetadataFailureArchiveHandler(archive));

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/example/repository.git",
			Path.Combine(temporary.Path, "target"),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.Equal("master", result.DefaultBranch);
		Assert.Equal("master", File.ReadAllText(Path.Combine(result.LocalPath, "app.txt")));
	}

	[Fact]
	public async Task SymbolicLinkArchiveEntriesAreSkippedWithCountedDiagnostic()
	{
		using var temporary = new TemporaryDirectory();
		var archive = CreateArchiveWithSymbolicLink();
		using var service = new ZipDownloadService(new ArchiveHandler(archive));
		var progressFrames = new List<string>();
		var progress = new SynchronousProgress(progressFrames.Add);

		var result = await service.DownloadAndExtractAsync(
			"https://github.com/example/repository.git",
			Path.Combine(temporary.Path, "target"),
			progress,
			TestContext.Current.CancellationToken);

		Assert.True(result.Success, result.ErrorMessage);
		Assert.True(File.Exists(Path.Combine(result.LocalPath, "app.txt")));
		Assert.False(File.Exists(Path.Combine(result.LocalPath, "link.txt")));
		Assert.Contains(
			"DPX-ZIP-SYMLINK-SKIPPED: 1 symbolic link entry was skipped.",
			progressFrames);
	}

	private static byte[] CreateArchive(params (string Path, string Content)[] files)
	{
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (path, content) in files)
			{
				var entry = archive.CreateEntry(path);
				using var writer = new StreamWriter(entry.Open());
				writer.Write(content);
			}
		}
		return memory.ToArray();
	}

	private static byte[] CreateArchiveWithSymbolicLink()
	{
		using var memory = new MemoryStream();
		using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
		{
			var regular = archive.CreateEntry("repository-main/app.txt");
			using (var writer = new StreamWriter(regular.Open()))
				writer.Write("content");
			var link = archive.CreateEntry("repository-main/link.txt");
			link.ExternalAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
			using var linkWriter = new StreamWriter(link.Open());
			linkWriter.Write("app.txt");
		}
		return memory.ToArray();
	}

	private sealed class StalledArchiveHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (request.RequestUri!.Host == "api.github.com")
				return Task.FromResult(JsonResponse("{\"default_branch\":\"main\"}"));
			var content = new StreamContent(new NeverProgressStream());
			content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}

	private sealed class MetadataTimeoutHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) =>
			throw new TaskCanceledException("internal timeout");
	}

	private sealed class MetadataFailureArchiveHandler(byte[] archive) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (request.RequestUri!.Host == "api.github.com")
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
			return Task.FromResult(request.RequestUri.AbsolutePath.Contains("/main.zip", StringComparison.Ordinal)
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: BytesResponse(archive));
		}
	}

	private sealed class ArchiveHandler(byte[] archive) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) =>
			Task.FromResult(request.RequestUri!.Host == "api.github.com"
				? JsonResponse("{\"default_branch\":\"main\"}")
				: BytesResponse(archive));
	}

	private sealed class NeverProgressStream : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => 0; set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 0;
		}
	}

	private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
	{
		public void Report(string value) => report(value);
	}

	private static HttpResponseMessage JsonResponse(string json) =>
		new(HttpStatusCode.OK) { Content = new StringContent(json) };

	private static HttpResponseMessage BytesResponse(byte[] bytes) =>
		new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}
