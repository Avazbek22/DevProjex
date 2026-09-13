namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ProcessEntryPointResolverTests
{
	[Fact]
	public void ResolveSelfLaunchPath_WithoutAppImage_UsesCurrentProcessPath()
	{
		var previous = Environment.GetEnvironmentVariable("APPIMAGE");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE", null);

			Assert.Equal(
				Environment.ProcessPath,
				ProcessEntryPointResolver.ResolveSelfLaunchPath());
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE", previous);
		}
	}

	[Fact]
	public void ResolveSelfLaunchPath_WithExistingAppImage_UsesAppImagePath()
	{
		using var temp = new TemporaryDirectory();
		var appImagePath = temp.CreateFile(
			"DevProjex-5.2-x86_64.AppImage",
			"appimage fixture");
		var previous = Environment.GetEnvironmentVariable("APPIMAGE");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE", appImagePath);

			Assert.Equal(
				appImagePath,
				ProcessEntryPointResolver.ResolveSelfLaunchPath());
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE", previous);
		}
	}

	[Fact]
	public void ResolveSelfLaunchPath_WithMissingAppImage_UsesCurrentProcessPath()
	{
		using var temp = new TemporaryDirectory();
		var missingAppImagePath = Path.Combine(
			temp.Path,
			"missing.AppImage");
		var previous = Environment.GetEnvironmentVariable("APPIMAGE");
		try
		{
			Environment.SetEnvironmentVariable("APPIMAGE", missingAppImagePath);

			Assert.Equal(
				Environment.ProcessPath,
				ProcessEntryPointResolver.ResolveSelfLaunchPath());
		}
		finally
		{
			Environment.SetEnvironmentVariable("APPIMAGE", previous);
		}
	}

	[Theory]
	[InlineData("dotnet", true)]
	[InlineData("dotnet.exe", true)]
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe", true)]
	[InlineData("/usr/local/share/dotnet/dotnet", true)]
	[InlineData(@"/usr/local/share/dotnet\DOTNET.EXE", true)]
	[InlineData("DevProjex.exe", false)]
	[InlineData(@"C:\tools\dotnet.exe.backup", false)]
	[InlineData("/usr/local/share/dotnet/", false)]
	[InlineData("/usr/local/share/dotnet/dotnet ", false)]
	[InlineData(" dotnet", false)]
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
