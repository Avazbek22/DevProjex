using System.Diagnostics;
using System.Text.Json;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Kernel.IO;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public sealed class TerminalSettingsStore
{
	private const int CurrentSchemaVersion = 1;
	private const int MaximumDocumentBytes = 512 * 1024;
	private const int MaximumProjectSettings = 32;
	private const UnixFileMode PrivateUnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
	private readonly Func<string> _appDataPathProvider;
	private readonly Action? _afterReadOpened;
	private readonly Action<string> _diagnosticSink;
	private readonly SemaphoreSlim _writeGate = new(1, 1);

	public TerminalSettingsStore(Func<string>? appDataPathProvider = null)
		: this(appDataPathProvider, afterReadOpened: null, diagnosticSink: null)
	{
	}

	internal TerminalSettingsStore(
		Func<string>? appDataPathProvider,
		Action? afterReadOpened,
		Action<string>? diagnosticSink = null)
	{
		_appDataPathProvider = appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
		_afterReadOpened = afterReadOpened;
		_diagnosticSink = diagnosticSink ?? (message => Trace.TraceWarning("{0}", message));
	}

	public TerminalScreenMode LoadScreenMode() =>
		LoadDocument()?.ScreenMode is { } screenMode && Enum.IsDefined(screenMode)
			? screenMode
			: TerminalScreenMode.Auto;

	public IReadOnlyList<string> LoadCommandHistory() =>
		new TerminalCommandHistory(LoadDocument()?.CommandHistory).Entries.ToArray();

	public AppLanguage? LoadLanguage()
	{
		var code = LoadDocument()?.Language;
		return AppLanguageUtility.TryParseCode(code, out var language)
			? language
			: null;
	}

	public TerminalProjectSettings? LoadProjectSettings(string projectRoot)
	{
		var normalizedRoot = NormalizeProjectRoot(projectRoot);
		return LoadDocument()?.Projects?
			.FirstOrDefault(project => PathComparer.Default.Equals(project.Root, normalizedRoot));
	}

	public async Task SaveProjectSettingsAsync(
		TerminalProjectSettings settings,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(settings);
		var normalized = settings with
		{
			Root = NormalizeProjectRoot(settings.Root),
			LastUsedUtc = DateTimeOffset.UtcNow
		};
		await UpdateAsync(current =>
		{
			var projects = (current.Projects ?? [])
				.Where(project => !PathComparer.Default.Equals(project.Root, normalized.Root))
				.Append(normalized)
				.OrderByDescending(static project => project.LastUsedUtc)
				.Take(MaximumProjectSettings)
				.ToArray();
			return current with { Projects = projects };
		}, cancellationToken).ConfigureAwait(false);
	}

	public async Task SaveScreenModeAsync(
		TerminalScreenMode screenMode,
		CancellationToken cancellationToken = default)
	{
		await UpdateAsync(
			current => current with { ScreenMode = screenMode },
			cancellationToken).ConfigureAwait(false);
	}

	public async Task SaveCommandHistoryAsync(
		IReadOnlyList<string> history,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(history);
		var normalized = new TerminalCommandHistory(history)
			.Entries
			.ToArray();
		await UpdateAsync(
			current => current with { CommandHistory = normalized },
			cancellationToken).ConfigureAwait(false);
	}

	public async Task SaveLanguageAsync(
		AppLanguage language,
		CancellationToken cancellationToken = default)
	{
		await UpdateAsync(
			current => current with { Language = AppLanguageUtility.ToCode(language) },
			cancellationToken).ConfigureAwait(false);
	}

	internal string GetPath() =>
		Path.Combine(_appDataPathProvider(), "DevProjex", "terminal-settings.json");

	private TerminalSettingsDocument? LoadDocument()
		=> LoadDocument(out _);

	private TerminalSettingsDocument? LoadDocument(out bool hasFutureSchema)
	{
		hasFutureSchema = false;
		try
		{
			var path = GetPath();
			if (!File.Exists(path))
				return null;
			TryEnsurePrivateUnixFileMode(path);

			using var source = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				bufferSize: 4 * 1024,
				FileOptions.SequentialScan);
			_afterReadOpened?.Invoke();
			using var stream = new MaximumLengthReadStream(
				source,
				MaximumDocumentBytes,
				static () => new TerminalSettingsLimitException());
			using var json = JsonDocument.Parse(stream);
			hasFutureSchema = IsFutureSchema(json.RootElement);
			if (hasFutureSchema)
				return null;

			var settings = json.RootElement.Deserialize<TerminalSettingsDocument>();
			return settings is { SchemaVersion: CurrentSchemaVersion }
				? settings
				: null;
		}
		catch (TerminalSettingsLimitException)
		{
			// The schema cannot be classified within this version's resource limit. Preserve the
			// document exactly as a potentially valid settings file written by a newer version.
			hasFutureSchema = true;
			_diagnosticSink("Terminal settings exceed the size limit; the document was preserved and ignored.");
			return null;
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       JsonException)
		{
			return null;
		}
	}

	private static bool IsFutureSchema(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object)
			return false;

		if (!root.TryGetProperty("SchemaVersion", out var version))
			return false;
		if (version.TryGetInt64(out var signed))
			return signed > CurrentSchemaVersion;
		return version.TryGetUInt64(out var unsigned) && unsigned > CurrentSchemaVersion;
	}

	private static void TryEnsurePrivateUnixFileMode(string path)
	{
		if (OperatingSystem.IsWindows())
			return;

		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
				return;

			using var handle = File.OpenHandle(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			if (File.GetUnixFileMode(handle) != PrivateUnixFileMode)
				File.SetUnixFileMode(handle, PrivateUnixFileMode);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
		                                      PlatformNotSupportedException or NotSupportedException)
		{
			// Unsupported permission metadata must not make terminal settings unreadable.
		}
	}

	private async Task UpdateAsync(
		Func<TerminalSettingsDocument, TerminalSettingsDocument> update,
		CancellationToken cancellationToken)
	{
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var path = GetPath();
			using var persistenceLock = await PersistenceFileLock
				.AcquireAsync(path, cancellationToken)
				.ConfigureAwait(false);
			var current = LoadDocument(out var hasFutureSchema);
			if (hasFutureSchema)
			{
				_diagnosticSink("Terminal settings update was skipped because the existing document could not be safely classified.");
				return;
			}
			current ??= new TerminalSettingsDocument(
				CurrentSchemaVersion,
				TerminalScreenMode.Auto,
				[]);
			var document = update(current) with { SchemaVersion = CurrentSchemaVersion };
			var directory = Path.GetDirectoryName(path)!;
			Directory.CreateDirectory(directory);
			var temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
			try
			{
				var streamOptions = new FileStreamOptions
				{
					Mode = FileMode.CreateNew,
					Access = FileAccess.Write,
					Share = FileShare.None,
					BufferSize = 4 * 1024,
					Options = FileOptions.Asynchronous | FileOptions.SequentialScan
				};
				if (!OperatingSystem.IsWindows())
					streamOptions.UnixCreateMode = PrivateUnixFileMode;

				await using (var stream = new FileStream(temporaryPath, streamOptions))
				{
					await JsonSerializer.SerializeAsync(
							stream,
							document,
							cancellationToken: cancellationToken)
						.ConfigureAwait(false);
					await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
					if (stream.Length > MaximumDocumentBytes)
					{
						_diagnosticSink(
							"Terminal settings update exceeded the size limit and was not committed.");
						return;
					}
				}

				if (File.Exists(path))
				{
					try
					{
						File.Replace(temporaryPath, path, destinationBackupFileName: null);
					}
					catch (NotSupportedException)
					{
						File.Move(temporaryPath, path, overwrite: true);
					}
				}
				else
				{
					File.Move(temporaryPath, path);
				}
			}
			finally
			{
				try
				{
					if (File.Exists(temporaryPath))
						File.Delete(temporaryPath);
				}
				catch (Exception exception) when (exception is
					       IOException or
					       UnauthorizedAccessException)
				{
					// A unique temporary settings file is harmless if a platform delays handle release.
				}
			}
		}
		finally
		{
			_writeGate.Release();
		}
	}

	private sealed record TerminalSettingsDocument(
		int SchemaVersion,
		TerminalScreenMode ScreenMode,
		IReadOnlyList<string>? CommandHistory = null,
		string? Language = null,
		IReadOnlyList<TerminalProjectSettings>? Projects = null);

	private static string NormalizeProjectRoot(string root) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

	private sealed class TerminalSettingsLimitException()
		: IOException("Terminal settings exceed the size limit.");
}

public sealed record TerminalProjectSettings(
	string Root,
	IReadOnlyList<string> SelectedPaths,
	IReadOnlyList<string> ExpandedPaths,
	string? FocusedPath,
	ProjectContextView PreviewView,
	ProjectContextDocumentFormat Format,
	DateTimeOffset LastUsedUtc);
