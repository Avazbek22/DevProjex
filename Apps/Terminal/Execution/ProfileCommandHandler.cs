using System.Globalization;
using System.Text.Json;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

public sealed class ProfileCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	public Task<int> ExportAsync(
		string projectPath,
		ProjectProfileReference profile,
		string outputPath,
		bool force,
		CancellationToken cancellationToken) =>
		ExportAsync(projectPath, profile, outputPath, force, dryRun: false, cancellationToken);

	public Task<int> ValidateAsync(
		string profilePath,
		CancellationToken cancellationToken) =>
		ValidateAsync(profilePath, json: false, cancellationToken);

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
		bool dryRun,
		CancellationToken cancellationToken)
	{
		var selection = await services.SelectionResolver
			.ResolveAsync(
				projectPath,
				profile,
				new ProjectSelectionSpec(),
				cancellationToken)
			.ConfigureAwait(false);
		if (dryRun)
		{
			var destination = services.PortableProfileService.ValidateSaveDestination(
				projectPath,
				outputPath,
				force);
			DryRunRenderer.WritePlan(
				environment,
				services.Localization,
				destination);
			return CommandLineExitCodes.Success;
		}
		var writtenPath = await services.PortableProfileService
			.SaveAsync(
				projectPath,
				outputPath,
				selection,
				force,
				cancellationToken)
			.ConfigureAwait(false);
		TerminalTextEscaping.WriteSingleLine(environment.Output, writtenPath);
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
			var legacy = ToLegacyProfile(plan, selection);
			var saveResult = services.LocalProfileStore.TrySaveProfileWithResult(projectPath, legacy);
			if (saveResult.WasTruncated)
			{
				throw new PortableProjectProfileException(
					"DPX-CLI-PROFILE-SELECTION-TOO-LARGE",
					services.Localization["Terminal.Error.ProfileSelectionTooLarge"]);
			}
			if (!saveResult.Succeeded)
			{
				throw new PortableProjectProfileException(
					"DPX-CLI-PROFILE-WRITE-FAILED",
					"The local project profile could not be saved.");
			}
		}

		TerminalTextEscaping.WriteSingleLine(
			environment.Output,
			apply
				? PathUtility.Normalize(projectPath)
				: Path.GetFullPath(profilePath));
		return CommandLineExitCodes.Success;
	}

	public async Task<int> ValidateAsync(
		string profilePath,
		bool json,
		CancellationToken cancellationToken)
	{
		var result = await services.PortableProfileService
			.ValidateAsync(profilePath, cancellationToken)
			.ConfigureAwait(false);
		if (json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-profile-validation",
					valid = result.IsValid,
					errors = result.Errors
				},
				new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
					WriteIndented = true
				}));
			return result.IsValid
				? CommandLineExitCodes.Success
				: CommandLineExitCodes.UsageError;
		}
		if (result.IsValid)
		{
			environment.Output.WriteLine(services.Localization["Terminal.Profile.Valid"]);
			return CommandLineExitCodes.Success;
		}

		environment.Error.WriteLine("error[DPX-CLI-PROFILE-INVALID]:");
		environment.Error.WriteLine(services.Localization["Terminal.Error.ProfileInvalid"]);
		return CommandLineExitCodes.UsageError;
	}

	public async Task<int> SaveAsync(
		string projectPath,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		var plan = await services.ContextFactory
			.BuildAsync(projectPath, selection, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		if (plan.HasErrors)
		{
			new ContextDiagnosticRenderer(environment, new TerminalOutputOptions(), services.Localization)
				.Write(plan.Diagnostics);
			return CommandLineExitCodes.PolicyFailure;
		}

		SaveLocalProfile(projectPath, plan, selection);
		TerminalTextEscaping.WriteSingleLine(environment.Output, PathUtility.Normalize(projectPath));
		return CommandLineExitCodes.Success;
	}

	public int Reset(string projectPath)
	{
		if (!services.LocalProfileStore.TryDeleteProfile(projectPath))
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-WRITE-FAILED",
				"The local project profile could not be reset.");
		}

		TerminalTextEscaping.WriteSingleLine(
			environment.Output,
			PathUtility.Normalize(projectPath));
		return CommandLineExitCodes.Success;
	}

	private void SaveLocalProfile(
		string projectPath,
		ProjectContextPlan plan,
		ProjectSelectionSpec selection)
	{
		var legacy = ToLegacyProfile(plan, selection);
		var saveResult = services.LocalProfileStore.TrySaveProfileWithResult(projectPath, legacy);
		if (saveResult.WasTruncated)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-SELECTION-TOO-LARGE",
				services.Localization["Terminal.Error.ProfileSelectionTooLarge"]);
		}
		if (!saveResult.Succeeded)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-WRITE-FAILED",
				"The local project profile could not be saved.");
		}
	}

	private static ProjectSelectionProfile ToLegacyProfile(
		ProjectContextPlan plan,
		ProjectSelectionSpec importedSelection)
	{
		var inheritsAllRoots = importedSelection.Roots is null;
		var inheritsAllExtensions = importedSelection.Extensions is null;
		var selectedRoots = inheritsAllRoots
			? new HashSet<string>(PathComparer.Default)
			: plan.SelectedRoots.ToHashSet(PathComparer.Default);
		var selectedExtensions = inheritsAllExtensions
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			: plan.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = ProjectSelectionAdapter.ToIgnoreOptions(plan.Selection).ToHashSet();
		var rootStates = inheritsAllRoots
			? new Dictionary<string, bool>(PathComparer.Default)
			: plan.AvailableRoots.ToDictionary(
				static root => root,
				selectedRoots.Contains,
				PathComparer.Default);
		var extensionStates = inheritsAllExtensions
			? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
			: plan.AvailableExtensions.ToDictionary(
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

	internal string BuildText(ProjectSelectionSpec selection)
	{
		var output = new StringBuilder();
		var all = services.Localization["Terminal.Profile.All"];
		output.Append(services.Localization["Terminal.Analysis.Profile"]).Append(": ")
			.AppendLine(TerminalTextEscaping.EscapeSingleLine(FormatProfile(selection.ProfileSource)));
		output.Append(services.Localization["Terminal.Analysis.GitMode"]).Append(": ")
			.AppendLine(selection.GitMode is { } gitMode
				? ProjectSelectionTokens.ToToken(gitMode)
				: ProjectSelectionTokens.ToToken(GitFilteringMode.None));
		output.Append(services.Localization["Terminal.Analysis.Roots"]).Append(": ")
			.AppendLine(selection.Roots is null ? all : JoinEscaped(selection.Roots));
		output.Append(services.Localization["Terminal.Analysis.Extensions"]).Append(": ")
			.AppendLine(selection.Extensions is null ? all : JoinEscaped(selection.Extensions));
		output.Append(services.Localization["Terminal.Profile.SelectedPaths"]).Append(": ")
			.AppendLine(selection.SelectedPaths is { Count: > 0 } selectedPaths
				? JoinEscaped(selectedPaths)
				: all);
		if (selection.Exclusions is { Count: > 0 } exclusions)
		{
			output.Append(services.Localization["Terminal.Analysis.Exclusions"]).Append(": ")
				.AppendLine(string.Join(", ", exclusions.Select(ProjectSelectionTokens.ToToken)));
		}
		output.Append(services.Localization["Settings.Ignore.HideSecrets"]).Append(": ")
			.AppendLine(FormatBoolean(selection.HideSecrets == true));
		output.Append(services.Localization["Settings.Ignore.HidePrivateData"]).Append(": ")
			.AppendLine(FormatBoolean(selection.HidePrivateData == true));
		output.Append(services.Localization["Settings.Ignore.CompressCode"]).Append(": ")
			.AppendLine(FormatBoolean(selection.CompressCode == true));
		output.Append(services.Localization["Settings.Ignore.StripComments"]).Append(": ")
			.AppendLine(FormatBoolean(selection.StripComments == true));
		output.Append(services.Localization["Settings.Ignore.StripBlankLines"]).Append(": ")
			.AppendLine(FormatBoolean(selection.StripBlankLines == true));
		return output.ToString().TrimEnd('\r', '\n');
	}

	private string FormatBoolean(bool value) =>
		services.Localization[value ? "Terminal.Value.Yes" : "Terminal.Value.No"];

	private static string JoinEscaped(IEnumerable<string> values) =>
		string.Join(", ", values.Select(TerminalTextEscaping.EscapeSingleLine));

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
					roots = selection.Roots?.OrderBy(static value => value, PathComparer.Default).ToArray(),
					extensions = selection.Extensions?
						.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
						.ToArray(),
					selectedPaths = (selection.SelectedPaths ?? [])
						.OrderBy(static value => value, PathComparer.Default)
						.ToArray(),
					gitMode = selection.GitMode is { } gitMode
						? ProjectSelectionTokens.ToToken(gitMode)
						: null,
					exclusions = ProjectSelectionTokens.OrderExclusions(selection.Exclusions ?? [])
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
