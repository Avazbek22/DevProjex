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
					cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		new ContextDiagnosticRenderer(environment, request.Output, services.Localization)
			.Write(plan.Diagnostics);
		if (plan.HasErrors)
			return CommandLineExitCodes.PolicyFailure;

		if (request.OutputPath is null or "-")
		{
			if (request.Format == TreeTextFormat.Ascii)
			{
				await WriteTextTreeAsync(
						environment.Output,
						plan,
						request.Output.Plain,
						cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				var payload = BuildPayload(plan, request.Format, cancellationToken);
				await environment.Output.WriteAsync(payload.AsMemory(), cancellationToken)
					.ConfigureAwait(false);
			}
			return CommandLineExitCodes.Success;
		}

		var requestedPath = Path.GetFullPath(request.OutputPath);
		_ = ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, requestedPath);
		var writtenPath = request.Format == TreeTextFormat.Ascii
			? await AtomicOutputWriter.WriteAsync(
					requestedPath,
					overwrite: false,
					async (destination, token) =>
					{
						await using var writer = new StreamWriter(
							destination,
							new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
							bufferSize: 16 * 1024,
							leaveOpen: true);
						await WriteTextTreeAsync(writer, plan, request.Output.Plain, token)
							.ConfigureAwait(false);
						await writer.FlushAsync(token).ConfigureAwait(false);
					},
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path))
				.ConfigureAwait(false)
			: await AtomicOutputWriter.WriteTextAsync(
					requestedPath,
					BuildPayload(plan, request.Format, cancellationToken),
					overwrite: false,
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateAnalysis(plan.SourceRoot, path))
				.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
		return CommandLineExitCodes.Success;
	}

	private string BuildPayload(
		ProjectContextPlan plan,
		TreeTextFormat format,
		CancellationToken cancellationToken)
	{
		var (displayRootPath, displayRootName) = ResolveDisplayIdentity(plan);
		return services.TreeExportService.BuildFullTreeWithCancellation(
				plan.SourceRoot,
				plan.ProjectedTree,
				format,
				displayRootPath,
				displayRootName,
				includeRootPath: true,
				cancellationToken: cancellationToken);
	}

	private Task WriteTextTreeAsync(
		TextWriter destination,
		ProjectContextPlan plan,
		bool plain,
		CancellationToken cancellationToken)
	{
		var (displayRootPath, displayRootName) = ResolveDisplayIdentity(plan);
		return plain
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
