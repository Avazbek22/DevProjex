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
	public async Task MissingExplicitSelectionNeverFallsBackToWholeTree()
	{
		using var workspace = CreateWorkspace();

		using var document = await ExportJsonWithDiagnosticAsync(
			workspace.Path,
			"DPX-SELECTION-PATH-MISSING",
			"--select", "missing.cs");

		Assert.Empty(ReadFilePaths(document));
		Assert.Equal(0, document.RootElement.GetProperty("metrics").GetProperty("files").GetInt32());
		Assert.Contains(
			document.RootElement.GetProperty("diagnostics").EnumerateArray(),
			static item => item.GetProperty("code").GetString() == "DPX-SELECTION-PATH-MISSING");
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
	public async Task SelectionPathCasingUsesThePlatformPathPolicy()
	{
		using var workspace = CreateWorkspace();

		using var document = OperatingSystem.IsWindows()
			? await ExportJsonAsync(workspace.Path, "--select", "SRC/A.CS")
			: await ExportJsonWithDiagnosticAsync(
				workspace.Path,
				"DPX-SELECTION-PATH-MISSING",
				"--select", "SRC/A.CS");

		if (OperatingSystem.IsWindows())
			Assert.Equal(FullContentPaths(workspace.Path, "src/a.cs"), ReadFilePaths(document));
		else
			Assert.Empty(ReadFilePaths(document));
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
			.Select(path => Path.Combine(rootPath, path.Replace('/', Path.DirectorySeparatorChar)))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();

	private sealed class ThrowingTextReader : TextReader
	{
		public override Task<string?> ReadLineAsync() =>
			throw new InvalidOperationException("Interactive stdin must not be read.");

		public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Interactive stdin must not be read.");
	}
}
