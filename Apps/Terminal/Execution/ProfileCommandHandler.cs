using System.Globalization;
using System.Text.Json;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class ProfileCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public async Task<int> ShowAsync(
		string projectPath,
		ProjectProfileReference profile,
		bool json,
		CancellationToken cancellationToken)
	{
		var selection = await services.SelectionResolver
			.ResolveAsync(
				projectPath,
				profile,
				new ProjectSelectionSpec(),
				cancellationToken)
			.ConfigureAwait(false);
		if (json)
			environment.Output.WriteLine(SerializeSelection(selection));
		else
			environment.Output.WriteLine(BuildText(selection));
		return CommandLineExitCodes.Success;
	}

	public async Task<int> ExportAsync(
		string projectPath,
		ProjectProfileReference profile,
		string outputPath,
		bool force,
		CancellationToken cancellationToken)
	{
		var selection = await services.SelectionResolver
			.ResolveAsync(
				projectPath,
				profile,
				new ProjectSelectionSpec(),
				cancellationToken)
			.ConfigureAwait(false);
		var writtenPath = await services.PortableProfileService
			.SaveAsync(
				projectPath,
				outputPath,
				selection,
				force,
				cancellationToken)
			.ConfigureAwait(false);
		environment.Output.WriteLine(writtenPath);
		return CommandLineExitCodes.Success;
	}

	public async Task<int> ImportAsync(
		string profilePath,
		string projectPath,
		bool apply,
		CancellationToken cancellationToken)
	{
		var selection = await services.PortableProfileService
			.LoadAsync(profilePath, cancellationToken)
			.ConfigureAwait(false);
		if (apply)
		{
			var plan = await services.ContextFactory
				.BuildAsync(projectPath, selection, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
			if (plan.HasErrors)
			{
				new ContextDiagnosticRenderer(
						environment,
						new TerminalOutputOptions(),
						services.Localization)
					.Write(plan.Diagnostics);
				return CommandLineExitCodes.PolicyFailure;
			}
			var legacy = ToLegacyProfile(plan);
			if (!services.LocalProfileStore.TrySaveProfile(projectPath, legacy))
			{
				throw new PortableProjectProfileException(
					"DPX-CLI-PROFILE-WRITE-FAILED",
					"The local project profile could not be saved.");
			}
		}

		environment.Output.WriteLine(apply
			? PathUtility.Normalize(projectPath)
			: Path.GetFullPath(profilePath));
		return CommandLineExitCodes.Success;
	}

	public async Task<int> ValidateAsync(
		string profilePath,
		CancellationToken cancellationToken)
	{
		var result = await services.PortableProfileService
			.ValidateAsync(profilePath, cancellationToken)
			.ConfigureAwait(false);
		if (result.IsValid)
		{
			environment.Output.WriteLine(services.Localization["Terminal.Profile.Valid"]);
			return CommandLineExitCodes.Success;
		}

		environment.Error.WriteLine("error[DPX-CLI-PROFILE-INVALID]:");
		environment.Error.WriteLine(services.Localization["Terminal.Error.ProfileInvalid"]);
		return CommandLineExitCodes.UsageError;
	}

	public int Reset(string projectPath)
	{
		if (!services.LocalProfileStore.TryDeleteProfile(projectPath))
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-WRITE-FAILED",
				"The local project profile could not be reset.");
		}

		environment.Output.WriteLine(PathUtility.Normalize(projectPath));
		return CommandLineExitCodes.Success;
	}

	private static ProjectSelectionProfile ToLegacyProfile(ProjectContextPlan plan)
	{
		var selectedRoots = plan.SelectedRoots.ToHashSet(PathComparer.Default);
		var selectedExtensions = plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = ProjectSelectionAdapter.ToIgnoreOptions(plan.Selection).ToHashSet();
		var rootStates = plan.AvailableRoots.ToDictionary(
			static root => root,
			selectedRoots.Contains,
			PathComparer.Default);
		var extensionStates = plan.AvailableExtensions.ToDictionary(
			static extension => extension,
			selectedExtensions.Contains,
			StringComparer.OrdinalIgnoreCase);
		var ignoreStates = Enum.GetValues<IgnoreOptionId>().ToDictionary(
			static option => option,
			selectedIgnoreOptions.Contains);

		return new ProjectSelectionProfile(
			selectedRoots,
			selectedExtensions,
			selectedIgnoreOptions,
			rootStates,
			extensionStates,
			ignoreStates,
			plan.Selection.SelectedPaths?.ToArray() ?? []);
	}

	private string BuildText(ProjectSelectionSpec selection)
	{
		var output = new StringBuilder();
		var all = services.Localization["Terminal.Profile.All"];
		output.Append(services.Localization["Terminal.Analysis.Profile"]).Append(": ")
			.AppendLine(FormatProfile(selection.ProfileSource));
		output.Append(services.Localization["Terminal.Analysis.GitMode"]).Append(": ")
			.AppendLine(selection.GitMode is { } gitMode
				? ProjectSelectionTokens.ToToken(gitMode)
				: ProjectSelectionTokens.ToToken(GitFilteringMode.None));
		output.Append(services.Localization["Terminal.Analysis.Roots"]).Append(": ")
			.AppendLine(selection.Roots is null ? all : string.Join(", ", selection.Roots));
		output.Append(services.Localization["Terminal.Analysis.Extensions"]).Append(": ")
			.AppendLine(selection.Extensions is null ? all : string.Join(", ", selection.Extensions));
		output.Append(services.Localization["Terminal.Profile.SelectedPaths"]).Append(": ")
			.AppendLine(string.Join(", ", selection.SelectedPaths ?? []));
		output.Append(services.Localization["Terminal.Analysis.Exclusions"]).Append(": ")
			.AppendLine(string.Join(
				", ",
				(selection.Exclusions ?? []).Select(ProjectSelectionTokens.ToToken)));
		output.Append(services.Localization["Settings.Ignore.HideSecrets"]).Append(": ")
			.AppendLine((selection.HideSecrets == true).ToString(CultureInfo.InvariantCulture));
		output.Append(services.Localization["Settings.Ignore.HidePrivateData"]).Append(": ")
			.AppendLine((selection.HidePrivateData == true).ToString(CultureInfo.InvariantCulture));
		output.Append(services.Localization["Settings.Ignore.CompressCode"]).Append(": ")
			.AppendLine((selection.CompressCode == true).ToString(CultureInfo.InvariantCulture));
		output.Append(services.Localization["Settings.Ignore.StripComments"]).Append(": ")
			.AppendLine((selection.StripComments == true).ToString(CultureInfo.InvariantCulture));
		output.Append(services.Localization["Settings.Ignore.StripBlankLines"]).Append(": ")
			.AppendLine((selection.StripBlankLines == true).ToString(CultureInfo.InvariantCulture));
		return output.ToString().TrimEnd('\r', '\n');
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

	private static string SerializeSelection(ProjectSelectionSpec selection) =>
		JsonSerializer.Serialize(
			new
			{
				schemaVersion = 1,
				kind = PortableProjectProfileService.DocumentKind,
				selection = new
				{
					roots = selection.Roots,
					extensions = selection.Extensions,
					selectedPaths = selection.SelectedPaths ?? [],
					gitMode = selection.GitMode is { } gitMode
						? ProjectSelectionTokens.ToToken(gitMode)
						: null,
					exclusions = (selection.Exclusions ?? [])
						.Select(ProjectSelectionTokens.ToToken)
						.ToArray(),
					hideSecrets = selection.HideSecrets == true,
					hidePrivateData = selection.HidePrivateData == true,
					compressCode = selection.CompressCode == true,
					stripComments = selection.StripComments == true,
					stripBlankLines = selection.StripBlankLines == true
				}
			},
			new JsonSerializerOptions
			{
				WriteIndented = true,
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase
			});
}
