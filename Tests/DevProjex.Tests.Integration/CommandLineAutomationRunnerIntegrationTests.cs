using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.Integration;

public sealed class CommandLineAutomationRunnerIntegrationTests
{
	public static TheoryData<string, string[], string[]> SingleIgnoreOptionExportCases => new()
	{
		{
			CommandLineOptionTokens.IgnoreGitIgnore,
			["git-generated.txt"],
			["node_modules", "bundle.js", ".cache", "Cached.cs", ".env", "empty-file.txt", "LICENSE", "empty-folder"]
		},
		{
			CommandLineOptionTokens.IgnoreSmartIgnore,
			["node_modules", "bundle.js"],
			["git-generated.txt", ".cache", "Cached.cs", ".env", "empty-file.txt", "LICENSE", "empty-folder"]
		},
		{
			CommandLineOptionTokens.IgnoreDotFolders,
			[".cache", "Cached.cs"],
			["git-generated.txt", "node_modules", "bundle.js", ".env", "empty-file.txt", "LICENSE", "empty-folder"]
		},
		{
			CommandLineOptionTokens.IgnoreDotFiles,
			[".env"],
			["git-generated.txt", "node_modules", "bundle.js", ".cache", "Cached.cs", "empty-file.txt", "LICENSE", "empty-folder"]
		},
		{
			CommandLineOptionTokens.IgnoreEmptyFiles,
			["empty-file.txt"],
			["git-generated.txt", "node_modules", "bundle.js", ".cache", "Cached.cs", ".env", "LICENSE", "empty-folder"]
		},
		{
			CommandLineOptionTokens.IgnoreEmptyFolders,
			["empty-folder"],
			["git-generated.txt", "node_modules", "bundle.js", ".cache", "Cached.cs", ".env", "empty-file.txt", "LICENSE"]
		},
		{
			CommandLineOptionTokens.IgnoreExtensionlessFiles,
			["LICENSE"],
			["git-generated.txt", "node_modules", "bundle.js", ".cache", "Cached.cs", ".env", "empty-file.txt", "empty-folder"]
		}
	};

