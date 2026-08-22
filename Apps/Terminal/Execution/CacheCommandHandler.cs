using System.Globalization;
using System.Text.Json;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

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
		var result = services.RepoCacheService.ListCacheEntriesForManagement();
		var entries = result.Entries;
		if (format == CliTextJsonFormat.Json)
		{
			WriteJsonList(entries, result.IsComplete);
		}
		else
		{
			foreach (var entry in entries)
				environment.Output.WriteLine(FormatTextEntry(entry));
		}

		if (result.IsComplete)
			return CommandLineExitCodes.Success;

		environment.Error.WriteLine(services.Localization.Format(
			"Terminal.Cache.ListIncomplete",
			result.UnavailableRootCount));
		return CommandLineExitCodes.PolicyFailure;
	}

	public int Remove(string repositoryUrl)
	{
		var result = services.RepoCacheService.RemoveCachedRepositoryWithResult(repositoryUrl);
		if (result.Removed + result.Retained + result.Failed == 0)
		{
			var safeUrl = TerminalTextEscaping.EscapeSingleLine(
				RepositoryUrlUtility.ToSafeDisplay(repositoryUrl));
			environment.Error.WriteLine(services.Localization.Format(
				"Terminal.Cache.NotFound",
				safeUrl));
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

	internal static string FormatTextEntry(RepositoryCacheCatalogEntry entry) =>
		string.Join(
			'\t',
			TerminalTextEscaping.EscapeSingleLine(entry.RepositoryUrl),
			ToToken(entry.State),
			TerminalTextEscaping.EscapeSingleLine(entry.Branch ?? "-"),
			TerminalTextEscaping.EscapeSingleLine(entry.CommitHash ?? "-"),
			entry.ApproximateSizeBytes.ToString(CultureInfo.InvariantCulture),
			entry.LastOpenedUtc.ToUniversalTime().ToString("O"),
			TerminalTextEscaping.EscapeSingleLine(NormalizePath(entry.LocalPath)));

	private void WriteJsonList(
		IReadOnlyList<RepositoryCacheCatalogEntry> entries,
		bool isComplete)
	{
		var items = entries.Select(static entry => new
		{
			url = entry.RepositoryUrl,
			state = ToToken(entry.State),
			branch = entry.Branch,
			commit = entry.CommitHash,
			localPath = NormalizePath(entry.LocalPath),
			approximateSizeBytes = entry.ApproximateSizeBytes,
			lastUsed = entry.LastOpenedUtc.ToUniversalTime()
		}).ToArray();
		var payload = isComplete
			? (object)new
			{
				schemaVersion = 1,
				kind = "devprojex-repository-cache",
				items
			}
			: new
			{
				schemaVersion = 1,
				kind = "devprojex-repository-cache",
				incomplete = true,
				items
			};
		environment.Output.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
	}
}
