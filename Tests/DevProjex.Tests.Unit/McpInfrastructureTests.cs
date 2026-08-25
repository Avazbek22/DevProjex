using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpInfrastructureTests
{
	[Fact]
	public async Task ProjectOperationGateCancelsARequestWaitingBehindAnotherOperation()
	{
		var gate = new McpProjectOperationGate();
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var first = gate.RunAsync(
			async () =>
			{
				firstStarted.SetResult();
				await releaseFirst.Task;
				return 1;
			},
			CancellationToken.None);
		await firstStarted.Task;
		using var cancellation = new CancellationTokenSource();
		var waiting = gate.RunAsync(() => Task.FromResult(2), cancellation.Token);

		cancellation.Cancel();

		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => waiting.WaitAsync(
					TimeSpan.FromSeconds(1),
					TestContext.Current.CancellationToken));
		}
		finally
		{
			releaseFirst.TrySetResult();
		}
		Assert.Equal(1, await first);
	}

	[Fact]
	public void RootRegistryRejectsTraversalAndAbsolutePathsOutsideRoot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFile("outside.txt", "outside");
		var registry = new McpRootRegistry([project]);

		var traversal = Assert.Throws<McpToolException>(() =>
			registry.ResolveExistingPath(project, "../outside.txt"));
		var absolute = Assert.Throws<McpToolException>(() =>
			registry.ResolveExistingPath(project, outside));

		Assert.Equal(McpErrorCodes.RootViolation, traversal.Code);
		Assert.Equal(McpErrorCodes.RootViolation, absolute.Code);
		Assert.Contains(project, traversal.Message, StringComparison.Ordinal);
		var missingEscape = Assert.Throws<McpToolException>(() =>
			registry.ResolveExistingPath(project, "../missing.txt"));
		Assert.Equal(McpErrorCodes.RootViolation, missingEscape.Code);
	}

	[Fact]
	public void RootRegistryReportsMalformedPathsAsInvalidArguments()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var registry = new McpRootRegistry([project]);

		var exception = Assert.Throws<McpToolException>(() =>
			registry.ResolveExistingPath(project, "invalid\0path.txt"));

		Assert.Equal(McpErrorCodes.InvalidArguments, exception.Code);
		Assert.Contains("valid path inside the project", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RootRegistryRejectsSymlinkEscapeWhenSymlinksAreAvailable()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFolder("outside");
		var link = Path.Combine(project, "escape");
		try
		{
			Directory.CreateSymbolicLink(link, outside);
		}
		catch (Exception creationException) when (creationException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
		{
			return;
		}

		var registry = new McpRootRegistry([project]);
		var exception = Assert.Throws<McpToolException>(() =>
			registry.ResolveExistingPath(project, "escape"));
		Assert.Equal(McpErrorCodes.RootViolation, exception.Code);
	}

	[Fact]
	public void ToolErrorsEscapeControlCharactersIntoOneSafeLine()
	{
		var result = McpToolResults.Error(new McpToolException(
			McpErrorCodes.RootViolation,
			$"{McpErrorCodes.RootViolation}: bad\r\npath\t\u001b[31m\u2028tail"));

		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.True(result.IsError);
		Assert.Equal(
			$"{McpErrorCodes.RootViolation}: bad\\r\\npath\\t\\u001B[31m\\u2028tail",
			text);
		Assert.DoesNotContain('\r', text);
		Assert.DoesNotContain('\n', text);
		Assert.DoesNotContain('\u001b', text);
	}

	[Fact]
	public void McpRelativePathsPreserveUnixBackslashesAsNameCharacters()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows treats a backslash as a directory separator.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var file = workspace.CreateFile("project/literal\\name.txt", "content");

		Assert.Equal("literal\\name.txt", McpProjectService.ToRelative(project, file));
	}

	[Theory]
	[InlineData("42", 42)]
	[InlineData(42, 42)]
	public void JsonArgumentsAcceptIntegerNumbersAndNumericStrings(object value, int expected)
	{
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["limit"] = JsonSerializer.SerializeToElement(value)
			}
		};

		var arguments = McpJsonArguments.Create(request, "limit");

		Assert.Equal(expected, arguments.OptionalInteger("limit", 1, 100));
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	[InlineData("true", true)]
	[InlineData("false", false)]
	public void JsonArgumentsAcceptBooleanValuesAndExactBooleanStrings(object value, bool expected)
	{
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["enabled"] = JsonSerializer.SerializeToElement(value)
			}
		};

		var arguments = McpJsonArguments.Create(request, "enabled");

		Assert.Equal(expected, arguments.OptionalBoolean("enabled", defaultValue: false));
	}

	[Theory]
	[InlineData("TRUE")]
	[InlineData("yes")]
	[InlineData(1)]
	public void JsonArgumentsRejectOtherBooleanRepresentations(object value)
	{
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["enabled"] = JsonSerializer.SerializeToElement(value)
			}
		};

		var exception = Assert.Throws<McpToolException>(() =>
			McpJsonArguments.Create(request, "enabled").OptionalBoolean("enabled", defaultValue: false));

		Assert.Equal(McpErrorCodes.InvalidArguments, exception.Code);
	}

	[Fact]
	public void JsonArgumentsPreserveWhitespaceForContentPatternsAndPaths()
	{
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["pattern"] = JsonSerializer.SerializeToElement(" "),
				["paths"] = JsonSerializer.SerializeToElement(new[] { " " }),
				["pack_id"] = JsonSerializer.SerializeToElement(" ")
			}
		};
		var arguments = McpJsonArguments.Create(request, "pattern", "paths", "pack_id");

		Assert.Equal(" ", arguments.RequiredString("pattern", allowWhitespace: true));
		Assert.Equal([" "], arguments.OptionalStringArray("paths", allowWhitespace: true));
		Assert.Equal(
			McpErrorCodes.InvalidArguments,
			Assert.Throws<McpToolException>(() => arguments.RequiredString("pack_id")).Code);
	}

	[Fact]
	public void DetailPolicyMapsLevelsAndUnionsThemWithProfileTransformations()
	{
		var full = McpDetailPolicy.Resolve(new ProjectSelectionSpec(), McpDetailLevel.Full);
		var compact = McpDetailPolicy.Resolve(new ProjectSelectionSpec(), McpDetailLevel.Compact);
		var signatures = McpDetailPolicy.Resolve(new ProjectSelectionSpec(), McpDetailLevel.Signatures);
		var union = McpDetailPolicy.Resolve(
			new ProjectSelectionSpec(CompressCode: true),
			McpDetailLevel.Compact);

		Assert.Equal(CodeTransformKinds.None, full.Kinds);
		Assert.Equal("full", full.Token);
		Assert.Equal(CodeTransformKinds.Comments | CodeTransformKinds.BlankLines, compact.Kinds);
		Assert.Equal("compact", compact.Token);
		Assert.Equal(
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments | CodeTransformKinds.BlankLines,
			signatures.Kinds);
		Assert.Equal(signatures, union);
	}

	[Fact]
	public void DetailPolicyRejectsUnknownTokensWithActionableValues()
	{
		var exception = Assert.Throws<McpToolException>(() => McpDetailPolicy.Parse("summary"));

		Assert.Equal(McpErrorCodes.InvalidArguments, exception.Code);
		Assert.Contains("full, compact, signatures", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void JsonArgumentsRejectUnknownPropertiesAndOutOfRangeStrings()
	{
		var unknown = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["extra"] = JsonSerializer.SerializeToElement(true)
			}
		};
		var range = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["limit"] = JsonSerializer.SerializeToElement("101")
			}
		};

		Assert.Equal(
			McpErrorCodes.InvalidArguments,
			Assert.Throws<McpToolException>(() => McpJsonArguments.Create(unknown, "limit")).Code);
		Assert.Equal(
			McpErrorCodes.InvalidRange,
			Assert.Throws<McpToolException>(() =>
				McpJsonArguments.Create(range, "limit").OptionalInteger("limit", 1, 100)).Code);
	}

	[Fact]
	public void GlobSetOnlyNarrowsAndRejectsUnsafePatterns()
	{
		var globs = McpGlobSet.Create(["src/**/*.cs"], ["**/*.generated.cs"]);

		Assert.True(globs.Includes("src/domain/model.cs"));
		Assert.False(globs.Includes("src/domain/model.generated.cs"));
		Assert.False(globs.Includes("docs/readme.md"));
		Assert.Equal(
			McpErrorCodes.InvalidPattern,
			Assert.Throws<McpToolException>(() => McpGlobSet.Create(["../*.cs"], null)).Code);
	}

	[Fact]
	public void SearchRegexRejectsInvalidPatternsAndTimesOutCatastrophicMatches()
	{
		Assert.Equal(
			McpErrorCodes.InvalidPattern,
			Assert.Throws<McpToolException>(() => new McpSearchRegex("[", ignoreCase: true)).Code);

		var regex = new McpSearchRegex("^(a+)+$", ignoreCase: false, TimeSpan.FromMilliseconds(1));
		var timeout = Assert.Throws<McpToolException>(() => regex.IsMatch(new string('a', 100_000) + "!"));
		Assert.Equal(McpErrorCodes.InvalidPattern, timeout.Code);
		Assert.Contains("simplify", timeout.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PackSweepRemovesStaleSessionsButPreservesAnActiveLease()
	{
		using var workspace = new TemporaryDirectory();
		var baseDirectory = Path.Combine(workspace.Path, "DevProjex", "mcp");
		var stale = Path.Combine(baseDirectory, "stale-session");
		Directory.CreateDirectory(stale);
		File.WriteAllText(Path.Combine(stale, "pack.tmp"), "stale");
		Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

		using var active = new McpPackRegistry(workspace.Path);
		Directory.SetLastWriteTimeUtc(active.SessionDirectory, DateTime.UtcNow.AddDays(-2));
		using var next = new McpPackRegistry(workspace.Path);

		Assert.False(Directory.Exists(stale));
		Assert.True(Directory.Exists(active.SessionDirectory));
	}

	[Fact]
	public async Task PackStorageIsPrivateToTheCurrentUnixUser()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Unix file modes do not apply on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var pack = await registry.CreateAsync(
			async (stream, token) =>
			{
				await stream.WriteAsync("redacted context"u8.ToArray(), token);
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
			File.GetUnixFileMode(registry.SessionDirectory));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(Path.Combine(registry.SessionDirectory, ".session.lock")));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(pack.Path));
	}

	[Fact]
	public async Task TextPageReaderHonorsLineAndCharacterCapsWithoutLoadingWholeStream()
	{
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("one\r\ntwo\rthree\nfour\nfive"));

		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 2,
			endLine: null,
			maximumLines: 2,
			maximumCharacters: 100,
			TestContext.Current.CancellationToken);

		Assert.Equal("two\nthree", page.Text);
		Assert.Equal(2, page.StartLine);
		Assert.Equal(3, page.EndLine);
		Assert.Equal(5, page.TotalLines);
		Assert.True(page.IsTruncated);
	}

	[Fact]
	public async Task TextPageReaderDoesNotMaterializeAnOversizedSingleLine()
	{
		await using var stream = new RepeatingByteStream((byte)'x', 8 * 1024 * 1024);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 1,
			endLine: null,
			maximumLines: 1,
			maximumCharacters: 50_000,
			TestContext.Current.CancellationToken);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(50_000, page.Text.Length);
		Assert.True(page.CharacterLimitReached);
		Assert.InRange(allocatedBytes, 0, 2 * 1024 * 1024);
	}

	[Fact]
	public async Task TextPageReaderUsesKnownPackMetricsWithoutScanningTheUnreadTail()
	{
		var content = Encoding.UTF8.GetBytes("first\n" + new string('x', 2 * 1024 * 1024));
		await using var stream = new MemoryStream(content);

		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 1,
			endLine: null,
			maximumLines: 1,
			maximumCharacters: 100,
			TestContext.Current.CancellationToken,
			knownTotalLines: 2);

		Assert.Equal("first", page.Text);
		Assert.Equal(2, page.TotalLines);
		Assert.True(page.IsTruncated);
		Assert.InRange(stream.Position, 1, 32 * 1024);
	}

	[Fact]
	public async Task TextRangesPreserveLeadingEmptyLines()
	{
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\r\nvalue\n"));

		var streamed = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 1,
			endLine: 2,
			maximumLines: 10,
			maximumCharacters: 100,
			TestContext.Current.CancellationToken);
		var sliced = McpTextRanges.Slice([string.Empty, "value"], 1, 2, 10, 100);

		Assert.Equal("\nvalue", streamed.Text);
		Assert.Equal("\nvalue", sliced.Text);
	}

	[Theory]
	[InlineData("value\n")]
	[InlineData("value\r\n")]
	[InlineData("value\r")]
	public async Task TextPageReaderPreservesTheTrailingEmptyLine(string content)
	{
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 2,
			endLine: 2,
			maximumLines: 10,
			maximumCharacters: 100,
			TestContext.Current.CancellationToken);

		Assert.Equal(string.Empty, page.Text);
		Assert.Equal(2, page.StartLine);
		Assert.Equal(2, page.EndLine);
		Assert.Equal(2, page.TotalLines);
		Assert.False(page.IsTruncated);
	}

	[Fact]
	public void TextRangesRejectInvalidRangesAndReportCharacterTruncation()
	{
		var page = McpTextRanges.Slice(["123456", "next"], 1, 2, 1000, 4);
		Assert.Equal("1234", page.Text);
		Assert.True(page.CharacterLimitReached);
		Assert.True(page.IsTruncated);

		var exception = Assert.Throws<McpToolException>(() =>
			McpTextRanges.Slice(["one"], 2, null, 1000, 50_000));
		Assert.Equal(McpErrorCodes.InvalidRange, exception.Code);
	}

	[Fact]
	public async Task TextRangeCharacterCapsNeverSplitAUnicodeScalar()
	{
		const string line = "x😀tail";
		await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));

		var streamed = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 1,
			endLine: null,
			maximumLines: 1,
			maximumCharacters: 2,
			TestContext.Current.CancellationToken);
		var sliced = McpTextRanges.Slice([line], 1, null, 1, 2);

		Assert.Equal("x", streamed.Text);
		Assert.Equal("x", sliced.Text);
	}

	private sealed class RepeatingByteStream(byte value, long length) : Stream
	{
		private long _position;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => length;
		public override long Position
		{
			get => _position;
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			Read(buffer.AsSpan(offset, count));

		public override int Read(Span<byte> buffer)
		{
			var count = (int)Math.Min(buffer.Length, length - _position);
			if (count <= 0)
				return 0;
			buffer[..count].Fill(value);
			_position += count;
			return count;
		}

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(Read(buffer.Span));
		}

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
