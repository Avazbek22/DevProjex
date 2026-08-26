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
		CancellationToken cancellationToken = default) =>
		RunAsync(roots, hidePrivateData: false, cancellationToken: cancellationToken);

	public static Task RunAsync(
		IReadOnlyList<string> roots,
		bool hidePrivateData,
		CancellationToken cancellationToken = default) =>
		RunAsync(
			roots,
			Console.OpenStandardInput(),
			Console.OpenStandardOutput(),
			hidePrivateData,
			cancellationToken);

	public static async Task RunAsync(
		IReadOnlyList<string> roots,
		Stream input,
		Stream output,
		CancellationToken cancellationToken = default,
		Func<string>? appDataPathProvider = null,
		string? tempRoot = null) =>
		await RunAsync(
				roots,
				input,
				output,
				false,
				cancellationToken,
				appDataPathProvider,
				tempRoot)
			.ConfigureAwait(false);

	public static async Task RunAsync(
		IReadOnlyList<string> roots,
		Stream input,
		Stream output,
		bool hidePrivateData,
		CancellationToken cancellationToken = default,
		Func<string>? appDataPathProvider = null,
		string? tempRoot = null)
	{
		ArgumentNullException.ThrowIfNull(roots);
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);

		var rootRegistry = new McpRootRegistry(roots);
		using var services = McpServices.Create(rootRegistry, appDataPathProvider);
		using var packs = new McpPackRegistry(tempRoot);
		var projectService = new McpProjectService(rootRegistry, services, hidePrivateData);
		var tools = new DevProjexMcpTools(rootRegistry, projectService, packs);
		var catalog = new DevProjexMcpToolCatalog(tools);

		var builder = Host.CreateApplicationBuilder([]);
		builder.Logging.ClearProviders();
		builder.Services.AddSingleton(services);
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

		using var host = builder.Build();
		await host.RunAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string ResolveVersion() =>
		typeof(McpServerHost).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
			.InformationalVersion ?? "0.0.0";
}
