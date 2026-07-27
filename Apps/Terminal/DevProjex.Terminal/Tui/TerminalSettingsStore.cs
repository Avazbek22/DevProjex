using System.Text.Json;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public sealed class TerminalSettingsStore(Func<string>? appDataPathProvider = null)
{
	private const int CurrentSchemaVersion = 1;
	private readonly Func<string> _appDataPathProvider =
		appDataPathProvider ?? UserDataPathResolver.GetConfigurationRoot;

	public TerminalScreenMode LoadScreenMode()
	{
		try
		{
			var path = GetPath();
			if (!File.Exists(path))
				return TerminalScreenMode.Auto;

			using var stream = File.OpenRead(path);
			var document = JsonSerializer.Deserialize<TerminalSettingsDocument>(stream);
			return document is { SchemaVersion: CurrentSchemaVersion } &&
			       Enum.IsDefined(document.ScreenMode)
				? document.ScreenMode
				: TerminalScreenMode.Auto;
		}
		catch
		{
			return TerminalScreenMode.Auto;
		}
	}

	public async Task SaveScreenModeAsync(
		TerminalScreenMode screenMode,
		CancellationToken cancellationToken = default)
	{
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
						new TerminalSettingsDocument(CurrentSchemaVersion, screenMode),
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
			catch
			{
				// A unique temporary settings file is harmless if a platform delays handle release.
			}
		}
	}

	internal string GetPath() =>
		Path.Combine(_appDataPathProvider(), "DevProjex", "terminal-settings.json");

	private sealed record TerminalSettingsDocument(
		int SchemaVersion,
		TerminalScreenMode ScreenMode);
}
