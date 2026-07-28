using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class AnalyzeCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ExecuteAsync(
		AnalyzeCommandRequest request,
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

		if (request.DryRun && request.OutputPath is not null and not "-")
		{
			environment.Output.WriteLine(Path.GetFullPath(request.OutputPath));
			return request.Strict && plan.Diagnostics.Count > 0
				? CommandLineExitCodes.PolicyFailure
				: CommandLineExitCodes.Success;
		}

		if (request.OutputPath is null or "-")
		{
			if (request.Format.Equals("json", StringComparison.OrdinalIgnoreCase))
			{
				await new MachineOutputRenderer(environment)
					.WriteAnalysisJsonAsync(plan, environment.Output, cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				new HumanOutputRenderer(environment, request.Output, services.Localization).WriteAnalysis(plan);
			}
		}
		else
		{
			var payload = request.Format.Equals("json", StringComparison.OrdinalIgnoreCase)
				? await BuildJsonAsync(plan, cancellationToken).ConfigureAwait(false)
				: AnalysisTextFormatter.Build(plan, services.Localization);
			var outputPath = await AtomicOutputWriter
				.WriteTextAsync(request.OutputPath, payload, overwrite: true, cancellationToken)
				.ConfigureAwait(false);
			environment.Output.WriteLine(outputPath);
		}

		return request.Strict && plan.Diagnostics.Count > 0
			? CommandLineExitCodes.PolicyFailure
			: CommandLineExitCodes.Success;
	}

	private async Task<string> BuildJsonAsync(
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		using var writer = new StringWriter();
		var nestedEnvironment = new WriterTerminalEnvironment(environment, writer);
		await new MachineOutputRenderer(nestedEnvironment)
			.WriteAnalysisJsonAsync(plan, writer, cancellationToken)
			.ConfigureAwait(false);
		return writer.ToString().TrimEnd('\r', '\n');
	}
}

internal static class AnalysisTextFormatter
{
	public static string Build(ProjectContextPlan plan, LocalizationService localization)
	{
		var output = new StringBuilder();
		output.Append(localization["Terminal.Analysis.Project"]).Append(": ")
			.AppendLine(plan.SourceIdentity?.DisplayName ?? plan.SourceRoot);
		if (plan.SourceIdentity?.RepositoryUrl is { Length: > 0 } repositoryUrl)
			output.Append(localization["Terminal.Analysis.Source"]).Append(": ").AppendLine(repositoryUrl);
		output.Append(localization["Terminal.Analysis.Profile"]).Append(": ")
			.AppendLine(plan.Selection.ProfileSource?.Kind.ToString().ToLowerInvariant() ?? "standard");
		output.Append(localization["Terminal.Analysis.GitMode"]).Append(": ")
			.AppendLine(plan.Selection.GitMode?.ToString() ?? "None");
		output.Append(localization["Terminal.Analysis.Files"]).Append(": ")
			.AppendLine(plan.IncludedFiles.Count.ToString());
		output.Append(localization["Terminal.Analysis.Folders"]).Append(": ")
			.AppendLine(plan.IncludedFolders.Count.ToString());
		output.Append(localization["Terminal.Analysis.Size"]).Append(": ")
			.Append(plan.IncludedBytes.ToString()).AppendLine(" B");
		output.Append(localization["Terminal.Analysis.Characters"]).Append(": ")
			.AppendLine(plan.Analysis.Metrics.Content.Chars.ToString());
		output.Append(localization["Terminal.Analysis.Tokens"]).Append(": ")
			.AppendLine(plan.Analysis.Metrics.Content.Tokens.ToString());
		output.Append(localization["Terminal.Analysis.Fingerprint"]).Append(": ")
			.AppendLine(plan.Fingerprint);
		foreach (var diagnostic in plan.Diagnostics)
		{
			output.Append(diagnostic.Code)
				.Append(": ")
				.AppendLine(ContextDiagnosticRenderer.ResolveMessage(localization, diagnostic.Code));
		}
		return output.ToString().TrimEnd('\r', '\n');
	}
}

internal sealed class WriterTerminalEnvironment(
	ITerminalEnvironment source,
	TextWriter output)
	: ITerminalEnvironment
{
	public TextReader Input => source.Input;
	public TextWriter Output => output;
	public TextWriter Error => source.Error;
	public bool IsInputInteractive => source.IsInputInteractive;
	public bool IsOutputInteractive => false;
	public bool IsErrorInteractive => source.IsErrorInteractive;
	public bool HasAttachedConsole => source.HasAttachedConsole;
	public bool IsTerminalHost => source.IsTerminalHost;
	public bool IsCi => source.IsCi;
	public bool IsTermDumb => source.IsTermDumb;
	public bool IsNoColor => true;
	public bool SupportsUnicode => source.SupportsUnicode;
	public int Width => source.Width;
	public int Height => source.Height;
	public IReadOnlyDictionary<string, string?> Variables => source.Variables;
}
