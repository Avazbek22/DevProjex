using System.CommandLine;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class SelectedPathsContractTests
{
	[Fact]
	public async Task NoSelectedPathsExportsTheWholeEffectiveTree()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonAsync(workspace.Path);

		Assert.Equal(
			FullContentPaths(workspace.Path, "docs/readme.md", "src/a.cs", "src/nested/b.cs"),
			ReadFilePaths(document));
	}

	[Fact]
	public async Task SingleDirectoryIncludesItsEffectiveSubtree()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonAsync(workspace.Path, "--select", "src");

		Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs", "src/nested/b.cs"), ReadFilePaths(document));
		Assert.Equal(["src"], ReadSelectedPaths(document));
	}

	[Fact]
	public async Task SingleFileSelectionReportsItsExactFilesystemSize()
	{
		using var workspace = CreateWorkspace();
		var selectedPath = Path.Combine(workspace.Path, "src", "a.cs");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", "src/a.cs");

		Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs"), ReadFilePaths(document));
		Assert.Equal(
			new FileInfo(selectedPath).Length,
			document.RootElement.GetProperty("metrics").GetProperty("bytes").GetInt64());
	}

	[Fact]
	public async Task ParentChildAndDuplicateSelectionsDoNotDuplicateFiles()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", "src",
			"--select", "src/nested/b.cs",
			"--select", "src");

		Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs", "src/nested/b.cs"), ReadFilePaths(document));
		Assert.Equal(["src"], ReadSelectedPaths(document));
	}

	[Fact]
	public async Task RootSelectionUsesCanonicalWholeTreeRepresentation()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonAsync(workspace.Path, "--select", ".");

		Assert.Equal(
			FullContentPaths(workspace.Path, "docs/readme.md", "src/a.cs", "src/nested/b.cs"),
			ReadFilePaths(document));
		Assert.Empty(ReadSelectedPaths(document));
	}

	[Fact]
	public async Task MissingExplicitSelectionIsAUsageErrorWithoutOutput()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select", "missing.cs");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SELECTION-PATH-MISSING", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("missing.cs", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SelectionExcludedByExtensionNeverFallsBackToWholeTree()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonWithDiagnosticAsync(
			workspace.Path,
			"DPX-SELECTION-PATH-MISSING",
			"--extension", "md",
			"--select", "src/a.cs");

		Assert.Empty(ReadFilePaths(document));
		Assert.Contains(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static item => item.GetProperty("code").GetString() == "DPX-SELECTION-PATH-MISSING");
	}

	[Theory]
	[InlineData("../outside")]
	[InlineData("src/../../outside")]
	public async Task ParentTraversalIsRejected(string selectedPath)
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select", selectedPath);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-SELECTION-PATH-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public async Task AbsoluteSelectionIsRejected()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select", System.IO.Path.Combine(workspace.Path, "src", "a.cs"));

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-SELECTION-PATH-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task InvalidWindowsSelectionNameIsAUsageError()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Asterisks are valid file-name characters on Unix filesystems.");
			return;
		}

		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select", "bad*name.cs");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SELECTION-PATH-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnicodeSelectionPreservesTheExactRelativePath()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile("данные/привет.cs", "class Привет {}\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", "данные/привет.cs");

		Assert.Equal(FullContentPaths(workspace.Path, "данные/привет.cs"), ReadFilePaths(document));
		Assert.Equal(["данные/привет.cs"], ReadSelectedPaths(document));
	}

	[Fact]
	public async Task UnixSelectionPreservesLeadingAndTrailingSpacesInFileName()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows normalizes trailing spaces in ordinary file names.");

		using var workspace = CreateWorkspace();
		const string relativePath = " leading-and-trailing .cs ";
		workspace.WriteFile(relativePath, "class SpacedName {}\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", relativePath);

		Assert.Equal(FullContentPaths(workspace.Path, relativePath), ReadFilePaths(document));
		Assert.Equal([relativePath], ReadSelectedPaths(document));
	}

	[Fact]
	public async Task UnixSelectionPreservesAWhitespaceOnlyFileName()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows does not support this file name through ordinary APIs.");
			return;
		}

		using var workspace = CreateWorkspace();
		const string relativePath = " ";
		workspace.WriteFile(relativePath, "whitespace-only file name\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", relativePath);

		Assert.Equal(FullContentPaths(workspace.Path, relativePath), ReadFilePaths(document));
		Assert.Equal([relativePath], ReadSelectedPaths(document));
	}

	[Fact]
	public void SelectionPathNormalizationPreservesSignificantWhitespace()
	{
		const string relativePath = " leading-and-trailing .cs ";

		Assert.Equal(relativePath, ProjectSelectionPath.NormalizeRelative(relativePath));
	}

	[Fact]
	public void SelectionPathNormalizationPreservesAWhitespaceOnlyName()
	{
		Assert.Equal(" ", ProjectSelectionPath.NormalizeRelative(" "));
	}

	[Fact]
	public async Task UnixSelectionPreservesALiteralBackslashInFileName()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows treats a backslash as a directory separator.");
			return;
		}

		using var workspace = CreateWorkspace();
		const string relativePath = "literal\\name.txt";
		workspace.WriteFile(relativePath, "literal backslash file name\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", relativePath);

		Assert.Equal(FullContentPaths(workspace.Path, relativePath), ReadFilePaths(document));
		Assert.Equal([relativePath], ReadSelectedPaths(document));
	}

	[Theory]
	[InlineData("\\leading-name.txt")]
	[InlineData("C:drive-relative-name.txt")]
	public async Task UnixSelectionUsesNativeRootedPathSemantics(string relativePath)
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows treats these values as rooted or drive-relative paths.");
			return;
		}

		using var workspace = CreateWorkspace();
		workspace.WriteFile(relativePath, "native Unix file name\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", relativePath);

		Assert.Equal(FullContentPaths(workspace.Path, relativePath), ReadFilePaths(document));
		Assert.Equal([relativePath], ReadSelectedPaths(document));
	}

	[Fact]
	public void SelectionPathNormalizationUsesOnlyNativeUnixSeparators()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows treats a backslash as a directory separator.");
			return;
		}

		Assert.Equal("literal\\name.txt", ProjectSelectionPath.NormalizeRelative("literal\\name.txt"));
	}

	[Fact]
	public async Task SelectionsAcrossMultipleRootFoldersAreCombinedWithoutFallback()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--root", "src",
			"--root", "docs",
			"--select", "src/a.cs",
			"--select", "docs/readme.md");

		Assert.Equal(FullContentPaths(workspace.Path, "docs/readme.md", "src/a.cs"), ReadFilePaths(document));
		Assert.Equal(["docs/readme.md", "src/a.cs"], ReadSelectedPaths(document));
	}

	[Fact]
	public async Task SelectFromFileCombinesWithDirectSelectionAndDeduplicates()
	{
		using var workspace = CreateWorkspace();
		var selectionFile = workspace.WriteFile(
			"selection.txt",
			"src/a.cs\n\ndocs/readme.md\nsrc/a.cs\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", "src/nested/b.cs",
			"--select-from", selectionFile);

		Assert.Equal(
			FullContentPaths(workspace.Path, "docs/readme.md", "src/a.cs", "src/nested/b.cs"),
			ReadFilePaths(document));
		Assert.Equal(
			["docs/readme.md", "src/a.cs", "src/nested/b.cs"],
			ReadSelectedPaths(document).Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task SelectionArgumentsPreserveCaseDistinctProjectEntriesBeforeResolution()
	{
		using var workspace = new TemporaryDirectory();
		var selectionFile = workspace.WriteFile("selection.txt", "foo.cs\n");
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var environment = new TestTerminalEnvironment();
		var selection = new SelectionOptions(localization, environment);
		var command = new RootCommand();
		selection.AddTo(command);
		var parseResult = command.Parse([
			"--select", "Foo.cs",
			"--select-from", selectionFile
		]);

		Assert.Empty(parseResult.Errors);
		var selectedPaths = await selection.ReadSelectedPathsAsync(
			parseResult,
			TestContext.Current.CancellationToken);

		Assert.NotNull(selectedPaths);
		Assert.Equal(
			["Foo.cs", "foo.cs"],
			selectedPaths!.Order(ProjectTreePathIdentity.CanonicalComparer));
	}

	[Fact]
	public async Task CaseSensitiveWorkspaceExportsBothDirectAndFileSelections()
	{
		using var workspace = new TemporaryDirectory();
		EnableCaseSensitiveDirectoryOrSkip(workspace.Path);
		workspace.WriteFile("Foo.cs", "upper-case-entry\n");
		workspace.WriteFile("foo.cs", "lower-case-entry\n");
		var selectionFile = workspace.WriteFile("selection.txt", "foo.cs\n");

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select", "Foo.cs",
			"--select-from", selectionFile);

		Assert.Equal(
			FullContentPaths(workspace.Path, "Foo.cs", "foo.cs"),
			ReadFilePaths(document));
		Assert.Equal(
			["Foo.cs", "foo.cs"],
			ReadSelectedPaths(document).Order(ProjectTreePathIdentity.CanonicalComparer));
	}

	[Fact]
	public async Task SelectFromFileAcceptsUtf8Bom()
	{
		using var workspace = CreateWorkspace();
		var selectionFile = Path.Combine(workspace.Path, "selection-utf8-bom.txt");
		await File.WriteAllTextAsync(
			selectionFile,
			"src/a.cs\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
			TestContext.Current.CancellationToken);

		using var document = await ExportJsonAsync(
			workspace.Path,
			"--select-from", selectionFile);

		Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs"), ReadFilePaths(document));
	}

	[Fact]
	public async Task SelectFromFileRejectsUtf16Bom()
	{
		using var workspace = CreateWorkspace();
		var selectionFile = Path.Combine(workspace.Path, "selection-utf16.txt");
		await File.WriteAllTextAsync(
			selectionFile,
			"src/a.cs\n",
			Encoding.Unicode,
			TestContext.Current.CancellationToken);
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", selectionFile);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SelectFromRedirectedStdinReadsUtf8Paths()
	{
		using var workspace = CreateWorkspace();
		workspace.WriteFile("данные/привет.cs", "class Привет {}\n");
		var environment = new TestTerminalEnvironment
		{
			Input = new StringReader("данные/привет.cs\n"),
			IsInputInteractive = false
		};

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", "-");

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(
			FullContentPaths(workspace.Path, "данные/привет.cs"),
			ReadFilePaths(document));
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task SelectFromInteractiveStdinFailsWithoutReading()
	{
		using var workspace = CreateWorkspace();
		var environment = new TestTerminalEnvironment
		{
			Input = new ThrowingTextReader(),
			IsInputInteractive = true
		};

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SelectFromRejectsMoreThanTheEntryLimit()
	{
		using var workspace = CreateWorkspace();
		var input = string.Concat(Enumerable.Repeat("src/a.cs\n", 100_001));
		var environment = new TestTerminalEnvironment
		{
			Input = new StringReader(input),
			IsInputInteractive = false
		};

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SelectFromRejectsAnOversizedSingleLineThroughBoundedReads()
	{
		using var workspace = CreateWorkspace();
		var input = new OversizedSingleLineReader(SelectionPathListReader.MaximumBytes + 1);
		var environment = new TestTerminalEnvironment
		{
			Input = input,
			IsInputInteractive = false
		};

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
		Assert.InRange(input.MaximumRequestedCharacters, 1, 16 * 1024);
		Assert.InRange(
			input.CharactersReturned,
			SelectionPathListReader.MaximumBytes + 1,
			SelectionPathListReader.MaximumBytes + 16 * 1024);
	}

	[Fact]
	public async Task SelectFromRawStdinRejectsInvalidUtf8()
	{
		using var workspace = CreateWorkspace();
		using var input = new MemoryStream([0x73, 0x72, 0x63, 0x2F, 0xC3, 0x28]);
		var environment = new TestTerminalEnvironment
		{
			RawInput = input,
			IsInputInteractive = false
		};

		var exitCode = await RunAsync(
			workspace.Path,
			environment,
			"--select-from", "-");

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-SELECT-FROM-INVALID", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SelectionPathCasingUsesThePlatformPathPolicy()
	{
		using var workspace = CreateWorkspace();
		const string selectedPath = "SRC/A.CS";

		if (OperatingSystem.IsWindows())
		{
			using var document = await ExportJsonAsync(workspace.Path, "--select", selectedPath);
			Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs"), ReadFilePaths(document));
		}
		else
		{
			var environment = new TestTerminalEnvironment();
			var exitCode = await RunAsync(workspace.Path, environment, "--select", selectedPath);
			var caseVariantExists = File.Exists(Path.Combine(workspace.Path, "SRC", "A.CS"));
			Assert.Equal(
				caseVariantExists ? CommandLineExitCodes.Success : CommandLineExitCodes.UsageError,
				exitCode);
			Assert.Contains("DPX-SELECTION-PATH-MISSING", environment.StandardError, StringComparison.Ordinal);
			Assert.Contains(selectedPath, environment.StandardError, StringComparison.Ordinal);
			if (caseVariantExists)
			{
				using var document = JsonDocument.Parse(environment.StandardOutput);
				Assert.Empty(ReadFilePaths(document));
			}
			else
			{
				Assert.Empty(environment.StandardOutput);
			}
		}
	}

	[Theory]
	[InlineData("analyze", "select")]
	[InlineData("analyze", "select-from")]
	[InlineData("tree", "select")]
	[InlineData("tree", "select-from")]
	[InlineData("context", "select")]
	[InlineData("context", "select-from")]
	[InlineData("project", "select")]
	[InlineData("project", "select-from")]
	public async Task PhysicallyMissingSelectionFailsBeforeAnyCommandWritesOutput(
		string command,
		string selectionSource)
	{
		using var workspace = CreateWorkspace();
		using var output = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var selectedValue = "missing/path.cs";
		if (selectionSource == "select-from")
			selectedValue = output.WriteFile("selection.txt", selectedValue + "\n");
		var destination = Path.Combine(output.Path, command == "project" ? "result.zip" : "result.txt");

		var exitCode = await RunSelectionCommandAsync(
			command,
			workspace.Path,
			destination,
			environment,
			$"--{selectionSource}",
			selectedValue);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-SELECTION-PATH-MISSING", environment.StandardError, StringComparison.Ordinal);
		Assert.False(File.Exists(destination));
		Assert.False(Directory.Exists(destination));
	}

	[Theory]
	[InlineData("analyze", "select")]
	[InlineData("analyze", "select-from")]
	[InlineData("tree", "select")]
	[InlineData("tree", "select-from")]
	[InlineData("context", "select")]
	[InlineData("context", "select-from")]
	public async Task ExistingSelectionExcludedFromEffectiveTreeRemainsAWarning(
		string command,
		string selectionSource)
	{
		using var workspace = CreateWorkspace();
		using var output = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var selectedValue = "src/a.cs";
		if (selectionSource == "select-from")
			selectedValue = output.WriteFile("selection.txt", selectedValue + "\n");
		var destination = Path.Combine(output.Path, "result.txt");

		var exitCode = await RunSelectionCommandAsync(
			command,
			workspace.Path,
			destination,
			environment,
			"--extension", "md",
			$"--{selectionSource}", selectedValue);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("DPX-SELECTION-PATH-MISSING", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("src/a.cs", environment.StandardError, StringComparison.Ordinal);
		Assert.True(File.Exists(destination));
	}

	[Fact]
	public async Task SelectedFileSymlinkNeverReadsItsExternalTarget()
	{
		using var workspace = CreateWorkspace();
		using var external = new TemporaryDirectory();
		var externalPath = external.WriteFile("secret.txt", "must-not-be-exported");
		var linkPath = Path.Combine(workspace.Path, "external-link.txt");
		try
		{
			try
			{
				File.CreateSymbolicLink(linkPath, externalPath);
				if (!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
					Assert.Skip("The environment did not create a detectable file symlink.");
			}
			catch (Exception exception) when (exception is
				       UnauthorizedAccessException or
				       IOException or
				       PlatformNotSupportedException)
			{
				Assert.Skip($"File symlinks are unavailable in this environment: {exception.GetType().Name}");
			}

			using var document = await ExportJsonWithDiagnosticAsync(
				workspace.Path,
				"DPX-SELECTION-PATH-MISSING",
				"--select", "external-link.txt");

			Assert.Empty(ReadFilePaths(document));
			Assert.DoesNotContain("must-not-be-exported", document.RootElement.GetRawText(), StringComparison.Ordinal);
		}
		finally
		{
			try
			{
				File.Delete(linkPath);
			}
			catch
			{
				// TemporaryDirectory cleanup remains the final fallback.
			}
		}
	}

	private static async Task<JsonDocument> ExportJsonAsync(
		string projectPath,
		params string[] selectionArguments)
		=> await ExportJsonWithDiagnosticAsync(
			projectPath,
			expectedDiagnostic: null,
			selectionArguments);

	private static async Task<JsonDocument> ExportJsonWithDiagnosticAsync(
		string projectPath,
		string? expectedDiagnostic,
		params string[] selectionArguments)
	{
		var environment = new TestTerminalEnvironment();
		var exitCode = await RunAsync(projectPath, environment, selectionArguments);
		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		if (expectedDiagnostic is null)
			Assert.Empty(environment.StandardError);
		else
			Assert.Contains(expectedDiagnostic, environment.StandardError, StringComparison.Ordinal);
		return JsonDocument.Parse(environment.StandardOutput);
	}

	private static Task<int> RunAsync(
		string projectPath,
		TestTerminalEnvironment environment,
		params string[] selectionArguments)
	{
		var arguments = new List<string>
		{
			"export", "context", projectPath,
			"--view", "content",
			"--format", "json",
			"--git-mode", "none",
			"--exclude", "none",
			"-o", "-"
		};
		arguments.AddRange(selectionArguments);
		return new TerminalApplication(environment, new TerminalServiceFactory())
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}

	private static TemporaryDirectory CreateWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/a.cs", "class A {}\n");
		workspace.WriteFile("src/nested/b.cs", "class B {}\n");
		workspace.WriteFile("docs/readme.md", "# Docs\n");
		return workspace;
	}

	private static void EnableCaseSensitiveDirectoryOrSkip(string directoryPath)
	{
		if (!OperatingSystem.IsWindows())
			return;

		try
		{
			using var process = System.Diagnostics.Process.Start(
				new System.Diagnostics.ProcessStartInfo("fsutil.exe")
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					ArgumentList = { "file", "setCaseSensitiveInfo", directoryPath, "enable" }
				});
			if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)))
			{
				try
				{
					process?.Kill(entireProcessTree: true);
				}
				catch (InvalidOperationException)
				{
				}

				Assert.Skip("Windows per-directory case sensitivity could not be enabled.");
			}

			if (process.ExitCode != 0)
				Assert.Skip("Windows per-directory case sensitivity is unavailable.");
		}
		catch (Exception exception) when (exception is
			       InvalidOperationException or
			       IOException or
			       System.ComponentModel.Win32Exception)
		{
			Assert.Skip(
				$"Windows per-directory case sensitivity is unavailable: {exception.GetType().Name}.");
		}
	}

	private static string[] ReadFilePaths(JsonDocument document) =>
		document.RootElement
			.GetProperty("files")
			.EnumerateArray()
			.Select(static item => item.GetProperty("path").GetString() ?? string.Empty)
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();

	private static string[] ReadSelectedPaths(JsonDocument document) =>
		document.RootElement
			.GetProperty("selection")
			.GetProperty("selectedPaths")
			.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static string[] FullContentPaths(string rootPath, params string[] relativePaths) =>
		relativePaths
			.Select(path => PathUtility.NormalizeSeparators(
				Path.Combine(rootPath, path.Replace('/', Path.DirectorySeparatorChar))))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();

	private sealed class ThrowingTextReader : TextReader
	{
		public override Task<string?> ReadLineAsync() =>
			throw new InvalidOperationException("Interactive stdin must not be read.");

		public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Interactive stdin must not be read.");
	}

	private static Task<int> RunSelectionCommandAsync(
		string command,
		string projectPath,
		string destination,
		TestTerminalEnvironment environment,
		params string[] selectionArguments)
	{
		var arguments = command switch
		{
			"analyze" => new List<string>
			{
				"analyze", projectPath, "--format", "json", "--git-mode", "none", "-o", destination
			},
			"tree" => new List<string>
			{
				"tree", projectPath, "--format", "text", "--git-mode", "none", "-o", destination
			},
			"context" => new List<string>
			{
				"export", "context", projectPath, "--view", "content", "--format", "text",
				"--git-mode", "none", "-o", destination
			},
			"project" => new List<string>
			{
				"export", "project", projectPath, "--as", "zip", "--git-mode", "none", "-o", destination
			},
			_ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
		};
		arguments.AddRange(selectionArguments);
		return new TerminalApplication(environment, new TerminalServiceFactory())
			.RunAsync(arguments, TestContext.Current.CancellationToken);
	}

	private sealed class OversizedSingleLineReader(long length) : TextReader
	{
		private long _remaining = length;

		public int MaximumRequestedCharacters { get; private set; }
		public long CharactersReturned { get; private set; }

		public override ValueTask<int> ReadAsync(
			Memory<char> buffer,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			MaximumRequestedCharacters = Math.Max(MaximumRequestedCharacters, buffer.Length);
			var count = (int)Math.Min(buffer.Length, _remaining);
			if (count == 0)
				return ValueTask.FromResult(0);
			buffer.Span[..count].Fill('x');
			_remaining -= count;
			CharactersReturned += count;
			return ValueTask.FromResult(count);
		}
	}
}
