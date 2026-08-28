using System.CommandLine;
using System.CommandLine.Parsing;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using DevProjex.Terminal.Tui;
using DevProjex.Mcp;

namespace DevProjex.Terminal.CommandLine;

public sealed class DevProjexCommandTree
{
	private static readonly TimeSpan MaximumRequestTimeout = TimeSpan.FromTicks(
		(uint.MaxValue - 1L) * TimeSpan.TicksPerMillisecond);
	private readonly ITerminalEnvironment environment;
	private readonly TerminalServiceFactory _serviceFactory;
	private readonly IDeveloperCommandRunner? _developerCommandRunner;
	private readonly bool _implicitTuiInvocation;
	private readonly LocalizationService _localization;
	private readonly Option<AppLanguage> _language;
	private readonly OutputOptions _output;
	private readonly ITerminalOperationObserver _operationObserver;

	public DevProjexCommandTree(
		ITerminalEnvironment environment,
		TerminalServiceFactory? serviceFactory = null,
		IDeveloperCommandRunner? developerCommandRunner = null,
		bool implicitTuiInvocation = false,
		LocalizationService? localization = null)
		: this(
			environment,
			serviceFactory,
			developerCommandRunner,
			implicitTuiInvocation,
			localization,
			NullTerminalOperationObserver.Instance)
	{
	}

	internal DevProjexCommandTree(
		ITerminalEnvironment environment,
		TerminalServiceFactory? serviceFactory,
		IDeveloperCommandRunner? developerCommandRunner,
		bool implicitTuiInvocation,
		LocalizationService? localization,
		ITerminalOperationObserver operationObserver)
	{
		this.environment = environment ??
			throw new ArgumentNullException(nameof(environment));
		_serviceFactory = serviceFactory ?? new TerminalServiceFactory();
		_developerCommandRunner = developerCommandRunner;
		_implicitTuiInvocation = implicitTuiInvocation;
		_localization = localization ?? new LocalizationService(
			new JsonLocalizationCatalog(),
			AppLanguageUtility.DetectSystemLanguage());
		_language = CreateLanguageOption(_localization, environment);
		_output = new OutputOptions(_localization, environment);
		_operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
	}


	public DevProjexRootCommand Build()
	{
		var root = new DevProjexRootCommand(L("Terminal.Command.Root"));
		var defaultVersionOption = root.Options.OfType<VersionOption>().Single();
		root.Options.Remove(defaultVersionOption);
		root.Options.Add(new VersionOption("--version", "-v"));
		_language.Recursive = true;
		root.Options.Add(_language);
		_output.AddGlobalsTo(root);
		root.Subcommands.Add(BuildTuiCommand());
		root.Subcommands.Add(BuildMcpCommand());
		root.Subcommands.Add(BuildOpenCommand());
		root.Subcommands.Add(BuildAnalyzeCommand());
		root.Subcommands.Add(BuildTreeCommand());
		root.Subcommands.Add(BuildExportCommand());
		root.Subcommands.Add(BuildProfileCommand());
		root.Subcommands.Add(BuildRecentCommand());
		root.Subcommands.Add(BuildCacheCommand());
		root.Subcommands.Add(BuildUiCommand());
		root.Subcommands.Add(BuildDoctorCommand());
		root.Subcommands.Add(BuildHelpCommand(root));
		root.Subcommands.Add(BuildCompletionCommand(root));
		root.Subcommands.Add(BuildDevCommand(root));
		_output.AddValidatorsTo(root);
		CliExamplesRegistry.Set(
			root,
			"devprojex",
			"devprojex analyze .",
			"devprojex export context . -o ../devprojex-context.md");
		return root;
	}

