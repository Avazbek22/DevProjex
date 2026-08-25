using System.Text.Json;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public sealed class TerminalSettingsStore(Func<string>? appDataPathProvider = null)
{
	private const int CurrentSchemaVersion = 1;
	private const int MaximumPersistedCommandLength = 4_096;
	private const int MaximumDocumentBytes = 512 * 1024;
	private readonly Func<string> _appDataPathProvider =
		appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;
	private readonly SemaphoreSlim _writeGate = new(1, 1);

	public TerminalScreenMode LoadScreenMode() =>
		LoadDocument()?.ScreenMode is { } screenMode && Enum.IsDefined(screenMode)
			? screenMode
			: TerminalScreenMode.Auto;

	public IReadOnlyList<string> LoadCommandHistory() =>
		new TerminalCommandHistory(LoadDocument()?.CommandHistory).Entries.ToArray();

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
			.Select(TruncateCommand)
			.ToArray();
		await UpdateAsync(
			current => current with { CommandHistory = normalized },
			cancellationToken).ConfigureAwait(false);
	}

	private static string TruncateCommand(string command)
	{
		if (command.Length <= MaximumPersistedCommandLength)
			return command;

		var length = MaximumPersistedCommandLength;
		if (char.IsHighSurrogate(command[length - 1]) && char.IsLowSurrogate(command[length]))
			length--;
		return command[..length];
	}

	internal string GetPath() =>
		Path.Combine(_appDataPathProvider(), "DevProjex", "terminal-settings.json");

	private TerminalSettingsDocument? LoadDocument()
	{
		try
		{
			var path = GetPath();
			if (!File.Exists(path))
				return null;

			using var stream = new MaximumLengthReadStream(
				new FileStream(
					path,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read,
					bufferSize: 4 * 1024,
					FileOptions.SequentialScan),
				MaximumDocumentBytes,
				static () => new IOException("Terminal settings exceed the size limit."));
			var document = JsonSerializer.Deserialize<TerminalSettingsDocument>(stream);
			return document is { SchemaVersion: CurrentSchemaVersion }
				? document
				: null;
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       JsonException)
		{
			return null;
		}
	}

	private async Task UpdateAsync(
		Func<TerminalSettingsDocument, TerminalSettingsDocument> update,
		CancellationToken cancellationToken)
	{
		await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var current = LoadDocument() ??
			              new TerminalSettingsDocument(
				              CurrentSchemaVersion,
				              TerminalScreenMode.Auto,
				              []);
			var document = update(current) with { SchemaVersion = CurrentSchemaVersion };
			var path = GetPath();
			var directory = Path.GetDirectoryName(path)!;
			Directory.CreateDirectory(directory);
			var temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
			try
			{
				await using (var stream = new FileStream(
					             temporaryPath,
					             FileMode.CreateNew,
					             FileAccess.Write,
					             FileShare.None,
					             4 * 1024,
					             FileOptions.Asynchronous | FileOptions.SequentialScan))
				{
					await JsonSerializer.SerializeAsync(
							stream,
							document,
							cancellationToken: cancellationToken)
						.ConfigureAwait(false);
					await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
				}

				File.Move(temporaryPath, path, overwrite: true);
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
		IReadOnlyList<string>? CommandHistory = null);
}
