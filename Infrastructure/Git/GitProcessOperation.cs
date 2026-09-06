using System.Globalization;
using DevProjex.Application.Context;

namespace DevProjex.Infrastructure.Git;

internal enum GitProcessProfile
{
	LocalRead,
	ManagedCheckout,
	ExplicitNetwork
}

internal enum GitOperationKind
{
	ReadTrackedIndex,
	ReadStagedChanges,
	ReadWorkingChanges,
	ReadUntracked,
	ResolveCommit,
	ReadRefDiff,
	ReadConfigValue,
	ReadRemoteUrl,
	ListBranches,
	CloneRepository,
	FetchBranch,
	FetchDeepen,
	ManagedCheckout,
	ManagedWorktreeAdd,
	ManagedWorktreeRemove,
	ManagedWorktreePrune,
	ManagedWorktreeList,
	ManagedConfigWrite
}

internal enum GitConfigReadKind
{
	PathComparisonSemantics,
	UnsafeDrivers,
	WorktreeBranch,
	PromisorRemotes,
	NetworkOverrides
}

internal enum GitBranchListKind
{
	Local,
	CachedRemote,
	Current,
	RemoteHead,
	RemoteHeads
}

internal enum GitManagedCheckoutKind
{
	Detach,
	SwitchBranch,
	ResetBranch,
	HardReset
}

internal enum GitManagedConfigWriteKind
{
	EnableWorktreeConfig,
	ClearWorktreeBranch,
	SetWorktreeBranch,
	AddTrackedBranch
}

internal sealed record GitProcessOperation
{
	private GitProcessOperation(
		GitOperationKind kind,
		GitProcessProfile profile,
		string? value = null,
		string? secondaryValue = null,
		int depth = 0,
		GitConfigReadKind configReadKind = default,
		GitBranchListKind branchListKind = default,
		GitManagedCheckoutKind checkoutKind = default,
		GitManagedConfigWriteKind configWriteKind = default,
		IReadOnlyList<string>? filterDrivers = null,
		string? allowedProtocols = null)
	{
		Kind = kind;
		Profile = profile;
		Value = value;
		SecondaryValue = secondaryValue;
		Depth = depth;
		ConfigReadKind = configReadKind;
		BranchListKind = branchListKind;
		CheckoutKind = checkoutKind;
		ConfigWriteKind = configWriteKind;
		FilterDrivers = filterDrivers ?? [];
		AllowedProtocols = allowedProtocols;
	}

	public GitOperationKind Kind { get; }
	public GitProcessProfile Profile { get; }
	public string? Value { get; }
	public string? SecondaryValue { get; }
	public int Depth { get; }
	public GitConfigReadKind ConfigReadKind { get; }
	public GitBranchListKind BranchListKind { get; }
	public GitManagedCheckoutKind CheckoutKind { get; }
	public GitManagedConfigWriteKind ConfigWriteKind { get; }
	public IReadOnlyList<string> FilterDrivers { get; }
	public string? AllowedProtocols { get; }

	public TimeSpan Deadline => Profile switch
	{
		GitProcessProfile.LocalRead => TimeSpan.FromSeconds(30),
		GitProcessProfile.ManagedCheckout => TimeSpan.FromMinutes(2),
		GitProcessProfile.ExplicitNetwork => TimeSpan.FromMinutes(10),
		_ => throw new ArgumentOutOfRangeException()
	};

	public static GitProcessOperation ReadTrackedIndex() =>
		new(GitOperationKind.ReadTrackedIndex, GitProcessProfile.LocalRead);

	public static GitProcessOperation ReadStagedChanges() =>
		new(GitOperationKind.ReadStagedChanges, GitProcessProfile.LocalRead);

	public static GitProcessOperation ReadWorkingChanges() =>
		new(GitOperationKind.ReadWorkingChanges, GitProcessProfile.LocalRead);

	public static GitProcessOperation ReadUntracked() =>
		new(GitOperationKind.ReadUntracked, GitProcessProfile.LocalRead);

	public static GitProcessOperation ResolveCommit(string reference) =>
		new(
			GitOperationKind.ResolveCommit,
			GitProcessProfile.LocalRead,
			ValidateRevision(reference));

	public static GitProcessOperation ReadRefDiff(string range)
	{
		if (!GitScopeSelection.IsValidDiffRange(range))
			throw new ArgumentException("The Git diff range is invalid.", nameof(range));
		return new GitProcessOperation(
			GitOperationKind.ReadRefDiff,
			GitProcessProfile.LocalRead,
			range);
	}

	public static GitProcessOperation ReadConfigValue(GitConfigReadKind kind) =>
		new(
			GitOperationKind.ReadConfigValue,
			GitProcessProfile.LocalRead,
			configReadKind: kind);

