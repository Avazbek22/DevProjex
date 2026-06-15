namespace DevProjex.Tests.Integration;

public sealed class IgnoreOwnershipAuditServiceContractTests
{
	[Fact]
	public void AuditRootDirectories_MixedOwners_UsesSamePriorityAsDecisionEngine()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", ".git-owned/\n");
		temp.CreateFile("package.json", "{}\n");
		temp.CreateFile(".dot-owned/payload.txt", "dot\n");
		temp.CreateFile(".git-owned/payload.txt", "git\n");
		temp.CreateFile("node_modules/pkg/index.js", "smart\n");
		temp.CreateFile("visible-root/file.txt", "visible\n");

		var rules = new IgnoreRulesService(new SmartIgnoreService([new FrontendArtifactsIgnoreRule()]))
			.Build(
				temp.Path,
				[
					IgnoreOptionId.UseGitIgnore,
					IgnoreOptionId.SmartIgnore,
					IgnoreOptionId.DotFolders,
					IgnoreOptionId.HiddenFolders
				],
				selectedRootFolders: []);

		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			temp.Path,
			rules,
			TestContext.Current.CancellationToken);

		Assert.False(audit.RootAccessDenied);
		Assert.False(audit.HadAccessDenied);
		Assert.Equal(2, audit.PhysicalDotDirectories);
		Assert.Equal(1, audit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(1, audit.Count(IgnoreDecisionOwner.SmartIgnore));
		Assert.Equal(1, audit.Count(IgnoreDecisionOwner.DotFolders));
		Assert.Equal(1, audit.Count(IgnoreDecisionOwner.None));
	}

	[Fact]
	public void AuditRootDirectories_WhenGitIgnoreDisabled_ReassignsGitOwnedDotRootsToDotFolders()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", ".git-owned/\n");
		temp.CreateFile(".git-owned/payload.txt", "git\n");
		temp.CreateFile(".dot-owned/payload.txt", "dot\n");

		var rules = new IgnoreRulesService(new SmartIgnoreService([]))
			.Build(
				temp.Path,
				[IgnoreOptionId.DotFolders],
				selectedRootFolders: []);

		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			temp.Path,
			rules,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, audit.PhysicalDotDirectories);
		Assert.Equal(0, audit.Count(IgnoreDecisionOwner.GitIgnore));
		Assert.Equal(2, audit.Count(IgnoreDecisionOwner.DotFolders));
	}

	[Fact]
	public void AuditRootDirectories_PreCanceledToken_ThrowsBeforeReturningPartialCounts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}\n");
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.ThrowsAny<OperationCanceledException>(() =>
			new IgnoreOwnershipAuditService().AuditRootDirectories(
				temp.Path,
				CreatePlainRules(ignoreDotFolders: false),
				cts.Token));
	}

	[Fact]
	public void AuditRootDirectories_SkipsDirectoryReparsePoints()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".real-dot/payload.txt", "real\n");
		temp.CreateFile("target/payload.txt", "target\n");
		var linkPath = Path.Combine(temp.Path, ".linked-dot");

		if (!TryCreateDirectorySymlink(linkPath, Path.Combine(temp.Path, "target")))
			return;

		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			temp.Path,
			CreatePlainRules(ignoreDotFolders: true),
			TestContext.Current.CancellationToken);

		Assert.Equal(1, audit.PhysicalDotDirectories);
		Assert.Equal(1, audit.Count(IgnoreDecisionOwner.DotFolders));
	}

	[Fact]
	public void AuditRootDirectories_MissingRootReportsDiagnosticFailureWithoutThrowing()
	{
		using var temp = new TemporaryDirectory();
		var missingRoot = Path.Combine(temp.Path, "deleted");

		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			missingRoot,
			CreatePlainRules(ignoreDotFolders: true),
			TestContext.Current.CancellationToken);

		Assert.False(audit.RootAccessDenied);
		Assert.True(audit.HadAccessDenied);
		Assert.Equal(0, audit.PhysicalDotDirectories);
		Assert.Empty(audit.RootDirectoryOwners);
	}

	[Fact]
	public void AuditRootDirectories_HiddenDotFolderOwnership_FollowsPlatformContract()
	{
		using var temp = new TemporaryDirectory();
		var hiddenDot = temp.CreateDirectory(".hidden-dot");
		TryMarkHidden(hiddenDot);
		var isHidden = File.GetAttributes(hiddenDot).HasFlag(FileAttributes.Hidden);
		var rules = CreatePlainRules(ignoreDotFolders: false) with { IgnoreHiddenFolders = true };

		var audit = new IgnoreOwnershipAuditService().AuditRootDirectories(
			temp.Path,
			rules,
			TestContext.Current.CancellationToken);

		var hiddenOwnsDot = IgnoreRuleSemantics.ShouldIgnoreHiddenDirectory(
			ignoreHiddenFolders: true,
			isHidden,
			isDot: true,
			ignoreDotFolders: false);
		Assert.Equal(hiddenOwnsDot ? 1 : 0, audit.Count(IgnoreDecisionOwner.HiddenFolders));
		Assert.Equal(hiddenOwnsDot ? 0 : 1, audit.Count(IgnoreDecisionOwner.None));
	}

	private static IgnoreRules CreatePlainRules(bool ignoreDotFolders)
	{
		return new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: ignoreDotFolders,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}

	private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
	{
		try
		{
			Directory.CreateSymbolicLink(linkPath, targetPath);
			return Directory.Exists(linkPath) &&
			       File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	private static void TryMarkHidden(string path)
	{
		try
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
		}
	}
}
