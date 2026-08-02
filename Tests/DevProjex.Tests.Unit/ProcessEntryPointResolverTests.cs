namespace DevProjex.Tests.Unit;

public sealed class ProcessEntryPointResolverTests
{
	[Theory]
	[InlineData("dotnet", true)]
	[InlineData("dotnet.exe", true)]
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
	[InlineData("/usr/local/share/dotnet/dotnet", true)]
	[InlineData(@"/usr/local/share/dotnet\DOTNET.EXE", true)]
	[InlineData("DevProjex.exe", false)]
	[InlineData(@"C:\tools\dotnet.exe.backup", false)]
	[InlineData("/usr/local/share/dotnet/", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void IsDotnetHost_UsesExecutableFileName(string? path, bool expected)
	{
		Assert.Equal(expected, ProcessEntryPointResolver.IsDotnetHost(path));
	}

	[Fact]
	public void CurrentTestProcess_ResolvesExistingManagedAssemblyAndLaunchArtifact()
	{
		var assemblyPath = ProcessEntryPointResolver.ResolveManagedAssemblyPath();
		var artifactPath = ProcessEntryPointResolver.ResolveCurrentArtifactPath();

		Assert.NotNull(assemblyPath);
		Assert.True(File.Exists(assemblyPath), assemblyPath);
		Assert.NotNull(artifactPath);
		Assert.True(File.Exists(artifactPath), artifactPath);
		Assert.False(ProcessEntryPointResolver.IsSingleFile());
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public void InternalDesktopLaunchNeverAttachesToParentConsole(
		bool hasPendingDesktopRequest,
		bool expected)
	{
		Assert.Equal(
			expected,
			Program.ShouldAttachConsoleForInvocation(
				hasPendingDesktopRequest));
	}
}