	public static GitProcessOperation ReadRemoteUrl() =>
		new(GitOperationKind.ReadRemoteUrl, GitProcessProfile.LocalRead);

	public static GitProcessOperation ListBranches(
		GitBranchListKind kind,
		string? remoteUrl = null,
		bool allowFileTransport = false)
	{
		if (kind == GitBranchListKind.RemoteHeads)
			remoteUrl = GitNetworkPolicy.ValidateUrl(remoteUrl, allowFileTransport);
		else if (remoteUrl is not null)
			throw new ArgumentException("Only a remote-head query accepts a URL.", nameof(remoteUrl));

		return new GitProcessOperation(
			GitOperationKind.ListBranches,
			kind == GitBranchListKind.RemoteHeads
				? GitProcessProfile.ExplicitNetwork
				: GitProcessProfile.LocalRead,
			remoteUrl,
			branchListKind: kind,
			allowedProtocols: kind == GitBranchListKind.RemoteHeads
				? GitNetworkPolicy.GetAllowedProtocols(remoteUrl!)
				: null);
	}

	public static GitProcessOperation CloneRepository(
		string url,
		string targetDirectory,
		bool allowFileTransport = false)
	{
		var validatedUrl = GitNetworkPolicy.ValidateUrl(url, allowFileTransport);
		return new GitProcessOperation(
			GitOperationKind.CloneRepository,
			GitProcessProfile.ExplicitNetwork,
			validatedUrl,
			ValidateAbsolutePath(targetDirectory),
			allowedProtocols: GitNetworkPolicy.GetAllowedProtocols(validatedUrl));
	}

	public static GitProcessOperation FetchBranch(
		string remoteUrl,
		string branch,
		int depth = 1,
		bool allowFileTransport = false)
	{
		var validatedUrl = GitNetworkPolicy.ValidateUrl(remoteUrl, allowFileTransport);
		return new GitProcessOperation(
			GitOperationKind.FetchBranch,
			GitProcessProfile.ExplicitNetwork,
			validatedUrl,
			BuildRemoteTrackingRefSpec(GitBranchNameValidator.ValidateAndNormalize(branch)),
			ValidateDepth(depth),
			allowedProtocols: GitNetworkPolicy.GetAllowedProtocols(validatedUrl));
	}

	public static GitProcessOperation FetchRefSpec(
		string remoteUrl,
		string refspec,
		int depth = 1,
		bool allowFileTransport = false)
	{
		if (!IsSafeRefSpec(refspec))
			throw new ArgumentException("The Git refspec is invalid.", nameof(refspec));
		var validatedUrl = GitNetworkPolicy.ValidateUrl(remoteUrl, allowFileTransport);
		return new GitProcessOperation(
			GitOperationKind.FetchBranch,
			GitProcessProfile.ExplicitNetwork,
			validatedUrl,
			refspec,
			ValidateDepth(depth),
			allowedProtocols: GitNetworkPolicy.GetAllowedProtocols(validatedUrl));
	}

	public static GitProcessOperation FetchDeepen(
		string remoteUrl,
		int depth,
		string? refspec = null,
		bool allowFileTransport = false)
	{
		if (refspec is not null && !IsSafeRefSpec(refspec))
			throw new ArgumentException("The Git refspec is invalid.", nameof(refspec));
		var validatedUrl = GitNetworkPolicy.ValidateUrl(remoteUrl, allowFileTransport);
		return new GitProcessOperation(
			GitOperationKind.FetchDeepen,
			GitProcessProfile.ExplicitNetwork,
			validatedUrl,
			refspec,
			ValidateDepth(depth),
			allowedProtocols: GitNetworkPolicy.GetAllowedProtocols(validatedUrl));
	}

	public static GitProcessOperation ManagedCheckout(
		GitManagedCheckoutKind kind,
		string revision,
		string? branch = null,
		IReadOnlyList<string>? filterDrivers = null)
	{
		var validatedRevision = ValidateRevision(revision);
		if (kind == GitManagedCheckoutKind.ResetBranch)
			branch = GitBranchNameValidator.ValidateAndNormalize(branch!);
		else if (branch is not null)
			throw new ArgumentException("Only reset-branch checkout accepts a branch name.", nameof(branch));

		return new GitProcessOperation(
			GitOperationKind.ManagedCheckout,
			GitProcessProfile.ManagedCheckout,
			validatedRevision,
			branch,
			checkoutKind: kind,
			filterDrivers: ValidateFilterDrivers(filterDrivers));
	}

	public static GitProcessOperation ManagedWorktreeAdd(
		string path,
		string revision,
		IReadOnlyList<string>? filterDrivers = null) =>
		new(
			GitOperationKind.ManagedWorktreeAdd,
			GitProcessProfile.ManagedCheckout,
			ValidateAbsolutePath(path),
			ValidateRevision(revision),
			filterDrivers: ValidateFilterDrivers(filterDrivers));

