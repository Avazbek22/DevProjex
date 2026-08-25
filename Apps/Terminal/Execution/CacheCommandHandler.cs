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
		TerminalTextEscaping.WriteSingleLine(
			environment.Output,
			services.RepoCacheService.CacheRootPath);
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
			foreach (var line in FormatTextEntries(entries))
				environment.Output.WriteLine(line);
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

	private static string NormalizePath(string path) => PathUtility.NormalizeSeparators(path);

	internal static IReadOnlyList<string> FormatTextEntries(
		IReadOnlyList<RepositoryCacheCatalogEntry> entries) =>
		TerminalColumnLayout.Format(entries.Select(static entry => new[]
		{
			TerminalTextEscaping.EscapeSingleLine(entry.RepositoryUrl),
			ToToken(entry.State),
			TerminalTextEscaping.EscapeSingleLine(entry.Branch ?? "-"),
			TerminalTextEscaping.EscapeSingleLine(entry.CommitHash ?? "-"),
			FormatByteSize(entry.ApproximateSizeBytes),
			entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
			TerminalTextEscaping.EscapeSingleLine(NormalizePath(entry.LocalPath))
		}).ToArray());

	internal static string FormatByteSize(long bytes)
	{
		string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
		var value = Math.Max(0, bytes);
		var display = (double)value;
		var unit = 0;
		while (display >= 1024 && unit < units.Length - 1)
		{
			display /= 1024;
			unit++;
		}

		return unit == 0
			? $"{value.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
			: $"{display.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
	}

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
