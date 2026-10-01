using DevProjex.Application.Ranking;
using DevProjex.Application.Secrets;

namespace DevProjex.Application.Context;

public sealed record ProjectContextTokenAdmission(
	ProjectContextPlan Plan,
	ProjectContextWriteResult WriteResult);

public sealed class ProjectContextTokenAdmissionService(ProjectContextDocumentService documentService)
{
	public async Task<ProjectContextTokenAdmission> AdmitMeasuredAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		long maximumEstimatedTokens,
		PreparedSecretRedactionOutput measured,
		ImportanceRankingReport? ranking = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(measured);
		var result = await documentService.EvaluateMeasuredTokenBudgetAsync(
				plan,
				view,
				format,
				maximumEstimatedTokens,
				measured,
				ranking,
				cancellationToken)
			.ConfigureAwait(false);
		if (view == ProjectContextView.Tree || result.TokenBudget is null)
			return new ProjectContextTokenAdmission(plan, result);

		var measuredPlan = ProjectContextDocumentService.ApplyMeasuredContentMetrics(plan, measured);
		return new ProjectContextTokenAdmission(
			measuredPlan with { IncludedFiles = result.TokenBudget.AdmittedSourceFiles },
			result);
	}
}
