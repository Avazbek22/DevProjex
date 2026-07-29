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

		var outputPath = request.OutputPath is not null and not "-"
			? ExactOutputDestinationValidator.ValidateAnalysis(
				plan.SourceRoot,
				request.OutputPath)
			: null;

		if (request.OutputPath is null or "-")
		{
			if (request.Format == AnalysisOutputFormat.Json)
			{
				await new MachineOutputRenderer(environment)
					.WriteAnalysisJsonAsync(plan, environment.Output, cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				if (request.Output.Plain ||
				    !environment.IsOutputInteractive ||
				    environment.IsTermDumb)
				{
					await environment.Output.WriteAsync(
							AnalysisTextFormatter.Build(plan, services.Localization).AsMemory(),
							cancellationToken)
						.ConfigureAwait(false);
				}
				else
				{
					new HumanOutputRenderer(environment, request.Output, services.Localization)
						.WriteAnalysis(plan);
				}
			}
		}
		else
		{
			var payload = request.Format == AnalysisOutputFormat.Json
				? await BuildJsonAsync(plan, cancellationToken).ConfigureAwait(false)
				: AnalysisTextFormatter.Build(plan, services.Localization);
			var writtenPath = await AtomicOutputWriter
				.WriteTextAsync(
					outputPath!,
					payload,
					overwrite: false,
					cancellationToken,
					path => ExactOutputDestinationValidator.ValidateAnalysis(
						plan.SourceRoot,
						path))
				.ConfigureAwait(false);
			environment.Output.WriteLine(writtenPath);
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
		var rows = BuildRows(plan, localization);
		return string.Join(
			       Environment.NewLine,
			       rows.Select(static row => $"{row.Label}: {row.Value}")) +
		       Environment.NewLine;
	}

	public static IReadOnlyList<AnalysisTextRow> BuildRows(
		ProjectContextPlan plan,
		LocalizationService localization)
	{
		var rows = new List<AnalysisTextRow>
		{
			new(
				localization["Terminal.Analysis.Project"],
				plan.SourceIdentity?.DisplayName ?? plan.SourceRoot)
		};
		if (plan.SourceIdentity?.RepositoryUrl is { Length: > 0 } repositoryUrl)
			rows.Add(new AnalysisTextRow(localization["Terminal.Analysis.Source"], repositoryUrl));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Profile"],
			FormatProfile(plan.Selection.ProfileSource)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.GitMode"],
			ProjectSelectionTokens.ToToken(plan.Selection.GitMode!.Value)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Exclusions"],
			string.Join(", ", plan.Selection.Exclusions!.Select(ProjectSelectionTokens.ToToken))));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Roots"],
			string.Join(", ", plan.SelectedRoots)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Extensions"],
			string.Join(", ", plan.SelectedExtensions)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Files"],
			plan.IncludedFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Folders"],
			plan.IncludedFolders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Size"],
			$"{plan.IncludedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} B"));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Characters"],
			plan.Analysis.Metrics.Content.Chars.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Tokens"],
			plan.Analysis.Metrics.Content.Tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		rows.Add(new AnalysisTextRow(
			localization["Terminal.Analysis.Fingerprint"],
			plan.Fingerprint));
		rows.AddRange(plan.Diagnostics.Select(diagnostic => new AnalysisTextRow(
			diagnostic.Code,
			ContextDiagnosticRenderer.ResolveMessage(localization, diagnostic.Code))));
		return rows;
	}

	private static string FormatProfile(ProjectProfileReference? profile) =>
		profile?.Kind switch
		{
			null => "standard",
			ProjectProfileSourceKind.Standard => "standard",
			ProjectProfileSourceKind.Local => "local",
			ProjectProfileSourceKind.Portable => profile.Path ?? "portable",
			_ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
		};
}

internal sealed record AnalysisTextRow(string Label, string Value);

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
