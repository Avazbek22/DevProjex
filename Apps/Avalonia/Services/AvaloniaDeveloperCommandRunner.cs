using DevProjex.Terminal.DesktopControl;
using DevProjex.Terminal.Execution;

namespace DevProjex.Avalonia.Services;

internal sealed class AvaloniaDeveloperCommandRunner(
	TextWriter output,
	TextWriter error)
	: IDeveloperCommandRunner
{
	public Task<int> RunAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		return request.Kind switch
		{
			DeveloperCommandKind.AnalysisBenchmark => RunAnalysisBenchmarkAsync(request, cancellationToken),
			DeveloperCommandKind.UiBenchmark => RunUiBenchmarkAsync(request, cancellationToken),
			DeveloperCommandKind.Session => RunSessionAsync(request, cancellationToken),
			_ => Task.FromResult(CommandLineExitCodes.UsageError)
		};
	}

	private async Task<int> RunAnalysisBenchmarkAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken)
	{
		var context = new CommandLineBenchmarkContext(
			output,
			error,
			CreateAnalysisServices,
			ResolveVersion,
			new DefaultCommandLineBenchmarkProcessRunner(),
			ResolveLocalAppData);
		return await new CommandLineBenchmarkRunner(context)
			.RunAsync(request.ProjectPath, request.OutputPath, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<int> RunUiBenchmarkAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken)
	{
		var context = new CommandLineUiBenchmarkContext(
			output,
			error,
			ResolveVersion,
			new DefaultCommandLineBenchmarkProcessRunner(),
			ResolveLocalAppData);
		return await new CommandLineUiBenchmarkRunner(context)
			.RunAsync(request.ProjectPath, request.OutputPath, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<int> RunSessionAsync(
		DeveloperCommandRequest request,
		CancellationToken cancellationToken)
	{
		var reportPath = string.IsNullOrWhiteSpace(request.OutputPath)
			? Path.Combine(
				ResolveLocalAppData(),
				"DevProjex",
				"Benchmarks",
				$"session-{DateTimeOffset.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.json")
			: Path.GetFullPath(request.OutputPath);
		var processRequest = DesktopDiagnosticProcessRequestFactory.Create(
			request.ProjectPath,
			reportPath,
			request.Scenario);
		var result = await new DefaultCommandLineBenchmarkProcessRunner()
			.RunAsync(processRequest, index: 1, isWarmup: false, cancellationToken)
			.ConfigureAwait(false);
		if (result.ExitCode == CommandLineExitCodes.Success)
			output.WriteLine(Path.GetFullPath(reportPath));
		else
			error.WriteLine("DevProjex desktop diagnostic session failed.");
		return result.ExitCode;
	}

	private static BenchmarkAnalysisServices CreateAnalysisServices()
	{
		using var services = new TerminalServiceFactory().Create(AppLanguage.En);
		return new BenchmarkAnalysisServices(
			services.AnalysisService,
			services.AnalysisReportWriter);
	}

	private static string ResolveVersion()
	{
		var assembly = typeof(AvaloniaDeveloperCommandRunner).Assembly;
		return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
		       assembly.GetName().Version?.ToString() ??
		       "unknown";
	}

	private static string ResolveLocalAppData()
	{
		var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		return string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path;
	}
}

internal static class DesktopDiagnosticProcessRequestFactory
{
	public static CommandLineBenchmarkProcessRequest Create(
		string projectPath,
		string outputPath,
		string scenario)
	{
		var processPath = ProcessEntryPointResolver.ResolveSelfLaunchPath();
		var assemblyPath = ProcessEntryPointResolver.ResolveManagedAssemblyPath();
		var arguments = new List<string>();
		var fileName = processPath;
		if (string.IsNullOrWhiteSpace(fileName))
		{
			if (string.IsNullOrWhiteSpace(assemblyPath))
				throw new InvalidOperationException("The current DevProjex process entry point is unavailable.");
			fileName = "dotnet";
			arguments.Add(assemblyPath);
		}
		else if (ProcessEntryPointResolver.IsDotnetHost(fileName) &&
		         !string.IsNullOrWhiteSpace(assemblyPath))
		{
			arguments.Add(assemblyPath);
		}

		var requestPath = DesktopDiagnosticRequestStore.Create(
			new DesktopDiagnosticRequest(
				Path.GetFullPath(projectPath),
				Path.GetFullPath(outputPath),
				scenario));
		return new CommandLineBenchmarkProcessRequest(
			fileName,
			arguments,
			Directory.GetCurrentDirectory(),
			$"{fileName} <internal-desktop-diagnostic>",
			new Dictionary<string, string?>
			{
				[DesktopDiagnosticRequestStore.EnvironmentVariable] = requestPath
			});
	}
}
