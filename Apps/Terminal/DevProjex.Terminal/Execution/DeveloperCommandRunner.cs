namespace DevProjex.Terminal.Execution;

public enum DeveloperCommandKind
{
	AnalysisBenchmark,
	UiBenchmark,
	Session
}

public sealed record DeveloperCommandRequest(
	DeveloperCommandKind Kind,
	string ProjectPath,
	string? OutputPath = null,
	string Scenario = "standard");

public interface IDeveloperCommandRunner
{
	Task<int> RunAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken);
}
