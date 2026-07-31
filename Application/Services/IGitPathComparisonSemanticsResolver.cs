namespace DevProjex.Application.Services;

public interface IGitPathComparisonSemanticsResolver
{
	GitPathComparisonSemantics Resolve(string scopeRootPath);

	void Invalidate(string rootPath);
}

public sealed class PlatformGitPathComparisonSemanticsResolver
	: IGitPathComparisonSemanticsResolver
{
	public static PlatformGitPathComparisonSemanticsResolver Instance { get; } = new();

	private PlatformGitPathComparisonSemanticsResolver()
	{
	}

	public GitPathComparisonSemantics Resolve(string scopeRootPath) =>
		GitPathComparisonSemantics.PlatformDefault;

	public void Invalidate(string rootPath)
	{
	}
}
