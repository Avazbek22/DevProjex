using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class TreeCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		TreeCommandRequest request,
		CancellationToken cancellationToken)
	{
		var plan = await new StatusRenderer(environment, request.Output)
			.RunAsync(
				services.Localization["Terminal.Status.AnalyzingProject"],
				() => services.ContextFactory.BuildAsync(
					request.ProjectPath,
					request.Selection,
					includeOutputMetrics: false,
					cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		if (request.OutputPath is null or "-")
		{
			await WriteTreeAsync(
					environment.Output,
					plan,
					request.Format,
					request.Output.Plain,
					cancellationToken)
				.ConfigureAwait(false);
			return CommandLineExitCodes.Success;
		}

		var requestedPath = Path.GetFullPath(request.OutputPath);
		_ = ExactOutputDestinationValidator.ValidateAnalysis(
			plan.SourceRoot,
			requestedPath,
			request.Force);
		var writtenPath = await AtomicOutputWriter.WriteAsync(
				requestedPath,
				overwrite: request.Force,
				async (destination, token) =>
				{
					await using var writer = new StreamWriter(
						destination,
						new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
						bufferSize: 16 * 1024,
						leaveOpen: true);
					await WriteTreeAsync(
							writer,
							plan,
							request.Format,
							request.Output.Plain,
							token)
						.ConfigureAwait(false);
					await writer.FlushAsync(token).ConfigureAwait(false);
				},
				cancellationToken,
				path => ExactOutputDestinationValidator.ValidateAnalysis(
					plan.SourceRoot,
					path,
					request.Force))
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		return CommandLineExitCodes.Success;
	}

	private Task WriteTreeAsync(
		TextWriter destination,
		ProjectContextPlan plan,
		TreeTextFormat format,
		bool plain,
		CancellationToken cancellationToken)
	{
		var (displayRootPath, displayRootName) = ResolveDisplayIdentity(plan);
		return plain && format == TreeTextFormat.Ascii
			? services.TreeExportService.WriteFullTreePlainAsync(
				destination,
				plan.SourceRoot,
				plan.ProjectedTree,
				displayRootPath,
				displayRootName,
				cancellationToken: cancellationToken)
			: services.TreeExportService.WriteFullTreeAsync(
				destination,
				plan.SourceRoot,
				plan.ProjectedTree,
				format,
				displayRootPath,
				displayRootName,
				cancellationToken: cancellationToken);
	}

	private static (string Path, string? Name) ResolveDisplayIdentity(ProjectContextPlan plan) =>
		(plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot,
		plan.SourceIdentity?.DisplayName);
}
