namespace DevProjex.Infrastructure.Git;

internal static class GitProcessStartInfoFactory
{
	private static readonly string[] CommonEnvironmentVariables =
	[
		"PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "SystemRoot", "LANG", "LC_ALL"
	];

	public static ProcessStartInfo Create(
		string? workingDirectory,
		GitProcessOperation operation,
		bool redirectStandardInput = true,
		GitAskPassSession? askPass = null)
	{
		ArgumentNullException.ThrowIfNull(operation);
		var isolation = GitRuntime.IsolationPaths;
		var executable = GitRuntime.GitExecutable;
		if (!GitExecutableLocator.IsSafeForRepository(executable, workingDirectory))
			throw new InvalidOperationException("The pinned Git executable is inside the selected project or one of its parent directories.");
		if (operation.Profile == GitProcessProfile.ManagedCheckout)
			ValidateManagedScope(workingDirectory, operation);

		var startInfo = CreateBase(executable, workingDirectory, redirectStandardInput);
		ApplyEnvironmentAllowlist(startInfo, operation.Profile);
		AddProfileArguments(startInfo, operation, isolation);
		foreach (var argument in operation.BuildArguments(isolation))
			startInfo.ArgumentList.Add(argument);
		ApplyProfileEnvironment(startInfo, operation, isolation);
		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);
		ApplyTrustedGitEnvironment(startInfo, operation, isolation);
		askPass?.Apply(startInfo);
		return startInfo;
	}

	internal static ProcessStartInfo CreateForTesting(
		string? workingDirectory,
		GitProcessOperation operation,
		string executable)
	{
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);
		var isolation = GitRuntime.IsolationPaths;
		var startInfo = CreateBase(Path.GetFullPath(executable), workingDirectory, redirectStandardInput: true);
		ApplyEnvironmentAllowlist(startInfo, operation.Profile);
		AddProfileArguments(startInfo, operation, isolation);
		foreach (var argument in operation.BuildArguments(isolation))
			startInfo.ArgumentList.Add(argument);
		GitProcessEnvironmentSanitizer.RemoveRepositoryOverrides(startInfo);
		ApplyTrustedGitEnvironment(startInfo, operation, isolation);
		return startInfo;
	}

	internal static ProcessStartInfo CreateVersionProbe()
	{
		var startInfo = CreateBase(GitRuntime.GitExecutable, workingDirectory: null, redirectStandardInput: true);
		ApplyEnvironmentAllowlist(startInfo, GitProcessProfile.LocalRead);
		startInfo.ArgumentList.Add("--version");
		startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		startInfo.Environment["GIT_CONFIG_GLOBAL"] = GitRuntime.IsolationPaths.EmptyGlobalConfigFile;
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		return startInfo;
	}

	private static ProcessStartInfo CreateBase(
		string executable,
		string? workingDirectory,
		bool redirectStandardInput)
	{
		if (!Path.IsPathFullyQualified(executable))
			throw new ArgumentException("The Git executable path must be absolute.", nameof(executable));
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = redirectStandardInput,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		if (!string.IsNullOrEmpty(workingDirectory))
			startInfo.WorkingDirectory = Path.GetFullPath(workingDirectory);
		return startInfo;
	}

	private static void AddProfileArguments(
		ProcessStartInfo startInfo,
		GitProcessOperation operation,
		GitIsolationPaths isolation)
	{
		startInfo.ArgumentList.Add("--no-pager");
		startInfo.ArgumentList.Add("--no-optional-locks");
		AddConfig(startInfo, "core.fsmonitor=false");
		AddConfig(startInfo, "core.quotepath=false");
		AddConfig(startInfo, $"core.hooksPath={isolation.EmptyHooksDirectory}");
		AddConfig(startInfo, "credential.helper=");
		AddConfig(startInfo, "core.askPass=");
		AddConfig(startInfo, "log.showSignature=false");
		AddConfig(startInfo, "submodule.recurse=false");
		AddConfig(startInfo, $"core.attributesFile={isolation.EmptyAttributesFile}");
		AddConfig(startInfo, $"core.excludesFile={isolation.EmptyExcludesFile}");
		if (OperatingSystem.IsWindows())
			AddConfig(startInfo, "core.longpaths=true");

		switch (operation.Profile)
		{
			case GitProcessProfile.LocalRead:
				AddConfig(startInfo, "protocol.allow=never");
				break;
			case GitProcessProfile.ManagedCheckout:
				AddConfig(startInfo, "protocol.allow=never");
				foreach (var driver in operation.FilterDrivers)
				{
					AddConfig(startInfo, $"filter.{driver}.clean=");
					AddConfig(startInfo, $"filter.{driver}.smudge=");
					AddConfig(startInfo, $"filter.{driver}.process=");
					AddConfig(startInfo, $"filter.{driver}.required=false");
				}
				break;
			case GitProcessProfile.ExplicitNetwork:
				AddConfig(startInfo, $"protocol.allow={GitNetworkPolicy.GetAllowedProtocols(operation.Value!)}");
				AddConfig(startInfo, "http.extraHeader=");
				AddConfig(startInfo, "http.cookieFile=");
				AddConfig(startInfo, "http.proxy=");
				AddConfig(startInfo, "remote.origin.uploadpack=");
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}

	private static void AddConfig(ProcessStartInfo startInfo, string value)
	{
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(value);
	}

	private static void ApplyEnvironmentAllowlist(
		ProcessStartInfo startInfo,
		GitProcessProfile profile)
	{
		var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		foreach (var name in CommonEnvironmentVariables)
			inherited[name] = Environment.GetEnvironmentVariable(name);
		if (profile == GitProcessProfile.ExplicitNetwork)
			inherited["SSH_AUTH_SOCK"] = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");

		startInfo.Environment.Clear();
		foreach (var (name, value) in inherited)
		{
			if (!string.IsNullOrEmpty(value))
				startInfo.Environment[name] = value;
		}
	}

	private static void ApplyProfileEnvironment(
		ProcessStartInfo startInfo,
		GitProcessOperation operation,
		GitIsolationPaths isolation)
	{
		startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		startInfo.Environment["GIT_CONFIG_GLOBAL"] = isolation.EmptyGlobalConfigFile;
		startInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
		startInfo.Environment["GIT_PROTOCOL_FROM_USER"] = "0";
		startInfo.Environment["GIT_ALLOW_PROTOCOL"] = operation.Profile == GitProcessProfile.ExplicitNetwork
			? GitNetworkPolicy.GetAllowedProtocols(operation.Value!)
			: string.Empty;
	}

	private static void ApplyTrustedGitEnvironment(
		ProcessStartInfo startInfo,
		GitProcessOperation? operation,
		GitIsolationPaths isolation)
	{
		startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
		startInfo.Environment["GIT_CONFIG_GLOBAL"] = isolation.EmptyGlobalConfigFile;
		startInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
		startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
		startInfo.Environment["GIT_PROTOCOL_FROM_USER"] = "0";
		startInfo.Environment["GIT_ALLOW_PROTOCOL"] = operation?.Profile == GitProcessProfile.ExplicitNetwork
			? GitNetworkPolicy.GetAllowedProtocols(operation.Value!)
			: string.Empty;
		startInfo.Environment["GIT_ASKPASS"] = string.Empty;
		startInfo.Environment["SSH_ASKPASS"] = string.Empty;
		startInfo.Environment["SSH_ASKPASS_REQUIRE"] = "never";
		startInfo.Environment["GCM_INTERACTIVE"] = "Never";
		startInfo.Environment["GCM_GUI_PROMPT"] = "false";
		if (operation?.Profile != GitProcessProfile.ExplicitNetwork)
			return;
		if (GitNetworkPolicy.GetAllowedProtocols(operation.Value!) == "ssh")
		{
			var ssh = GitRuntime.SshExecutable ??
			          throw new InvalidOperationException("A safe SSH executable is unavailable.");
			startInfo.Environment["GIT_SSH_COMMAND"] = $"\"{ssh.Replace("\"", "\\\"")}\" -o BatchMode=yes";
			startInfo.Environment["GIT_SSH_VARIANT"] = "ssh";
		}
	}

	private static void ValidateManagedScope(string? workingDirectory, GitProcessOperation operation)
	{
		if (string.IsNullOrWhiteSpace(workingDirectory) || !RepositoryCacheLayout.IsManaged(workingDirectory))
			throw new InvalidOperationException("Managed Git writes require an application-owned cache repository.");
		var container = RepositoryCacheLayout.GetContainer(workingDirectory);
		if (!RepositoryFileLease.HasActiveLeaseWithin(container))
			throw new InvalidOperationException("Managed Git writes require an active repository cache lease.");
		if (operation.Kind is GitOperationKind.ManagedWorktreeAdd or GitOperationKind.ManagedWorktreeRemove &&
		    !PathUtility.IsPathInside(operation.Value!, container))
		{
			throw new InvalidOperationException("The managed worktree path is outside the application cache.");
		}
	}
}
