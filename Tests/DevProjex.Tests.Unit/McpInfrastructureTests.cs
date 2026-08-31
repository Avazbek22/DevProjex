using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpInfrastructureTests
{
	public static TheoryData<string, int, bool> PackCheckpointBoundaryCases()
	{
		var cases = new TheoryData<string, int, bool>();
		foreach (var lineEnding in new[] { "\n", "\r\n", "\r" })
		{
			foreach (var lineCount in new[] { 255, 256, 257, 512 })
			{
				cases.Add(lineEnding, lineCount, false);
				cases.Add(lineEnding, lineCount, true);
			}
		}

		return cases;
	}

	[Fact]
	public void ServerHostExposesOneUnambiguousRunEntryPoint()
	{
		var method = Assert.Single(
			typeof(McpServerHost).GetMethods(
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.Static),
			static candidate => candidate.Name == nameof(McpServerHost.RunAsync));

		Assert.Equal(
			[
				typeof(IReadOnlyList<string>),
				typeof(bool),
				typeof(bool),
				typeof(GitFilteringMode?),
				typeof(CancellationToken)
			],
			method.GetParameters().Select(static parameter => parameter.ParameterType));
	}

	[Fact]
	public async Task BoundedTreeWriter_StopsBeforeMaterializingLinesBeyondTheLimit()
	{
		using var writer = new McpBoundedLineTextWriter(maximumLines: 2);
		await writer.WriteAsync("first\r\nsecond\n".AsMemory(), TestContext.Current.CancellationToken);

		await Assert.ThrowsAsync<McpLineLimitReachedException>(async () =>
			await writer.WriteAsync("third".AsMemory(), TestContext.Current.CancellationToken));

		Assert.True(writer.IsTruncated);
		Assert.Equal("first\nsecond", writer.Text);
	}

	[Fact]
	public void TopFileRanking_RemainsBoundedAndUsesStableContractOrder()
	{
		var ranking = new TopFileRanking(capacity: 3);
		foreach (var item in new[]
		         {
			         ("z.cs", 10L),
			         ("b.cs", 30L),
			         ("a.cs", 30L),
			         ("c.cs", 20L),
			         ("ignored.cs", 1L)
		         })
		{
			ranking.Add(item.Item1, item.Item2);
		}

		Assert.Equal(
			[new TopFileMetric("a.cs", 30), new TopFileMetric("b.cs", 30), new TopFileMetric("c.cs", 20)],
			ranking.Items);
	}

	[Fact]
	public void TopFileRanking_ProjectsOnlyTheBoundedWinners()
	{
		var ranking = new TopFileRanking(capacity: 10);
		for (var index = 0; index < 20_000; index++)
			ranking.Add($"file-{index:D5}.cs", index);
		var mappedPaths = 0;

		var projected = ranking.Project(item =>
		{
			mappedPaths++;
			return item.Path;
		});

		Assert.Equal(10, mappedPaths);
		Assert.Equal(10, projected.Length);
		Assert.Equal("file-19999.cs", projected[0]);
	}

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
	public void ProjectPlanContainmentRejectsAnIncludedFileOutsideRoot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFile("outside.txt", "outside");
		var registry = new McpRootRegistry([project]);

		var exception = Assert.Throws<McpToolException>(() =>
			McpProjectService.ValidatePlanContainment(
				registry,
				project,
				[outside],
				TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.RootViolation, exception.Code);
	}

	[Fact]
	public void RootRegistryDoesNotExposeMutableAllowedRoots()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFolder("outside");
		var registry = new McpRootRegistry([project]);
		var exposedRoots = Assert.IsAssignableFrom<IList<string>>(registry.Roots);

		Assert.True(exposedRoots.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => exposedRoots[0] = outside);
		Assert.Equal(
			McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true),
			registry.ResolveProject(project));
		Assert.Equal(
			McpErrorCodes.UnknownProject,
			Assert.Throws<McpToolException>(() => registry.ResolveProject(outside)).Code);
	}

	[Fact]
	public void RootRegistryRejectsEmptyConfiguredRoot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");

		Assert.Throws<ArgumentException>(() =>
			new McpRootRegistry([project, string.Empty]));
	}

	[Fact]
	public void RootRegistryAllowsSelectingAWhitespaceOnlyUnixRoot()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Whitespace-only path segments are invalid on Windows.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var whitespaceRoot = workspace.CreateFolder(" ");
		var otherRoot = workspace.CreateFolder("other");
		var registry = new McpRootRegistry([whitespaceRoot, otherRoot]);

		var resolved = registry.ResolveProject(whitespaceRoot);

		Assert.True(PathComparer.Default.Equals(
			McpRootRegistry.ResolvePhysicalExistingPath(whitespaceRoot, requireDirectory: true),
			resolved));
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
	public void RootJailFileOpenerReadsAnOrdinaryFileFromTheVerifiedHandle()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var source = workspace.CreateFile("project/source.txt", "verified content");
		var registry = new McpRootRegistry([project]);
		var opener = new McpRootJailFileStreamOpener(registry);

		using var stream = opener.OpenRead(
			source,
			bufferSize: 4096,
			FileShare.Read,
			asynchronous: false);
		using var reader = new StreamReader(stream);

		Assert.Equal("verified content", reader.ReadToEnd());
		Assert.True(PathComparer.Default.Equals(
			McpRootRegistry.ResolvePhysicalExistingPath(source, requireDirectory: false),
			McpRootJailFileStreamOpener.ResolveOpenedPath(stream.SafeFileHandle)));
	}

	[Fact]
	public void RootJailFileOpenerRejectsAnAncestorSymlinkEscapeAfterLexicalAcceptance()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFolder("outside");
		workspace.CreateFile("outside/secret.txt", "outside");
		var link = Path.Combine(project, "alias");
		CreateDirectoryAliasOrSkip(link, outside);
		try
		{
			var registry = new McpRootRegistry([project]);
			var canonicalProject = registry.ResolveProject(project);
			var candidate = Path.Combine(canonicalProject, "alias", "secret.txt");
			Assert.True(PathComparer.Default.Equals(canonicalProject, registry.FindLexicalRoot(candidate)));
			var opener = new McpRootJailFileStreamOpener(registry);

			var exception = Assert.Throws<McpToolException>(() =>
				opener.OpenRead(
					candidate,
					bufferSize: 4096,
					FileShare.Read,
					asynchronous: false));

			Assert.Equal(McpErrorCodes.RootViolation, exception.Code);
			Assert.Contains(canonicalProject, exception.Message, StringComparison.Ordinal);
		}
		finally
		{
			if (Directory.Exists(link))
				Directory.Delete(link);
		}
	}

	[Fact]
	public void RootRegistryRevalidatesTheImplicitSingleRootAfterFilesystemReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var original = Path.Combine(workspace.Path, "original-project");
		var outside = workspace.CreateFolder("outside");
		var registry = new McpRootRegistry([project]);
		Directory.Move(project, original);
		CreateDirectoryAliasOrSkip(project, outside);
		try
		{
			var exception = Assert.Throws<McpToolException>(() => registry.ResolveProject(project: null));

			Assert.Equal(McpErrorCodes.UnknownProject, exception.Code);
		}
		finally
		{
			if (Directory.Exists(project))
				Directory.Delete(project);
		}
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

	private static void CreateDirectoryAliasOrSkip(string linkPath, string targetPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			try
			{
				Directory.CreateSymbolicLink(linkPath, targetPath);
				return;
			}
			catch (Exception exception) when (
				exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
			{
				Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
			}
		}

		using var process = Process.Start(new ProcessStartInfo("cmd.exe")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			ArgumentList = { "/c", "mklink", "/J", linkPath, targetPath }
		});
		if (process is null ||
		    !process.WaitForExit(TimeSpan.FromSeconds(5)) ||
		    process.ExitCode != 0 ||
		    !Directory.Exists(linkPath))
		{
			try
			{
				process?.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}
			Assert.Skip("Windows junction creation is unavailable.");
		}
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

	[Fact]
	public void McpRequestedPathsSelectExactFilesAndDirectoryDescendants()
	{
		using var workspace = new TemporaryDirectory();
		var sourceDirectory = PathUtility.Normalize(workspace.CreateFolder("project/src"));
		var exactFile = PathUtility.Normalize(workspace.CreateFile("project/README.md", "readme"));
		var nestedFile = PathUtility.Normalize(workspace.CreateFile("project/src/nested/File.cs", "code"));
		var siblingFile = PathUtility.Normalize(workspace.CreateFile("project/tests/Test.cs", "test"));
		var requestedPaths = new HashSet<string>(PathComparer.Default)
		{
			sourceDirectory,
			exactFile
		};
		var requestedDirectories = new HashSet<string>(PathComparer.Default)
		{
			sourceDirectory
		};

		Assert.True(McpProjectService.MatchesRequested(
			exactFile,
			requestedPaths,
			requestedDirectories));
		Assert.True(McpProjectService.MatchesRequested(
			nestedFile,
			requestedPaths,
			requestedDirectories));
		Assert.False(McpProjectService.MatchesRequested(
			siblingFile,
			requestedPaths,
			requestedDirectories));
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
	[InlineData("42.0", 42)]
	[InlineData("4.2e1", 42)]
	public void JsonArgumentsAcceptIntegralJsonNumberForms(string json, long expected)
	{
		using var document = JsonDocument.Parse(json);
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["limit"] = document.RootElement.Clone()
			}
		};
		var arguments = McpJsonArguments.Create(request, "limit");

		Assert.Equal((int)expected, arguments.OptionalInteger("limit", 1, 100));
		Assert.Equal(expected, arguments.OptionalInt64("limit", 1, 100));
	}

	[Theory]
	[InlineData("42.5")]
	[InlineData("\"4.2e1\"")]
	public void JsonArgumentsRejectFractionalNumbersAndExponentStrings(string json)
	{
		using var document = JsonDocument.Parse(json);
		var request = new CallToolRequestParams
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["limit"] = document.RootElement.Clone()
			}
		};

		var exception = Assert.Throws<McpToolException>(() =>
			McpJsonArguments.Create(request, "limit").OptionalInt64("limit", 1, 100));

		Assert.Equal(McpErrorCodes.InvalidRange, exception.Code);
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
	public void RootJailFileOpenerRejectsADirectPathOutsideEveryRoot()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var outside = workspace.CreateFile("outside/secret.txt", "outside");
		var registry = new McpRootRegistry([project]);
		var opener = new McpRootJailFileStreamOpener(registry);

		var exception = Assert.Throws<McpToolException>(() =>
			opener.OpenRead(
				outside,
				bufferSize: 4096,
				FileShare.Read,
				asynchronous: false));

		Assert.Equal(McpErrorCodes.RootViolation, exception.Code);
		Assert.Contains(project, exception.Message, StringComparison.Ordinal);
		Assert.Contains("Valid roots", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RootRegistryRejectsCaseOnlySiblingFromOpenedHandleValidation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("Allowed");
		var registry = new McpRootRegistry([project]);
		var canonicalRoot = Assert.Single(registry.Roots);
		var leafName = Path.GetFileName(canonicalRoot);
		var alternateName = char.IsUpper(leafName[0])
			? char.ToLowerInvariant(leafName[0]) + leafName[1..]
			: char.ToUpperInvariant(leafName[0]) + leafName[1..];
		var caseOnlySiblingPath = Path.Combine(
			Path.GetDirectoryName(canonicalRoot)!,
			alternateName,
			"same.txt");

		var exception = Assert.Throws<McpToolException>(() =>
			registry.EnsureOpenedPathIsWithin(
				canonicalRoot,
				"../" + alternateName + "/same.txt",
				caseOnlySiblingPath));

		Assert.Equal(McpErrorCodes.RootViolation, exception.Code);
	}

	[Fact]
	public void RequestedPathMatchingKeepsCaseDistinctProjectEntriesIndependent()
	{
		var requested = new HashSet<string>(StringComparer.Ordinal)
		{
			Path.GetFullPath(Path.Combine("project", "Foo.cs"))
		};

		Assert.True(McpProjectService.MatchesRequested(
			Path.GetFullPath(Path.Combine("project", "Foo.cs")),
			requested,
			new HashSet<string>(StringComparer.Ordinal)));
		Assert.False(McpProjectService.MatchesRequested(
			Path.GetFullPath(Path.Combine("project", "foo.cs")),
			requested,
			new HashSet<string>(StringComparer.Ordinal)));
	}

	[Fact]
	public async Task PreparedAnalyzerReadsTrustedOutputWithoutWeakeningSourceJail()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var source = workspace.CreateFile("project/source.txt", "source");
		var preparedPath = workspace.CreateFile("prepared/source.txt", "redacted");
		var registry = new McpRootRegistry([project]);
		var sourceAnalyzer = new FileContentAnalyzer(
			new McpRootJailFileStreamOpener(registry).OpenRead);
		await using var prepared = new PreparedSecretRedactionOutput(
			workingDirectory: null,
			new Dictionary<string, PreparedSecretFile>(PathComparer.Default)
			{
				[source] = new PreparedSecretFile(
					source,
					preparedPath,
					FileContentClassification.Text,
					Encoding: null,
					Redactions: [])
			},
			snapshot: null);
		var analyzer = new PreparedSecretFileContentAnalyzer(
			sourceAnalyzer,
			new FileContentAnalyzer(),
			prepared);

		var content = await analyzer.TryReadAsTextAsync(
			source,
			TestContext.Current.CancellationToken);

		Assert.NotNull(content);
		Assert.Equal("redacted", content.Content);
		await Assert.ThrowsAsync<McpToolException>(async () =>
			await sourceAnalyzer.TryReadAsTextAsync(
				preparedPath,
				TestContext.Current.CancellationToken));
	}

	[Fact]
	public void GlobQuestionMarkMatchesOneUnicodeScalar()
	{
		var one = McpGlobSet.Create(["?.txt"], ["a.txt"]);
		var two = McpGlobSet.Create(["??.txt"], null);
		var nested = McpGlobSet.Create(["**/?.txt"], null);

		Assert.False(one.Includes("a.txt"));
		Assert.True(one.Includes("界.txt"));
		Assert.True(one.Includes("😀.txt"));
		Assert.False(one.Includes("ab.txt"));
		Assert.False(one.Includes("😀😀.txt"));
		Assert.True(two.Includes("a界.txt"));
		Assert.True(two.Includes("😀界.txt"));
		Assert.False(two.Includes("😀.txt"));
		Assert.True(nested.Includes("src/😀.txt"));
		Assert.False(nested.Includes("src/😀😀.txt"));
	}

	[Fact(Timeout = 2_000)]
	public void GlobSetAlternatingWildcardsCannotCauseCatastrophicBacktracking()
	{
		var pattern = string.Concat(Enumerable.Repeat("*a", 12)) + "b";
		var candidate = new string('a', 36) + "c";
		var globs = McpGlobSet.Create([pattern], null);

		Assert.False(globs.Includes(candidate));
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

		var oversized = Assert.Throws<McpToolException>(() =>
			new McpSearchRegex(
				new string('x', McpSearchRegex.MaximumPatternLength + 1),
				ignoreCase: true));
		Assert.Equal(McpErrorCodes.InvalidPattern, oversized.Code);
		Assert.Contains("4096", oversized.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void McpStringLimitsCountUnicodeScalarValuesAtPublishedBoundaries()
	{
		var regexBoundary = string.Concat(Enumerable.Repeat("😀", McpSearchRegex.MaximumPatternLength));
		var regexOversized = regexBoundary + "😀";
		var globBoundary = string.Concat(Enumerable.Repeat("😀", 508)) + ".txt";
		var globOversized = "😀" + globBoundary;

		Assert.False(McpUnicodeLength.ExceedsScalarValueCount(regexBoundary, 4096));
		Assert.True(McpUnicodeLength.ExceedsScalarValueCount(regexOversized, 4096));
		Assert.True(new McpSearchRegex(regexBoundary, ignoreCase: false).IsMatch(regexBoundary));
		Assert.Equal(
			McpErrorCodes.InvalidPattern,
			Assert.Throws<McpToolException>(() =>
				new McpSearchRegex(regexOversized, ignoreCase: false)).Code);
		Assert.True(McpGlobSet.Create([globBoundary], null).Includes(globBoundary));
		Assert.Equal(
			McpErrorCodes.InvalidPattern,
			Assert.Throws<McpToolException>(() => McpGlobSet.Create([globOversized], null)).Code);
	}

	[Fact]
	public void JsonPathArgumentsEnforceItemAndUnicodeScalarCaps()
	{
		var boundaryPath = string.Concat(Enumerable.Repeat("😀", McpProjectService.MaximumRequestedPathLength));
		var allowed = RequestWithPaths(Enumerable.Repeat("path", McpProjectService.MaximumRequestedPaths).ToArray());
		var scalarBoundary = RequestWithPaths([boundaryPath]);
		var tooMany = RequestWithPaths(
			Enumerable.Repeat("path", McpProjectService.MaximumRequestedPaths + 1).ToArray());
		var tooLong = RequestWithPaths([boundaryPath + "😀"]);

		Assert.Equal(
			McpProjectService.MaximumRequestedPaths,
			McpJsonArguments.Create(allowed, "paths").OptionalStringArray(
				"paths",
				maximumItems: McpProjectService.MaximumRequestedPaths,
				maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)!.Count);
		Assert.Equal(
			boundaryPath,
			Assert.Single(McpJsonArguments.Create(scalarBoundary, "paths").OptionalStringArray(
				"paths",
				maximumItems: McpProjectService.MaximumRequestedPaths,
				maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)!));
		Assert.Equal(
			McpErrorCodes.InvalidArguments,
			Assert.Throws<McpToolException>(() =>
				McpJsonArguments.Create(tooMany, "paths").OptionalStringArray(
					"paths",
					maximumItems: McpProjectService.MaximumRequestedPaths,
					maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)).Code);
		Assert.Equal(
			McpErrorCodes.InvalidArguments,
			Assert.Throws<McpToolException>(() =>
				McpJsonArguments.Create(tooLong, "paths").OptionalStringArray(
					"paths",
					maximumItems: McpProjectService.MaximumRequestedPaths,
					maximumItemScalarValues: McpProjectService.MaximumRequestedPathLength)).Code);
	}

	[Fact]
	public void RequestedPathNormalizationDeduplicatesAliasesWithoutCollapsingCase()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var source = workspace.CreateFolder("project/src");

		var normalized = McpProjectService.NormalizeRequestedPathTokens(
			project,
			["src", "./src", "src/.", source + Path.DirectorySeparatorChar, "Foo", "foo"]);

		Assert.Equal(3, normalized.Count);
		Assert.Equal(PathUtility.Normalize(source), normalized[0]);
		Assert.Contains(Path.Combine(project, "Foo"), normalized, StringComparer.Ordinal);
		Assert.Contains(Path.Combine(project, "foo"), normalized, StringComparer.Ordinal);
	}

	[Fact]
	public void TrustedDiagnosticFormattingNeverEchoesDiagnosticPathsOrMessages()
	{
		const string sensitivePath = "user/private/secret-name.txt";
		const string sensitiveMessage = "scanner warning contains user content";
		var warnings = McpTrustedDiagnosticFormatter.FormatWarnings(
		new ContextDiagnostic[]
		{
			new ContextDiagnostic(
				"DPX-SELECTION-PATH-MISSING",
				ContextDiagnosticSeverity.Warning,
				"Selected path is not present.",
				sensitivePath),
			new ContextDiagnostic(
				"DPX-PROJECT-SELECTION-WARNING",
				ContextDiagnosticSeverity.Warning,
				sensitiveMessage,
				sensitivePath),
			new ContextDiagnostic(
				GitScopeFilter.DeletedDiagnosticCode,
				ContextDiagnosticSeverity.Warning,
				"unsafe",
				sensitivePath,
				2)
		});

		Assert.NotNull(warnings);
		Assert.Contains("1 requested path is not present", warnings, StringComparison.Ordinal);
		Assert.Contains("1 selection warning", warnings, StringComparison.Ordinal);
		Assert.Contains("Deleted files excluded from the Git state: 2.", warnings, StringComparison.Ordinal);
		Assert.DoesNotContain(sensitivePath, warnings, StringComparison.Ordinal);
		Assert.DoesNotContain(sensitiveMessage, warnings, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RemoteSourceResolverCapsDistinctKeysReusesExistingSourcesAndDisposesOnce()
	{
		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var firstRoot = workspace.CreateFolder("cache/first");
		var secondRoot = workspace.CreateFolder("cache/second");
		var thirdRoot = workspace.CreateFolder("cache/third");
		const string firstUrl = "https://example.test/owner/first.git";
		const string secondUrl = "https://example.test/owner/second.git";
		const string thirdUrl = "https://example.test/owner/third.git";
		var cache = new TrackingRemoteCache(new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[firstUrl] = firstRoot,
			[secondUrl] = secondRoot,
			[thirdUrl] = thirdRoot
		});
		var resolver = new McpProjectSourceResolver(
			new McpRootRegistry([local]),
			allowRemote: true,
			() => new McpRemoteProjectServices(cache, new GitRepositoryService()),
			maximumRemoteSources: 2);

		var first = await resolver.ResolveAsync(firstUrl, branch: null, TestContext.Current.CancellationToken);
		var repeated = await resolver.ResolveAsync(firstUrl, branch: null, TestContext.Current.CancellationToken);
		var second = await resolver.ResolveAsync(secondUrl, branch: null, TestContext.Current.CancellationToken);
		var limit = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(thirdUrl, branch: null, TestContext.Current.CancellationToken));
		var retained = await resolver.ResolveAsync(firstUrl, branch: null, TestContext.Current.CancellationToken);
		resolver.Dispose();
		resolver.Dispose();

		Assert.Same(first, repeated);
		Assert.Same(first, retained);
		Assert.NotSame(first, second);
		Assert.Equal(McpErrorCodes.RemoteLimit, limit.Code);
		Assert.Contains("Reuse an existing project URL and branch", limit.Message, StringComparison.Ordinal);
		Assert.Equal(2, cache.AcquireSessionCallCount);
		Assert.Equal(1, cache.DisposeCount);
		Assert.All(cache.Sessions, static session => Assert.Equal(1, session.DisposeCount));
	}

	[Fact]
	public async Task ConcurrentRemoteSourceReservationsShareAKeyAndKeepTheHardCap()
	{
		using var workspace = new TemporaryDirectory();
		var local = workspace.CreateFolder("local");
		var firstRoot = workspace.CreateFolder("cache/first");
		var secondRoot = workspace.CreateFolder("cache/second");
		const string firstUrl = "https://example.test/owner/first.git";
		const string secondUrl = "https://example.test/owner/second.git";
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cache = new TrackingRemoteCache(new Dictionary<string, string>(StringComparer.Ordinal)
		{
			[firstUrl] = firstRoot,
			[secondUrl] = secondRoot
		})
		{
			SessionGate = gate.Task
		};
		var resolver = new McpProjectSourceResolver(
			new McpRootRegistry([local]),
			allowRemote: true,
			() => new McpRemoteProjectServices(cache, new GitRepositoryService()),
			maximumRemoteSources: 1);

		var first = resolver.ResolveAsync(firstUrl, branch: null, TestContext.Current.CancellationToken);
		Assert.True(SpinWait.SpinUntil(() => cache.AcquireSessionCallCount >= 1, TimeSpan.FromSeconds(2)));
		var sameKey = resolver.ResolveAsync(firstUrl, branch: null, TestContext.Current.CancellationToken);
		Assert.True(SpinWait.SpinUntil(() => cache.AcquireSessionCallCount >= 2, TimeSpan.FromSeconds(2)));
		var limit = await Assert.ThrowsAsync<McpToolException>(() =>
			resolver.ResolveAsync(secondUrl, branch: null, TestContext.Current.CancellationToken));
		gate.SetResult();
		var resolved = await Task.WhenAll(first, sameKey);
		resolver.Dispose();

		Assert.Same(resolved[0], resolved[1]);
		Assert.Equal(McpErrorCodes.RemoteLimit, limit.Code);
		Assert.Equal(2, cache.AcquireSessionCallCount);
		Assert.Equal(1, cache.DisposeCount);
		Assert.All(cache.Sessions, static session => Assert.Equal(1, session.DisposeCount));
	}

	private static CallToolRequestParams RequestWithPaths(string[] paths) =>
		new()
		{
			Name = "test",
			Arguments = new Dictionary<string, JsonElement>
			{
				["paths"] = JsonSerializer.SerializeToElement(paths)
			}
		};

	[Fact]
	public async Task PackSweepRemovesOnlyStaleOwnedSessionsAndPreservesAnActiveLease()
	{
		using var workspace = new TemporaryDirectory();
		var baseDirectory = Path.Combine(workspace.Path, "DevProjex", "mcp");
		var stale = Path.Combine(baseDirectory, new string('a', 32));
		Directory.CreateDirectory(stale);
		File.WriteAllText(Path.Combine(stale, ".session.lock"), string.Empty);
		File.WriteAllText(Path.Combine(stale, "pack.tmp"), "stale");
		Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
		var foreign = Path.Combine(baseDirectory, "unrelated-old-data");
		Directory.CreateDirectory(foreign);
		var protectedFile = Path.Combine(foreign, "keep.txt");
		File.WriteAllText(protectedFile, "keep");
		Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));

		using var active = new McpPackRegistry(workspace.Path);
		await active.ScavengingCompletion.WaitAsync(TestContext.Current.CancellationToken);
		Directory.SetLastWriteTimeUtc(active.SessionDirectory, DateTime.UtcNow.AddDays(-2));
		using var next = new McpPackRegistry(workspace.Path);
		await next.ScavengingCompletion.WaitAsync(TestContext.Current.CancellationToken);

		Assert.False(Directory.Exists(stale));
		Assert.True(File.Exists(protectedFile));
		Assert.True(Directory.Exists(active.SessionDirectory));
	}

	[Fact]
	public async Task PackSweepNeverTraversesASymbolicLinkSessionDirectory()
	{
		using var workspace = new TemporaryDirectory();
		var target = Path.Combine(workspace.Path, "protected-target");
		Directory.CreateDirectory(target);
		File.WriteAllText(Path.Combine(target, ".session.lock"), string.Empty);
		var protectedFile = Path.Combine(target, "keep.txt");
		File.WriteAllText(protectedFile, "keep");
		Directory.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-2));

		var baseDirectory = Path.Combine(workspace.Path, "DevProjex", "mcp");
		Directory.CreateDirectory(baseDirectory);
		var link = Path.Combine(baseDirectory, new string('b', 32));
		try
		{
			Directory.CreateSymbolicLink(link, target);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		using var registry = new McpPackRegistry(workspace.Path);
		await registry.ScavengingCompletion.WaitAsync(TestContext.Current.CancellationToken);

		Assert.True(File.Exists(protectedFile));
		Assert.True(Directory.Exists(link));
	}

	[Fact]
	public async Task PackRegistryConstructionDoesNotWaitForScavengingAndDisposeCancelsIt()
	{
		using var workspace = new TemporaryDirectory();
		var scavengeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var scavengeCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var construction = Task.Run(() => new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: McpPackRegistry.MaximumPackBytes,
			maximumSessionBytes: McpPackRegistry.MaximumSessionBytes,
			scavengeOperation: async cancellationToken =>
			{
				scavengeStarted.TrySetResult();
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
				finally
				{
					if (cancellationToken.IsCancellationRequested)
						scavengeCanceled.TrySetResult();
				}
			}),
			TestContext.Current.CancellationToken);
		var registry = await construction.WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);
		var sessionDirectory = registry.SessionDirectory;
		await scavengeStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);

		await registry.DisposeAsync().AsTask().WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);

		Assert.True(scavengeCanceled.Task.IsCompletedSuccessfully);
		Assert.False(Directory.Exists(sessionDirectory));
	}

	[Fact]
	public void PackStorageRejectsASymbolicLinkBaseDirectory()
	{
		using var workspace = new TemporaryDirectory();
		using var target = new TemporaryDirectory();
		var productDirectory = Path.Combine(workspace.Path, "DevProjex");
		Directory.CreateDirectory(productDirectory);
		var link = Path.Combine(productDirectory, "mcp");
		try
		{
			Directory.CreateSymbolicLink(link, target.Path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		var storageException = Assert.Throws<IOException>(() => new McpPackRegistry(workspace.Path));

		Assert.Contains("symbolic link", storageException.Message, StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFileSystemEntries(target.Path));
	}

	[Fact]
	public void PackStorageRejectsASymbolicLinkProductDirectory()
	{
		using var workspace = new TemporaryDirectory();
		using var target = new TemporaryDirectory();
		var link = Path.Combine(workspace.Path, "DevProjex");
		try
		{
			Directory.CreateSymbolicLink(link, target.Path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
			return;
		}

		var storageException = Assert.Throws<IOException>(() => new McpPackRegistry(workspace.Path));

		Assert.Contains("symbolic link", storageException.Message, StringComparison.Ordinal);
		Assert.Empty(Directory.EnumerateFileSystemEntries(target.Path));
	}

	[Fact]
	public void PackStorageRemovesItsSessionDirectoryWhenLeaseCreationFails()
	{
		using var workspace = new TemporaryDirectory();
		string? sessionDirectory = null;

		var exception = Record.Exception(() => new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: McpPackRegistry.MaximumPackBytes,
			maximumSessionBytes: McpPackRegistry.MaximumSessionBytes,
			onSessionDirectoryCreated: path =>
			{
				sessionDirectory = path;
				Directory.CreateDirectory(Path.Combine(path, ".session.lock"));
			}));

		Assert.True(exception is IOException or UnauthorizedAccessException, exception?.ToString());
		Assert.NotNull(sessionDirectory);
		Assert.False(Directory.Exists(sessionDirectory));
	}

	[Fact]
	public async Task PackCreationTracksUtf8MetricsAcrossWriteBoundaries()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		const string content = "alpha α\r\nemoji 😀\rtail\n";
		var bytes = Encoding.UTF8.GetBytes(content);

		var pack = await registry.CreateAsync(
			async (stream, token) =>
			{
				for (var index = 0; index < bytes.Length; index++)
					await stream.WriteAsync(bytes.AsMemory(index, 1), token);
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(content.Length, pack.Characters);
		Assert.Equal(4, pack.Lines);
		Assert.Equal(bytes.Length, pack.Bytes);
		Assert.Equal(content, await File.ReadAllTextAsync(pack.Path, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public async Task PackLineCheckpointsSkipCompletedPrefixesWithoutChangingRanges(string lineEnding)
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var line = $"α{lineEnding}";
		var content = string.Concat(Enumerable.Repeat(line, 600));
		var bytes = Encoding.UTF8.GetBytes(content);
		var pack = await registry.CreateAsync(
			async (stream, token) =>
			{
				for (var offset = 0; offset < bytes.Length; offset += 7)
				{
					var count = Math.Min(7, bytes.Length - offset);
					await stream.WriteAsync(bytes.AsMemory(offset, count), token);
				}
			},
			TestContext.Current.CancellationToken);

		var checkpoint = pack.ResolveLineCheckpoint(300);
		Assert.Equal(257, checkpoint.LineNumber);
		Assert.Equal(256L * Encoding.UTF8.GetByteCount(line), checkpoint.ByteOffset);
		await using var stream = new FileStream(
			pack.Path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		stream.Seek(checkpoint.ByteOffset, SeekOrigin.Begin);
		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 300,
			endLine: 301,
			maximumLines: 1000,
			maximumCharacters: 50_000,
			TestContext.Current.CancellationToken,
			knownTotalLines: pack.Lines,
			firstStreamLineNumber: checkpoint.LineNumber);

		Assert.Equal("α\nα", page.Text);
		Assert.Equal(300, page.StartLine);
		Assert.Equal(301, page.EndLine);
		Assert.Equal(601, page.TotalLines);
	}

	[Theory]
	[MemberData(nameof(PackCheckpointBoundaryCases))]
	public async Task PackLineCheckpointsPreserveBoundaryRanges(
		string lineEnding,
		int contentLineCount,
		bool hasTrailingLineEnding)
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var content = string.Join(lineEnding, Enumerable.Repeat("x", contentLineCount));
		if (hasTrailingLineEnding)
			content += lineEnding;
		var bytes = Encoding.UTF8.GetBytes(content);
		var pack = await registry.CreateAsync(
			async (stream, token) =>
			{
				for (var offset = 0; offset < bytes.Length; offset++)
					await stream.WriteAsync(bytes.AsMemory(offset, 1), token);
			},
			TestContext.Current.CancellationToken);

		var expectedLineCount = contentLineCount + (hasTrailingLineEnding ? 1 : 0);
		Assert.Equal(expectedLineCount, pack.Lines);
		Assert.DoesNotContain(
			pack.LineCheckpoints.Skip(1),
			checkpoint => checkpoint.ByteOffset == pack.Bytes);

		var checkpoint = pack.ResolveLineCheckpoint(expectedLineCount);
		Assert.InRange(expectedLineCount - checkpoint.LineNumber, 0, 256);
		await using var stream = new FileStream(
			pack.Path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		stream.Seek(checkpoint.ByteOffset, SeekOrigin.Begin);
		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: expectedLineCount,
			endLine: expectedLineCount,
			maximumLines: 1000,
			maximumCharacters: 50_000,
			TestContext.Current.CancellationToken,
			knownTotalLines: pack.Lines,
			firstStreamLineNumber: checkpoint.LineNumber);

		Assert.Equal(hasTrailingLineEnding ? string.Empty : "x", page.Text);
		Assert.Equal(expectedLineCount, page.StartLine);
		Assert.Equal(expectedLineCount, page.EndLine);
		Assert.Equal(expectedLineCount, page.TotalLines);
		Assert.False(page.IsTruncated);
	}

	[Fact]
	public async Task PackLineCheckpointAtEofFallsBackForTrailingEmptyLine()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\r\n", 256)));
		var pack = await registry.CreateAsync(
			async (stream, token) =>
			{
				for (var offset = 0; offset < bytes.Length; offset++)
					await stream.WriteAsync(bytes.AsMemory(offset, 1), token);
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(257, pack.Lines);
		var checkpoint = pack.ResolveLineCheckpoint(257);
		Assert.Equal(new McpPackLineCheckpoint(1, 0), checkpoint);
		await using var stream = new FileStream(
			pack.Path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);
		var page = await McpTextRanges.ReadPageAsync(
			stream,
			startLine: 257,
			endLine: 257,
			maximumLines: 1000,
			maximumCharacters: 50_000,
			TestContext.Current.CancellationToken,
			knownTotalLines: pack.Lines,
			firstStreamLineNumber: checkpoint.LineNumber);

		Assert.Equal(string.Empty, page.Text);
		Assert.Equal(257, page.StartLine);
		Assert.Equal(257, page.EndLine);
		Assert.Equal(257, page.TotalLines);
		Assert.False(page.IsTruncated);
	}

	[Fact]
	public async Task PackCreationDoesNotPublishWhenCancellationArrivesAfterWriting()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: 16,
			maximumSessionBytes: 16);
		using var cancellation = new CancellationTokenSource();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CreateAsync(
			async (stream, _) =>
			{
				await stream.WriteAsync("redacted"u8.ToArray());
				cancellation.Cancel();
			},
			cancellation.Token));

		Assert.Empty(Directory.EnumerateFiles(registry.SessionDirectory, "*.pack"));
		var replacement = await registry.CreateAsync(
			async (stream, token) => await stream.WriteAsync(new byte[16], token),
			TestContext.Current.CancellationToken);
		Assert.Equal(16, replacement.Bytes);
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
	public async Task PackCreationCleanupPreservesTheOriginalWriterFailure()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var expected = new InvalidOperationException("writer failed");

		var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateAsync(
			async (stream, _) =>
			{
				var path = Assert.Single(Directory.EnumerateFiles(registry.SessionDirectory, "*.pack"));
				await stream.DisposeAsync();
				File.Delete(path);
				Directory.CreateDirectory(path);
				throw expected;
			},
			TestContext.Current.CancellationToken));

		Assert.Same(expected, actual);
	}

	[Fact]
	public async Task PackRegistryDisposeDuringCreateRejectsPublicationAndRemovesSession()
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpPackRegistry(workspace.Path);
		var sessionDirectory = registry.SessionDirectory;
		var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var create = registry.CreateAsync(
			async (stream, token) =>
			{
				await stream.WriteAsync("first"u8.ToArray(), token);
				writerStarted.TrySetResult();
				await releaseWriter.Task.WaitAsync(token);
				await stream.WriteAsync("second"u8.ToArray(), token);
			},
			TestContext.Current.CancellationToken);
		await writerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		registry.Dispose();
		releaseWriter.TrySetResult();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => create);
		Assert.False(Directory.Exists(sessionDirectory));
	}

	[Fact]
	public async Task PackRegistryDisposeDuringCreateKeepsSessionLeaseUntilWriterFinishes()
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpPackRegistry(workspace.Path);
		var leasePath = Path.Combine(registry.SessionDirectory, ".session.lock");
		var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var create = registry.CreateAsync(
			async (stream, token) =>
			{
				await stream.WriteAsync("first"u8.ToArray(), token);
				writerStarted.TrySetResult();
				await releaseWriter.Task.WaitAsync(token);
			},
			TestContext.Current.CancellationToken);
		await writerStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		registry.Dispose();

		Assert.Throws<IOException>(() => new FileStream(
			leasePath,
			FileMode.Open,
			FileAccess.ReadWrite,
			FileShare.Delete));

		releaseWriter.TrySetResult();
		await Assert.ThrowsAsync<ObjectDisposedException>(() => create);
	}

	[Fact]
	public async Task PackRemovalIgnoresCleanupAccessFailures()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(workspace.Path);
		var packId = await registry.StoreAsync("redacted context", TestContext.Current.CancellationToken);
		var path = registry.Resolve(packId);
		File.Delete(path);
		Directory.CreateDirectory(path);

		registry.Remove(packId);

		Assert.True(Directory.Exists(path));
	}

	[Fact]
	public async Task PackStorageEnforcesSinglePackLimitWhileWriting()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: 8,
			maximumSessionBytes: 16);

		var exception = await Assert.ThrowsAsync<McpToolException>(() => registry.CreateAsync(
			async (stream, token) => await stream.WriteAsync(new byte[9], token),
			TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.PackTooLarge, exception.Code);
		Assert.Empty(Directory.EnumerateFiles(registry.SessionDirectory, "*.pack"));
	}

	[Fact]
	public async Task PackStorageAccountsForConcurrentSessionReservationsAtomically()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: 8,
			maximumSessionBytes: 10);
		var bothWritersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var readyWriters = 0;

		Task<McpPackDocument> CreatePackAsync() => registry.CreateAsync(
			async (stream, token) =>
			{
				if (Interlocked.Increment(ref readyWriters) == 2)
					bothWritersReady.TrySetResult();
				await bothWritersReady.Task.WaitAsync(token);
				await stream.WriteAsync(new byte[6], token);
			},
			TestContext.Current.CancellationToken);
		static async Task<object> CaptureAsync(Task<McpPackDocument> task)
		{
			try
			{
				return await task;
			}
			catch (Exception exception)
			{
				return exception;
			}
		}

		var results = await Task.WhenAll(
			CaptureAsync(CreatePackAsync()),
			CaptureAsync(CreatePackAsync()));

		Assert.Single(results, static result => result is McpPackDocument);
		var failure = Assert.IsType<McpToolException>(Assert.Single(results, static result => result is Exception));
		Assert.Equal(McpErrorCodes.PackTooLarge, failure.Code);
		Assert.Single(Directory.EnumerateFiles(registry.SessionDirectory, "*.pack"));
	}

	[Fact]
	public async Task RemovingAbandonedPackReclaimsSessionQuota()
	{
		using var workspace = new TemporaryDirectory();
		using var registry = new McpPackRegistry(
			workspace.Path,
			timeProvider: null,
			maximumPackBytes: 8,
			maximumSessionBytes: 8);
		var abandoned = await registry.CreateAsync(
			async (stream, token) => await stream.WriteAsync(new byte[8], token),
			TestContext.Current.CancellationToken);

		registry.Remove(abandoned.Id);
		var replacement = await registry.CreateAsync(
			async (stream, token) => await stream.WriteAsync(new byte[8], token),
			TestContext.Current.CancellationToken);

		Assert.Equal(8, replacement.Bytes);
		Assert.Single(Directory.EnumerateFiles(registry.SessionDirectory, "*.pack"));
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
		var direct = McpTextRanges.Slice(
			"\r\nvalue\n",
			1,
			2,
			10,
			100,
			TestContext.Current.CancellationToken);

		Assert.Equal("\nvalue", streamed.Text);
		Assert.Equal("\nvalue", sliced.Text);
		Assert.Equal("\nvalue", direct.Text);
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
		var direct = McpTextRanges.Slice(
			content,
			2,
			2,
			10,
			100,
			TestContext.Current.CancellationToken);

		Assert.Equal(string.Empty, page.Text);
		Assert.Equal(string.Empty, direct.Text);
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
	public void TextSliceDoesNotMaterializeEveryLineOfLargeContent()
	{
		var content = new string('\n', 8 * 1024 * 1024);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var page = McpTextRanges.Slice(
			content,
			startLine: 1,
			endLine: null,
			maximumLines: 1000,
			maximumCharacters: 50_000,
			TestContext.Current.CancellationToken);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(8 * 1024 * 1024 + 1, page.TotalLines);
		Assert.Equal(1000, page.EndLine);
		Assert.InRange(allocatedBytes, 0, 2 * 1024 * 1024);
	}

	[Fact]
	public void SearchScannerKeepsOnlyBoundedMatchContext()
	{
		const string content = "before\r\nneedle\rcontext\nneedle-again\rafter";

		var result = McpSearchTextScanner.Scan(
			content,
			new McpSearchRegex("needle", ignoreCase: false),
			contextLines: 1,
			maximumStoredMatches: 1,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, result.TotalMatches);
		var match = Assert.Single(result.Matches);
		Assert.Equal(2, match.MatchLineNumber);
		Assert.Equal(
			["before", "needle", "context"],
			match.Lines.Select(line => content.Substring(line.Offset, line.Length)));

		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		Assert.Throws<OperationCanceledException>(() => McpSearchTextScanner.Scan(
			content,
			new McpSearchRegex("needle", ignoreCase: false),
			contextLines: 1,
			maximumStoredMatches: 1,
			cancellation.Token));
	}

	[Fact]
	public void SearchScannerDoesNotAllocateOneObjectPerSourceLine()
	{
		var content = new string('\n', 1024 * 1024);
		var regex = new McpSearchRegex("needle", ignoreCase: false);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var result = McpSearchTextScanner.Scan(
			content,
			regex,
			contextLines: 2,
			maximumStoredMatches: 50,
			TestContext.Current.CancellationToken);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(0, result.TotalMatches);
		Assert.Empty(result.Matches);
		Assert.InRange(allocatedBytes, 0, 2 * 1024 * 1024);
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

	private sealed class TrackingRemoteCache(IReadOnlyDictionary<string, string> repositories) : IRepoCacheService
	{
		private readonly object _sync = new();
		private readonly List<TrackingRemoteSession> _sessions = [];
		private int _acquireSessionCallCount;
		private int _disposeCount;

		public string CacheRootPath => Path.GetDirectoryName(repositories.Values.First())!;
		public IReadOnlyList<string> CacheSearchRootPaths => [CacheRootPath];
		public IReadOnlyList<TrackingRemoteSession> Sessions
		{
			get
			{
				lock (_sync)
					return _sessions.ToArray();
			}
		}
		public int AcquireSessionCallCount => Volatile.Read(ref _acquireSessionCallCount);
		public int DisposeCount => Volatile.Read(ref _disposeCount);
		public Task? SessionGate { get; init; }

		public async Task<IRepositoryCacheSession?> TryAcquireRepositorySessionAsync(
			string repositoryUrl,
			string? branch = null,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _acquireSessionCallCount);
			if (SessionGate is not null)
				await SessionGate.WaitAsync(cancellationToken);
			var session = new TrackingRemoteSession(repositories[repositoryUrl], repositoryUrl, branch);
			lock (_sync)
				_sessions.Add(session);
			return session;
		}

		public Task<IAsyncDisposable> AcquireRepositoryOperationAsync(
			string repositoryUrl,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult<IAsyncDisposable>(NoopAsyncDisposable.Instance);
		}

		public void Dispose() => Interlocked.Increment(ref _disposeCount);
		public string CreateRepositoryDirectory(string repositoryUrl) => throw new NotSupportedException();
		public string CreateRepositoryStagingDirectory(string repositoryUrl) => throw new NotSupportedException();
		public string PublishRepositoryDirectory(string stagingPath, string repositoryUrl) => throw new NotSupportedException();
		public RepositoryCacheIndexEntry? FindIndexedRepository(string repositoryUrl) => null;
		public IReadOnlyList<RepositoryCacheCatalogEntry> ListIndexedRepositories() => [];
		public RepositoryCacheManagementListResult ListCacheEntriesForManagement() => new([], 0);
		public Task<IRepositoryCacheSession?> TryAcquireRepositorySessionByPathAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => Task.FromResult<IRepositoryCacheSession?>(null);
		public void RecordIndexedRepository(
			string repositoryUrl,
			string localPath,
			string? branch = null,
			string? commitHash = null,
			RepositoryCacheEntryState state = RepositoryCacheEntryState.Ready) => throw new NotSupportedException();
		public void RemoveIndexedRepository(string localPath) => throw new NotSupportedException();
		public void DeleteRepositoryDirectory(string path) => throw new NotSupportedException();
		public void ClearAllCache() => throw new NotSupportedException();
		public CacheClearResult ClearAllCacheWithResult() => throw new NotSupportedException();
		public CacheClearResult RemoveCachedRepositoryWithResult(string repositoryUrl) => throw new NotSupportedException();
		public void CleanupStaleCacheOnStartup() => throw new NotSupportedException();
		public void RequestStaleCacheCleanupOnStartup() => throw new NotSupportedException();
		public void CollectGarbage() => throw new NotSupportedException();
		public void RequestGarbageCollection() => throw new NotSupportedException();
		public void RefreshIndexedRepositorySize(string localPath) => throw new NotSupportedException();
		public bool IsInCache(string path) => false;
		public bool PathsBelongToSameRepository(string left, string right) => false;
	}

	private sealed class TrackingRemoteSession(
		string repositoryPath,
		string repositoryUrl,
		string? branch) : IRepositoryCacheSession
	{
		public string RepositoryPath { get; } = repositoryPath;
		public string RepositoryUrl { get; } = repositoryUrl;
		public string? Branch { get; } = branch;
		public RepositoryCacheContentKind ContentKind => RepositoryCacheContentKind.Git;
		public int DisposeCount { get; private set; }
		public void Dispose() => DisposeCount++;
	}

	private sealed class NoopAsyncDisposable : IAsyncDisposable
	{
		public static NoopAsyncDisposable Instance { get; } = new();
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
