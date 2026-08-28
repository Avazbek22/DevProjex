using System.Globalization;
using System.Text.Json;
using DevProjex.Kernel.Models;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Terminal.Tui;

namespace DevProjex.Terminal.Execution;

internal sealed class CacheCommandHandler(
	TerminalCacheServices services,
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

	public int WriteList(CliTextJsonFormat format, TerminalOutputOptions outputOptions)
	{
		var result = services.RepoCacheService.ListCacheEntriesForManagement();
		var entries = result.Entries;
		if (format == CliTextJsonFormat.Json)
		{
			WriteJsonList(entries, result.IsComplete, result.BusyRootCount > 0);
		}
		else
		{
			foreach (var line in FormatTextEntries(entries, services.Localization, environment, outputOptions))
				environment.Output.WriteLine(line);
			if (environment.IsOutputInteractive && !environment.IsTermDumb)
			{
				var totalBytes = entries.Sum(static entry => Math.Max(0, entry.ApproximateSizeBytes));
				environment.Output.WriteLine(services.Localization.Format(
					"Terminal.Cache.Summary",
					entries.Count,
					FormatByteSize(totalBytes)));
			}
		}

		if (result.IsComplete)
			return CommandLineExitCodes.Success;

		WriteIncompleteWarnings(result);
		return result.BusyRootCount > 0
			? CommandLineExitCodes.RuntimeError
			: CommandLineExitCodes.PolicyFailure;
	}

	public int Remove(string repositoryUrl)
		=> Remove(repositoryUrl, CliTextJsonFormat.Text, dryRun: false);

	public int Remove(
		string repositoryUrl,
		CliTextJsonFormat format,
		bool dryRun)
	{
		var lookup = FindEntries(repositoryUrl);
		var before = lookup.Entries;
		if (dryRun)
		{
			if (before.Count == 0)
				return lookup.Management.BusyRootCount > 0
					? WriteBusy(format, dryRun: true)
					: WriteNotFound(repositoryUrl, format, dryRun: true);
			return WriteRemovalResult(
				new CacheClearResult(
					before.Count,
					0,
					lookup.Management.UnavailableRootCount,
					lookup.Management.BusyRootCount),
				before.Sum(static entry => Math.Max(0, entry.ApproximateSizeBytes)),
				format,
				dryRun: true);
		}
		if (before.Count == 0 && lookup.Management.BusyRootCount > 0)
			return WriteBusy(format, dryRun: false);
		var result = services.RepoCacheService.RemoveCachedRepositoryWithResult(repositoryUrl);
		if (result.Removed + result.Retained + result.Failed == 0)
			return result.BusyRootCount > 0
				? WriteBusy(format, dryRun: false)
				: WriteNotFound(repositoryUrl, format, dryRun: false);

		return WriteRemovalResult(result, ResolveRemovedBytes(before), format, dryRun: false);
	}

	public int Clear() => Clear(CliTextJsonFormat.Text, dryRun: false);

	public int Clear(CliTextJsonFormat format, bool dryRun)
	{
		var before = services.RepoCacheService.ListCacheEntriesForManagement();
		var bytes = before.Entries.Sum(static entry => Math.Max(0, entry.ApproximateSizeBytes));
		if (dryRun)
		{
			return WriteRemovalResult(
				new CacheClearResult(
					before.Entries.Count,
					0,
					before.UnavailableRootCount,
					before.BusyRootCount),
				bytes,
				format,
				dryRun: true);
		}
		if (before.Entries.Count == 0 && before.BusyRootCount > 0)
			return WriteBusy(format, dryRun: false);

		var result = services.RepoCacheService.ClearAllCacheWithResult();
		return WriteRemovalResult(result, ResolveRemovedBytes(before.Entries), format, dryRun: false);
	}

	public async Task<int> UpdateAsync(string repositoryUrl, CancellationToken cancellationToken)
	{
		var lookup = FindEntries(repositoryUrl);
		var indexed = lookup.Entries.FirstOrDefault();
		if (indexed is null)
			return lookup.Management.BusyRootCount > 0
				? WriteBusy(CliTextJsonFormat.Text, dryRun: false)
				: WriteNotFound(repositoryUrl, CliTextJsonFormat.Text, dryRun: false);

		using var lease = await new TerminalRepositoryCloneCoordinator(
				services.GitRepositoryService,
				services.RepoCacheService)
			.AcquireAsync(repositoryUrl, progress: null, phaseChanged: null, cancellationToken)
			.ConfigureAwait(false);
		if (lease.UpdateFailed)
		{
			environment.Error.WriteLine(services.Localization["Terminal.Cache.UpdateFailed"]);
			return CommandLineExitCodes.RuntimeError;
		}

		var repositoryPath = lease.Result.LocalPath;
		var commit = await services.GitRepositoryService
			.GetHeadCommitAsync(repositoryPath, cancellationToken)
			.ConfigureAwait(false);
		services.RepoCacheService.RecordIndexedRepository(
			repositoryUrl,
			repositoryPath,
			lease.Result.DefaultBranch,
			commit);
		services.RepoCacheService.RefreshIndexedRepositorySize(repositoryPath);
		environment.Output.WriteLine(services.Localization["Toast.Git.UpdatesApplied"]);
		return CommandLineExitCodes.Success;
	}

	private int WriteRemovalResult(
		CacheClearResult result,
		long bytes,
		CliTextJsonFormat format,
		bool dryRun)
	{
		if (format == CliTextJsonFormat.Json)
		{
			var payload = new Dictionary<string, object?>
			{
				["schemaVersion"] = 1,
				["kind"] = "devprojex-cache-removal",
				["dryRun"] = dryRun,
				["removed"] = result.Removed,
				["retained"] = result.Retained,
				["failed"] = result.Failed,
				["bytes"] = Math.Max(0, bytes)
			};
			if (result.BusyRootCount > 0)
				payload["busy"] = true;
			environment.Output.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
		}
		else
		{
			environment.Output.WriteLine(services.Localization.Format(
				dryRun ? "Terminal.Cache.DryRunResult" : "Terminal.Cache.ResultWithSize",
				result.Removed,
				result.Retained,
				result.Failed,
				FormatByteSize(bytes)));
		}
		if (result.BusyRootCount > 0)
		{
			environment.Error.WriteLine(services.Localization["Terminal.Cache.IndexBusy"]);
			return CommandLineExitCodes.RuntimeError;
		}
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

	internal static IReadOnlyList<string> FormatTextEntries(
		IReadOnlyList<RepositoryCacheCatalogEntry> entries,
		LocalizationService localization,
		ITerminalEnvironment environment,
		TerminalOutputOptions outputOptions)
	{
		var rows = entries.Select(entry => new[]
		{
			TerminalTextEscaping.EscapeSingleLine(entry.RepositoryUrl),
			localization[entry.State == RepositoryCacheEntryState.Ready
				? "Terminal.Tui.DestinationReady"
				: "Terminal.Tui.RecentRepositories.Damaged"],
			TerminalTextEscaping.EscapeSingleLine(entry.Branch ?? "-"),
			TerminalTextEscaping.EscapeSingleLine(ShortCommit(entry.CommitHash)),
			FormatByteSize(entry.ApproximateSizeBytes),
			entry.LastOpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
			TerminalTextEscaping.EscapeSingleLine(NormalizePath(entry.LocalPath))
		}).ToArray();
		return TerminalColumnLayout.FormatForOutput(
			rows,
			[
				localization["Terminal.Tui.RecentRepositories.Repository"],
				localization["Terminal.Tui.Recent.Status"],
				localization["Terminal.Tui.RecentRepositories.Branch"],
				localization["Terminal.Tui.Commit"],
				localization["Terminal.Analysis.Size"],
				localization["Terminal.Tui.Recent.LastOpened"],
				localization["Terminal.Tui.Recent.Path"]
			],
			environment,
			outputOptions,
			truncationColumn: 6);
	}

	private int WriteNotFound(
		string repositoryUrl,
		CliTextJsonFormat format,
		bool dryRun)
	{
		if (format == CliTextJsonFormat.Json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-cache-removal",
					dryRun,
					notFound = true,
					removed = 0,
					retained = 0,
					failed = 0,
					bytes = 0
				},
				JsonOptions));
			return CommandLineExitCodes.UsageError;
		}

		var safeUrl = TerminalTextEscaping.EscapeSingleLine(
			RepositoryUrlUtility.ToSafeDisplay(repositoryUrl));
		environment.Error.WriteLine(services.Localization.Format(
			"Terminal.Cache.NotFound",
			safeUrl));
		return CommandLineExitCodes.UsageError;
	}

	private CacheEntryLookup FindEntries(string repositoryUrl)
	{
		var management = services.RepoCacheService.ListCacheEntriesForManagement();
		string identity;
		try
		{
			identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		}
		catch
		{
			return new CacheEntryLookup([], management);
		}
		return new CacheEntryLookup(management.Entries
			.Where(entry => string.Equals(
				RepositoryUrlUtility.GetComparisonKey(entry.RepositoryUrl),
				identity,
				StringComparison.Ordinal))
			.ToArray(), management);
	}

	private int WriteBusy(CliTextJsonFormat format, bool dryRun)
	{
		if (format == CliTextJsonFormat.Json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-cache-removal",
					dryRun,
					busy = true,
					removed = 0,
					retained = 0,
					failed = 0,
					bytes = 0
				},
				JsonOptions));
		}
		environment.Error.WriteLine(services.Localization["Terminal.Cache.IndexBusy"]);
		return CommandLineExitCodes.RuntimeError;
	}

	private void WriteIncompleteWarnings(RepositoryCacheManagementListResult result)
	{
		if (result.BusyRootCount > 0)
			environment.Error.WriteLine(services.Localization["Terminal.Cache.IndexBusy"]);
		if (result.NonBusyUnavailableRootCount > 0)
			environment.Error.WriteLine(services.Localization.Format(
				"Terminal.Cache.ListIncomplete",
				result.NonBusyUnavailableRootCount));
	}

	private long ResolveRemovedBytes(IReadOnlyList<RepositoryCacheCatalogEntry> before)
	{
		var remaining = services.RepoCacheService.ListCacheEntriesForManagement().Entries
			.Select(static entry => entry.LocalPath)
			.ToHashSet(PathComparer.Default);
		return before
			.Where(entry => !remaining.Contains(entry.LocalPath))
			.Sum(static entry => Math.Max(0, entry.ApproximateSizeBytes));
	}

	private static string ShortCommit(string? commitHash) =>
		string.IsNullOrWhiteSpace(commitHash)
			? "-"
			: commitHash.Length <= 12 ? commitHash : commitHash[..12];

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
		bool isComplete,
		bool isBusy = false)
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
			: isBusy
				? (object)new
				{
					schemaVersion = 1,
					kind = "devprojex-repository-cache",
					incomplete = true,
					busy = true,
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

	private sealed record CacheEntryLookup(
		IReadOnlyList<RepositoryCacheCatalogEntry> Entries,
		RepositoryCacheManagementListResult Management);
}
