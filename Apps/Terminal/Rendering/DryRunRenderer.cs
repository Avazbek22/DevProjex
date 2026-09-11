using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Execution;

namespace DevProjex.Terminal.Rendering;

internal static class DryRunRenderer
{
	public static void WritePlan(
		ITerminalEnvironment environment,
		LocalizationService localization,
		string destination,
		ProjectContextPlan? plan = null)
	{
		var displayDestination = destination == "-"
			? localization["Terminal.Value.Stdout"]
			: TerminalTextEscaping.EscapeSingleLine(destination);
		environment.Error.WriteLine(
			localization.Format("Terminal.DryRun.Ready", displayDestination));
		if (plan is null)
			return;
		environment.Error.WriteLine(localization.Format(
			"Terminal.DryRun.Inventory",
			plan.IncludedFiles.Count,
			plan.IncludedFolders.Count));
		environment.Error.WriteLine(localization.Format(
			"Terminal.DryRun.Metrics",
			CacheCommandHandler.FormatByteSize(plan.IncludedBytes),
			plan.Analysis.Metrics.Content.Tokens));
		// Only when the call actually asked for a mix, so an ordinary plan keeps its exact lines.
		if (ContentDetailSelection.Resolve(plan.Selection) is { } detailPolicy)
		{
			var mix = ContentDetailMix.Create(detailPolicy, plan.SourceRoot, plan.IncludedFiles);
			environment.Error.WriteLine(localization.Format(
				"Terminal.DryRun.Detail",
				mix.FullFileCount,
				mix.CompactFileCount,
				mix.SignaturesFileCount));
			if (mix.UnmatchedPatterns.Count > 0)
			{
				environment.Error.WriteLine(localization.Format(
					"Terminal.DryRun.DetailUnmatched",
					string.Join(
						", ",
						mix.UnmatchedPatterns.Select(TerminalTextEscaping.EscapeSingleLine))));
			}
		}
		environment.Error.WriteLine(localization.Format(
			"Terminal.DryRun.Profile",
			TerminalTextEscaping.EscapeSingleLine(FormatProfile(plan.Selection.ProfileSource))));
		if (plan.FileSizeFilter is { } sizeFilter)
		{
			environment.Error.WriteLine(localization.Format(
				"Terminal.DryRun.SizeFilter",
				CacheCommandHandler.FormatByteSize(sizeFilter.MaximumFileBytes),
				sizeFilter.ExcludedFiles,
				CacheCommandHandler.FormatByteSize(sizeFilter.ExcludedBytes)));
		}
	}

	private static string FormatProfile(ProjectProfileReference? profile) =>
		profile?.Kind switch
		{
			null or ProjectProfileSourceKind.Standard => "standard",
			ProjectProfileSourceKind.Local => "local",
			ProjectProfileSourceKind.Portable => profile.Path ?? "portable",
			_ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
		};
}
