using System.Diagnostics;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpProfileStagingTests
{
	[Fact]
	public void ProfileStagingUsesOwnerOnlyDirectoryPermissionsOnUnix()
	{
		if (OperatingSystem.IsWindows())
			return;
		using var temporary = new TemporaryDirectory();

		var staging = McpServices.CreatePrivateProfileStagingDirectory(temporary.Path);

		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
			File.GetUnixFileMode(staging));
	}

	[Fact]
	public void ProfileStagingUsesOwnerOnlyFilePermissionsOnUnix()
	{
		if (OperatingSystem.IsWindows())
			return;
		using var temporary = new TemporaryDirectory();
		var staging = McpServices.CreatePrivateProfileStagingDirectory(temporary.Path);
		var path = Path.Combine(staging, "profile.json");

		using var stream = McpServices.CreatePrivateProfileStagingFile(path);

		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(path));
	}

	[Fact]
	public void ProfileStagingRejectsAnExistingDirectoryAlias()
	{
		using var temporary = new TemporaryDirectory();
		var outside = temporary.CreateFolder("outside");
		var staging = Path.Combine(temporary.Path, "mcp-profile-staging");
		if (!TryCreateDirectoryAlias(staging, outside))
		{
			Assert.Skip("Directory aliases are unavailable in this environment.");
			return;
		}

		var error = Assert.Throws<PortableProjectProfileException>(
			() => McpServices.CreatePrivateProfileStagingDirectory(temporary.Path));

		Assert.Equal("DPX-CLI-PROFILE-INVALID", error.Code);
		Assert.Equal("The portable profile staging directory is unsafe.", error.Message);
	}

	[Fact]
	public void ProfileStagingRejectsAnAliasedUserDataRoot()
	{
		using var temporary = new TemporaryDirectory();
		var outside = temporary.CreateFolder("outside");
		var dataRoot = Path.Combine(temporary.Path, "linked-data");
		if (!TryCreateDirectoryAlias(dataRoot, outside))
		{
			Assert.Skip("Directory aliases are unavailable in this environment.");
			return;
		}

		var error = Assert.Throws<PortableProjectProfileException>(
			() => McpServices.CreatePrivateProfileStagingDirectory(dataRoot));

		Assert.Equal("DPX-CLI-PROFILE-INVALID", error.Code);
		Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
	}

	private static bool TryCreateDirectoryAlias(string link, string target)
	{
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				Directory.CreateSymbolicLink(link, target);
				return true;
			}
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
				Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			if (process is null)
				return false;
			process.WaitForExit();
			return process.ExitCode == 0;
		}
		catch (Exception exception) when (exception is
				   UnauthorizedAccessException or
				   IOException or
				   PlatformNotSupportedException)
		{
			return false;
		}
	}
}
