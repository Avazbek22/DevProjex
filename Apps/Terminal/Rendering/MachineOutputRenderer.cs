using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Rendering;

public sealed class MachineOutputRenderer(ITerminalEnvironment environment)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	public async Task WriteAnalysisJsonAsync(
		ProjectContextPlan plan,
		TextWriter writer,
		CancellationToken cancellationToken)
	{
		var document = new
		{
			schemaVersion = 1,
			kind = "devprojex-analysis",
			project = new
			{
				root = ResolveDocumentRoot(plan).Replace('\\', '/'),
				name = plan.SourceIdentity?.DisplayName ??
				       Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
				source = plan.SourceIdentity is
				{
					SourceType: ProjectSourceType.GitClone,
					RepositoryUrl.Length: > 0
				} identity
					? new
					{
						type = "git",
						repositoryUrl = identity.RepositoryUrl,
						branch = identity.Branch,
						commit = identity.CommitHash
					}
					: null
			},
			selection = new
			{
				gitMode = ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value),
				exclusions = plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken).ToArray(),
				roots = plan.SelectedRoots,
				extensions = plan.SelectedExtensions,
				selectedPaths = plan.Selection.SelectedPaths ?? []
			},
			inventory = new
			{
				files = plan.IncludedFiles.Count,
				folders = plan.IncludedFolders.Count
			},
			metrics = new
			{
				bytes = plan.IncludedBytes,
				tree = plan.Analysis.Metrics.Tree,
				content = plan.Analysis.Metrics.Content
			},
			diagnostics = plan.Diagnostics.Select(static diagnostic => new
			{
				code = diagnostic.Code,
				severity = diagnostic.Severity.ToString().ToLowerInvariant(),
				message = diagnostic.Message,
				path = diagnostic.Path?.Replace('\\', '/')
			}),
			fingerprint = plan.Fingerprint
		};
		var json = JsonSerializer.Serialize(document, JsonOptions);
		await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	public TextWriter StandardOutput => environment.Output;

	private static string ResolveDocumentRoot(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;
}
