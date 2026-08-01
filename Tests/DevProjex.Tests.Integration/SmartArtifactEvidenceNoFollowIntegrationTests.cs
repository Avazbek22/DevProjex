namespace DevProjex.Tests.Integration;

public sealed class SmartArtifactEvidenceNoFollowIntegrationTests
{
	[Theory]
	[MemberData(nameof(SymbolicLinkStates))]
	public void SmartArtifactEvidenceNoFollow_FileMarkerMatrixPreservesSourceTree(
		SmartArtifactEvidenceEntryState entryState)
	{
		using var project = new TemporaryDirectory();
		using var external = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("build/source.ts", "export const source = true;\n");
		var markerPath = Path.Combine(project.Path, "build", "asset-manifest.json");
		var targetPath = Path.Combine(external.Path, "asset-manifest.json");
		CreateFileEvidenceOrSkip(markerPath, targetPath, entryState);
		var rules = CreateFrontendRules(project.Path);

		Assert.Equal(
			entryState == SmartArtifactEvidenceEntryState.Regular,
			rules.IsSmartIgnoredDirectory(Path.Combine(project.Path, "build"), "build"));
		Assert.Equal(
			entryState != SmartArtifactEvidenceEntryState.Regular,
			ContainsPath(BuildTree(project.Path, rules).Root, "build/source.ts"));
	}

	[Theory]
	[MemberData(nameof(SymbolicLinkStates))]
	public void SmartArtifactEvidenceNoFollow_DirectoryMarkerMatrixPreservesSourceTree(
		SmartArtifactEvidenceEntryState entryState)
	{
		using var project = new TemporaryDirectory();
		using var external = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("build/source.ts", "export const source = true;\n");
		var markerPath = Path.Combine(project.Path, "build", "static");
		var targetPath = Path.Combine(external.Path, "static");
		CreateDirectoryEvidenceOrSkip(markerPath, targetPath, entryState);
		var rules = CreateFrontendRules(project.Path);

		Assert.Equal(
			entryState == SmartArtifactEvidenceEntryState.Regular,
			rules.IsSmartIgnoredDirectory(Path.Combine(project.Path, "build"), "build"));
		Assert.Equal(
			entryState != SmartArtifactEvidenceEntryState.Regular,
			ContainsPath(BuildTree(project.Path, rules).Root, "build/source.ts"));
	}

	[Fact]
	public void SmartArtifactEvidenceNoFollow_WindowsJunctionMarkerPreservesSourceTree()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Windows junction evidence can only be exercised on Windows.");

		using var project = new TemporaryDirectory();
		using var external = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("build/source.ts", "export const source = true;\n");
		var junctionPath = Path.Combine(project.Path, "build", "static");
		var targetPath = external.CreateDirectory("static");
		try
		{
			CreateWindowsJunctionOrSkip(junctionPath, targetPath);
			var rules = CreateFrontendRules(project.Path);

			Assert.False(rules.IsSmartIgnoredDirectory(Path.Combine(project.Path, "build"), "build"));
			Assert.True(ContainsPath(BuildTree(project.Path, rules).Root, "build/source.ts"));
		}
		finally
		{
			DeleteDirectoryLink(junctionPath);
		}
	}

	public static TheoryData<SmartArtifactEvidenceEntryState> SymbolicLinkStates() => new()
	{
		SmartArtifactEvidenceEntryState.Regular,
		SmartArtifactEvidenceEntryState.SymbolicLink,
		SmartArtifactEvidenceEntryState.DanglingSymbolicLink
	};

	private static IgnoreRules CreateFrontendRules(string rootPath) =>
		new IgnoreRulesService(new SmartIgnoreService([new FrontendArtifactsIgnoreRule()])).Build(
			rootPath,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: []);

	private static TreeBuildResult BuildTree(string rootPath, IgnoreRules rules) =>
		new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".ts" },
				AllowedRootFolders: new HashSet<string>(PathComparer.Default) { "build" },
				IgnoreRules: rules),
			TestContext.Current.CancellationToken);

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	private static void CreateFileEvidenceOrSkip(
		string markerPath,
		string targetPath,
		SmartArtifactEvidenceEntryState entryState)
	{
		if (entryState == SmartArtifactEvidenceEntryState.Regular)
		{
			File.WriteAllText(markerPath, "{}\n");
			return;
		}

		if (entryState == SmartArtifactEvidenceEntryState.SymbolicLink)
			File.WriteAllText(targetPath, "{}\n");

		try
		{
			File.CreateSymbolicLink(markerPath, targetPath);
			if (string.IsNullOrEmpty(new FileInfo(markerPath).LinkTarget))
				Assert.Skip("The filesystem did not preserve the file symbolic link.");
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
		{
			Assert.Skip($"File symbolic links are unavailable: {exception.GetType().Name}.");
		}
	}

	private static void CreateDirectoryEvidenceOrSkip(
		string markerPath,
		string targetPath,
		SmartArtifactEvidenceEntryState entryState)
	{
		if (entryState == SmartArtifactEvidenceEntryState.Regular)
		{
			Directory.CreateDirectory(markerPath);
			return;
		}

		if (entryState == SmartArtifactEvidenceEntryState.SymbolicLink)
			Directory.CreateDirectory(targetPath);

		try
		{
			Directory.CreateSymbolicLink(markerPath, targetPath);
			if (string.IsNullOrEmpty(new DirectoryInfo(markerPath).LinkTarget))
				Assert.Skip("The filesystem did not preserve the directory symbolic link.");
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
		{
			Assert.Skip($"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}
	}

	private static void CreateWindowsJunctionOrSkip(string junctionPath, string targetPath)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add("/c");
		process.StartInfo.ArgumentList.Add("mklink");
		process.StartInfo.ArgumentList.Add("/J");
		process.StartInfo.ArgumentList.Add(junctionPath);
		process.StartInfo.ArgumentList.Add(targetPath);

		try
		{
			process.Start();
			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Skip("Windows junction creation timed out.");
			}

			if (process.ExitCode != 0 ||
			    !Directory.Exists(junctionPath) ||
			    !File.GetAttributes(junctionPath).HasFlag(FileAttributes.ReparsePoint))
			{
				Assert.Skip("The test environment did not allow creating a Windows junction.");
			}
		}
		catch (Exception exception) when (exception is
		       InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Windows junction creation is unavailable: {exception.GetType().Name}.");
		}
	}

	private static void DeleteDirectoryLink(string path)
	{
		try
		{
			if (Directory.Exists(path) || !string.IsNullOrEmpty(new DirectoryInfo(path).LinkTarget))
				Directory.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The enclosing temporary workspace performs a final best-effort cleanup.
		}
	}

	public enum SmartArtifactEvidenceEntryState
	{
		Regular,
		SymbolicLink,
		DanglingSymbolicLink
	}
}