	public static TheoryData<string, string[]> ExplicitIgnoreOptionReportCases => new()
	{
		{ CommandLineOptionTokens.IgnoreNone, [] },
		{ CommandLineOptionTokens.IgnoreSmartIgnore, ["smartIgnore"] },
		{ CommandLineOptionTokens.IgnoreGitIgnore, ["useGitIgnore"] },
		{ CommandLineOptionTokens.IgnoreHiddenFolders, ["hiddenFolders"] },
		{ CommandLineOptionTokens.IgnoreHiddenFiles, ["hiddenFiles"] },
		{ CommandLineOptionTokens.IgnoreDotFolders, ["dotFolders"] },
		{ CommandLineOptionTokens.IgnoreDotFiles, ["dotFiles"] },
		{ CommandLineOptionTokens.IgnoreEmptyFolders, ["emptyFolders"] },
		{ CommandLineOptionTokens.IgnoreEmptyFiles, ["emptyFiles"] },
		{ CommandLineOptionTokens.IgnoreExtensionlessFiles, ["extensionlessFiles"] }
	};

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiWritesReportAndPrintsReportPathToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("tests", "AppTests.cs"), "class AppTests {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "cli-report.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);
		var context = CreateContext(output, error);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(reportPath));

		var json = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(temp.Path, root.GetProperty("rootPath").GetString());
		Assert.Equal("src", root.GetProperty("selection").GetProperty("selectedRootFolders")[0].GetString());
		Assert.Equal(".cs", root.GetProperty("selection").GetProperty("selectedExtensions")[0].GetString());
		Assert.Empty(root.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
		Assert.True(root.GetProperty("timing").GetProperty("totalMilliseconds").GetDouble() >= 0);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_BareReportWritesToUnicodeDocumentsFolderWithUniqueName()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var documentsPath = temp.CreateDirectory("Документы с пробелами");
		var reportId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
		var expectedPath = Path.Combine(
			documentsPath,
			"DevProjex",
			"reports",
			"devprojex-report-2026-06-20_12-13-14-cccccccccccccccccccccccccccccccc.json");
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 20, 12, 13, 14, TimeSpan.Zero),
			reportIdProvider: () => reportId);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Report,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error, services => services with { ReportPathResolver = resolver }),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{expectedPath}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(expectedPath));

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(expectedPath, TestContext.Current.CancellationToken));
		Assert.Equal(temp.Path, document.RootElement.GetProperty("rootPath").GetString());
		Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(expectedPath)!, "*.tmp"));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_BareReportFallsBackToUnicodeUserProfileWhenDocumentsUnavailable()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var userProfilePath = temp.CreateDirectory("Профиль пользователя");
		var reportId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
		var expectedPath = Path.Combine(
			userProfilePath,
			"DevProjex",
			"reports",
			"devprojex-report-2026-06-20_12-13-14-dddddddddddddddddddddddddddddddd.json");
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: _ => "  ",
			userProfilePathProvider: () => userProfilePath,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 20, 12, 13, 14, TimeSpan.Zero),
			reportIdProvider: () => reportId);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Silent,
			temp.Path,
			CommandLineOptionTokens.Report
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error, services => services with { ReportPathResolver = resolver }),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{expectedPath}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(expectedPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ConcurrentBareReportsKeepBothFilesWhenTimestampMatches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var documentsPath = temp.CreateDirectory("Documents");
		var timestamp = new DateTimeOffset(2026, 6, 20, 12, 13, 14, TimeSpan.Zero);
		var firstId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
		var secondId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
		var firstPath = BuildDefaultReportPath(documentsPath, timestamp, firstId);
		var secondPath = BuildDefaultReportPath(documentsPath, timestamp, secondId);
		var firstResolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
			utcNowProvider: () => timestamp,
			reportIdProvider: () => firstId);
		var secondResolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
			utcNowProvider: () => timestamp,
			reportIdProvider: () => secondId);
		using var firstOutput = new StringWriter();
		using var firstError = new StringWriter();
		using var secondOutput = new StringWriter();
		using var secondError = new StringWriter();
		var firstParseResult = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi, temp.Path, CommandLineOptionTokens.Report]);
		var secondParseResult = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi, temp.Path, CommandLineOptionTokens.Report]);

		var exitCodes = await Task.WhenAll(
			CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
				firstParseResult,
				CreateContext(firstOutput, firstError, services => services with { ReportPathResolver = firstResolver }),
				TestContext.Current.CancellationToken),
			CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
				secondParseResult,
				CreateContext(secondOutput, secondError, services => services with { ReportPathResolver = secondResolver }),
				TestContext.Current.CancellationToken));

		Assert.Equal([CommandLineExitCodes.Success, CommandLineExitCodes.Success], exitCodes);
		Assert.Equal($"{firstPath}{Environment.NewLine}", firstOutput.ToString());
		Assert.Equal($"{secondPath}{Environment.NewLine}", secondOutput.ToString());
		Assert.Equal(string.Empty, firstError.ToString());
		Assert.Equal(string.Empty, secondError.ToString());
		Assert.True(File.Exists(firstPath));
		Assert.True(File.Exists(secondPath));
		Assert.NotEqual(firstPath, secondPath);
		Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(firstPath)!, "*.tmp"));

		using var firstDocument = JsonDocument.Parse(await File.ReadAllTextAsync(firstPath, TestContext.Current.CancellationToken));
		using var secondDocument = JsonDocument.Parse(await File.ReadAllTextAsync(secondPath, TestContext.Current.CancellationToken));
		Assert.Equal(temp.Path, firstDocument.RootElement.GetProperty("rootPath").GetString());
		Assert.Equal(temp.Path, secondDocument.RootElement.GetProperty("rootPath").GetString());
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_BareReportReturnsRuntimeErrorWhenDefaultDocumentsPathIsAFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var blockedDocumentsPath = Path.Combine(temp.Path, "Documents file");
		await File.WriteAllTextAsync(blockedDocumentsPath, "occupied", TestContext.Current.CancellationToken);
		var resolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? blockedDocumentsPath : string.Empty,
			reportIdProvider: () => Guid.Parse("abababab-abab-abab-abab-abababababab"));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			temp.Path,
			CommandLineOptionTokens.Report
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error, services => services with { ReportPathResolver = resolver }),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.StartsWith("DevProjex: ", error.ToString(), StringComparison.Ordinal);
		Assert.Equal("occupied", await File.ReadAllTextAsync(blockedDocumentsPath, TestContext.Current.CancellationToken));
		Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiMissingRootReturnsRuntimeErrorAndDoesNotCreateReport()
	{
		using var temp = new TemporaryDirectory();
		var missingRoot = Path.Combine(temp.Path, "missing");
		var reportPath = Path.Combine(temp.Path, "reports", "missing.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, missingRoot,
			CommandLineOptionTokens.ReportPath, reportPath
		]);
		var context = CreateContext(output, error);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Project path was not found", error.ToString(), StringComparison.Ordinal);
		Assert.False(File.Exists(reportPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeToStdoutWithoutNoUiUsesSelectionFilters()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("src", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("App.cs", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExplicitGitAndSmartIgnoreOverridesAffectMixedWorkspaceIndependently()
	{
		using var temp = new TemporaryDirectory();
		SeedMixedIgnoreWorkspace(temp);

		var noIgnores = await RunTreeExportAsync(temp.Path, CommandLineOptionTokens.IgnoreNone);
		var gitOnly = await RunTreeExportAsync(temp.Path, CommandLineOptionTokens.IgnoreGitIgnore);
		var smartOnly = await RunTreeExportAsync(temp.Path, CommandLineOptionTokens.IgnoreSmartIgnore);
		var gitAndSmart = await RunTreeExportAsync(
			temp.Path,
			CommandLineOptionTokens.IgnoreGitIgnore,
			CommandLineOptionTokens.IgnoreSmartIgnore);

		AssertCommandSucceeded(noIgnores);
		AssertCommandSucceeded(gitOnly);
		AssertCommandSucceeded(smartOnly);
		AssertCommandSucceeded(gitAndSmart);

		Assert.Contains("git-cache.txt", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("node_modules", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("bundle.js", noIgnores.Stdout, StringComparison.Ordinal);

		Assert.DoesNotContain("git-cache.txt", gitOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("node_modules", gitOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("bundle.js", gitOnly.Stdout, StringComparison.Ordinal);

		Assert.Contains("git-cache.txt", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("node_modules", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("bundle.js", smartOnly.Stdout, StringComparison.Ordinal);

		Assert.DoesNotContain("git-cache.txt", gitAndSmart.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("node_modules", gitAndSmart.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("bundle.js", gitAndSmart.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(SingleIgnoreOptionExportCases))]
	public async Task RunUtilityOrHeadlessAsync_SingleIgnoreOptionOverrideHidesOnlyExpectedTreeEntries(
		string ignoreOptionName,
		string[] hiddenEntries,
		string[] visibleEntries)
	{
		using var temp = new TemporaryDirectory();
		SeedFullIgnoreOverrideWorkspace(temp);

		var result = await RunTreeExportAsync(temp.Path, ignoreOptionName);

		AssertCommandSucceeded(result);
		foreach (var entry in hiddenEntries)
			Assert.DoesNotContain(entry, result.Stdout, StringComparison.Ordinal);
		foreach (var entry in visibleEntries)
			Assert.Contains(entry, result.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(ExplicitIgnoreOptionReportCases))]
	public async Task RunUtilityOrHeadlessAsync_NoUiReportRecordsEveryExplicitIgnoreOverride(
		string ignoreOptionName,
		string[] expectedSelectedIgnoreOptions)
	{
		using var temp = new TemporaryDirectory();
		SeedFullIgnoreOverrideWorkspace(temp);

		var result = await RunAutomationAsync(
			CommandLineOptionTokens.NoUi,
			temp.Path,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, ignoreOptionName);

		AssertCommandSucceeded(result);
		using var document = JsonDocument.Parse(result.Stdout);
		var actual = ReadStringArray(document.RootElement.GetProperty("selection").GetProperty("selectedIgnoreOptions"));
		Assert.Equal(expectedSelectedIgnoreOptions, actual);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiReportSerializesMultipleIgnoreOverridesInStableEnumOrder()
	{
		using var temp = new TemporaryDirectory();
		SeedFullIgnoreOverrideWorkspace(temp);

		var result = await RunAutomationAsync(
			CommandLineOptionTokens.NoUi,
			temp.Path,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreExtensionlessFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreEmptyFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreEmptyFolders,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreHiddenFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreHiddenFolders,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreGitIgnore,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		AssertCommandSucceeded(result);
		using var document = JsonDocument.Parse(result.Stdout);
		var actual = ReadStringArray(document.RootElement.GetProperty("selection").GetProperty("selectedIgnoreOptions"));
		Assert.Equal(
			[
				"smartIgnore",
				"useGitIgnore",
				"hiddenFolders",
				"hiddenFiles",
				"dotFolders",
				"dotFiles",
				"emptyFolders",
				"emptyFiles",
				"extensionlessFiles"
			],
			actual);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiImplicitReportUsesDynamicIgnoreDefaults()
	{
		using var temp = new TemporaryDirectory();
		SeedDynamicDefaultIgnoreWorkspace(temp);

		var defaultResult = await RunAutomationAsync(CommandLineOptionTokens.NoUi, temp.Path);
		var noIgnoreResult = await RunAutomationAsync(
			CommandLineOptionTokens.NoUi,
			temp.Path,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		AssertCommandSucceeded(defaultResult);
		AssertCommandSucceeded(noIgnoreResult);

		using var defaultDocument = JsonDocument.Parse(defaultResult.Stdout);
		using var noIgnoreDocument = JsonDocument.Parse(noIgnoreResult.Stdout);
		var defaultRoot = defaultDocument.RootElement;
		var noIgnoreRoot = noIgnoreDocument.RootElement;
		var selectedDefaults = ReadStringArray(defaultRoot.GetProperty("selection").GetProperty("selectedIgnoreOptions"));

		Assert.Contains("useGitIgnore", selectedDefaults);
		Assert.Contains("dotFolders", selectedDefaults);
		Assert.Contains("emptyFiles", selectedDefaults);
		Assert.Contains("emptyFolders", selectedDefaults);
		Assert.Contains("extensionlessFiles", selectedDefaults);
		Assert.Empty(noIgnoreRoot.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
		Assert.True(
			ReadTreeFileCount(defaultRoot) < ReadTreeFileCount(noIgnoreRoot),
			"Headless dynamic defaults should hide the same noise that the UI checks by default.");
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeContentToFileWritesUtf8PayloadAndPrintsPath()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		var exportPath = Path.Combine(temp.Path, "exports", "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(exportPath));

		var bytes = await File.ReadAllBytesAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
		var payload = Encoding.UTF8.GetString(bytes);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("appsettings.json", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ConvenienceAliasesExportTreeContentToFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		var exportPath = Path.Combine(temp.Path, "exports", "alias-context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, exportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		var payload = await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("appsettings.json", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportContentOnlyWritesNoTreeBranches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("App.cs:", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("class App", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("├──", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportJsonTreeToStdoutWritesParseableTreeOnly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		using var document = JsonDocument.Parse(output.ToString());
		var root = document.RootElement;
		Assert.Equal(Path.GetFullPath(temp.Path), root.GetProperty("rootPath").GetString());
		Assert.Equal("App.cs", root.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.DoesNotContain("class App", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_FormatAliasWritesJsonTreeToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		using var document = JsonDocument.Parse(output.ToString());
		Assert.Equal("App.cs", document.RootElement.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.DoesNotContain("class App", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_StrictExportWritesPayloadThenReturnsRuntimeErrorForWarnings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "exports", "strict-context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.True(File.Exists(exportPath));
		Assert.Contains("Strict mode failed", error.ToString(), StringComparison.Ordinal);
		Assert.Contains("Selected root folder was not found", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ReportAndExportToDifferentFilesWritesBothAndPrintsBothPaths()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "out", "report.json");
		var exportPath = Path.Combine(temp.Path, "out", "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Equal(
			$"{Path.GetFullPath(reportPath)}{Environment.NewLine}{Path.GetFullPath(exportPath)}{Environment.NewLine}",
			output.ToString());
		Assert.True(File.Exists(reportPath));
		Assert.True(File.Exists(exportPath));
		using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, reportDocument.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Contains("App.cs", await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ReportFileAndStdoutExportReturnsUsageErrorWithoutWritingReport()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "out", "report.json");

		var result = await RunAutomationAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Cannot combine stdout export with --report", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(reportPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ReportAndExportSamePathReturnsUsageErrorWithoutWritingFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var sharedPath = Path.Combine(temp.Path, "out", "same.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, sharedPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, sharedPath
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--report-path and --output must point to different files", error.ToString(), StringComparison.Ordinal);
		Assert.False(File.Exists(sharedPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ContentExportWithNoTextFilesWritesEmptyStdout()
	{
		using var temp = new TemporaryDirectory();
		var binaryPath = Path.Combine(temp.Path, "src", "image.bin");
		Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
		await File.WriteAllBytesAsync(binaryPath, [0, 1, 2, 3, 0], TestContext.Current.CancellationToken);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "bin",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Equal(string.Empty, error.ToString());
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ContentExportWithJsonFormatReturnsUsageError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.ExportFormat, "json"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--export-format applies only to tree", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOptionsBeforeExportParseAndRunCorrectly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("App.cs", await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOutputInsideProjectIsCreatedAfterScanAndNotSelfIncluded()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "context.txt");

		var result = await RunAutomationAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		AssertCommandSucceeded(result);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", result.Stdout);
		Assert.True(File.Exists(exportPath));
		var payload = await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("context.txt", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOutputExistingDirectoryReturnsRuntimeError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputDirectory = temp.CreateDirectory("existing-output-directory");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, outputDirectory,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.StartsWith("DevProjex: ", error.ToString(), StringComparison.Ordinal);
		Assert.True(Directory.Exists(outputDirectory));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeContentJsonWritesJsonTreeAndPlainTextContent()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var payload = output.ToString();
		var separatorIndex = payload.IndexOf("\u00A0", StringComparison.Ordinal);
		Assert.True(separatorIndex > 0, "Expected tree-content JSON export to separate JSON tree from plain text content.");
		using var document = JsonDocument.Parse(payload[..separatorIndex].TrimEnd('\r', '\n'));
		Assert.Equal("App.cs", document.RootElement.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.Contains("class App", payload[separatorIndex..], StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOverwritesExistingFileAndRemovesTailBytes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "context.txt");
		await File.WriteAllTextAsync(exportPath, new string('x', 2048), TestContext.Current.CancellationToken);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		var payload = await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('x', 64), payload, StringComparison.Ordinal);
	}

	private static async Task<CommandLineAutomationResult> RunTreeExportAsync(
		string projectPath,
		params string[] ignoreOptions)
	{
		var args = new List<string>
		{
			projectPath,
			CommandLineOptionTokens.Export,
			"tree"
		};

		foreach (var ignoreOption in ignoreOptions)
		{
			args.Add(CommandLineOptionTokens.Ignore);
			args.Add(ignoreOption);
		}

		return await RunAutomationAsync(args.ToArray());
	}

	private static async Task<CommandLineAutomationResult> RunAutomationAsync(params string[] args)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(args);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		return new CommandLineAutomationResult(exitCode, output.ToString(), error.ToString());
	}

	private static void AssertCommandSucceeded(CommandLineAutomationResult result)
	{
		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
	}

	private static void SeedMixedIgnoreWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(Path.Combine("git-app", ".gitignore"), "git-output/\n");
		temp.CreateFile(Path.Combine("git-app", "src", "GitApp.cs"), "class GitApp {}\n");
		temp.CreateFile(Path.Combine("git-app", "git-output", "git-cache.txt"), "git cache\n");
		temp.CreateFile(Path.Combine("web-app", "package.json"), "{}\n");
		temp.CreateFile(Path.Combine("web-app", "src", "WebApp.cs"), "class WebApp {}\n");
		temp.CreateFile(Path.Combine("web-app", "node_modules", "bundle.js"), "bundle\n");
	}

	private static void SeedFullIgnoreOverrideWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(Path.Combine("git-app", ".gitignore"), "git-output/\n");
		temp.CreateFile(Path.Combine("git-app", "src", "GitApp.cs"), "class GitApp {}\n");
		temp.CreateFile(Path.Combine("git-app", "git-output", "git-generated.txt"), "git generated\n");
		temp.CreateFile(Path.Combine("web-app", "package.json"), "{}\n");
		temp.CreateFile(Path.Combine("web-app", "src", "WebApp.cs"), "class WebApp {}\n");
		temp.CreateFile(Path.Combine("web-app", "node_modules", "bundle.js"), "bundle\n");
		temp.CreateFile(Path.Combine("general", ".cache", "Cached.cs"), "class Cached {}\n");
		temp.CreateFile(Path.Combine("general", ".env"), "SECRET=true\n");
		temp.CreateFile(Path.Combine("general", "empty-file.txt"), string.Empty);
		temp.CreateFile(Path.Combine("general", "LICENSE"), "license\n");
		temp.CreateFile(Path.Combine("general", "src", "General.cs"), "class General {}\n");
		temp.CreateDirectory(Path.Combine("general", "empty-folder"));
	}

	private static void SeedDynamicDefaultIgnoreWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile(".gitignore", "[Bb]in/\n*.g.cs\n");
		temp.CreateFile(Path.Combine("src", "Program.cs"), "class Program {}\n");
		temp.CreateFile(Path.Combine("src", "Generated.g.cs"), "// generated\n");
		temp.CreateFile(Path.Combine("src", "Empty.cs"), string.Empty);
		temp.CreateFile(Path.Combine("bin", "Debug", "App.dll"), "binary placeholder\n");
		temp.CreateFile(Path.Combine(".cache", "Cached.cs"), "class Cached {}\n");
		temp.CreateFile("LICENSE", "license\n");
		temp.CreateDirectory("empty-folder");
	}

	private static string[] ReadStringArray(JsonElement element) =>
		element.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static int ReadTreeFileCount(JsonElement root) =>
		root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32();

	private static CommandLineAutomationContext CreateContext(
		TextWriter output,
		TextWriter error,
		Func<AvaloniaAppServices, AvaloniaAppServices>? configureServices = null) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: options =>
			{
				var services = AvaloniaCompositionRoot.CreateDefault(options);
				return configureServices?.Invoke(services) ?? services;
			},
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");

	private static string BuildDefaultReportPath(string documentsPath, DateTimeOffset timestamp, Guid reportId) =>
		Path.Combine(
			documentsPath,
			"DevProjex",
			"reports",
			$"devprojex-report-{timestamp:yyyy-MM-dd_HH-mm-ss}-{reportId:N}.json");

	private sealed record CommandLineAutomationResult(int ExitCode, string Stdout, string Stderr);
}