	public static GitProcessOperation ManagedWorktreeRemove(string path) =>
		new(
			GitOperationKind.ManagedWorktreeRemove,
			GitProcessProfile.ManagedCheckout,
			ValidateAbsolutePath(path));

	public static GitProcessOperation ManagedWorktreePrune() =>
		new(GitOperationKind.ManagedWorktreePrune, GitProcessProfile.ManagedCheckout);

	public static GitProcessOperation ManagedWorktreeList() =>
		new(GitOperationKind.ManagedWorktreeList, GitProcessProfile.LocalRead);

	public static GitProcessOperation ManagedConfigWrite(
		GitManagedConfigWriteKind kind,
		string? value = null)
	{
		if (kind is GitManagedConfigWriteKind.SetWorktreeBranch or GitManagedConfigWriteKind.AddTrackedBranch)
			value = GitBranchNameValidator.ValidateAndNormalize(value!);
		else if (value is not null)
			throw new ArgumentException("This managed config operation takes no value.", nameof(value));
		return new GitProcessOperation(
			GitOperationKind.ManagedConfigWrite,
			GitProcessProfile.ManagedCheckout,
			value,
			configWriteKind: kind);
	}

	internal IReadOnlyList<string> BuildArguments(GitIsolationPaths isolation)
	{
		ArgumentNullException.ThrowIfNull(isolation);
		return Kind switch
		{
			GitOperationKind.ReadTrackedIndex => ["ls-files", "--cached", "--full-name", "-z", "--"],
			GitOperationKind.ReadStagedChanges =>
				["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--cached", "--"],
			GitOperationKind.ReadWorkingChanges =>
				["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", "--"],
			GitOperationKind.ReadUntracked => ["ls-files", "--others", "--exclude-standard", "-z", "--"],
			GitOperationKind.ResolveCommit =>
				["rev-parse", "--verify", "--quiet", "--end-of-options", Value! + "^{commit}"],
			GitOperationKind.ReadRefDiff =>
				["diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z", Value!, "--"],
			GitOperationKind.ReadConfigValue => BuildConfigReadArguments(),
			GitOperationKind.ReadRemoteUrl => ["config", "--get", "remote.origin.url"],
			GitOperationKind.ListBranches => BuildBranchListArguments(),
			GitOperationKind.CloneRepository =>
				["clone", "--no-checkout", "--no-recurse-submodules", $"--template={isolation.EmptyTemplateDirectory}", "--depth", "1", "--progress", Value!, SecondaryValue!],
			GitOperationKind.FetchBranch =>
				["fetch", "--no-recurse-submodules", "--no-auto-maintenance", "--no-tags", "--depth", Depth.ToString(CultureInfo.InvariantCulture), Value!, SecondaryValue!],
			GitOperationKind.FetchDeepen => BuildFetchDeepenArguments(),
			GitOperationKind.ManagedCheckout => BuildCheckoutArguments(),
			GitOperationKind.ManagedWorktreeAdd => ["worktree", "add", "--detach", Value!, SecondaryValue!],
			GitOperationKind.ManagedWorktreeRemove => ["worktree", "remove", "--force", Value!],
			GitOperationKind.ManagedWorktreePrune => ["worktree", "prune"],
			GitOperationKind.ManagedWorktreeList => ["worktree", "list", "--porcelain"],
			GitOperationKind.ManagedConfigWrite => BuildConfigWriteArguments(),
			_ => throw new ArgumentOutOfRangeException()
		};
	}

	private IReadOnlyList<string> BuildConfigReadArguments() => ConfigReadKind switch
	{
		GitConfigReadKind.PathComparisonSemantics =>
			["config", "--show-scope", "--type=bool", "--get-regexp", "^core\\.(repositoryformatversion|ignorecase|precomposeunicode)$"],
		GitConfigReadKind.UnsafeDrivers =>
			["config", "--name-only", "--get-regexp", "^(filter\\..*\\.(clean|smudge|process|required)|diff\\..*\\.(command|textconv)|diff\\.external)$"],
		GitConfigReadKind.WorktreeBranch => ["config", "--worktree", "--get", "devprojex.branch"],
		GitConfigReadKind.PromisorRemotes =>
			["config", "--name-only", "--get-regexp", "^remote\\..*\\.promisor$"],
		GitConfigReadKind.NetworkOverrides =>
			["config", "--name-only", "--get-regexp", "^(url\\..*\\.insteadof|http(\\..*)?\\.(extraheader|cookiefile|proxy)|core\\.(gitproxy|sshcommand)|remote\\..*\\.uploadpack)$"],
		_ => throw new ArgumentOutOfRangeException()
	};

