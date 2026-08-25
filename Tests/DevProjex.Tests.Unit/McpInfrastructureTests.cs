using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpInfrastructureTests
{
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
}
