namespace DevProjex.Infrastructure.Git;

internal sealed class GitAskPassSession : IDisposable
{
	internal const string PasswordEnvironmentVariable = "DEVPROJEX_GIT_ASKPASS_PASSWORD";
	private const string UserNameConfigKey = "credential.username";
	private readonly string _directoryPath;

	private GitAskPassSession(string directoryPath, string helperPath, GitCloneAuthentication authentication)
	{
		_directoryPath = directoryPath;
		HelperPath = helperPath;
		Authentication = authentication;
	}

	public string HelperPath { get; }
	public GitCloneAuthentication Authentication { get; }

	public static GitAskPassSession Create(GitCloneAuthentication authentication)
	{
		ArgumentNullException.ThrowIfNull(authentication);
		var directoryPath = Path.Combine(
			Path.GetTempPath(),
			"DevProjex",
			"git-askpass",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directoryPath);
		var helperPath = Path.Combine(
			directoryPath,
			OperatingSystem.IsWindows() ? "askpass.cmd" : "askpass.sh");
		var contents = OperatingSystem.IsWindows()
			? "@echo off\r\npowershell.exe -NoLogo -NoProfile -NonInteractive -Command \"[Console]::Out.WriteLine($env:" +
			  PasswordEnvironmentVariable + ")\"\r\n"
			: "#!/bin/sh\nprintf '%s\\n' \"$" + PasswordEnvironmentVariable + "\"\n";
		File.WriteAllText(helperPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				helperPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		return new GitAskPassSession(directoryPath, helperPath, authentication);
	}

	public void Apply(ProcessStartInfo startInfo)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		startInfo.Environment["GIT_ASKPASS"] = HelperPath;
		startInfo.Environment["SSH_ASKPASS"] = HelperPath;
		startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "force";
		startInfo.Environment[PasswordEnvironmentVariable] = Authentication.Password;
		startInfo.Environment["GIT_CONFIG_COUNT"] = "2";
		startInfo.Environment["GIT_CONFIG_KEY_0"] = UserNameConfigKey;
		startInfo.Environment["GIT_CONFIG_VALUE_0"] = Authentication.UserName;
		startInfo.Environment["GIT_CONFIG_KEY_1"] = "credential.helper";
		startInfo.Environment["GIT_CONFIG_VALUE_1"] = string.Empty;
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directoryPath, recursive: true);
		}
		catch
		{
			// The helper contains no credentials; stale files are safe and can be removed later.
		}
	}
}
