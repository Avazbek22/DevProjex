using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevProjex.Mcp;

public static class McpServerHost
{
	private const string Instructions =
		"Recommended flow: list_projects, then get_tree or analyze, then search_project or get_file, " +
		"then pack_context and read_pack for large results.";

	public static Task RunAsync(
		IReadOnlyList<string> roots,
		bool hidePrivateData = false,
		bool allowRemote = false,
		GitFilteringMode? gitMode = null,
		IReadOnlyCollection<ProjectExclusion>? exclusions = null,
		CancellationToken cancellationToken = default) =>
		RunWithStandardStreamsAsync(
			roots,
			hidePrivateData,
			allowRemote,
			gitMode,
			exclusions,
			appDataPathProvider: null,
			cancellationToken);

	internal static Task RunWithStandardStreamsAsync(
		IReadOnlyList<string> roots,
		bool hidePrivateData,
		bool allowRemote,
		GitFilteringMode? gitMode,
		IReadOnlyCollection<ProjectExclusion>? exclusions,
		Func<string>? appDataPathProvider,
		CancellationToken cancellationToken)
	{
		ValidateGitMode(gitMode);
		ValidateExclusions(exclusions);
		return RunWithStreamsAsync(
			roots,
			Console.OpenStandardInput(),
			Console.OpenStandardOutput(),
			hidePrivateData,
			cancellationToken,
			appDataPathProvider,
			allowRemote: allowRemote,
			gitMode: gitMode,
			exclusions: exclusions);
	}

	internal static async Task RunWithStreamsAsync(
		IReadOnlyList<string> roots,
		Stream input,
		Stream output,
		bool hidePrivateData = false,
		CancellationToken cancellationToken = default,
		Func<string>? appDataPathProvider = null,
		string? tempRoot = null,
		Func<McpProjectRootJail, McpServices>? servicesFactory = null,
		bool allowRemote = false,
		Func<McpRemoteProjectServices>? remoteServicesFactory = null,
		GitFilteringMode? gitMode = null,
		IReadOnlyCollection<ProjectExclusion>? exclusions = null)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
		ValidateGitMode(gitMode);
		ValidateExclusions(exclusions);

		var rootRegistry = new McpRootRegistry(roots);
		using var projectSources = new McpProjectSourceResolver(
			rootRegistry,
			allowRemote,
			() => remoteServicesFactory?.Invoke() ??
			      McpRemoteProjectServices.Create(appDataPathProvider));
		var rootJail = new McpProjectRootJail(rootRegistry, projectSources);
		var services = new Lazy<McpServices>(
			() => servicesFactory?.Invoke(rootJail) ?? McpServices.Create(rootJail, appDataPathProvider),
			LazyThreadSafetyMode.ExecutionAndPublication);
		await using var packs = new McpPackRegistry(tempRoot);
		var projectService = new Lazy<McpProjectService>(
			() => new McpProjectService(
				projectSources,
				rootJail,
				services.Value,
				hidePrivateData,
				gitMode,
				exclusions),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var tools = new DevProjexMcpTools(rootRegistry, projectService, packs);
		var catalog = new DevProjexMcpToolCatalog(tools, allowRemote);

		var builder = Host.CreateApplicationBuilder([]);
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton(packs);
		builder.Services.AddMcpServer(options =>
			{
				options.ServerInfo = new Implementation
				{
					Name = "devprojex",
					Title = "DevProjex",
					Version = ResolveVersion()
				};
				options.ServerInstructions = Instructions;
			})
			.WithStreamServerTransport(input, output)
			.WithTools<DevProjexMcpToolCatalog>(catalog)
			.WithRequestFilters(filters => filters.AddListToolsFilter(next => async (request, token) =>
			{
				var result = await next(request, token).ConfigureAwait(false);
				result.Tools = result.Tools
					.OrderBy(tool => catalog.IndexOf(tool.Name))
					.ToArray();
				return result;
			}));

		try
		{
			using var host = builder.Build();
			await host.RunAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			if (services.IsValueCreated)
				services.Value.Dispose();
		}
	}

	internal static void ValidateGitMode(GitFilteringMode? gitMode)
	{
		if (gitMode is null or GitFilteringMode.None or GitFilteringMode.RespectGitIgnore or
		    GitFilteringMode.TrackedFilesOnly)
		{
			return;
		}

		throw new ArgumentOutOfRangeException(
			nameof(gitMode),
			gitMode,
			"The MCP server Git mode must be none, gitignore, or tracked.");
	}

	internal static void ValidateExclusions(IReadOnlyCollection<ProjectExclusion>? exclusions)
	{
		if (exclusions is null)
			return;

		foreach (var exclusion in exclusions)
		{
			// Content redaction is never part of the exclusion baseline; only the eight
			// path-visibility toggles from the shared presentation catalog are accepted.
			if (!ProjectSelectionSpec.StandardExclusions.Contains(exclusion))
			{
				throw new ArgumentOutOfRangeException(
					nameof(exclusions),
					exclusion,
					"The MCP server exclusion baseline accepts only path exclusion toggles.");
			}
		}
	}

	private static string ResolveVersion() =>
		typeof(McpServerHost).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion ?? "0.0.0";
}
