using System.CommandLine;
using System.CommandLine.Invocation;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Tui;

namespace DevProjex.Terminal.CommandLine;

public sealed class DevProjexCommandTree(
	ITerminalEnvironment environment,
	TerminalServiceFactory? serviceFactory = null,
	IDeveloperCommandRunner? developerCommandRunner = null,
	bool implicitTuiInvocation = false,
	LocalizationService? localization = null)
{
	private readonly TerminalServiceFactory _serviceFactory = serviceFactory ?? new TerminalServiceFactory();
	private readonly IDeveloperCommandRunner? _developerCommandRunner = developerCommandRunner;
	private readonly bool _implicitTuiInvocation = implicitTuiInvocation;
	private readonly LocalizationService _localization = localization ?? new LocalizationService(
		new JsonLocalizationCatalog(),
		AppLanguageUtility.DetectSystemLanguage());
	private readonly Option<string> _language = CreateLanguageOption(localization);

	public RootCommand Build()
	{
		var root = new RootCommand(L("Terminal.Command.Root"));
		_language.Recursive = true;
		root.Options.Add(_language);
		root.Subcommands.Add(BuildTuiCommand());
		root.Subcommands.Add(BuildOpenCommand());
		root.Subcommands.Add(BuildAnalyzeCommand());
		root.Subcommands.Add(BuildExportCommand());
		root.Subcommands.Add(BuildProfileCommand());
		root.Subcommands.Add(BuildUiCommand());
		root.Subcommands.Add(BuildDoctorCommand());
		root.Subcommands.Add(BuildCompletionCommand(root));
		root.Subcommands.Add(BuildDevCommand());
		return root;
	}

	private Command BuildTuiCommand()
	{
		var command = new Command("tui", L("Terminal.Command.Tui"));
		var project = ProjectArgument();
		var profile = new Option<string>("--profile")
		{
			Description = L("Terminal.Option.Profile"),
			DefaultValueFactory = _ => "auto"
		};
		var screen = Choice("--screen", L("Terminal.Option.Screen"), "auto", "alternate", "inline");
		var mouse = new Option<bool>("--mouse") { Description = L("Terminal.Option.Mouse") };
		var noMouse = new Option<bool>("--no-mouse") { Description = L("Terminal.Option.NoMouse") };
		var color = Choice("--color", L("Terminal.Option.Color"), "auto", "always", "never");
		var plain = new Option<bool>("--plain") { Description = L("Terminal.Option.Plain") };
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(screen);
		command.Options.Add(mouse);
		command.Options.Add(noMouse);
		command.Options.Add(color);
		command.Options.Add(plain);
		command.Validators.Add(result =>
		{
			if (result.GetValue(mouse) && result.GetValue(noMouse))
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.MouseConflict")));
		});
		command.SetAction(async (parseResult, cancellationToken) =>
		{
			var output = new TerminalOutputOptions(
				CliValueParser.ParseColor(parseResult.GetValue(color) ?? "auto"),
				Plain: parseResult.GetValue(plain));
			return await CommandExecution.RunAsync(
				environment,
				output,
				async () =>
				{
					var services = CreateServices(parseResult);
					var projectPath = parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					var profileValue = parseResult.GetValue(profile) ?? "auto";
					var profileReference = ResolveTuiProfile(services, projectPath, profileValue);
					var screenResult = parseResult.GetResult(screen);
					var hasExplicitScreenMode = screenResult is { Implicit: false };
					var screenMode = hasExplicitScreenMode
						? ParseScreenMode(parseResult.GetValue(screen) ?? "auto")
						: services.TerminalSettingsStore.LoadScreenMode();
					if (hasExplicitScreenMode)
					{
						await services.TerminalSettingsStore
							.SaveScreenModeAsync(screenMode, cancellationToken)
							.ConfigureAwait(false);
					}
					var workspace = new TerminalWorkspace(services, environment);
					return await workspace.RunAsync(
						new TerminalWorkspaceOptions(
							projectPath,
							profileReference,
							screenMode,
							MouseEnabled: !parseResult.GetValue(noMouse),
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
		var project = ProjectArgument();
		var format = Choice("--format", L("Terminal.Option.Format"), "text", "json");
		var outputPath = OutputPathOption();
		var strict = new Option<bool>("--strict") { Description = L("Terminal.Option.Strict") };
		var dryRun = new Option<bool>("--dry-run") { Description = L("Terminal.Option.DryRun") };
		var selection = new SelectionOptions(_localization);
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(strict);
		command.Options.Add(dryRun);
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
					return await new AnalyzeCommandHandler(services, environment)
						.ExecuteAsync(
							new AnalyzeCommandRequest(
								projectPath,
								spec,
								parseResult.GetValue(format) ?? "text",
								parseResult.GetValue(outputPath),
								parseResult.GetValue(strict),
								parseResult.GetValue(dryRun),
								outputOptions),
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
		command.Subcommands.Add(BuildExportContextCommand());
		command.Subcommands.Add(BuildExportProjectCommand());
		SetParentHelpAction(command, "export");
		return command;
	}

	private Command BuildExportContextCommand()
	{
		var command = new Command("context", L("Terminal.Command.ExportContext"));
		var project = ProjectArgument();
		var view = Choice("--view", L("Terminal.Option.View"), "tree-content", "tree", "content");
		var format = Choice("--format", L("Terminal.Option.DocumentFormat"), "markdown", "text", "json", "xml");
		var outputPath = OutputPathOption();
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceContext") };
		var dryRun = new Option<bool>("--dry-run") { Description = L("Terminal.Option.DryRun") };
		var selection = new SelectionOptions(_localization);
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(view);
		command.Options.Add(format);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
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
					return await new ExportContextCommandHandler(services, environment)
						.ExecuteAsync(
							new ExportContextCommandRequest(
								projectPath,
								spec,
								ParseContextView(parseResult.GetValue(view) ?? "tree-content"),
								ParseDocumentFormat(parseResult.GetValue(format) ?? "markdown"),
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
		var project = ProjectArgument();
		var kind = Choice("--as", L("Terminal.Option.OutputKind"), null, "folder", "zip");
		kind.Required = true;
		var outputPath = new Option<string>("--output", "-o")
		{
			Description = L("Terminal.Option.ExactDestination"),
			Required = true
		};
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceZip") };
		var dryRun = new Option<bool>("--dry-run") { Description = L("Terminal.Option.DryRun") };
		var selection = new SelectionOptions(_localization);
		var output = new OutputOptions(_localization);
		command.Arguments.Add(project);
		command.Options.Add(kind);
		command.Options.Add(outputPath);
		command.Options.Add(force);
		command.Options.Add(dryRun);
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
					return await new ExportProjectCommandHandler(services, environment)
						.ExecuteAsync(
							new ExportProjectCommandRequest(
								projectPath,
								spec,
								(parseResult.GetValue(kind) ?? "folder") == "zip"
									? ProjectCopyExportFormat.Zip
									: ProjectCopyExportFormat.Folder,
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
		var project = ProjectArgument();
		var last = new Option<bool>("--last") { Description = L("Terminal.Option.Last") };
		var newWindow = new Option<bool>("--new-window") { Description = L("Terminal.Option.NewWindow") };
		var wait = new Option<bool>("--wait") { Description = L("Terminal.Option.Wait") };
		var preview = new Option<bool>("--preview") { Description = L("Terminal.Option.Preview") };
		var view = NullableChoice("--view", L("Terminal.Option.PreviewView"), "tree", "content", "tree-content");
		var format = NullableChoice("--tree-format", L("Terminal.Option.TreeFormat"), "text", "markdown", "json", "xml");
		var filter = new Option<string?>("--filter") { Description = L("Terminal.Option.Filter") };
		var search = new Option<string?>("--search") { Description = L("Terminal.Option.Search") };
		var elevationAttempted = new Option<bool>("--internal-elevation-attempted")
		{
			Hidden = true
		};
		var selection = new SelectionOptions(_localization, "auto");
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
		selection.AddTo(command);
		command.Validators.Add(result =>
		{
			if (result.GetValue(last) && result.GetResult(project) is not null)
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.LastProjectConflict")));
			if (result.GetValue(filter) is not null && result.GetValue(search) is not null)
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
					var projectPath = useLast
						? null
						: parseResult.GetValue(project) ?? Directory.GetCurrentDirectory();
					ProjectSelectionSpec? spec = null;
					if (projectPath is not null)
					{
						var services = CreateServices(parseResult);
						spec = await selection.ResolveAsync(
							parseResult,
							projectPath,
							services,
							cancellationToken).ConfigureAwait(false);
					}
					var viewValue = parseResult.GetValue(view);
					return await new DesktopCommandHandler(environment)
						.OpenAsync(
							new DesktopOpenRequest(
								projectPath,
								useLast,
								parseResult.GetValue(newWindow),
								parseResult.GetValue(wait),
								parseResult.GetValue(preview) || viewValue is not null || parseResult.GetValue(search) is not null,
								ParseDesktopView(viewValue ?? "tree-content"),
								parseResult.GetValue(format) is { } treeFormat
									? ParseTreeFormat(treeFormat)
									: null,
								parseResult.GetValue(filter),
								parseResult.GetValue(search),
								spec,
								ParseLanguage(parseResult.GetValue(_language) ?? "en"),
								parseResult.GetValue(elevationAttempted)),
							cancellationToken)
						.ConfigureAwait(false);
				},
				_localization).ConfigureAwait(false);
		});
		return command;
	}

	private Command BuildProfileCommand()
	{
		var command = new Command("profile", L("Terminal.Command.Profile"));
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
		var format = Choice("--format", L("Terminal.Option.Format"), "text", "json");
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				handler => handler.ShowAsync(
					parseResult.GetValue(project) ?? Directory.GetCurrentDirectory(),
					parseResult.GetValue(profile) ?? "standard",
					parseResult.GetValue(format) ?? "text",
					cancellationToken)));
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
			Required = true
		};
		var force = new Option<bool>("--force") { Description = L("Terminal.Option.ForceProfile") };
		command.Arguments.Add(project);
		command.Options.Add(profile);
		command.Options.Add(output);
		command.Options.Add(force);
		command.SetAction((parseResult, cancellationToken) =>
			RunProfileAsync(
				parseResult,
				handler => handler.ExportAsync(
					parseResult.GetValue(project) ?? Directory.GetCurrentDirectory(),
					parseResult.GetValue(profile) ?? "local",
					parseResult.GetValue(output)!,
					parseResult.GetValue(force),
					cancellationToken)));
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
				handler => handler.ImportAsync(
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
				handler => handler.ValidateAsync(parseResult.GetValue(file)!, cancellationToken)));
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
		var format = Choice("--format", L("Terminal.Option.Format"), "text", "json");
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => new DesktopCommandHandler(environment).ListAsync(
					(parseResult.GetValue(format) ?? "text") == "json",
					cancellationToken),
				_localization));
		return command;
	}

	private Command BuildUiPreviewCommand()
	{
		var command = new Command("preview", L("Terminal.Command.UiPreview"));
		var open = new Command("open", L("Terminal.Command.UiPreviewOpen"));
		var openView = NullableChoice("--view", L("Terminal.Option.PreviewView"), "tree", "content", "tree-content");
		open.Options.Add(openView);
		AddDesktopAction(
			open,
			"preview.open",
			parseResult => new { view = parseResult.GetValue(openView) });
		command.Subcommands.Add(open);
		command.Subcommands.Add(BuildUiSimpleCommand("close", L("Terminal.Command.UiPreviewClose"), "preview.close", static _ => new { }));

		var setView = new Command("set-view", L("Terminal.Command.UiPreviewSetView"));
		var view = ChoiceArgument("VIEW", "tree", "content", "tree-content");
		setView.Arguments.Add(view);
		AddDesktopAction(setView, "preview.set-view", result => new { view = result.GetValue(view) });
		command.Subcommands.Add(setView);
		SetParentHelpAction(command, "ui", "preview");
		return command;
	}

	private Command BuildUiTreeCommand()
	{
		var command = new Command("tree", L("Terminal.Command.UiTree"));
		var setFormat = new Command("set-format", L("Terminal.Command.UiTreeSetFormat"));
		var format = ChoiceArgument("FORMAT", "text", "markdown", "json", "xml");
		setFormat.Arguments.Add(format);
		AddDesktopAction(setFormat, "tree.set-format", result => new { format = result.GetValue(format) });
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
		var timeout = new Option<string>("--timeout")
		{
			Description = L("Terminal.Option.Timeout"),
			DefaultValueFactory = _ => "10s"
		};
		timeout.Validators.Add(result =>
		{
			if (!TryParseDuration(result.GetValueOrDefault<string>(), out _))
				result.AddError(LocalizedParseError.Create(L("Terminal.Validation.Timeout")));
		});
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
						ParseDuration(parseResult.GetValue(timeout) ?? "10s")),
					action,
					payload(parseResult),
					cancellationToken),
				_localization));
	}

	private Command BuildDoctorCommand()
	{
		var command = new Command("doctor", L("Terminal.Command.Doctor"));
		var format = Choice("--format", L("Terminal.Option.Format"), "text", "json");
		command.Options.Add(format);
		command.SetAction((parseResult, cancellationToken) =>
			CommandExecution.RunAsync(
				environment,
				new TerminalOutputOptions(),
				() => new DoctorCommandHandler(CreateServices(parseResult), environment)
					.ExecuteAsync(
						(parseResult.GetValue(format) ?? "text") == "json",
						cancellationToken),
				_localization));
		return command;
	}

	private Command BuildCompletionCommand(RootCommand root)
	{
		var command = new Command("completion", L("Terminal.Command.Completion"));
		var shell = ChoiceArgument("SHELL", "bash", "zsh", "fish", "powershell");
		command.Arguments.Add(shell);
		command.SetAction(parseResult =>
		{
			environment.Output.WriteLine(CompletionScriptGenerator.Generate(root, parseResult.GetValue(shell)!));
			return CommandLineExitCodes.Success;
		});
		return command;
	}

	private Command BuildDevCommand()
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
		var scenario = Choice(
			"--scenario",
			L("Terminal.Option.Scenario"),
			"standard",
			"preview-search-retention",
			"project-memory-lifecycle");
		session.Arguments.Add(sessionProject);
		session.Options.Add(sessionOutput);
		session.Options.Add(scenario);
		session.SetAction((parseResult, cancellationToken) => RunDeveloperCommandAsync(
			new DeveloperCommandRequest(
				DeveloperCommandKind.Session,
				parseResult.GetValue(sessionProject) ?? Directory.GetCurrentDirectory(),
				parseResult.GetValue(sessionOutput),
				parseResult.GetValue(scenario) ?? "standard"),
			cancellationToken));
		command.Subcommands.Add(session);
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
		Func<ProfileCommandHandler, Task<int>> operation) =>
		CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => operation(new ProfileCommandHandler(CreateServices(parseResult), environment)),
			_localization);

	private TerminalServices CreateServices(ParseResult parseResult) =>
		_serviceFactory.Create(ParseLanguage(parseResult.GetValue(_language) ?? "en"));

	private static ProjectProfileReference ResolveTuiProfile(
		TerminalServices services,
		string projectPath,
		string value)
	{
		if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return services.LocalProfileStore.TryLoadProfile(projectPath, out _)
				? ProjectProfileReference.Local
				: ProjectProfileReference.Standard;
		}
		if (value.Equals("standard", StringComparison.OrdinalIgnoreCase))
			return ProjectProfileReference.Standard;
		if (value.Equals("local", StringComparison.OrdinalIgnoreCase))
			return ProjectProfileReference.Local;
		return new ProjectProfileReference(ProjectProfileSourceKind.Portable, Path.GetFullPath(value));
	}

	private Argument<string?> ProjectArgument() =>
		new("PROJECT")
		{
			Description = L("Terminal.Argument.Project"),
			Arity = ArgumentArity.ZeroOrOne,
			DefaultValueFactory = _ => Directory.GetCurrentDirectory()
		};

	private static Argument<string> RequiredArgument(string name) =>
		new(name)
		{
			Arity = ArgumentArity.ExactlyOne
		};

	private Argument<string> ChoiceArgument(string name, params string[] choices)
	{
		var argument = RequiredArgument(name);
		argument.CompletionSources.Add(choices);
		argument.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string>();
			if (value is null || !choices.Contains(value, StringComparer.OrdinalIgnoreCase))
				result.AddError(LocalizedParseError.Create(_localization.Format(
					"Terminal.Validation.Choice",
					name,
					string.Join(", ", choices))));
		});
		return argument;
	}

	private Option<string?> OutputPathOption() =>
		new("--output", "-o")
		{
			Description = L("Terminal.Option.Output")
		};

	private Option<string?> OptionalOutputPath() =>
		new("--output", "-o")
		{
			Description = L("Terminal.Option.ReportOutput")
		};

	private Option<string> ProfileOption(string defaultValue) =>
		new("--profile")
		{
			Description = L("Terminal.Option.Profile"),
			DefaultValueFactory = _ => defaultValue
		};

	private Option<string> Choice(
		string name,
		string description,
		string? defaultValue,
		params string[] values)
	{
		var allowedValues = defaultValue is null
			? values
			: new[] { defaultValue }.Concat(values).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var option = new Option<string>(name)
		{
			Description = description
		};
		if (defaultValue is not null)
			option.DefaultValueFactory = _ => defaultValue;
		option.CompletionSources.Add(allowedValues);
		option.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string>();
			if (value is null || !allowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
				result.AddError(LocalizedParseError.Create(_localization.Format(
					"Terminal.Validation.Choice",
					name,
					string.Join(", ", allowedValues))));
		});
		return option;
	}

	private static Option<string> CreateLanguageOption(LocalizationService? localization)
	{
		string[] values = ["en", "ru", "de", "fr", "it", "es", "pt", "pt-pt", "kk", "tg", "uz"];
		var text = localization ?? new LocalizationService(
			new JsonLocalizationCatalog(),
			AppLanguageUtility.DetectSystemLanguage());
		var option = new Option<string>("--language")
		{
			Description = text["Terminal.Option.Language"],
			DefaultValueFactory = _ => ResolveDefaultLanguage()
		};
		option.CompletionSources.Add(values);
		option.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string>();
			if (value is null || !values.Contains(value, StringComparer.OrdinalIgnoreCase))
				result.AddError(LocalizedParseError.Create(text.Format(
					"Terminal.Validation.Choice",
					"--language",
					string.Join(", ", values))));
		});
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

	private Option<string?> NullableChoice(
		string name,
		string description,
		params string[] values)
	{
		var option = new Option<string?>(name) { Description = description };
		option.CompletionSources.Add(values);
		option.Validators.Add(result =>
		{
			var value = result.GetValueOrDefault<string?>();
			if (value is not null && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
				result.AddError(LocalizedParseError.Create(_localization.Format(
					"Terminal.Validation.Choice",
					name,
					string.Join(", ", values))));
		});
		return option;
	}

	private static ProjectContextView ParseContextView(string value) => value switch
	{
		"tree" => ProjectContextView.Tree,
		"content" => ProjectContextView.Content,
		_ => ProjectContextView.TreeContent
	};

	private static ProjectContextDocumentFormat ParseDocumentFormat(string value) => value switch
	{
		"text" => ProjectContextDocumentFormat.Text,
		"json" => ProjectContextDocumentFormat.Json,
		"xml" => ProjectContextDocumentFormat.Xml,
		_ => ProjectContextDocumentFormat.Markdown
	};

	private static DesktopPreviewView ParseDesktopView(string value) => value switch
	{
		"tree" => DesktopPreviewView.Tree,
		"content" => DesktopPreviewView.Content,
		_ => DesktopPreviewView.TreeContent
	};

	private static TreeTextFormat ParseTreeFormat(string value) => value switch
	{
		"markdown" => TreeTextFormat.Markdown,
		"json" => TreeTextFormat.Json,
		"xml" => TreeTextFormat.Xml,
		_ => TreeTextFormat.Ascii
	};

	private static TerminalScreenMode ParseScreenMode(string value) => value switch
	{
		"alternate" => TerminalScreenMode.Alternate,
		"inline" => TerminalScreenMode.Inline,
		_ => TerminalScreenMode.Auto
	};

	private static bool TryParseDuration(string? value, out TimeSpan duration)
	{
		duration = default;
		if (string.IsNullOrWhiteSpace(value))
			return false;
		if (TimeSpan.TryParse(value, out duration))
			return duration > TimeSpan.Zero;
		if (value.EndsWith('s') &&
		    double.TryParse(value[..^1], System.Globalization.NumberStyles.Number,
			    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
		{
			duration = TimeSpan.FromSeconds(seconds);
			return duration > TimeSpan.Zero;
		}
		if (value.EndsWith('m') &&
		    double.TryParse(value[..^1], System.Globalization.NumberStyles.Number,
			    System.Globalization.CultureInfo.InvariantCulture, out var minutes))
		{
			duration = TimeSpan.FromMinutes(minutes);
			return duration > TimeSpan.Zero;
		}
		return false;
	}

	private static TimeSpan ParseDuration(string value) =>
		TryParseDuration(value, out var duration)
			? duration
			: TimeSpan.FromSeconds(10);

	private static AppLanguage ParseLanguage(string value) =>
		TerminalLanguageResolver.ParseOrDefault(value);

	private static string ResolveDefaultLanguage() =>
		AppLanguageUtility.ToCode(AppLanguageUtility.DetectSystemLanguage());

	private string L(string key) => _localization[key];
}
