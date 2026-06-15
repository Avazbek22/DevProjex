namespace DevProjex.Application.Services;

public sealed class IgnoreOwnershipAuditService
{
	public IgnoreOwnershipAuditResult AuditRootDirectories(
		string rootPath,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		var counts = new Dictionary<IgnoreDecisionOwner, int>();
		var physicalDotDirectories = 0;
		var rootAccessDenied = false;
		var hadAccessDenied = false;

		IgnoreRules.GitIgnoreScanContext gitIgnoreContext;
		try
		{
			gitIgnoreContext = rules.CreateGitIgnoreScanContext(rootPath);
		}
		catch
		{
			gitIgnoreContext = IgnoreRules.GitIgnoreScanContext.Disabled(rules);
		}

		try
		{
			foreach (var directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
			{
				cancellationToken.ThrowIfCancellationRequested();

				var name = Path.GetFileName(directoryPath);
				if (string.IsNullOrWhiteSpace(name))
					continue;
				if (IsReparsePoint(directoryPath))
					continue;

				var isDot = IgnoreRuleSemantics.IsDotName(name);
				if (isDot)
					physicalDotDirectories++;

				var gitIgnore = rules.UseGitIgnore
					? gitIgnoreContext.Evaluate(directoryPath, name, isDirectory: true, name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
				var owner = IgnoreDecisionEngine
					.EvaluateDirectory(directoryPath, name, HasHiddenAttribute(directoryPath), rules, gitIgnore)
					.Owner;
				Increment(counts, owner);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			rootAccessDenied = true;
			hadAccessDenied = true;
		}
		catch
		{
			// Ownership audit is diagnostic. Unreadable roots should report partial data
			// instead of destabilizing the main application workflow.
			hadAccessDenied = true;
		}

		return new IgnoreOwnershipAuditResult(
			physicalDotDirectories,
			counts,
			rootAccessDenied,
			hadAccessDenied);
	}

	private static void Increment(Dictionary<IgnoreDecisionOwner, int> counts, IgnoreDecisionOwner owner)
	{
		counts.TryGetValue(owner, out var current);
		counts[owner] = current + 1;
	}

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return true;
		}
	}

	private static bool HasHiddenAttribute(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
		}
		catch
		{
			return false;
		}
	}
}

public sealed record IgnoreOwnershipAuditResult(
	int PhysicalDotDirectories,
	IReadOnlyDictionary<IgnoreDecisionOwner, int> RootDirectoryOwners,
	bool RootAccessDenied,
	bool HadAccessDenied)
{
	public int Count(IgnoreDecisionOwner owner) =>
		RootDirectoryOwners.TryGetValue(owner, out var count) ? count : 0;
}