	private Command BuildMcpCommand()
	{
		var command = new Command("mcp", L("Terminal.Command.Mcp"));
		var roots = new Option<string[]>("--root")
		{
			Description = L("Terminal.Option.McpRoot"),
			HelpName = "PATH",
			Arity = ArgumentArity.OneOrMore,
			AllowMultipleArgumentsPerToken = false
		};
		var hidePrivateData = new Option<bool>("--hide-private-data")
		{
			Description = L("Terminal.Option.HidePrivateData")
		};
		roots.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
			context,
			FileSystemCompletionKind.Directories,
			Directory.GetCurrentDirectory()));
		command.Options.Add(roots);
		command.Options.Add(hidePrivateData);
		CliExamplesRegistry.Set(
			command,
			"devprojex mcp",
			"devprojex mcp --root . --root ../shared",
			"devprojex mcp --root . --hide-private-data");
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var explicitRoots = parseResult.GetValue(roots) ?? [];
			var resolvedRoots = McpRootSourceResolver.Resolve(
				explicitRoots,
				environment.Variables,
				Directory.GetCurrentDirectory());
			try
			{
				await McpServerHost.RunAsync(
						resolvedRoots,
						parseResult.GetValue(hidePrivateData),
						cancellationToken)
					.ConfigureAwait(false);
				return CommandLineExitCodes.Success;
			}
			catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
			{
				environment.Error.WriteLine(
					$"error[DPX-MCP-STARTUP]: {TerminalTextEscaping.EscapeSingleLine(exception.Message)}");
				return CommandLineExitCodes.UsageError;
			}
		});
		return command;
	}

	private Command BuildTuiCommand()
	{
		var command = new Command("tui", L("Terminal.Command.Tui"));
		CliExamplesRegistry.Set(
			command,
			"devprojex tui .",
			"devprojex tui . --screen inline");
		var project = ProjectSourceArgument();
		var profile = CliChoiceSymbols.ProfileOption(
			L("Terminal.Option.Profile"),
			"auto",
			_localization,
			allowAuto: true);
		var screen = CliChoiceSymbols.Option(
			"--screen",
			L("Terminal.Option.Screen"),
			TerminalScreenMode.Auto,
			CliChoiceSets.ScreenMode,
			_localization);
		var mouse = new Option<bool>("--mouse") { Description = L("Terminal.Option.Mouse") };
		var noMouse = new Option<bool>("--no-mouse") { Description = L("Terminal.Option.NoMouse") };
		var branch = BranchOption();
		CliHelpMetadataRegistry.SuppressParserDefault(screen);
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(screen);
		command.Options.Add(mouse);
		command.Options.Add(noMouse);
		command.Options.Add(branch);
		CompletionConflictRegistry.RegisterMutual(mouse, noMouse);
		command.Validators.Add(result =>
		{
			if (result.GetValue(mouse) && result.GetValue(noMouse))
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.MouseConflict")));
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			if (!TerminalTuiInteractivityGate.TryEnter(environment, _localization))
				return CommandLineExitCodes.UsageError;

			var output = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				output,
				async () =>
				{
					using var serviceScope = CreateServiceScope(parseResult);
					var services = serviceScope.Services;
					ApplyTuiLanguage(parseResult, services);
					var projectSource = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					await using var resolvedSource = await new TerminalProjectSourceResolver(
							services,
							environment,
							output)
						.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
						.ConfigureAwait(false);
					var projectPath = resolvedSource.ProjectPath;
					var profileValue = parseResult.GetValue(profile);
					var profileReference = profileValue.Resolve(projectPath, services);
					var screenResult = parseResult.GetResult(screen);
					var hasExplicitScreenMode = screenResult is { Implicit: false };
					var screenMode = hasExplicitScreenMode
						? parseResult.GetValue(screen)
						: services.TerminalSettingsStore.LoadScreenMode();
					var workspace = new TerminalWorkspace(
						services,
						environment,
						_operationObserver);
					return await workspace.RunAsync(
						new TerminalWorkspaceOptions(
							projectPath,
							profileReference,
							screenMode,
							MouseMode: parseResult.GetValue(noMouse)
								? TerminalMouseMode.Disabled
								: parseResult.GetValue(mouse)
									? TerminalMouseMode.Enabled
									: TerminalMouseMode.Auto,
							ColorMode: output.Color,
							Plain: output.Plain,
							ShowWelcome: _implicitTuiInvocation),
						cancellationToken).ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildAnalyzeCommand()
	{
		var command = new Command("analyze", L("Terminal.Command.Analyze"));
		CliExamplesRegistry.Set(
			command,
			"devprojex analyze .",
			"devprojex analyze . --format json -o -");
		var project = ProjectSourceArgument();
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		var outputPath = OutputPathOption();
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceContext") };
		var strict = new Option<bool>("--strict") { Description = L("Terminal.Option.Strict") };
		var branch = BranchOption();
		var findings = new Option<bool>("--findings")
		{
			Description = L("Terminal.Option.Findings")
		};
		var failOnFindings = new Option<bool>("--fail-on-findings")
		{
			Description = L("Terminal.Option.FailOnFindings")
		};
		var selection = new SelectionOptions(_localization, environment);
		command.Arguments.Add(project);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(strict);
		command.Options.Add(findings);
		command.Options.Add(failOnFindings);
		command.Options.Add(branch);
		selection.AddTo(command);
		_output.AddProgressTo(command);
		ConfigureFileForce(command, force, outputPath);
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					using var serviceScope = CreateServiceScope(parseResult);
					var services = serviceScope.Services;
					var selectedPaths = await selection.ReadSelectedPathsAsync(
						parseResult,
						cancellationToken).ConfigureAwait(false);
					var projectSource = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					await using var resolvedSource = await new TerminalProjectSourceResolver(
							services,
							environment,
							outputOptions)
						.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
						.ConfigureAwait(false);
					var projectPath = resolvedSource.ProjectPath;
					var spec = await selection.ResolveAsync(
						parseResult,
						projectPath,
						services,
						selectedPaths,
						cancellationToken).ConfigureAwait(false);
					if ((parseResult.GetValue(findings) || parseResult.GetValue(failOnFindings)) &&
					    selection.GetHideSecretsOverride(parseResult) is null)
					{
						spec = spec with { HideSecrets = true };
					}
					return await new AnalyzeCommandHandler(services, environment)
						.ExecuteAsync(
							new AnalyzeCommandRequest(
								projectPath,
								spec,
								parseResult.GetValue(format) switch
								{
									CliTextJsonFormat.Text => AnalysisOutputFormat.Text,
									CliTextJsonFormat.Json => AnalysisOutputFormat.Json,
									_ => throw new ArgumentOutOfRangeException()
								},
								parseResult.GetValue(outputPath),
								parseResult.GetValue(strict),
								outputOptions,
								parseResult.GetValue(findings),
								parseResult.GetValue(failOnFindings),
								Force: parseResult.GetValue(force)),
							cancellationToken)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildExportCommand()
	{
		var command = new Command("export", L("Terminal.Command.Export"));
		CliExamplesRegistry.Set(
			command,
			"devprojex export context .",
			"devprojex export project . --as zip -o ../devprojex-project.zip");
		command.Subcommands.Add(BuildExportContextCommand());
		command.Subcommands.Add(BuildExportProjectCommand());
		SetParentHelpAction(command, "export");
		return command;
	}

	private Command BuildExportContextCommand()
	{
		var command = new Command("context", L("Terminal.Command.ExportContext"));
		command.Aliases.Add("ctx");
		CliExamplesRegistry.Set(
			command,
			"devprojex export context . --view tree-content --format markdown -o ../devprojex-context.md",
			"devprojex export context . --format json -o -",
			"devprojex export context https://github.com/owner/repo -o -");
		var project = ProjectSourceArgument();
		var view = CliChoiceSymbols.Option(
			"--view",
			L("Terminal.Option.View"),
			ProjectContextView.TreeContent,
			CliChoiceSets.ContextView,
			_localization);
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.DocumentFormat"),
			ProjectContextDocumentFormat.Markdown,
			CliChoiceSets.ContextDocumentFormat,
			_localization);
		format.Aliases.Add("-f");
		var outputPath = OutputPathOption();
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceContext") };
		var dryRun = new Option<bool>("--dry-run", "-n") { Description = L("Terminal.Option.DryRun") };
		var branch = BranchOption();
		var selection = new SelectionOptions(_localization, environment);
		command.Arguments.Add(project);
		command.Options.Add(view);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
		command.Options.Add(branch);
		selection.AddTo(command);
		_output.AddProgressTo(command);
		CompletionAvailabilityRegistry.RegisterOption(
			force,
			result =>
				result.GetResult(outputPath) is { Implicit: false } &&
				CliParseValue.TryGet(result, outputPath, out var destination) &&
				destination is not null and not "-");
		command.Validators.Add(result =>
		{
			var outputResult = result.GetResult(outputPath);
			var hasStdoutDestination =
				outputResult is null ||
				(CliParseValue.TryGet(result, outputPath, out var destination) &&
				 destination == "-");
			if (result.GetValue(force) && hasStdoutDestination)
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.ForceRequiresFileOutput")));
			}
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					using var serviceScope = CreateServiceScope(parseResult);
					var services = serviceScope.Services;
					var selectedPaths = await selection.ReadSelectedPathsAsync(
						parseResult,
						cancellationToken).ConfigureAwait(false);
					var projectSource = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					await using var resolvedSource = await new TerminalProjectSourceResolver(
							services,
							environment,
							outputOptions)
						.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
						.ConfigureAwait(false);
					var projectPath = resolvedSource.ProjectPath;
					var spec = await selection.ResolveAsync(
						parseResult,
						projectPath,
						services,
						selectedPaths,
						cancellationToken).ConfigureAwait(false);
					return await new ExportContextCommandHandler(services, environment)
						.ExecuteAsync(
							new ExportContextCommandRequest(
								projectPath,
								spec,
								parseResult.GetValue(view),
								parseResult.GetValue(format),
								parseResult.GetValue(outputPath),
								parseResult.GetValue(force),
								parseResult.GetValue(dryRun),
								outputOptions),
							cancellationToken)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildExportProjectCommand()
	{
		var command = new Command("project", L("Terminal.Command.ExportProject"));
		command.Aliases.Add("proj");
		CliExamplesRegistry.Set(
			command,
			"devprojex export project . --as folder -o ../devprojex-submission",
			"devprojex export project . --as zip -o ../devprojex-submission.zip");
		var project = ProjectSourceArgument();
		var kind = CliChoiceSymbols.RequiredOption(
			"--as",
			L("Terminal.Option.OutputKind"),
			CliChoiceSets.ProjectExportFormat,
			_localization);
		var outputPath = new Option<string>("--output", "-o")
		{
			Description = L("Terminal.Option.ExactDestination"),
			HelpName = "PATH",
			Required = true
		};
		outputPath.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
			context,
			FileSystemCompletionKind.FilesAndDirectories));
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceZip") };
		var dryRun = new Option<bool>("--dry-run", "-n") { Description = L("Terminal.Option.DryRun") };
		var branch = BranchOption();
		var selection = new SelectionOptions(_localization, environment);
		command.Arguments.Add(project);
		command.Options.Add(kind);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
		command.Options.Add(branch);
		selection.AddTo(command);
		_output.AddProgressTo(command);
		CompletionAvailabilityRegistry.RegisterOption(
			force,
			result =>
				CliParseValue.TryGet(result, kind, out var outputKind) &&
				outputKind == ProjectCopyExportFormat.Zip &&
				(!CliParseValue.TryGet(result, outputPath, out var destination) ||
				 destination != "-"));
		command.Validators.Add(result =>
		{
			if (!CliParseValue.TryGet(result, kind, out var outputKind))
				return;
			if (outputKind == ProjectCopyExportFormat.Folder && result.GetValue(force))
			{
				result.AddError(LocalizedParseError.Create(
					"DPX-CLI-FORCE-NOT-SUPPORTED",
					L("Terminal.Error.ForceNotSupported")));
			}
			if (outputKind == ProjectCopyExportFormat.Folder &&
			    CliParseValue.TryGet(result, outputPath, out var folderDestination) &&
			    folderDestination == "-")
			{
				result.AddError(LocalizedParseError.Create(
					"DPX-CLI-FOLDER-STDOUT-NOT-SUPPORTED",
					L("Terminal.Error.FolderStdoutNotSupported")));
			}
			if (result.GetValue(force) &&
			    CliParseValue.TryGet(result, outputPath, out var forcedDestination) &&
			    forcedDestination == "-")
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.ForceRequiresFileOutput")));
			}
			if (outputKind == ProjectCopyExportFormat.Zip &&
			    CliParseValue.TryGet(result, outputPath, out var destination) &&
			    destination is not null &&
			    destination != "-" &&
			    !destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				result.AddError(LocalizedParseError.Create(
					"DPX-CLI-ZIP-EXTENSION-REQUIRED",
					L("Terminal.Error.ZipExtensionRequired")));
			}
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					using var serviceScope = CreateServiceScope(parseResult);
					var services = serviceScope.Services;
					var selectedPaths = await selection.ReadSelectedPathsAsync(
						parseResult,
						cancellationToken).ConfigureAwait(false);
					var projectSource = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					await using var resolvedSource = await new TerminalProjectSourceResolver(
							services,
							environment,
							outputOptions)
						.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
						.ConfigureAwait(false);
					var projectPath = resolvedSource.ProjectPath;
					var spec = await selection.ResolveAsync(
						parseResult,
						projectPath,
						services,
						selectedPaths,
						cancellationToken).ConfigureAwait(false);
					return await new ExportProjectCommandHandler(services, environment)
						.ExecuteAsync(
							new ExportProjectCommandRequest(
								projectPath,
								spec,
								parseResult.GetValue(kind),
								parseResult.GetValue(outputPath)!,
								parseResult.GetValue(force),
								parseResult.GetValue(dryRun),
								outputOptions),
							cancellationToken)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildOpenCommand()
	{
		var command = new Command("open", L("Terminal.Command.Open"));
		CliExamplesRegistry.Set(
			command,
			"devprojex open . --preview",
			"devprojex open --last");
		var project = ProjectSourceArgument();
		var last = new Option<bool>("--last") { Description = L("Terminal.Option.Last") };
		var newWindow = new Option<bool>("--new-window") { Description = L("Terminal.Option.NewWindow") };
		var wait = new Option<bool>("--wait") { Description = L("Terminal.Option.Wait") };
		var preview = new Option<bool>("--preview") { Description = L("Terminal.Option.Preview") };
		var view = CliChoiceSymbols.NullableOption(
			"--view",
			L("Terminal.Option.PreviewView"),
			CliChoiceSets.DesktopView,
			_localization);
		var format = CliChoiceSymbols.NullableOption(
			"--tree-format",
			L("Terminal.Option.TreeFormat"),
			CliChoiceSets.TreeFormat,
			_localization);
		format.Aliases.Add("--format");
		var filter = new Option<string?>("--filter") { Description = L("Terminal.Option.Filter") };
		var search = new Option<string?>("--search") { Description = L("Terminal.Option.Search") };
		filter.HelpName = "QUERY";
		search.HelpName = "QUERY";
		var elevationAttempted = new Option<bool>("--internal-elevation-attempted")
		{
			Hidden = true
		};
		var branch = BranchOption();
		var selection = new SelectionOptions(_localization, environment, "auto");
		command.Arguments.Add(project);
		command.Options.Add(last);
		command.Options.Add(newWindow);
		command.Options.Add(wait);
		command.Options.Add(preview);
		command.Options.Add(view);
		command.Options.Add(format);
		command.Options.Add(filter);
		command.Options.Add(search);
		command.Options.Add(elevationAttempted);
		command.Options.Add(branch);
		selection.AddTo(command);
		CompletionConflictRegistry.RegisterMutual(filter, search);
		foreach (var symbol in selection.AllSymbols)
			CompletionConflictRegistry.RegisterMutual(last, symbol);
		CompletionConflictRegistry.RegisterMutual(last, branch);
		command.Validators.Add(result =>
		{
			if (result.GetValue(last) && result.GetResult(project) is not null)
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.LastProjectConflict")));
			if (result.GetValue(last) &&
			    HasExplicitSelectionOverride(result, selection))
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.LastSelectionConflict")));
			}
			if (result.GetValue(last) &&
			    result.GetResult(branch) is { Implicit: false })
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.LastBranchConflict")));
			}
			if (CliParseValue.TryGet(result, filter, out var filterValue) &&
			    filterValue is not null &&
			    CliParseValue.TryGet(result, search, out var searchValue) &&
			    searchValue is not null)
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.FilterSearchConflict")));
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var useLast = parseResult.GetValue(last);
					var selectedPaths = await selection.ReadSelectedPathsAsync(
						parseResult,
						cancellationToken).ConfigureAwait(false);
					var projectSource = useLast
						? null
						: parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					ProjectSelectionSpec? spec = null;
					using var serviceScope = projectSource is null
						? null
						: CreateServiceScope(parseResult);
					var services = serviceScope?.Services;
					ResolvedTerminalProjectSource? resolvedSource = null;
					if (projectSource is not null)
					{
						resolvedSource = await new TerminalProjectSourceResolver(
								services!,
								environment,
								outputOptions)
							.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
							.ConfigureAwait(false);
					}
					await using var sourceLease = resolvedSource;
					var projectPath = resolvedSource?.ProjectPath;
					if (projectPath is not null)
					{
						spec = await selection.ResolveAsync(
							parseResult,
							projectPath,
							services!,
							selectedPaths,
							cancellationToken).ConfigureAwait(false);
						var readinessExitCode = ValidateDesktopOpenGitReadiness(
							services!,
							projectPath,
							spec,
							cancellationToken);
						if (readinessExitCode is not null)
							return readinessExitCode.Value;
					}
					var viewValue = parseResult.GetValue(view);
					TreeTextFormat? treeFormatValue = parseResult.GetValue(format) is { } requestedTreeFormat
						? ParseTreeFormat(requestedTreeFormat)
						: null;
					return await new DesktopCommandHandler(environment)
						.OpenAsync(
							DesktopOpenRequestFactory.Create(
								projectPath,
								useLast,
								parseResult.GetValue(newWindow),
								parseResult.GetValue(wait),
								parseResult.GetValue(preview),
								viewValue,
								treeFormatValue,
								parseResult.GetValue(filter),
								parseResult.GetValue(search),
								spec,
								parseResult.GetValue(_language),
								parseResult.GetValue(elevationAttempted)),
							cancellationToken,
							resolvedSource is { IsRepositoryUrl: true }
								? resolvedSource.SafeRepositoryUrl
								: null)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildProfileCommand()
	{
		var command = new Command("profile", L("Terminal.Command.Profile"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile export . --profile standard -o ../devprojex-profile.json",
			"devprojex profile validate ../devprojex-profile.json",
			"devprojex profile show . --profile ../devprojex-profile.json --format json");
		command.Subcommands.Add(BuildProfileShowCommand());
		command.Subcommands.Add(BuildProfileExportCommand());
		command.Subcommands.Add(BuildProfileImportCommand());
		command.Subcommands.Add(BuildProfileValidateCommand());
		command.Subcommands.Add(BuildProfileResetCommand());
		command.Subcommands.Add(BuildProfileSaveCommand());
		SetParentHelpAction(command, "profile");
		return command;
	}

	private Command BuildProfileShowCommand()
	{
		var command = new Command("show", L("Terminal.Command.ProfileShow"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile show .",
			"devprojex profile show . --profile ../devprojex-profile.json --format json");
		var project = ProjectArgument();
		var profile = ProfileOption("standard");
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				(services, handler) =>
				{
					var projectPath =
						parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					return handler.ShowAsync(
						projectPath,
						parseResult.GetValue(profile).Resolve(projectPath, services),
						parseResult.GetValue(format) == CliTextJsonFormat.Json,
						cancellationToken);
				}));
		return command;
	}

	private Command BuildProfileExportCommand()
	{
		var command = new Command("export", L("Terminal.Command.ProfileExport"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile export . --profile standard -o ../devprojex-profile.json");
		var project = ProjectArgument();
		var profile = ProfileOption("local");
		var output = new Option<string>("--output", "-o")
		{
			Description = L("Terminal.Option.ProfileDestination"),
			HelpName = "FILE",
			Required = true
		};
		output.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
			context,
			FileSystemCompletionKind.FilesAndDirectories));
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceProfile") };
		var dryRun = new Option<bool>("--dry-run", "-n") { Description = L("Terminal.Option.DryRun") };
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(output);
		command.Options.Add(force);
		command.Options.Add(dryRun);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				(services, handler) =>
				{
					var projectPath =
						parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					return handler.ExportAsync(
						projectPath,
						parseResult.GetValue(profile).Resolve(projectPath, services),
						parseResult.GetValue(output)!,
						parseResult.GetValue(force),
						parseResult.GetValue(dryRun),
						cancellationToken);
				}));
		return command;
	}

	private Command BuildProfileImportCommand()
	{
		var command = new Command("import", L("Terminal.Command.ProfileImport"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile import ../devprojex-profile.json .",
			"devprojex profile import ../devprojex-profile.json . --apply");
		var file = RequiredArgument("FILE");
		var project = ProjectArgument();
		var apply = new Option<bool>("--apply") { Description = L("Terminal.Option.ApplyProfile") };
		command.Arguments.Add(file);
		command.Arguments.Add(project);
		command.Options.Add(apply);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				(_, handler) => handler.ImportAsync(
					parseResult.GetValue(file)!,
					parseResult.GetValue(project) ?? Directory.GetCurrentDirectory(),
					parseResult.GetValue(apply),
					cancellationToken)));
		return command;
	}

	private Command BuildProfileValidateCommand()
	{
		var command = new Command("validate", L("Terminal.Command.ProfileValidate"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile validate ../devprojex-profile.json");
		var file = RequiredArgument("FILE");
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		command.Arguments.Add(file);
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				(_, handler) =>
					handler.ValidateAsync(
						parseResult.GetValue(file)!,
						parseResult.GetValue(format) == CliTextJsonFormat.Json,
						cancellationToken)));
		return command;
	}

	private Command BuildProfileSaveCommand()
	{
		var command = new Command("save", L("Terminal.Command.ProfileSave"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile save . --root src --extension .cs");
		var project = ProjectArgument();
		var selection = new SelectionOptions(_localization, environment);
		command.Arguments.Add(project);
		selection.AddTo(command);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				async (services, handler) =>
				{
					var projectPath = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					var spec = await selection.ResolveAsync(
						parseResult,
						projectPath,
						services,
						cancellationToken).ConfigureAwait(false);
					return await handler.SaveAsync(projectPath, spec, cancellationToken)
						.ConfigureAwait(false);
				}));
		return command;
	}

	private Command BuildProfileResetCommand()
	{
		var command = new Command("reset", L("Terminal.Command.ProfileReset"));
		CliExamplesRegistry.Set(
			command,
			"devprojex profile reset .");
		var project = ProjectArgument();
		command.Arguments.Add(project);
		command.SetAction(parseResult =>
		{
			var output = _output.Get(parseResult);
			return CommandExecution.RunAsync(
					environment,
					output,
					() => RunWithServicesAsync(
						parseResult,
						services => Task.FromResult(
							new ProfileCommandHandler(services, environment)
								.Reset(parseResult.GetValue(project) ?? Directory.GetCurrentDirectory()))),
					_localization)
				.GetAwaiter()
				.GetResult();
		});
		return command;
	}

	private Command BuildUiCommand()
	{
		var command = new Command("ui", L("Terminal.Command.Ui"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui list",
			"devprojex ui status");
		command.Subcommands.Add(BuildUiListCommand());
		command.Subcommands.Add(BuildUiSimpleCommand(
			"status",
			L("Terminal.Command.UiStatus"),
			"status",
			static _ => new { },
			"devprojex ui status --project ."));
		command.Subcommands.Add(BuildUiSimpleCommand(
			"activate",
			L("Terminal.Command.UiActivate"),
			"activate",
			static _ => new { },
			"devprojex ui activate --project ."));
		command.Subcommands.Add(BuildUiPreviewCommand());
		command.Subcommands.Add(BuildUiTreeCommand());
		command.Subcommands.Add(BuildUiFilterCommand());
		command.Subcommands.Add(BuildUiSearchCommand());
		SetParentHelpAction(command, "ui");
		return command;
	}

	private Command BuildUiListCommand()
	{
		var command = new Command("list", L("Terminal.Command.UiList"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui list",
			"devprojex ui list --format json");
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		var timeout = TimeoutOption();
		command.Options.Add(format);
		command.Options.Add(timeout);
		command.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => new DesktopCommandHandler(environment, localization: _localization).ListAsync(
					parseResult.GetValue(format) == CliTextJsonFormat.Json,
					_output.Get(parseResult),
					parseResult.GetValue(timeout),
					cancellationToken),
				_localization));
		return command;
	}

	private Command BuildUiPreviewCommand()
	{
		var command = new Command("preview", L("Terminal.Command.UiPreview"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui preview open --view tree-content --project .",
			"devprojex ui preview close --project .");
		var open = new Command("open", L("Terminal.Command.UiPreviewOpen"));
		CliExamplesRegistry.Set(
			open,
			"devprojex ui preview open --view tree-content --project .");
		var openView = CliChoiceSymbols.NullableOption(
			"--view",
			L("Terminal.Option.PreviewView"),
			CliChoiceSets.DesktopView,
			_localization);
		open.Options.Add(openView);
		AddDesktopAction(
			open,
			"preview.open",
			parseResult => new
			{
				view = parseResult.GetValue(openView) is { } value
					? CliChoiceSets.DesktopView.ToToken(value)
					: null
			});
		command.Subcommands.Add(open);
		command.Subcommands.Add(BuildUiSimpleCommand(
			"close",
			L("Terminal.Command.UiPreviewClose"),
			"preview.close",
			static _ => new { },
			"devprojex ui preview close --project ."));

		var setView = new Command("set-view", L("Terminal.Command.UiPreviewSetView"));
		CliExamplesRegistry.Set(
			setView,
			"devprojex ui preview set-view tree-content --project .");
		var view = CliChoiceSymbols.Argument(
			"VIEW",
			CliChoiceSets.DesktopView,
			_localization);
		setView.Arguments.Add(view);
		AddDesktopAction(
			setView,
			"preview.set-view",
			result => new { view = CliChoiceSets.DesktopView.ToToken(result.GetValue(view)) });
		command.Subcommands.Add(setView);
		SetParentHelpAction(command, "ui", "preview");
		return command;
	}

	private Command BuildUiTreeCommand()
	{
		var command = new Command("tree", L("Terminal.Command.UiTree"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui tree set-format json --project .");
		var setFormat = new Command("set-format", L("Terminal.Command.UiTreeSetFormat"));
		CliExamplesRegistry.Set(
			setFormat,
			"devprojex ui tree set-format json --project .");
		var format = CliChoiceSymbols.Argument(
			"FORMAT",
			CliChoiceSets.TreeFormat,
			_localization);
		setFormat.Arguments.Add(format);
		AddDesktopAction(
			setFormat,
			"tree.set-format",
			result => new { format = CliChoiceSets.TreeFormat.ToToken(result.GetValue(format)) });
		command.Subcommands.Add(setFormat);
		SetParentHelpAction(command, "ui", "tree");
		return command;
	}

	private Command BuildUiFilterCommand()
	{
		var command = new Command("filter", L("Terminal.Command.UiFilter"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui filter set Program --project .",
			"devprojex ui filter clear --project .");
		var set = new Command("set", L("Terminal.Command.UiFilterSet"));
		CliExamplesRegistry.Set(
			set,
			"devprojex ui filter set Program --project .");
		var query = RequiredArgument("QUERY");
		set.Arguments.Add(query);
		AddDesktopAction(set, "filter.set", result => new { query = result.GetValue(query) });
		command.Subcommands.Add(set);
		command.Subcommands.Add(BuildUiSimpleCommand(
			"clear",
			L("Terminal.Command.UiFilterClear"),
			"filter.clear",
			static _ => new { },
			"devprojex ui filter clear --project ."));
		SetParentHelpAction(command, "ui", "filter");
		return command;
	}

	private Command BuildUiSearchCommand()
	{
		var command = new Command("search", L("Terminal.Command.UiSearch"));
		CliExamplesRegistry.Set(
			command,
			"devprojex ui search set TODO --project .",
			"devprojex ui search next --project .",
			"devprojex ui search clear --project .");
		var set = new Command("set", L("Terminal.Command.UiSearchSet"));
		CliExamplesRegistry.Set(
			set,
			"devprojex ui search set TODO --project .");
		var query = RequiredArgument("QUERY");
		set.Arguments.Add(query);
		AddDesktopAction(set, "search.set", result => new { query = result.GetValue(query) });
		command.Subcommands.Add(set);
		command.Subcommands.Add(BuildUiSimpleCommand(
			"next",
			L("Terminal.Command.UiSearchNext"),
			"search.next",
			static _ => new { },
			"devprojex ui search next --project ."));
		command.Subcommands.Add(BuildUiSimpleCommand(
			"previous",
			L("Terminal.Command.UiSearchPrevious"),
			"search.previous",
			static _ => new { },
			"devprojex ui search previous --project ."));
		command.Subcommands.Add(BuildUiSimpleCommand(
			"clear",
			L("Terminal.Command.UiSearchClear"),
			"search.clear",
			static _ => new { },
			"devprojex ui search clear --project ."));
		SetParentHelpAction(command, "ui", "search");
		return command;
	}

	private Command BuildUiSimpleCommand(
		string name,
		string description,
		string action,
		Func<ParseResult, object> payload,
		params string[] examples)
	{
		var command = new Command(name, description);
		CliExamplesRegistry.Set(command, examples);
		AddDesktopAction(command, action, payload);
		return command;
	}

	private void AddDesktopAction(
		Command command,
		string action,
		Func<ParseResult, object> payload)
	{
		var instance = new Option<string?>("--instance") { Description = L("Terminal.Option.Instance") };
		var project = new Option<string?>("--project") { Description = L("Terminal.Option.TargetProject") };
		var timeout = TimeoutOption();
		instance.HelpName = "ID";
		project.HelpName = "PATH";
		project.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
			context,
			FileSystemCompletionKind.Directories));
		command.Options.Add(instance);
		command.Options.Add(project);
		command.Options.Add(timeout);
		command.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => new DesktopCommandHandler(environment).SendAsync(
					new DesktopTarget(
						parseResult.GetValue(instance),
						parseResult.GetValue(project),
						parseResult.GetValue(timeout)),
					action,
					payload(parseResult),
					cancellationToken),
				_localization));
	}

	private Command BuildDoctorCommand()
	{
		var command = new Command("doctor", L("Terminal.Command.Doctor"));
		CliExamplesRegistry.Set(
			command,
			"devprojex doctor",
			"devprojex doctor --format json");
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => new DoctorCommandHandler(services, environment)
						.ExecuteAsync(
							parseResult.GetValue(format) == CliTextJsonFormat.Json,
							cancellationToken)),
				_localization));
		return command;
	}

	private Command BuildRecentCommand()
	{
		var command = new Command("recent", L("Terminal.Command.Recent"));
		CliExamplesRegistry.Set(
			command,
			"devprojex recent",
			"devprojex recent --kind repository --format json");
		var kind = CliChoiceSymbols.Option(
			"--kind",
			L("Terminal.Option.RecentKind"),
			CliRecentKind.All,
			CliChoiceSets.RecentKind,
			_localization);
		var limit = new Option<int>("--limit")
		{
			Description = L("Terminal.Option.Limit"),
			HelpName = "N",
			DefaultValueFactory = _ => 48
		};
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		command.Options.Add(kind);
		command.Options.Add(limit);
		command.Options.Add(format);
		command.Validators.Add(result =>
		{
			if (CliParseValue.TryGet(result, limit, out var value) && value is < 1 or > 100_000)
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.Limit")));
			}
		});
		command.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => Task.FromResult(new RecentCommandHandler(
							services,
							environment)
						.Execute(
							parseResult.GetValue(kind),
							parseResult.GetValue(limit),
							parseResult.GetValue(format),
							_output.Get(parseResult)))),
				_localization));
		return command;
	}

	private Command BuildCacheCommand()
	{
		var command = new Command("cache", L("Terminal.Command.Cache"));
		CliExamplesRegistry.Set(
			command,
			"devprojex cache path",
			"devprojex cache list --format json",
			"devprojex cache remove https://github.com/owner/repo --force");

		var path = new Command("path", L("Terminal.Command.CachePath"));
		CliExamplesRegistry.Set(
			path,
			"devprojex cache path");
		path.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => Task.FromResult(new CacheCommandHandler(
							services,
							environment)
						.WritePath())),
				_localization));

		var list = new Command("list", L("Terminal.Command.CacheList"));
		CliExamplesRegistry.Set(
			list,
			"devprojex cache list",
			"devprojex cache list --format json");
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		format.Aliases.Add("-f");
		list.Options.Add(format);
		list.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => Task.FromResult(new CacheCommandHandler(
							services,
							environment)
						.WriteList(parseResult.GetValue(format), _output.Get(parseResult)))),
				_localization));

		var remove = new Command("remove", L("Terminal.Command.CacheRemove"));
		CliExamplesRegistry.Set(
			remove,
			"devprojex cache remove https://github.com/owner/repo --force");
		var repositoryUrl = new Argument<string>("URL")
		{
			Description = L("Terminal.Argument.RepositoryUrl"),
			Arity = ArgumentArity.ExactlyOne
		};
		var removeForce = new Option<bool>("--force", "-y", "--yes")
		{
			Description = L("Terminal.Option.CacheForce")
		};
		var removeDryRun = new Option<bool>("--dry-run", "-n")
		{
			Description = L("Terminal.Option.DryRun")
		};
		var removeFormat = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		removeFormat.Aliases.Add("-f");
		remove.Arguments.Add(repositoryUrl);
		remove.Options.Add(removeForce);
		remove.Options.Add(removeDryRun);
		remove.Options.Add(removeFormat);
		RequireForce(remove, removeForce, removeDryRun);
		remove.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => Task.FromResult(new CacheCommandHandler(
							services,
							environment)
						.Remove(
							parseResult.GetValue(repositoryUrl)!,
							parseResult.GetValue(removeFormat),
							parseResult.GetValue(removeDryRun)))),
				_localization));

		var clear = new Command("clear", L("Terminal.Command.CacheClear"));
		CliExamplesRegistry.Set(
			clear,
			"devprojex cache clear --force");
		var clearForce = new Option<bool>("--force", "-y", "--yes")
		{
			Description = L("Terminal.Option.CacheForce")
		};
		var clearDryRun = new Option<bool>("--dry-run", "-n")
		{
			Description = L("Terminal.Option.DryRun")
		};
		var clearFormat = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.Format"),
			CliTextJsonFormat.Text,
			CliChoiceSets.TextJson,
			_localization);
		clearFormat.Aliases.Add("-f");
		clear.Options.Add(clearForce);
		clear.Options.Add(clearDryRun);
		clear.Options.Add(clearFormat);
		RequireForce(clear, clearForce, clearDryRun);
		clear.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => Task.FromResult(new CacheCommandHandler(
							services,
							environment)
						.Clear(
							parseResult.GetValue(clearFormat),
							parseResult.GetValue(clearDryRun)))),
				_localization));

		var update = new Command("update", L("Terminal.Command.CacheUpdate"));
		CliExamplesRegistry.Set(
			update,
			"devprojex cache update https://github.com/owner/repo");
		var updateRepositoryUrl = new Argument<string>("URL")
		{
			Description = L("Terminal.Argument.RepositoryUrl"),
			Arity = ArgumentArity.ExactlyOne
		};
		update.Arguments.Add(updateRepositoryUrl);
		update.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				_output.Get(parseResult),
				() => RunWithServicesAsync(
					parseResult,
					services => new CacheCommandHandler(services, environment)
						.UpdateAsync(parseResult.GetValue(updateRepositoryUrl)!, cancellationToken)),
				_localization));

		command.Subcommands.Add(path);
		command.Subcommands.Add(list);
		command.Subcommands.Add(remove);
		command.Subcommands.Add(clear);
		command.Subcommands.Add(update);
		SetParentHelpAction(command, "cache");
		return command;
	}

	private void RequireForce(
		Command command,
		Option<bool> force,
		Option<bool> dryRun)
	{
		CliHelpMetadataRegistry.MarkRequired(force);
		command.Validators.Add(result =>
		{
			if (!result.GetValue(force) && !result.GetValue(dryRun))
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.CacheForceRequired")));
			}
		});
	}

	private Command BuildTreeCommand()
	{
		var command = new Command("tree", L("Terminal.Command.Tree"));
		CliExamplesRegistry.Set(
			command,
			"devprojex tree .",
			"devprojex tree . --format json -o -");
		var project = ProjectSourceArgument();
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.TreeFormat"),
			ProjectContextDocumentFormat.Text,
			CliChoiceSets.TreeFormat,
			_localization);
		format.Aliases.Add("-f");
		var outputPath = OutputPathOption();
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceContext") };
		var branch = BranchOption();
		var selection = new SelectionOptions(
			_localization,
			environment,
			includeContentTransformations: false);
		command.Arguments.Add(project);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(branch);
		selection.AddTo(command);
		_output.AddProgressTo(command);
		ConfigureFileForce(command, force, outputPath);
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = _output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					using var serviceScope = CreateServiceScope(parseResult);
					var services = serviceScope.Services;
					var projectSource = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					await using var resolvedSource = await new TerminalProjectSourceResolver(
							services,
							environment,
							outputOptions)
						.ResolveAsync(projectSource, parseResult.GetValue(branch), cancellationToken)
						.ConfigureAwait(false);
					var projectPath = resolvedSource.ProjectPath;
					var spec = await selection.ResolveAsync(
						parseResult,
						projectPath,
						services,
						cancellationToken).ConfigureAwait(false);
					return await new TreeCommandHandler(services, environment)
						.ExecuteAsync(
							new TreeCommandRequest(
								projectPath,
								spec,
								ParseTreeFormat(parseResult.GetValue(format)),
								parseResult.GetValue(outputPath),
								outputOptions,
								Force: parseResult.GetValue(force)),
							cancellationToken)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildHelpCommand(RootCommand root)
	{
		var command = new Command("help", L("Terminal.Command.Help"));
		CliExamplesRegistry.Set(
			command,
			"devprojex help",
			"devprojex help export context");
		var commandPath = new Argument<string[]>("COMMAND")
		{
			Description = L("Terminal.Argument.HelpCommand"),
			HelpName = "COMMAND",
			Arity = ArgumentArity.ZeroOrMore
		};
		commandPath.CompletionSources.Add(context => CompleteHelpCommandPath(
			root,
			commandPath,
			context));
		command.Arguments.Add(commandPath);
		command.SetAction(parseResult =>
		{
			var requestedPath = parseResult.GetValue(commandPath) ?? [];
			if (!TryResolveCommand(root, requestedPath, out var target, out var canonicalPath))
			{
				var unknown = requestedPath.Length == 0
					? string.Empty
					: requestedPath[^1];
				environment.Error.WriteLine(
					$"error[DPX-CLI-UNKNOWN-COMMAND]: " +
					_localization.Format("Terminal.Error.UnknownCommand", unknown));
				environment.Error.WriteLine(_localization["Terminal.Hint.Help"]);
				return CommandLineExitCodes.UsageError;
			}

			new CommandHelpRenderer(environment, _localization).Write(target, canonicalPath);
			return CommandLineExitCodes.Success;
		});
		return command;
	}

	private Command BuildCompletionCommand(RootCommand root)
	{
		var command = new Command("completion", L("Terminal.Command.Completion"));
		CliExamplesRegistry.Set(
			command,
			"devprojex completion powershell",
			"devprojex completion bash");
		var shell = CliChoiceSymbols.Argument(
			"SHELL",
			CliChoiceSets.CompletionShell,
			_localization);
		command.Arguments.Add(shell);
		command.SetAction(parseResult =>
		{
			environment.Output.Write(CompletionScriptGenerator.Generate(
				root,
				CliChoiceSets.CompletionShell.ToToken(parseResult.GetValue(shell))));
			return CommandLineExitCodes.Success;
		});
		return command;
	}

	private Command BuildDevCommand(RootCommand root)
	{
		var command = new Command("dev", L("Terminal.Command.Dev"))
		{
			Hidden = true
		};
		var benchmark = new Command("benchmark", L("Terminal.Command.DevBenchmark"));
		var analysis = new Command("analysis", L("Terminal.Command.DevBenchmarkAnalysis"));
		var analysisProject = ProjectArgument();
		var analysisOutput = OptionalOutputPath();
		analysis.Arguments.Add(analysisProject);
		analysis.Options.Add(analysisOutput);
		analysis.SetAction((parseResult, cancellationToken) => RunDeveloperCommandAsync(
			new DeveloperCommandRequest(
				DeveloperCommandKind.AnalysisBenchmark,
				parseResult.GetValue(analysisProject) ?? Directory.GetCurrentDirectory(),
				parseResult.GetValue(analysisOutput)),
			cancellationToken));
		var ui = new Command("ui", L("Terminal.Command.DevBenchmarkUi"));
		var uiProject = ProjectArgument();
		var uiOutput = OptionalOutputPath();
		ui.Arguments.Add(uiProject);
		ui.Options.Add(uiOutput);
		ui.SetAction((parseResult, cancellationToken) => RunDeveloperCommandAsync(
			new DeveloperCommandRequest(
				DeveloperCommandKind.UiBenchmark,
				parseResult.GetValue(uiProject) ?? Directory.GetCurrentDirectory(),
				parseResult.GetValue(uiOutput)),
			cancellationToken));
		benchmark.Subcommands.Add(analysis);
		benchmark.Subcommands.Add(ui);
		SetParentHelpAction(benchmark, "dev", "benchmark");
		command.Subcommands.Add(benchmark);
		var session = new Command("session", L("Terminal.Command.DevSession"));
		var sessionProject = ProjectArgument();
		var sessionOutput = OptionalOutputPath();
		var scenario = CliChoiceSymbols.Option(
			"--scenario",
			L("Terminal.Option.Scenario"),
			CliDeveloperScenario.Standard,
			CliChoiceSets.DeveloperScenario,
			_localization);
		session.Arguments.Add(sessionProject);
		session.Options.Add(sessionOutput);
		session.Options.Add(scenario);
		session.SetAction((parseResult, cancellationToken) => RunDeveloperCommandAsync(
			new DeveloperCommandRequest(
				DeveloperCommandKind.Session,
				parseResult.GetValue(sessionProject) ?? Directory.GetCurrentDirectory(),
				parseResult.GetValue(sessionOutput),
				CliChoiceSets.DeveloperScenario.ToToken(parseResult.GetValue(scenario))),
			cancellationToken));
		command.Subcommands.Add(session);
		var complete = new Command("complete")
		{
			Hidden = true
		};
		var position = new Option<int>("--position")
		{
			HelpName = "OFFSET",
			Required = true
		};
		var positionUnit = new Option<string?>("--position-unit")
		{
			Hidden = true
		};
		var commandLine = new Argument<string>("COMMAND_LINE")
		{
			Arity = ArgumentArity.ExactlyOne
		};
		var base64 = new Option<bool>("--base64")
		{
			Hidden = true
		};
		var nullDelimited = new Option<bool>("--null")
		{
			Hidden = true
		};
		var workingDirectoryBase64 = new Option<string?>("--working-directory-base64")
		{
			Hidden = true
		};
		var bashCurrentWord = new Option<string?>("--bash-current-word")
		{
			Hidden = true,
			Arity = ArgumentArity.ZeroOrOne
		};
		complete.Options.Add(position);
		complete.Options.Add(positionUnit);
		complete.Options.Add(base64);
		complete.Options.Add(nullDelimited);
		complete.Options.Add(workingDirectoryBase64);
		complete.Options.Add(bashCurrentWord);
		complete.Arguments.Add(commandLine);
		complete.SetAction(parseResult =>
		{
			var useBase64Transport = parseResult.GetValue(base64);
			var useNullDelimitedTransport = parseResult.GetValue(nullDelimited);
			if (useBase64Transport && useNullDelimitedTransport)
			{
				environment.Error.WriteLine("error[DPX-CLI-INVALID-SYNTAX]:");
				environment.Error.WriteLine(L("Terminal.Error.ParserRejected"));
				return CommandLineExitCodes.UsageError;
			}
			var completionCommandLine =
				parseResult.GetValue(commandLine) ?? string.Empty;
			if (useBase64Transport &&
			    !CompletionCommandLineTransport.TryDecodeBase64(
				    completionCommandLine,
				    out completionCommandLine))
			{
				environment.Error.WriteLine("error[DPX-CLI-INVALID-SYNTAX]:");
				environment.Error.WriteLine(L("Terminal.Error.ParserRejected"));
				return CommandLineExitCodes.UsageError;
			}
			if (!CompletionCursorPositionNormalizer.TryNormalize(
				    completionCommandLine,
				    parseResult.GetValue(position),
				    parseResult.GetValue(positionUnit),
				    out var completionPosition))
			{
				environment.Error.WriteLine("error[DPX-CLI-INVALID-SYNTAX]:");
				environment.Error.WriteLine(L("Terminal.Error.ParserRejected"));
				return CommandLineExitCodes.UsageError;
			}

			string? completionWorkingDirectory = null;
			var encodedWorkingDirectory = parseResult.GetValue(workingDirectoryBase64);
			if (encodedWorkingDirectory is not null &&
			    !CompletionCommandLineTransport.TryDecodeBase64(
				    encodedWorkingDirectory,
				    out completionWorkingDirectory))
			{
				environment.Error.WriteLine("error[DPX-CLI-INVALID-SYNTAX]:");
				environment.Error.WriteLine(L("Terminal.Error.ParserRejected"));
				return CommandLineExitCodes.UsageError;
			}

			foreach (var candidate in ContextAwareCompletionEngine.Complete(
				         root,
				         completionCommandLine,
				         completionPosition,
				         completionWorkingDirectory,
				         parseResult.GetValue(bashCurrentWord),
				         parseResult.GetResult(bashCurrentWord) is not null))
			{
				if (useBase64Transport)
				{
					environment.Output.WriteLine(
						CompletionCommandLineTransport.EncodeBase64(candidate));
				}
				else if (useNullDelimitedTransport)
				{
					environment.Output.Write(candidate);
					environment.Output.Write('\0');
				}
				else
				{
					environment.Output.WriteLine(candidate);
				}
			}
			return CommandLineExitCodes.Success;
		});
		command.Subcommands.Add(complete);
		SetParentHelpAction(command, "dev");
		return command;
	}

	private Task<int> RunDeveloperCommandAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken)
	{
		if (_developerCommandRunner is null)
		{
			environment.Error.WriteLine("error[DPX-DEV-RUNNER-UNAVAILABLE]:");
			environment.Error.WriteLine(L("Terminal.Error.DevRunnerUnavailable"));
			return Task.FromResult(CommandLineExitCodes.RuntimeError);
		}

		return _developerCommandRunner.RunAsync(request, cancellationToken);
	}

	private Task<int> RunProfileAsync(
		ParseResult parseResult,
		Func<TerminalServices, ProfileCommandHandler, Task<int>> operation)
	{
		return CommandExecution.RunAsync(
			environment,
			_output.Get(parseResult),
			() => RunWithServicesAsync(
				parseResult,
				services => operation(services, new ProfileCommandHandler(services, environment))),
			_localization);
	}

	private async Task<int> RunWithServicesAsync(
		ParseResult parseResult,
		Func<TerminalServices, Task<int>> operation)
	{
		using var serviceScope = CreateServiceScope(parseResult);
		return await operation(serviceScope.Services).ConfigureAwait(false);
	}

	private TerminalServiceScope CreateServiceScope(ParseResult parseResult) =>
		_serviceFactory.CreateScope(parseResult.GetValue(_language));

	private void ApplyTuiLanguage(ParseResult parseResult, TerminalServices services)
	{
		var commandLineLanguage = parseResult.GetValue(_language);
		var explicitLanguage = parseResult.GetResult(_language) is { Implicit: false }
			? commandLineLanguage
			: (AppLanguage?)null;
		var language = TerminalWorkspaceLanguagePolicy.Resolve(
			commandLineLanguage,
			explicitLanguage,
			services.TerminalSettingsStore.LoadLanguage());
		_localization.SetLanguage(language);
		services.Localization.SetLanguage(language);
	}

	private Argument<string?> ProjectArgument() =>
		new("PROJECT")
		{
			Description = L("Terminal.Argument.Project"),
			HelpName = "PROJECT",
			Arity = ArgumentArity.ZeroOrOne,
			DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
			CompletionSources =
			{
				context => FileSystemCompletionSource.Complete(
					context,
					FileSystemCompletionKind.Directories)
			}
		};

	private Argument<string?> ProjectSourceArgument() =>
		new("PROJECT")
		{
			Description = L("Terminal.Argument.ProjectSource"),
			HelpName = "PROJECT",
			Arity = ArgumentArity.ZeroOrOne,
			DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
			CompletionSources =
			{
				context => FileSystemCompletionSource.Complete(
					context,
					FileSystemCompletionKind.Directories)
			}
		};

	private Option<string?> BranchOption() =>
		new("--branch", "-b")
		{
			Description = L("Terminal.Option.Branch"),
			HelpName = "NAME"
		};

	private Option<TimeSpan> TimeoutOption()
	{
		var option = new Option<TimeSpan>("--timeout")
		{
			Description = L("Terminal.Option.Timeout"),
			HelpName = "DURATION",
			DefaultValueFactory = _ => TimeSpan.FromSeconds(10),
			CustomParser = result => ParseDuration(result, _localization)
		};
		CliHelpMetadataRegistry.SetDefaultDisplay(option, "10s");
		return option;
	}

	private void ConfigureFileForce(
		Command command,
		Option<bool> force,
		Option<string?> outputPath)
	{
		CompletionAvailabilityRegistry.RegisterOption(
			force,
			result =>
				result.GetResult(outputPath) is { Implicit: false } &&
				CliParseValue.TryGet(result, outputPath, out var destination) &&
				destination is not null and not "-");
		command.Validators.Add(result =>
		{
			var hasFileDestination =
				result.GetResult(outputPath) is { Implicit: false } &&
				CliParseValue.TryGet(result, outputPath, out var destination) &&
				destination is not null and not "-";
			if (result.GetValue(force) && !hasFileDestination)
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.ForceRequiresFileOutput")));
			}
		});
	}

	private static Argument<string> RequiredArgument(string name)
	{
		var argument = new Argument<string>(name)
		{
			HelpName = name,
			Arity = ArgumentArity.ExactlyOne
		};
		if (name == "FILE")
		{
			argument.CompletionSources.Add(context => FileSystemCompletionSource.Complete(
				context,
				FileSystemCompletionKind.FilesAndDirectories));
		}
		return argument;
	}

	private Option<string?> OutputPathOption() =>
		new("--output", "-o")
		{
			Description = L("Terminal.Option.Output"),
			HelpName = "PATH|-",
			DefaultValueFactory = _ => "-",
			CompletionSources =
			{
				context => FileSystemCompletionSource.Complete(
					context,
					FileSystemCompletionKind.FilesAndDirectories)
			}
		};

	private Option<string?> OptionalOutputPath() =>
		new("--output", "-o")
		{
			Description = L("Terminal.Option.ReportOutput"),
			HelpName = "PATH",
			CompletionSources =
			{
				context => FileSystemCompletionSource.Complete(
					context,
					FileSystemCompletionKind.FilesAndDirectories)
			}
		};

	private Option<CliProfileValue> ProfileOption(string defaultValue) =>
		CliChoiceSymbols.ProfileOption(
			L("Terminal.Option.Profile"),
			defaultValue,
			_localization);

	private static Option<AppLanguage> CreateLanguageOption(
		LocalizationService localization,
		ITerminalEnvironment environment)
	{
		var option = CliChoiceSymbols.Option(
			"--language",
			localization["Terminal.Option.Language"],
			TerminalLanguageResolver.Resolve([], environment.Variables),
			CliChoiceSets.Language,
			localization);
		option.HelpName = "CODE";
		return option;
	}

	private void SetParentHelpAction(Command command, params string[] path) =>
		command.SetAction(_ =>
		{
			new CommandHelpRenderer(environment, _localization).Write(
				command,
				new[] { "devprojex" }.Concat(path).ToArray());
			return CommandLineExitCodes.Success;
		});

	private static bool TryResolveCommand(
		RootCommand root,
		IReadOnlyList<string> requestedPath,
		out Command command,
		out IReadOnlyList<string> canonicalPath)
	{
		command = root;
		var path = new List<string> { "devprojex" };
		foreach (var token in requestedPath)
		{
			var child = command.Subcommands.FirstOrDefault(candidate =>
				!candidate.Hidden &&
				(candidate.Name.Equals(token, StringComparison.Ordinal) ||
				 candidate.Aliases.Contains(token, StringComparer.Ordinal)));
			if (child is null)
			{
				canonicalPath = [];
				return false;
			}

			command = child;
			path.Add(child.Name);
		}

		canonicalPath = path;
		return true;
	}

	private static string[] CompleteHelpCommandPath(
		RootCommand root,
		Argument<string[]> commandPath,
		System.CommandLine.Completions.CompletionContext context)
	{
		var requestedPath = context.ParseResult.GetValue(commandPath)?.ToList() ?? [];
		var word = context.WordToComplete ?? string.Empty;
		if (word.Length > 0 &&
		    requestedPath.Count > 0 &&
		    string.Equals(requestedPath[^1], word, StringComparison.Ordinal))
		{
			requestedPath.RemoveAt(requestedPath.Count - 1);
		}
		if (!TryResolveCommand(root, requestedPath, out var parent, out _))
			return [];

		return parent.Subcommands
			.Where(static candidate => !candidate.Hidden)
			.SelectMany(static candidate => new[] { candidate.Name }.Concat(candidate.Aliases))
			.Where(candidate => candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(static candidate => candidate, StringComparer.Ordinal)
			.ToArray();
	}

	private static bool HasExplicitSelectionOverride(
		CommandResult result,
		SelectionOptions selection) =>
		selection.AllSymbols.Any(symbol =>
			result.GetResult(symbol) is { Implicit: false });

	private int? ValidateDesktopOpenGitReadiness(
		TerminalServices services,
		string projectPath,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		if (selection.GitMode != GitFilteringMode.TrackedFilesOnly)
			return null;

		var loaded = services.AnalysisService.Load(
			new ProjectAnalysisRequest(
				projectPath,
				selection.Roots,
				selection.Extensions,
				ProjectSelectionAdapter.ToIgnoreOptions(selection)),
			cancellationToken);
		var readiness = ProjectContextGitReadiness.Evaluate(
			GitFilteringMode.TrackedFilesOnly,
			loaded.DiscoveredGitTrackedIndexCount,
			loaded.UnavailableGitTrackedIndexCount);
		if (readiness.CreateDiagnostic(PathUtility.Normalize(projectPath)) is not { } diagnostic)
			return null;

		new ContextDiagnosticRenderer(
			environment,
			new TerminalOutputOptions(),
			services.Localization).Write([diagnostic]);
		return diagnostic.Severity == ContextDiagnosticSeverity.Error
			? CommandLineExitCodes.PolicyFailure
			: null;
	}

	private static TreeTextFormat ParseTreeFormat(ProjectContextDocumentFormat value) => value switch
	{
		ProjectContextDocumentFormat.Text => TreeTextFormat.Ascii,
		ProjectContextDocumentFormat.Markdown => TreeTextFormat.Markdown,
		ProjectContextDocumentFormat.Json => TreeTextFormat.Json,
		ProjectContextDocumentFormat.Xml => TreeTextFormat.Xml,
		_ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
	};

	private static TimeSpan ParseDuration(
		ArgumentResult result,
		LocalizationService localization)
	{
		if (result.Tokens.Count == 1 &&
		    TryParseDurationToken(result.Tokens[0].Value, out var duration))
		{
			return duration;
		}

		result.AddError(LocalizedParseError.Create(localization["Terminal.Validation.Timeout"]));
		return default;
	}

	private static bool TryParseDurationToken(string value, out TimeSpan duration)
	{
		if (TimeSpan.TryParse(
			    value,
			    System.Globalization.CultureInfo.InvariantCulture,
			    out duration))
		{
			return IsSupportedRequestTimeout(duration);
		}
		if (value.EndsWith('s') &&
		    double.TryParse(value[..^1], System.Globalization.NumberStyles.Number,
			    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
		{
			return TryCreateDuration(seconds, out duration);
		}
		if (value.EndsWith('m') &&
		    double.TryParse(value[..^1], System.Globalization.NumberStyles.Number,
			    System.Globalization.CultureInfo.InvariantCulture, out var minutes))
		{
			return TryCreateDuration(minutes * 60d, out duration);
		}
		return false;
	}

	private static bool TryCreateDuration(double seconds, out TimeSpan duration)
	{
		duration = default;
		if (!double.IsFinite(seconds) ||
		    seconds <= 0 ||
		    seconds > TimeSpan.MaxValue.TotalSeconds)
		{
			return false;
		}

		try
		{
			duration = TimeSpan.FromSeconds(seconds);
			return IsSupportedRequestTimeout(duration);
		}
		catch (ArgumentException)
		{
			duration = default;
			return false;
		}
		catch (OverflowException)
		{
			duration = default;
			return false;
		}
	}

	private static bool IsSupportedRequestTimeout(TimeSpan duration) =>
		duration > TimeSpan.Zero && duration <= MaximumRequestTimeout;

	private string L(string key) => _localization[key];
}