	private IReadOnlyList<string> BuildBranchListArguments() => BranchListKind switch
	{
		GitBranchListKind.Local => ["branch"],
		GitBranchListKind.CachedRemote => ["branch", "-r"],
		GitBranchListKind.Current => ["rev-parse", "--abbrev-ref", "HEAD"],
		GitBranchListKind.RemoteHead => ["symbolic-ref", "refs/remotes/origin/HEAD"],
		GitBranchListKind.RemoteHeads => ["ls-remote", "--heads", Value!],
		_ => throw new ArgumentOutOfRangeException()
	};

	private IReadOnlyList<string> BuildFetchDeepenArguments()
	{
		var arguments = new List<string>
		{
			"fetch", "--no-recurse-submodules", "--no-auto-maintenance", "--quiet", "--no-tags",
			"--deepen", Depth.ToString(CultureInfo.InvariantCulture), Value!
		};
		if (SecondaryValue is not null)
			arguments.Add(SecondaryValue);
		return arguments;
	}

	private IReadOnlyList<string> BuildCheckoutArguments() => CheckoutKind switch
	{
		GitManagedCheckoutKind.Detach => ["checkout", "--detach", Value!],
		GitManagedCheckoutKind.SwitchBranch => ["checkout", Value!],
		GitManagedCheckoutKind.ResetBranch => ["checkout", "-B", SecondaryValue!, Value!],
		GitManagedCheckoutKind.HardReset => ["reset", "--hard", Value!],
		_ => throw new ArgumentOutOfRangeException()
	};

	private IReadOnlyList<string> BuildConfigWriteArguments() => ConfigWriteKind switch
	{
		GitManagedConfigWriteKind.EnableWorktreeConfig => ["config", "extensions.worktreeConfig", "true"],
		GitManagedConfigWriteKind.ClearWorktreeBranch => ["config", "--worktree", "--unset-all", "devprojex.branch"],
		GitManagedConfigWriteKind.SetWorktreeBranch => ["config", "--worktree", "devprojex.branch", Value!],
		GitManagedConfigWriteKind.AddTrackedBranch => ["remote", "set-branches", "--add", "origin", Value!],
		_ => throw new ArgumentOutOfRangeException()
	};

	private static int ValidateDepth(int depth) => depth is > 0 and <= 10_000
		? depth
		: throw new ArgumentOutOfRangeException(nameof(depth));

	private static string ValidateAbsolutePath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!Path.IsPathFullyQualified(path))
			throw new ArgumentException("An absolute path is required.", nameof(path));
		return Path.GetFullPath(path);
	}

	private static string ValidateRevision(string revision)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(revision);
		if (revision[0] == '-' || revision.IndexOf('\0') >= 0 || revision.Any(char.IsWhiteSpace))
			throw new ArgumentException("The Git revision is invalid.", nameof(revision));
		return revision;
	}

	private static IReadOnlyList<string> ValidateFilterDrivers(IReadOnlyList<string>? drivers)
	{
		if (drivers is null or { Count: 0 })
			return [];
		var result = new string[drivers.Count];
		for (var index = 0; index < drivers.Count; index++)
		{
			var driver = drivers[index];
			if (string.IsNullOrWhiteSpace(driver) ||
			    driver.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
			{
				throw new ArgumentException("A Git filter driver name is invalid.", nameof(drivers));
			}
			result[index] = driver;
		}
		return result;
	}

	private static bool IsSafeRefSpec(string value) =>
		value.Length > 0 && value[0] != '-' && value.IndexOf('\0') < 0 && !value.Any(char.IsWhiteSpace);

	private static string BuildRemoteTrackingRefSpec(string branch) =>
		$"+refs/heads/{branch}:refs/remotes/origin/{branch}";
}

internal static class GitNetworkPolicy
{
	private static readonly string[] SupportedSchemes = ["https", "ssh"];

	public static string ValidateUrl(string? url, bool allowFileTransport = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);
		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
		    (SupportedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) ||
		     allowFileTransport && uri.IsFile) &&
		    (!string.IsNullOrWhiteSpace(uri.Host) || uri.IsFile))
		{
			return url;
		}

		// Git's scp-like SSH syntax has no URI scheme.
		var colon = url.IndexOf(':');
		if (colon > 0 &&
		    !url.Contains("://", StringComparison.Ordinal) &&
		    !(colon == 1 && char.IsAsciiLetter(url[0])) &&
		    !url.AsSpan(colon + 1).IsEmpty &&
		    url[colon + 1] != ':' &&
		    url.AsSpan(0, colon).IndexOfAny('/', '\\') < 0)
		{
			return url;
		}

		throw new ArgumentException("Only explicit HTTPS and SSH Git URLs are allowed.", nameof(url));
	}

	public static string GetAllowedProtocols(string url)
	{
		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
			return "file";
		return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https" : "ssh";
	}
}
