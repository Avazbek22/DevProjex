using DevProjex.Mcp;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Plan containment confirms every included file in one pass. The pass may walk a directory shared
/// by many files once, but each file must still be accepted or rejected exactly as resolving it on
/// its own would.
/// </summary>
public sealed class McpPlanContainmentTests
{
	[Fact]
	public void ANewPassDoesNotTrustADirectoryLinkRepointedAfterAnAcceptedPass()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var inside = CreateFolder(basePath, "project/real");
		var outside = CreateFolder(basePath, "project-sibling");
		File.WriteAllText(Path.Combine(inside, "same.ts"), "export const inside = 1;");
		File.WriteAllText(Path.Combine(outside, "same.ts"), "export const outside = 2;");
		var alias = Path.Combine(project, "alias");
		CreateDirectoryLinkOrSkip(alias, inside);
		var candidate = Path.Combine(alias, "same.ts");
		var registry = new McpRootRegistry([project]);
		McpProjectService.ValidatePlanContainment(registry, project, [candidate], TestContext.Current.CancellationToken);
		Assert.True(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
		Directory.Delete(alias);
		CreateDirectoryLinkOrSkip(alias, outside);

		var error = Assert.Throws<McpToolException>(() => McpProjectService.ValidatePlanContainment(
			registry, project, [candidate], TestContext.Current.CancellationToken));
		Assert.Equal(McpErrorCodes.RootViolation, error.Code);
		Assert.True(File.Exists(Path.Combine(inside, "same.ts")));
		Assert.True(File.Exists(Path.Combine(outside, "same.ts")));
	}

