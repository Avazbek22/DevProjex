using System.CommandLine;
using System.CommandLine.Parsing;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;
using DevProjex.Terminal.Tui;

namespace DevProjex.Terminal.CommandLine;

public sealed class DevProjexCommandTree
{
	private readonly ITerminalEnvironment environment;
	private readonly TerminalServiceFactory _serviceFactory;
	private readonly IDeveloperCommandRunner? _developerCommandRunner;
	private readonly bool _implicitTuiInvocation;
	private readonly LocalizationService _localization;
	private readonly Option<AppLanguage> _language;
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
		_language = CreateLanguageOption(localization);
		_operationObserver = operationObserver ??
			throw new ArgumentNullException(nameof(operationObserver));
	}


	public RootCommand Build()
	{
		var root = new RootCommand(L("Terminal.Command.Root"));
		var defaultVersionOption = root.Options.OfType<VersionOption>().Single();
		root.Options.Remove(defaultVersionOption);
		root.Options.Add(new VersionOption("--version", "-v"));
		_language.Recursive = true;
		root.Options.Add(_language);
		root.Subcommands.Add(BuildTuiCommand());
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
		CliExamplesRegistry.Set(
			root,
			"devprojex",
			"devprojex analyze .",
			"devprojex export context . -o ../devprojex-context.md");
		return root;
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
		var color = CliChoiceSymbols.Option(
			"--color",
			L("Terminal.Option.Color"),
			TerminalColorMode.Auto,
			CliChoiceSets.ColorMode,
			_localization);
		var plain = new Option<bool>("--plain") { Description = L("Terminal.Option.Plain") };
		var branch = BranchOption();
		CliHelpMetadataRegistry.SuppressParserDefault(screen);
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(screen);
		command.Options.Add(mouse);
		command.Options.Add(noMouse);
		command.Options.Add(color);
		command.Options.Add(plain);
		command.Options.Add(branch);
		CompletionConflictRegistry.RegisterMutual(mouse, noMouse);
		CompletionAvailabilityRegistry.RegisterOption(
			plain,
			result =>
				!CliParseValue.TryGet(result, color, out var colorValue) ||
				colorValue != TerminalColorMode.Always);
		CompletionAvailabilityRegistry.RegisterValue(
			color,
			(result, value) =>
				!value.Equals("always", StringComparison.Ordinal) ||
				!CliParseValue.TryGet(result, plain, out var plainValue) ||
				!plainValue);
		command.Validators.Add(result =>
		{
			if (result.GetValue(mouse) && result.GetValue(noMouse))
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.MouseConflict")));
			if (CliParseValue.TryGet(result, plain, out var plainValue) &&
			    plainValue &&
			    CliParseValue.TryGet(result, color, out var colorValue) &&
			    colorValue == TerminalColorMode.Always)
			{
				result.AddError(LocalizedParseError.Create(
					L("Terminal.Validation.PlainColorConflict")));
			}
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			if (!TerminalTuiInteractivityGate.TryEnter(environment, _localization))
				return CommandLineExitCodes.UsageError;

			var output = new TerminalOutputOptions(
				parseResult.GetValue(color),
				Plain: parseResult.GetValue(plain));
			return await CommandExecution.RunAsync(
				environment,
				output,
				async () =>
				{
					var services = CreateServices(parseResult);
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
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(strict);
		command.Options.Add(findings);
		command.Options.Add(failOnFindings);
		command.Options.Add(branch);
		selection.AddTo(command);
		output.AddTo(command);
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var services = CreateServices(parseResult);
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
								parseResult.GetValue(failOnFindings)),
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
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(view);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
		command.Options.Add(branch);
		selection.AddTo(command);
		output.AddTo(command);
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
			var outputOptions = output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var services = CreateServices(parseResult);
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
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(kind);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
		command.Options.Add(branch);
		selection.AddTo(command);
		output.AddTo(command);
		CompletionAvailabilityRegistry.RegisterOption(
			force,
			result =>
				CliParseValue.TryGet(result, kind, out var outputKind) &&
				outputKind == ProjectCopyExportFormat.Zip &&
				(!CliParseValue.TryGet(result, outputPath, out var destination) ||
				 destination != "-"));
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var services = CreateServices(parseResult);
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
		CompletionConflictRegistry.RegisterMutual(last, selection.Profile);
		CompletionConflictRegistry.RegisterMutual(last, selection.Roots);
		CompletionConflictRegistry.RegisterMutual(last, selection.Extensions);
		CompletionConflictRegistry.RegisterMutual(last, selection.SelectedPaths);
		CompletionConflictRegistry.RegisterMutual(last, selection.SelectedPathsSource);
		CompletionConflictRegistry.RegisterMutual(last, selection.GitMode);
		CompletionConflictRegistry.RegisterMutual(last, selection.Exclusions);
		CompletionConflictRegistry.RegisterMutual(last, selection.HideSecrets);
		CompletionConflictRegistry.RegisterMutual(last, selection.HidePrivateData);
		CompletionConflictRegistry.RegisterMutual(last, selection.CompressCode);
		CompletionConflictRegistry.RegisterMutual(last, selection.StripComments);
		CompletionConflictRegistry.RegisterMutual(last, selection.StripBlankLines);
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
			var outputOptions = new TerminalOutputOptions();
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var useLast = parseResult.GetValue(last);
					var projectSource = useLast
						? null
						: parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					ProjectSelectionSpec? spec = null;
					TerminalServices? services = null;
					ResolvedTerminalProjectSource? resolvedSource = null;
					if (projectSource is not null)
					{
						services = CreateServices(parseResult);
						resolvedSource = await new TerminalProjectSourceResolver(
								services,
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
			"devprojex profile show .",
			"devprojex profile validate ./.devprojex/profile.json");
		command.Subcommands.Add(BuildProfileShowCommand());
		command.Subcommands.Add(BuildProfileExportCommand());
		command.Subcommands.Add(BuildProfileImportCommand());
		command.Subcommands.Add(BuildProfileValidateCommand());
		command.Subcommands.Add(BuildProfileResetCommand());
		SetParentHelpAction(command, "profile");
		return command;
	}

	private Command BuildProfileShowCommand()
	{
		var command = new Command("show", L("Terminal.Command.ProfileShow"));
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
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(output);
		command.Options.Add(force);
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
						cancellationToken);
				}));
		return command;
	}

	private Command BuildProfileImportCommand()
	{
		var command = new Command("import", L("Terminal.Command.ProfileImport"));
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
		var file = RequiredArgument("FILE");
		command.Arguments.Add(file);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				(_, handler) =>
					handler.ValidateAsync(parseResult.GetValue(file)!, cancellationToken)));
		return command;
	}

	private Command BuildProfileResetCommand()
	{
		var command = new Command("reset", L("Terminal.Command.ProfileReset"));
		var project = ProjectArgument();
		command.Arguments.Add(project);
		command.SetAction(parseResult =>
		{
			var output = new TerminalOutputOptions();
			return CommandExecution.RunAsync(
					environment,
					output,
					() => Task.FromResult(
						new ProfileCommandHandler(CreateServices(parseResult), environment)
							.Reset(parseResult.GetValue(project) ?? Directory.GetCurrentDirectory())),
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
		command.Subcommands.Add(BuildUiSimpleCommand("status", L("Terminal.Command.UiStatus"), "status", static _ => new { }));
		command.Subcommands.Add(BuildUiSimpleCommand("activate", L("Terminal.Command.UiActivate"), "activate", static _ => new { }));
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
				new TerminalOutputOptions(),
				() => new DesktopCommandHandler(environment).ListAsync(
					parseResult.GetValue(format) == CliTextJsonFormat.Json,
					cancellationToken),
				_localization));
		return command;
	}

	private Command BuildUiPreviewCommand()
	{
		var command = new Command("preview", L("Terminal.Command.UiPreview"));
		var open = new Command("open", L("Terminal.Command.UiPreviewOpen"));
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
		command.Subcommands.Add(BuildUiSimpleCommand("close", L("Terminal.Command.UiPreviewClose"), "preview.close", static _ => new { }));

		var setView = new Command("set-view", L("Terminal.Command.UiPreviewSetView"));
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
		var setFormat = new Command("set-format", L("Terminal.Command.UiTreeSetFormat"));
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
		var set = new Command("set", L("Terminal.Command.UiFilterSet"));
		var query = RequiredArgument("QUERY");
		set.Arguments.Add(query);
		AddDesktopAction(set, "filter.set", result => new { query = result.GetValue(query) });
		command.Subcommands.Add(set);
		command.Subcommands.Add(BuildUiSimpleCommand("clear", L("Terminal.Command.UiFilterClear"), "filter.clear", static _ => new { }));
		SetParentHelpAction(command, "ui", "filter");
		return command;
	}

	private Command BuildUiSearchCommand()
	{
		var command = new Command("search", L("Terminal.Command.UiSearch"));
		var set = new Command("set", L("Terminal.Command.UiSearchSet"));
		var query = RequiredArgument("QUERY");
		set.Arguments.Add(query);
		AddDesktopAction(set, "search.set", result => new { query = result.GetValue(query) });
		command.Subcommands.Add(set);
		command.Subcommands.Add(BuildUiSimpleCommand("next", L("Terminal.Command.UiSearchNext"), "search.next", static _ => new { }));
		command.Subcommands.Add(BuildUiSimpleCommand("previous", L("Terminal.Command.UiSearchPrevious"), "search.previous", static _ => new { }));
		command.Subcommands.Add(BuildUiSimpleCommand("clear", L("Terminal.Command.UiSearchClear"), "search.clear", static _ => new { }));
		SetParentHelpAction(command, "ui", "search");
		return command;
	}

	private Command BuildUiSimpleCommand(
		string name,
		string description,
		string action,
		Func<ParseResult, object> payload)
	{
		var command = new Command(name, description);
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
		var timeout = new Option<TimeSpan>("--timeout")
		{
			Description = L("Terminal.Option.Timeout"),
			HelpName = "DURATION",
			DefaultValueFactory = _ => TimeSpan.FromSeconds(10),
			CustomParser = result => ParseDuration(result, _localization)
		};
		CliHelpMetadataRegistry.SetDefaultDisplay(timeout, "10s");
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
				new TerminalOutputOptions(),
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
				new TerminalOutputOptions(),
				() => new DoctorCommandHandler(CreateServices(parseResult), environment)
					.ExecuteAsync(
						parseResult.GetValue(format) == CliTextJsonFormat.Json,
						cancellationToken),
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
				new TerminalOutputOptions(),
				() => Task.FromResult(new RecentCommandHandler(
						CreateServices(parseResult),
						environment)
					.Execute(
						parseResult.GetValue(kind),
						parseResult.GetValue(limit),
						parseResult.GetValue(format))),
				_localization));
		return command;
	}

	private Command BuildCacheCommand()
	{
		var command = new Command("cache", L("Terminal.Command.Cache"));
		CliExamplesRegistry.Set(
			command,
			"devprojex cache list",
			"devprojex cache clear --force");

		var path = new Command("path", L("Terminal.Command.CachePath"));
		path.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => Task.FromResult(new CacheCommandHandler(
						CreateServices(parseResult),
						environment)
					.WritePath()),
				_localization));

		var list = new Command("list", L("Terminal.Command.CacheList"));
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
				new TerminalOutputOptions(),
				() => Task.FromResult(new CacheCommandHandler(
						CreateServices(parseResult),
						environment)
					.WriteList(parseResult.GetValue(format))),
				_localization));

		var remove = new Command("remove", L("Terminal.Command.CacheRemove"));
		var repositoryUrl = new Argument<string>("URL")
		{
			Description = L("Terminal.Argument.RepositoryUrl"),
			Arity = ArgumentArity.ExactlyOne
		};
		var removeForce = new Option<bool>("--force")
		{
			Description = L("Terminal.Option.CacheForce")
		};
		remove.Arguments.Add(repositoryUrl);
		remove.Options.Add(removeForce);
		RequireForce(remove, removeForce);
		remove.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => Task.FromResult(new CacheCommandHandler(
						CreateServices(parseResult),
						environment)
					.Remove(parseResult.GetValue(repositoryUrl)!)),
				_localization));

		var clear = new Command("clear", L("Terminal.Command.CacheClear"));
		var clearForce = new Option<bool>("--force")
		{
			Description = L("Terminal.Option.CacheForce")
		};
		clear.Options.Add(clearForce);
		RequireForce(clear, clearForce);
		clear.SetAction(parseResult =>
			CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => Task.FromResult(new CacheCommandHandler(
						CreateServices(parseResult),
						environment)
					.Clear()),
				_localization));

		command.Subcommands.Add(path);
		command.Subcommands.Add(list);
		command.Subcommands.Add(remove);
		command.Subcommands.Add(clear);
		SetParentHelpAction(command, "cache");
		return command;
	}

	private void RequireForce(Command command, Option<bool> force)
	{
		command.Validators.Add(result =>
		{
			if (!result.GetValue(force))
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
		var project = ProjectArgument();
		var format = CliChoiceSymbols.Option(
			"--format",
			L("Terminal.Option.TreeFormat"),
			ProjectContextDocumentFormat.Text,
			CliChoiceSets.TreeFormat,
			_localization);
		format.Aliases.Add("-f");
		var outputPath = OutputPathOption();
		var selection = new SelectionOptions(
			_localization,
			environment,
			includeContentTransformations: false);
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		selection.AddTo(command);
		output.AddTo(command);
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var outputOptions = output.Get(parseResult);
			return await CommandExecution.RunAsync(
				environment,
				outputOptions,
				async () =>
				{
					var services = CreateServices(parseResult);
					var projectPath = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
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
								outputOptions),
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
		var commandLine = new Argument<string>("COMMAND_LINE")
		{
			Arity = ArgumentArity.ExactlyOne
		};
		var base64 = new Option<bool>("--base64")
		{
			Hidden = true
		};
		var workingDirectoryBase64 = new Option<string?>("--working-directory-base64")
		{
			Hidden = true
		};
		complete.Options.Add(position);
		complete.Options.Add(base64);
		complete.Options.Add(workingDirectoryBase64);
		complete.Arguments.Add(commandLine);
		complete.SetAction(parseResult =>
		{
			var useBase64Transport = parseResult.GetValue(base64);
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
				         parseResult.GetValue(position),
				         completionWorkingDirectory))
			{
				environment.Output.WriteLine(
					useBase64Transport
						? CompletionCommandLineTransport.EncodeBase64(candidate)
						: candidate);
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
		var services = CreateServices(parseResult);
		return CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => operation(services, new ProfileCommandHandler(services, environment)),
			_localization);
	}

	private TerminalServices CreateServices(ParseResult parseResult) =>
		_serviceFactory.Create(parseResult.GetValue(_language));

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
		new("--branch")
		{
			Description = L("Terminal.Option.Branch"),
			HelpName = "NAME"
		};

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

	private static Option<AppLanguage> CreateLanguageOption(LocalizationService? localization)
	{
		var text = localization ?? new LocalizationService(
			new JsonLocalizationCatalog(),
			AppLanguageUtility.DetectSystemLanguage());
		return CliChoiceSymbols.Option(
			"--language",
			text["Terminal.Option.Language"],
			ResolveDefaultLanguage(),
			CliChoiceSets.Language,
			text);
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
		result.GetResult(selection.Profile) is { Implicit: false } ||
		result.GetResult(selection.Roots) is { Implicit: false } ||
		result.GetResult(selection.Extensions) is { Implicit: false } ||
		result.GetResult(selection.SelectedPaths) is { Implicit: false } ||
		result.GetResult(selection.SelectedPathsSource) is { Implicit: false } ||
		result.GetResult(selection.GitMode) is { Implicit: false } ||
		result.GetResult(selection.Exclusions) is { Implicit: false } ||
		result.GetResult(selection.HideSecrets) is { Implicit: false } ||
		selection.IncludesHidePrivateData &&
		result.GetResult(selection.HidePrivateData) is { Implicit: false } ||
		result.GetResult(selection.CompressCode) is { Implicit: false } ||
		result.GetResult(selection.StripComments) is { Implicit: false } ||
		result.GetResult(selection.StripBlankLines) is { Implicit: false };

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
			return duration > TimeSpan.Zero;
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
			return duration > TimeSpan.Zero;
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

	private static AppLanguage ResolveDefaultLanguage() =>
		AppLanguageUtility.DetectSystemLanguage();

	private string L(string key) => _localization[key];
}
