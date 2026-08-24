using System.Diagnostics;
using DevProjex.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessSpeaksOnlyJsonRpcAndStopsOnStandardInputEof()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var ignoredEnvironmentRoot = workspace.CreateDirectory("environment-root");
		workspace.WriteFile("project/app.cs", "internal sealed class ProcessMarker {}\n");
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
		}

		process.StandardInput.Close();
		await process.WaitForExitAsync(TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		var standardError = await standardErrorTask;
		Assert.Equal(0, process.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(standardError), $"Unexpected stderr: {standardError}");

		var transcript = recordingOutput.GetRecordedText();
		var messages = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.NotEmpty(messages);
		Assert.All(messages, static message =>
		{
			using var document = JsonDocument.Parse(message.TrimEnd('\r'));
			Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
		});
	}

	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private sealed class RecordingReadStream(Stream source) : Stream
	{
		private readonly MemoryStream _recording = new();
		private readonly object _sync = new();

		public string GetRecordedText()
		{
			lock (_sync)
				return Encoding.UTF8.GetString(_recording.ToArray());
		}

		public override async ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read > 0)
			{
				lock (_sync)
					_recording.Write(buffer.Span[..read]);
			}
			return read;
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			var read = source.Read(buffer, offset, count);
			if (read > 0)
			{
				lock (_sync)
					_recording.Write(buffer, offset, read);
			}
			return read;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				source.Dispose();
				_recording.Dispose();
			}
			base.Dispose(disposing);
		}

		public override bool CanRead => source.CanRead;
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
