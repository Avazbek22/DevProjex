using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.FileSystem;

public sealed class ProjectPathStartInfoFactoryTests
{
	public static TheoryData<DesktopPlatform, bool, string> PlatformPaths => new()
	{
		{ DesktopPlatform.Windows, false, "C:\\Project files\\данные \\\"quoted\\\"\\file.txt" },
		{ DesktopPlatform.Windows, true, "C:\\Project files\\данные" },
		{ DesktopPlatform.MacOS, false, "/Users/demo/Project files/данные \"quoted\"/file.txt" },
		{ DesktopPlatform.MacOS, true, "/Users/demo/Project files/данные" },
		{ DesktopPlatform.Linux, false, "/home/demo/Project files/данные \"quoted\"/file.txt" },
		{ DesktopPlatform.Linux, true, "/home/demo/Project files/данные" }
	};

	[Theory]
	[MemberData(nameof(PlatformPaths))]
	public void CreateCandidates_PreservesEachPathAsAnArgument(
		DesktopPlatform platform,
		bool isDirectory,
		string path)
	{
		var candidates = ProjectPathStartInfoFactory.CreateCandidates(platform, path, isDirectory);

		Assert.NotEmpty(candidates);
		var arguments = candidates
			.SelectMany(static candidate => candidate.StartInfo.ArgumentList)
			.ToArray();
		if (platform == DesktopPlatform.Linux && !isDirectory)
		{
			Assert.Contains(arguments, argument => argument.StartsWith("array:string:file://", StringComparison.Ordinal));
			Assert.Equal(
				path[..path.LastIndexOf('/')],
				candidates[1].StartInfo.ArgumentList[0]);
		}
		else if (platform == DesktopPlatform.Windows && !isDirectory)
		{
			Assert.Equal($"/select,{path}", Assert.Single(arguments));
		}
		else
		{
			Assert.Equal(path, arguments[^1]);
		}
	}

	[Fact]
	public void CreateCandidates_WindowsFile_UsesExplorerSelectWithoutManualQuoting()
	{
		const string path = @"C:\Project files\source.cs";

		var candidate = Assert.Single(ProjectPathStartInfoFactory.CreateCandidates(
			DesktopPlatform.Windows,
			path,
			isDirectory: false));

		Assert.Equal("explorer.exe", candidate.StartInfo.FileName);
		Assert.True(candidate.StartInfo.UseShellExecute);
		Assert.Equal($"/select,{path}", Assert.Single(candidate.StartInfo.ArgumentList));
		Assert.Empty(candidate.StartInfo.Arguments);
	}

	[Fact]
	public void CreateCandidates_MacFile_UsesOpenReveal()
	{
		const string path = "/Users/demo/source.cs";

		var candidate = Assert.Single(ProjectPathStartInfoFactory.CreateCandidates(
			DesktopPlatform.MacOS,
			path,
			isDirectory: false));

		Assert.Equal("open", candidate.StartInfo.FileName);
		Assert.Equal(["-R", path], candidate.StartInfo.ArgumentList);
	}

	[Fact]
	public void CreateCandidates_LinuxFile_UsesDbusThenParentFallback()
	{
		const string path = "/home/demo/project/source file.cs";

		var candidates = ProjectPathStartInfoFactory.CreateCandidates(
			DesktopPlatform.Linux,
			path,
			isDirectory: false);

		Assert.Equal(2, candidates.Count);
		Assert.Equal("dbus-send", candidates[0].StartInfo.FileName);
		Assert.True(candidates[0].RequiresSuccessfulExit);
		Assert.Contains(
			"array:string:file:///home/demo/project/source%20file.cs",
			candidates[0].StartInfo.ArgumentList);
		Assert.Equal("xdg-open", candidates[1].StartInfo.FileName);
		Assert.Equal("/home/demo/project", Assert.Single(candidates[1].StartInfo.ArgumentList));
	}

	[Fact]
	public void CreateCandidates_LongPath_RemainsOneArgument()
	{
		var path = "/home/demo/" + new string('x', 40_000) + "/file.txt";

		var candidate = Assert.Single(ProjectPathStartInfoFactory.CreateCandidates(
			DesktopPlatform.MacOS,
			path,
			isDirectory: true));

		Assert.Equal(path, Assert.Single(candidate.StartInfo.ArgumentList));
	}

	[Fact]
	public async Task LaunchAsync_MissingPath_DoesNotCreateOrLaunchAProcess()
	{
		var attempts = 0;
		var launcher = new ProjectPathLauncher(
			DesktopPlatform.Windows,
			_ => false,
			_ => false,
			(_, _) =>
			{
				attempts++;
				return Task.FromResult(true);
			});

		var result = await launcher.LaunchAsync(
			@"C:\missing\file.txt",
			isDirectory: false,
			TestContext.Current.CancellationToken);

		Assert.False(result.Succeeded);
		Assert.Equal(ProjectPathLaunchFailure.PathNotFound, result.Failure);
		Assert.Equal(0, attempts);
	}

	[Fact]
	public async Task LaunchAsync_LinuxDbusFailure_FallsBackToXdgOpen()
	{
		var commands = new List<string>();
		var launcher = new ProjectPathLauncher(
			DesktopPlatform.Linux,
			_ => true,
			_ => true,
			(candidate, _) =>
			{
				commands.Add(candidate.StartInfo.FileName);
				return Task.FromResult(candidate.StartInfo.FileName == "xdg-open");
			});

		var result = await launcher.LaunchAsync(
			"/home/demo/project/source.cs",
			isDirectory: false,
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Equal(["dbus-send", "xdg-open"], commands);
	}
}
