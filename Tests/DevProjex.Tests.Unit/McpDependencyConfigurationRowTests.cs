using DevProjex.Application.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpDependencyConfigurationRowTests
{
	[Theory]
	[InlineData(DependencyConfigurationState.Missing, "missing")]
	[InlineData(DependencyConfigurationState.Corrupt, "corrupt")]
	[InlineData(DependencyConfigurationState.UnsupportedSemantics, "unsupported-semantics")]
	public void RowsSpellProblemsLikeTheSummaryLine(DependencyConfigurationState state, string expected)
	{
		var text = DevProjexMcpTools.FormatDependencyConfigurationData(
			[new DependencyConfigurationDiagnostic("tsconfig.json", state, "reason", ["scope"])]);

		Assert.Equal($"[Dependency configuration] tsconfig.json · {expected}", text);
	}
}
