using System.Text.Json;
using DevProjex.Kernel.IO;

namespace DevProjex.Avalonia.Services;

public sealed class AgentActivityPreferenceStore(Func<string> stateRootProvider)
{
    private const string FileName = "agent-activity-view.json";
    private const int MaximumDocumentBytes = 4 * 1024;
    private readonly Func<string> _stateRootProvider = stateRootProvider;

    public bool Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path))
                return false;
            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: MaximumDocumentBytes,
                FileOptions.SequentialScan);
            using var bounded = new MaximumLengthReadStream(
                source,
                MaximumDocumentBytes,
                static () => new IOException("Agent activity preference exceeds the size limit."));
            var document = JsonSerializer.Deserialize<AgentActivityPreference>(bounded);
            return document?.Enabled == true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or JsonException or
                   System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning("Agent activity preference could not be read: {0}", exception.GetType().Name);
            return false;
        }
    }

    public bool TrySave(bool enabled)
    {
        string? temporaryPath = null;
        try
        {
            var path = GetPath();
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(
                temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(new AgentActivityPreference(enabled)));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or System.Security.SecurityException or
                   ArgumentException or NotSupportedException)
        {
            Trace.TraceWarning("Agent activity preference could not be saved: {0}", exception.GetType().Name);
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is
                           IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    Trace.TraceWarning("Agent activity preference temporary file could not be removed: {0}", exception.GetType().Name);
                }
            }
        }
    }

    private string GetPath() => Path.Combine(Path.GetFullPath(_stateRootProvider()), FileName);

    private sealed record AgentActivityPreference(bool Enabled);
}
