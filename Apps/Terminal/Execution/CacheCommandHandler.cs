using System.Globalization;
using System.Text.Json;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Execution;

internal sealed class CacheCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	public int WritePath()
	{
		environment.Output.WriteLine(services.RepoCacheService.CacheRootPath);
		return CommandLineExitCodes.Success;
	}

	public int WriteList(CliTextJsonFormat format)
	{
		var entries = services.RepoCacheService.ListIndexedRepositories();
		if (format == CliTextJsonFormat.Json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-repository-cache",
					items = entries.Select(static entry => new
					{
						url = entry.RepositoryUrl,
						state = ToToken(entry.State),
						branch = entry.Branch,
						commit = entry.CommitHash,
						localPath = NormalizePath(entry.LocalPath),
						approximateSizeBytes = entry.ApproximateSizeBytes,
						lastUsed = entry.LastOpenedUtc.ToUniversalTime()
					})
				},
				JsonOptions));
			return CommandLineExitCodes.Success;
		}

		foreach (var entry in entries)
		{
			environment.Output.WriteLine(string.Join(
				'\t',
				entry.RepositoryUrl,
				ToToken(entry.State),
				entry.Branch ?? "-",
				entry.CommitHash ?? "-",
				entry.ApproximateSizeBytes.ToString(CultureInfo.InvariantCulture),
				entry.LastOpenedUtc.ToUniversalTime().ToString("O"),
				entry.LocalPath));
		}

		return CommandLineExitCodes.Success;
	}

	public int Remove(string repositoryUrl)
	{
		var result = services.RepoCacheService.RemoveCachedRepositoryWithResult(repositoryUrl);
		if (result.Removed + result.Retained + result.Failed == 0)
		{
			environment.Error.WriteLine(services.Localization.Format(
				"Terminal.Cache.NotFound",
				repositoryUrl));
			return CommandLineExitCodes.RuntimeError;
		}

		return WriteRemovalResult(result);
	}

	public int Clear() =>
		WriteRemovalResult(services.RepoCacheService.ClearAllCacheWithResult());

	private int WriteRemovalResult(CacheClearResult result)
	{
		environment.Output.WriteLine(services.Localization.Format(
			"Terminal.Cache.Result",
			result.Removed,
			result.Retained,
			result.Failed));
		return result.IsComplete
			? CommandLineExitCodes.Success
			: CommandLineExitCodes.PolicyFailure;
	}

	private static string ToToken(RepositoryCacheEntryState state) =>
		state switch
		{
			RepositoryCacheEntryState.Ready => "ready",
			RepositoryCacheEntryState.Damaged => "damaged",
			_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
		};

	private static string NormalizePath(string path) => path.Replace('\\', '/');
}