	[Theory]
	[InlineData("project-other")]
	[InlineData("project🙂")]
	[InlineData("project.else")]
	public void ASharedLexicalPrefixDoesNotMakeASiblingPartOfTheRoot(string sibling)
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var accepted = CreateFile(basePath, "project/src/file.ts", "inside");
		var outside = CreateFile(basePath, sibling + "/src/file.ts", "outside");
		var registry = new McpRootRegistry([project]);
		var error = Assert.Throws<McpToolException>(() => McpProjectService.ValidatePlanContainment(
			registry, project, [accepted, accepted, outside], TestContext.Current.CancellationToken));
		Assert.Equal(McpErrorCodes.RootViolation, error.Code);
	}

	[Fact]
	public void AcceptsEveryFileAndWalksSharedDirectoriesOnce()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var files = Enumerable.Range(0, 12)
			.Select(index => CreateFile(basePath,
				Path.Combine("project", "src", "deep", $"nested{index % 3}", $"file{index}.cs"),
				"class Sample { }"))
			.ToArray();
		var registry = new McpRootRegistry([project]);

		long separately;
		using (var probes = McpProjectPathProbe.Count())
		{
			foreach (var file in files)
				_ = registry.ResolveExistingPath(project, file);
			separately = probes.Value;
		}

		using var batch = McpProjectPathProbe.Count();
		McpProjectService.ValidatePlanContainment(
			registry,
			project,
			files,
			TestContext.Current.CancellationToken);

		Assert.True(batch.Value > files.Length, $"Each file has to be opened; recorded {batch.Value}.");
		Assert.True(
			batch.Value < separately,
			$"One pass recorded {batch.Value} probes, resolving each file alone recorded {separately}.");
	}

	[Fact]
	public void RejectsAFileOutsideTheRootAfterAcceptedSiblings()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var inside = CreateFile(basePath, Path.Combine("project", "src", "inside.cs"), "class Inside { }");
		var outside = CreateFile(basePath, Path.Combine("outside", "src", "outside.cs"), "class Outside { }");
		var registry = new McpRootRegistry([project]);

		var expected = Assert.Throws<McpToolException>(() => registry.ResolveExistingPath(project, outside));
		var actual = Assert.Throws<McpToolException>(() =>
			McpProjectService.ValidatePlanContainment(
				registry,
				project,
				[inside, outside],
				TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.RootViolation, actual.Code);
		Assert.Equal(expected.Message, actual.Message);
	}

	[Fact]
	public void ReportsAMissingFileWithTheSameErrorAsResolvingItAlone()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var present = CreateFile(basePath, Path.Combine("project", "src", "present.cs"), "class Present { }");
		var missing = Path.Combine(project, "src", "missing.cs");
		var registry = new McpRootRegistry([project]);

		var expected = Assert.Throws<McpToolException>(() => registry.ResolveExistingPath(project, missing));
		var actual = Assert.Throws<McpToolException>(() =>
			McpProjectService.ValidatePlanContainment(
				registry,
				project,
				[present, missing],
				TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.PathNotFound, actual.Code);
		Assert.Equal(expected.Message, actual.Message);
	}

	[Fact]
	public void RejectsALinkedDirectoryThatLeavesTheRootEvenAfterItsParentWasAccepted()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var outside = CreateFolder(basePath, "outside");
		File.WriteAllText(Path.Combine(outside, "escaped.cs"), "class Escaped { }");
		var accepted = CreateFile(basePath, Path.Combine("project", "src", "accepted.cs"), "class Accepted { }");
		var link = Path.Combine(project, "src", "linked");
		CreateDirectoryLinkOrSkip(link, outside);
		var escaped = Path.Combine(link, "escaped.cs");
		var registry = new McpRootRegistry([project]);

		var expected = Assert.Throws<McpToolException>(() => registry.ResolveExistingPath(project, escaped));
		var actual = Assert.Throws<McpToolException>(() =>
			McpProjectService.ValidatePlanContainment(
				registry,
				project,
				[accepted, escaped],
				TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.RootViolation, actual.Code);
		Assert.Equal(expected.Message, actual.Message);
	}

	[Fact]
	public void ResolvesThroughAnInternalLinkedDirectoryLikeASingleResolution()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var target = CreateFolder(basePath, Path.Combine("project", "real"));
		var first = CreateFile(basePath, Path.Combine("project", "real", "first.cs"), "class First { }");
		CreateFile(basePath, Path.Combine("project", "real", "second.cs"), "class Second { }");
		var link = Path.Combine(project, "alias");
		CreateDirectoryLinkOrSkip(link, target);
		var registry = new McpRootRegistry([project]);
		string[] files = [first, Path.Combine(link, "first.cs"), Path.Combine(link, "second.cs")];

		McpProjectService.ValidatePlanContainment(
			registry,
			project,
			files,
			TestContext.Current.CancellationToken);

		Assert.All(files, file => Assert.StartsWith(
			registry.ResolveProject(project),
			registry.ResolveExistingPath(project, file),
			StringComparison.Ordinal));
	}

	[Fact]
	public void StopsBeforeTheFirstFileWhenCancelled()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var file = CreateFile(basePath, Path.Combine("project", "file.cs"), "class File { }");
		var registry = new McpRootRegistry([project]);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		using var probes = McpProjectPathProbe.Count();
		Assert.Throws<OperationCanceledException>(() =>
			McpProjectService.ValidatePlanContainment(registry, project, [file], cancellation.Token));

		Assert.Equal(0, probes.Value);
	}

	[Fact]
	public void AnEmptyPlanNeedsNoResolutionEvenWhenCancelled()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var registry = new McpRootRegistry([project]);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		using var probes = McpProjectPathProbe.Count();
		McpProjectService.ValidatePlanContainment(registry, project, [], cancellation.Token);

		Assert.Equal(0, probes.Value);
	}

	[Fact]
	public void AnInjectedResolverStillResolvesEveryFile()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var project = CreateFolder(basePath, "project");
		var files = Enumerable.Range(0, 5)
			.Select(index => CreateFile(basePath, Path.Combine("project", "src", $"file{index}.cs"), "class F { }"))
			.ToArray();
		var resolvedFiles = new List<string>();
		var registry = new McpRootRegistry(
			[project],
			(path, requireDirectory) =>
			{
				if (!requireDirectory)
					resolvedFiles.Add(path);
				return McpRootRegistry.ResolvePhysicalExistingPath(path, requireDirectory);
			});

		McpProjectService.ValidatePlanContainment(
			registry,
			project,
			files,
			TestContext.Current.CancellationToken);

		Assert.Equal(files.Length, resolvedFiles.Count);
	}

	/// <summary>
	/// A project can sit below a linked directory, as macOS temporary folders do below
	/// <c>/var</c> -> <c>/private/var</c>. Whichever spelling names the root and the files, one pass
	/// has to answer exactly as resolving each file alone through the root jail.
	/// </summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void AProjectBelowALinkedDirectoryIsJudgedLikeEachFileAlone(bool linkedRoot, bool linkedFiles)
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var real = CreateFolder(basePath, "real");
		var project = CreateFolder(basePath, Path.Combine("real", "project"));
		var relativeFiles = new[]
		{
			Path.Combine("src", "first.cs"),
			Path.Combine("src", "deep", "second.cs"),
			Path.Combine("src", "deep", "third.cs")
		};
		foreach (var relative in relativeFiles)
			CreateFile(basePath, Path.Combine("real", "project", relative), "class Sample { }");
		var alias = Path.Combine(basePath, "alias");
		CreateDirectoryLinkOrSkip(alias, real);
		var linkedProject = Path.Combine(alias, "project");
		var registry = new McpRootRegistry([linkedProject]);
		var root = linkedRoot ? linkedProject : project;
		var files = relativeFiles
			.Select(relative => Path.Combine(linkedFiles ? linkedProject : project, relative))
			.ToArray();
		var jail = new McpProjectRootJail(registry);

		var expected = files.Select(file => Outcome(() => jail.ResolveExistingPath(root, file)))
			.FirstOrDefault(static outcome => outcome is not null);
		var actual = Outcome(() => McpProjectService.ValidatePlanContainment(
			registry,
			root,
			files,
			TestContext.Current.CancellationToken));

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void TheResolvedRootOfAProjectOpenedThroughALinkAcceptsItsFiles()
	{
		using var workspace = new TemporaryDirectory();
		var basePath = PhysicalPath(workspace);
		var real = CreateFolder(basePath, "real");
		CreateFolder(basePath, Path.Combine("real", "project"));
		CreateFile(basePath, Path.Combine("real", "project", "src", "first.cs"), "class First { }");
		CreateFile(basePath, Path.Combine("real", "project", "src", "deep", "second.cs"), "class Second { }");
		var alias = Path.Combine(basePath, "alias");
		CreateDirectoryLinkOrSkip(alias, real);
		var registry = new McpRootRegistry([Path.Combine(alias, "project")]);
		// Plans are built under the root the registry resolves, as list_projects reports it.
		var root = registry.ResolveProject(Path.Combine(alias, "project"));
		string[] files =
		[
			Path.Combine(root, "src", "first.cs"),
			Path.Combine(root, "src", "deep", "second.cs")
		];

		McpProjectService.ValidatePlanContainment(
			registry,
			root,
			files,
			TestContext.Current.CancellationToken);

		Assert.All(files, file => Assert.Equal(file, registry.ResolveExistingPath(root, file)));
	}

	/// <summary>
	/// Plans are built under the physical root the registry resolves. A temporary folder can itself
	/// sit below a link (macOS keeps it below <c>/var</c> -> <c>/private/var</c>), so cases start from
	/// its physical spelling unless they test a linked spelling on purpose.
	/// </summary>
	private static string PhysicalPath(TemporaryDirectory workspace) =>
		McpRootRegistry.ResolvePhysicalExistingPath(workspace.Path, requireDirectory: true);

	private static string CreateFolder(string basePath, string relativePath) =>
		Directory.CreateDirectory(Path.Combine(basePath, relativePath)).FullName;

	private static string CreateFile(string basePath, string relativePath, string content)
	{
		var fullPath = Path.Combine(basePath, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
		File.WriteAllText(fullPath, content);
		return fullPath;
	}

	private static string? Outcome(Action action)
	{
		try
		{
			action();
			return null;
		}
		catch (McpToolException exception)
		{
			return exception.Code + ": " + exception.Message;
		}
	}

	/// <summary>
	/// Windows only allows symbolic links with developer mode, so a junction stands in for a
	/// directory link there; both are reparse points the resolution has to follow.
	/// </summary>
	private static void CreateDirectoryLinkOrSkip(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			if (!OperatingSystem.IsWindows())
				Assert.Skip("Creating directory symbolic links is unavailable in this environment.");
		}

		using var process = new System.Diagnostics.Process
		{
			StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath })
			process.StartInfo.ArgumentList.Add(argument);
		try
		{
			process.Start();
			if (!process.WaitForExit(5_000))
			{
				process.Kill(entireProcessTree: true);
				Assert.Skip("Windows junction creation timed out.");
			}
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Windows junction creation is unavailable: {exception.GetType().Name}.");
		}
		if (process.ExitCode != 0 ||
			!Directory.Exists(linkPath) ||
			!File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
		{
			Assert.Skip("The test environment did not allow creating a Windows junction.");
		}
	}
}
