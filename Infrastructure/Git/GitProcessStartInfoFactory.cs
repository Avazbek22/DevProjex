using System.Runtime.InteropServices;

namespace DevProjex.Infrastructure.Git;

internal static class GitProcessStartInfoFactory
{
	private static readonly string GitExecutable =
		RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git";

	public static ProcessStartInfo Create(
		string? workingDirectory,
		IReadOnlyList<string> arguments,
		bool redirectStandardInput = true,
		string? executable = null,
		GitAskPassSession? askPass = null)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		var startInfo = new ProcessStartInfo
		{
			FileName = executable ?? GitExecutable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = redirectStandardInput,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		if (!string.IsNullOrEmpty(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add("core.longpaths=true");
		}
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		startInfo.Environment["GIT_SSH_COMMAND"] = GitRepositoryService.NonInteractiveSshCommand;
		startInfo.Environment["GIT_SSH_VARIANT"] = "ssh";
		startInfo.Environment["GIT_ASKPASS"] = string.Empty;
		startInfo.Environment["SSH_ASKPASS"] = string.Empty;
		startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "never";
		startInfo.Environment["GCM_INTERACTIVE"] = "Never";
		startInfo.Environment["GCM_GUI_PROMPT"] = "false";
		askPass?.Apply(startInfo);
		return startInfo;
	}
}
