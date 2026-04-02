using System.Text.Json;
using System.Text.Json.Serialization;
using DevProjex.Kernel;

namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentProjectsStore(Func<string>? appDataPathProvider = null)
{
	private const int CurrentSchemaVersion = 1;
	private const int MaxRecentFolders = 10;
	private const int MaxRecentRepositories = 7;
	private const string FolderName = "DevProjex";
	private const string FileName = "recent-projects.json";
	private const string BackupExtension = ".bak";
	private static readonly string RepoCacheRootPath = Path.Combine(
		Path.GetTempPath(),
		FolderName,
		"RepoCache");

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly object _sync = new();
	private readonly Func<string> _appDataPathProvider =
		appDataPathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

	public RecentProjectsDb Load()
	{
		lock (_sync)
		{
			return LoadInternal();
		}
	}

	public RecentProjectsDb AddFolder(RecentProjectsDb? db, string path)
	{
		lock (_sync)
		{
			var state = Normalize(db ?? LoadInternal());
			if (!TryNormalizeFolderPath(path, out var normalizedPath))
				return state;

			MoveToFront(
				state.RecentFolders,
				normalizedPath,
				MaxRecentFolders,
				PathComparer.Default,
				static entry => entry.Path,
				static value => value,
				static (value, openedUtc) => new RecentFolderEntry
				{
					Path = value,
					OpenedUtc = openedUtc
				});

			TrySave(state);
			return state;
		}
	}

	public RecentProjectsDb AddRepository(RecentProjectsDb? db, string repositoryUrl)
	{
		lock (_sync)
		{
			var state = Normalize(db ?? LoadInternal());
			if (!TryNormalizeRepositoryUrl(repositoryUrl, out var normalizedUrl))
				return state;

			MoveToFront(
				state.RecentRepositories,
				normalizedUrl,
				MaxRecentRepositories,
				StringComparer.OrdinalIgnoreCase,
				static entry => entry.Url,
				static value => NormalizeRepositoryComparisonKey(value),
				static (value, openedUtc) => new RecentRepositoryEntry
				{
					Url = value,
					OpenedUtc = openedUtc
				});

			TrySave(state);
			return state;
		}
	}

	public bool TryPersist(RecentProjectsDb? db)
	{
		lock (_sync)
		{
			var state = Normalize(db ?? LoadInternal());
			return TrySave(state);
		}
	}

	public string GetPath()
	{
		var root = _appDataPathProvider();
		return Path.Combine(root, FolderName, FileName);
	}

	private RecentProjectsDb LoadInternal()
	{
		var path = GetPath();
		if (TryLoadFromPath(path, out var primaryDb, out var primaryRequiresRewrite))
		{
			if (primaryRequiresRewrite)
				TrySave(primaryDb);

			return primaryDb;
		}

		var backupPath = GetBackupPath(path);

		// Keep the last known-good snapshot as a recovery path.
		// A partially written or externally corrupted primary file must not silently erase history.
		if (TryLoadFromPath(backupPath, out var backupDb, out _))
		{
			TrySave(backupDb);
			return backupDb;
		}

		return CreateDefaultDb();
	}

	private static RecentProjectsDb CreateDefaultDb()
	{
		return new RecentProjectsDb
		{
			SchemaVersion = CurrentSchemaVersion,
			RecentFolders = [],
			RecentRepositories = []
		};
	}

	private static RecentProjectsDb Normalize(RecentProjectsDb db)
	{
		db.SchemaVersion = CurrentSchemaVersion;
		db.RecentFolders ??= [];
		db.RecentRepositories ??= [];

		db.RecentFolders = NormalizeFolders(db.RecentFolders);
		db.RecentRepositories = NormalizeRepositories(db.RecentRepositories);
		return db;
	}

	private static List<RecentFolderEntry> NormalizeFolders(IEnumerable<RecentFolderEntry> entries)
	{
		var ordered = entries
			.Where(static entry => entry is not null && TryNormalizeFolderPath(entry.Path, out _))
			.Select(static entry => new RecentFolderEntry
			{
				Path = PathUtility.Normalize(entry.Path),
				OpenedUtc = entry.OpenedUtc <= DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : entry.OpenedUtc
			})
			.Where(static entry => !IsRepoCachePath(entry.Path))
			.OrderByDescending(static entry => entry.OpenedUtc)
			.ToList();

		var unique = new List<RecentFolderEntry>();
		var seen = new HashSet<string>(PathComparer.Default);
		foreach (var entry in ordered)
		{
			if (seen.Add(entry.Path))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentFolders)
			unique.RemoveRange(MaxRecentFolders, unique.Count - MaxRecentFolders);

		return unique;
	}

	private static List<RecentRepositoryEntry> NormalizeRepositories(IEnumerable<RecentRepositoryEntry> entries)
	{
		var ordered = entries
			.Where(static entry => entry is not null && TryNormalizeRepositoryUrl(entry.Url, out _))
			.Select(static entry => new RecentRepositoryEntry
			{
				Url = NormalizeRepositoryUrl(entry.Url),
				OpenedUtc = entry.OpenedUtc <= DateTimeOffset.UnixEpoch ? DateTimeOffset.UtcNow : entry.OpenedUtc
			})
			.OrderByDescending(static entry => entry.OpenedUtc)
			.ToList();

		var unique = new List<RecentRepositoryEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in ordered)
		{
			if (seen.Add(NormalizeRepositoryComparisonKey(entry.Url)))
				unique.Add(entry);
		}

		if (unique.Count > MaxRecentRepositories)
			unique.RemoveRange(MaxRecentRepositories, unique.Count - MaxRecentRepositories);

		return unique;
	}

	private static void MoveToFront<TEntry>(
		List<TEntry> entries,
		string normalizedValue,
		int limit,
		IEqualityComparer<string> comparer,
		Func<TEntry, string> keySelector,
		Func<string, string> comparisonKeySelector,
		Func<string, DateTimeOffset, TEntry> factory)
	{
		var normalizedComparisonKey = comparisonKeySelector(normalizedValue);
		entries.RemoveAll(entry => comparer.Equals(comparisonKeySelector(keySelector(entry)), normalizedComparisonKey));
		entries.Insert(0, factory(normalizedValue, DateTimeOffset.UtcNow));

		if (entries.Count > limit)
			entries.RemoveRange(limit, entries.Count - limit);
	}

	private bool TrySave(RecentProjectsDb db)
	{
		try
		{
			var path = GetPath();
			var backupPath = GetBackupPath(path);
			var directory = Path.GetDirectoryName(path);
			if (string.IsNullOrWhiteSpace(directory))
				return false;

			Directory.CreateDirectory(directory);
			var json = JsonSerializer.Serialize(db, SerializerOptions);
			var tempPath = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
			File.WriteAllText(tempPath, json);

			try
			{
				if (File.Exists(path))
					File.Replace(tempPath, path, backupPath);
				else
					File.Move(tempPath, path);
			}
			catch
			{
				File.Move(tempPath, path, overwrite: true);
			}

			TryUpdateBackup(path, backupPath);
			return true;
		}
		catch
		{
			// Ignore persistence errors. The application must remain usable without this cache.
			return false;
		}
	}

	private static bool TryLoadFromPath(string path, out RecentProjectsDb db, out bool requiresRewrite)
	{
		db = CreateDefaultDb();
		requiresRewrite = false;

		if (!File.Exists(path))
			return false;

		try
		{
			var json = File.ReadAllText(path);
			var deserialized = JsonSerializer.Deserialize<RecentProjectsDb>(json, SerializerOptions);
			if (deserialized is null)
				return false;

			// Normalize in-memory and rewrite only structurally valid payloads.
			// Invalid payloads are left untouched so operators can inspect them and the backup can recover them.
			var originalSnapshot = JsonSerializer.Serialize(deserialized, SerializerOptions);
			var normalized = Normalize(deserialized);
			var normalizedSnapshot = JsonSerializer.Serialize(normalized, SerializerOptions);
			requiresRewrite = !string.Equals(originalSnapshot, normalizedSnapshot, StringComparison.Ordinal);
			db = normalized;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetBackupPath(string path) => $"{path}{BackupExtension}";

	private static void TryUpdateBackup(string path, string backupPath)
	{
		try
		{
			// Copy the final primary snapshot after a successful save instead of writing a separate branch.
			// That keeps the backup byte-for-byte aligned with the last committed primary state.
			if (File.Exists(path))
				File.Copy(path, backupPath, overwrite: true);
		}
		catch
		{
			// Best effort only. The primary file must remain authoritative.
		}
	}

	private static bool TryNormalizeFolderPath(string path, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			normalizedPath = PathUtility.Normalize(path);
			return !string.IsNullOrWhiteSpace(normalizedPath) && !IsRepoCachePath(normalizedPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsRepoCachePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			return PathUtility.IsPathInside(path, RepoCacheRootPath);
		}
		catch
		{
			return false;
		}
	}

	private static bool TryNormalizeRepositoryUrl(string repositoryUrl, out string normalizedUrl)
	{
		normalizedUrl = string.Empty;
		if (string.IsNullOrWhiteSpace(repositoryUrl))
			return false;

		normalizedUrl = NormalizeRepositoryUrl(repositoryUrl);
		return !string.IsNullOrWhiteSpace(normalizedUrl);
	}

	private static string NormalizeRepositoryUrl(string repositoryUrl)
	{
		var trimmed = repositoryUrl.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
			return string.Empty;

		trimmed = trimmed.Replace('\\', '/').TrimEnd('/');
		if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
		{
			var builder = new UriBuilder(uri)
			{
				Fragment = string.Empty,
				Query = string.Empty
			};

			return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
		}

		return trimmed;
	}

	private static string NormalizeRepositoryComparisonKey(string repositoryUrl)
	{
		var normalized = NormalizeRepositoryUrl(repositoryUrl);
		return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? normalized[..^4]
			: normalized;
	}
}
