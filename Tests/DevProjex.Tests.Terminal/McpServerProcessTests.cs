using System.Diagnostics;
using System.Threading.Channels;
using DevProjex.Application.Services;
using DevProjex.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed class McpServerProcessTests
{
	private const string Secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const string PrivateEmail = "alice.smith" + "@company.io";
	private const string PrivatePath = "/home/alice-smith/DevProjexMcpProcessProbe/project";

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RealProcessAppliesServerRedactionPolicyAndStopsOnStandardInputEof(
		bool hidePrivateData)
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userProfile))
			Assert.Skip("The environment does not expose a user profile directory.");
		using var workspace = new TemporaryDirectory(userProfile);
		var project = workspace.CreateDirectory("project");
		var physicalProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
		if (OutputRootPathPresentation.MaskLocalUserSegment(physicalProject) == physicalProject)
			Assert.Skip("The user profile path does not use a supported local-user layout.");
		var ignoredEnvironmentRoot = workspace.CreateDirectory("environment-root");
		workspace.WriteFile(
			"project/app.cs",
			$"internal sealed class ProcessMarker {{ const string Token = \"{Secret}\"; }}\n" +
			$"// Contact {PrivateEmail}\n" +
			$"// Project {PrivatePath}\n");
		var application = PublishedApplicationLocator.FindApplicationAssembly();
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = ignoredEnvironmentRoot
		};
		startInfo.ArgumentList.Add(application);
		startInfo.ArgumentList.Add("mcp");
		startInfo.ArgumentList.Add("--root");
		startInfo.ArgumentList.Add(project);
		if (hidePrivateData)
			startInfo.ArgumentList.Add("--hide-private-data");
		startInfo.Environment["CLAUDE_PROJECT_DIR"] = ignoredEnvironmentRoot;
		startInfo.Environment["DEVPROJEX_INTERNAL_DATA_ROOT"] = workspace.CreateDirectory("data");

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MCP process did not start.");
		var standardErrorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await using var recordingOutput = new RecordingReadStream(process.StandardOutput.BaseStream);
		await using (var client = await McpClient.CreateAsync(
			new StreamClientTransport(process.StandardInput.BaseStream, recordingOutput),
			clientOptions: null,
			loggerFactory: null,
			TestContext.Current.CancellationToken))
		{
			var tools = await client.ListToolsAsync(options: null, TestContext.Current.CancellationToken);
			Assert.Equal(7, tools.Count);
			var result = await client.CallToolAsync(
				"list_projects",
				new Dictionary<string, object?>(),
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotNull(result.StructuredContent);
			var structured = result.StructuredContent.Value;
			var listedProject = structured.GetProperty("projects")[0].GetProperty("path").GetString();
			var expectedProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
			var expectedIgnoredEnvironmentRoot = McpRootRegistry.ResolvePhysicalExistingPath(
				ignoredEnvironmentRoot,
				requireDirectory: true);
			Assert.True(string.Equals(expectedProject, listedProject, PathComparison));
			Assert.False(string.Equals(expectedIgnoredEnvironmentRoot, listedProject, PathComparison));
			var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
			using var textDocument = JsonDocument.Parse(text);
			Assert.True(JsonElement.DeepEquals(structured, textDocument.RootElement));

			var file = await client.CallToolAsync(
				"get_file",
				new Dictionary<string, object?> { ["path"] = "app.cs" },
				progress: null,
				options: null,
				TestContext.Current.CancellationToken);
			AssertRedactionPolicy(file, hidePrivateData);

			var progress = new InlineProgress<ProgressNotificationValue>();
			var pack = await client.CallToolAsync(
				"pack_context",
				new Dictionary<string, object?>
				{
					["view"] = "tree-content",
					["format"] = "text"
				},
				progress,
				options: null,
				TestContext.Current.CancellationToken);
			Assert.NotEqual(true, pack.IsError);
			AssertRedactionPolicy(pack, hidePrivateData);
			AssertGeneratedRootPathPolicy(pack, expectedProject, hidePrivateData);
		}

		process.StandardInput.Close();
		var standardOutputEofTask = recordingOutput.WaitForSourceEofAsync(TestContext.Current.CancellationToken);
		await Task.WhenAll(
			process.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
			standardOutputEofTask
				.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");

		var messages = ParseJsonRpcMessages(recordingOutput.GetRecordedText());
		Assert.Contains(messages, static message =>
		{
			using var document = JsonDocument.Parse(message);
			return document.RootElement.TryGetProperty("method", out var method) &&
			       method.GetString() == NotificationMethods.ProgressNotification;
		});
	}

	private static IReadOnlyList<string> ParseJsonRpcMessages(string transcript)
	{
		var lines = transcript.Split('\n');
		var messageCount = lines.Length;
		if (messageCount > 0 && lines[^1].Length == 0)
			messageCount--;

		Assert.True(messageCount > 0, "MCP stdout did not contain any JSON-RPC messages.");
		var messages = new string[messageCount];
		for (var index = 0; index < messageCount; index++)
		{
			var line = lines[index];
			var message = line.EndsWith('\r') ? line[..^1] : line;
			Assert.False(
				string.IsNullOrWhiteSpace(message),
				$"MCP stdout contained an empty non-protocol line at index {index}.");
			Assert.DoesNotContain('\r', message);
			Assert.StartsWith("{", message, StringComparison.Ordinal);
			Assert.EndsWith("}", message, StringComparison.Ordinal);
			using var document = JsonDocument.Parse(message);
			Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
			Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
			var hasMethod = document.RootElement.TryGetProperty("method", out var method) &&
			                method.ValueKind == JsonValueKind.String &&
			                !string.IsNullOrWhiteSpace(method.GetString());
			var hasId = document.RootElement.TryGetProperty("id", out _);
			var hasResult = document.RootElement.TryGetProperty("result", out _);
			var hasError = document.RootElement.TryGetProperty("error", out _);
			Assert.True(
				hasMethod ? !hasResult && !hasError : hasId && hasResult != hasError,
				$"MCP stdout contained an invalid JSON-RPC message at index {index}: {message}");
			messages[index] = message;
		}

		return messages;
	}

	private static void AssertRedactionPolicy(
		CallToolResult result,
		bool hidePrivateData)
	{
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.NotEqual(true, result.IsError);
		Assert.Contains("ProcessMarker", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
		if (hidePrivateData)
		{
			Assert.DoesNotContain(PrivateEmail, text, StringComparison.Ordinal);
			Assert.DoesNotContain(PrivatePath, text, StringComparison.Ordinal);
		}
		else
		{
			Assert.Contains(PrivateEmail, text, StringComparison.Ordinal);
			Assert.Contains(PrivatePath, text, StringComparison.Ordinal);
		}
	}

	private static void AssertGeneratedRootPathPolicy(
		CallToolResult result,
		string project,
		bool hidePrivateData)
	{
		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		var protectedProject = OutputRootPathPresentation.MaskLocalUserSegment(project);
		var expectedProject = hidePrivateData ? protectedProject : project;
		Assert.Contains(expectedProject, text, StringComparison.Ordinal);
		Assert.DoesNotContain(hidePrivateData ? project : protectedProject, text, StringComparison.Ordinal);
	}

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private sealed class InlineProgress<T> : IProgress<T>
	{
		private readonly List<T> _values = [];
		private readonly object _sync = new();

		public IReadOnlyList<T> Values
		{
			get
			{
				lock (_sync)
					return _values.ToArray();
			}
		}

		public void Report(T value)
		{
			lock (_sync)
				_values.Add(value);
		}
	}

	private sealed class RecordingReadStream : Stream
	{
		private const int ReadBufferSize = 8 * 1024;
		private static readonly Encoding StrictUtf8 = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true);
		private readonly Stream _source;
		private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
			new UnboundedChannelOptions
			{
				SingleReader = true,
				SingleWriter = true,
				AllowSynchronousContinuations = false
			});
		private readonly MemoryStream _recording = new();
		private readonly object _sync = new();
		private readonly CancellationTokenSource _lifetime = new();
		private readonly Task _pumpTask;
		private byte[]? _currentChunk;
		private int _currentOffset;
		private int _disposed;

		public RecordingReadStream(Stream source)
		{
			_source = source;
			_pumpTask = PumpAsync();
		}

		public string GetRecordedText()
		{
			lock (_sync)
				return StrictUtf8.GetString(_recording.ToArray());
		}

		public Task WaitForSourceEofAsync(CancellationToken cancellationToken) =>
			_pumpTask.WaitAsync(cancellationToken);

		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
			while (true)
			{
				if (_currentChunk is not null)
				{
					var count = Math.Min(buffer.Length, _currentChunk.Length - _currentOffset);
					_currentChunk.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
					_currentOffset += count;
					if (_currentOffset == _currentChunk.Length)
					{
						_currentChunk = null;
						_currentOffset = 0;
					}
					return count;
				}

				if (_chunks.Reader.TryRead(out _currentChunk))
					continue;
				if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
					return 0;
			}
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

		private async Task PumpAsync()
		{
			Exception? failure = null;
			try
			{
				while (true)
				{
					var buffer = new byte[ReadBufferSize];
					var read = await _source
						.ReadAsync(buffer, _lifetime.Token)
						.ConfigureAwait(false);
					if (read == 0)
						break;
					if (read != buffer.Length)
						Array.Resize(ref buffer, read);

					lock (_sync)
						_recording.Write(buffer);
					await _chunks.Writer
						.WriteAsync(buffer, _lifetime.Token)
						.ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
			{
			}
			catch (Exception exception)
			{
				failure = exception;
				throw;
			}
			finally
			{
				_chunks.Writer.TryComplete(failure);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				DisposeAsyncCore().AsTask().GetAwaiter().GetResult();
			base.Dispose(disposing);
		}

		public override async ValueTask DisposeAsync()
		{
			await DisposeAsyncCore().ConfigureAwait(false);
			GC.SuppressFinalize(this);
		}

		private async ValueTask DisposeAsyncCore()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			_lifetime.Cancel();
			await _source.DisposeAsync().ConfigureAwait(false);
			try
			{
				await _pumpTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			catch (IOException) when (_lifetime.IsCancellationRequested)
			{
			}
			finally
			{
				_lifetime.Dispose();
				_recording.Dispose();
			}
		}

		public override bool CanRead => Volatile.Read(ref _disposed) == 0;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
