using Tmds.DBus.Protocol;

namespace DevProjex.Avalonia.Services;

internal static class FlatpakDocumentPortal
{
    public static async Task<string?> GetHostPathAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DBusAddress.Session))
                return null;
            using var connection = new DBusConnection(DBusAddress.Session);
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var version = await connection.CallMethodAsync(CreateVersionRequest(connection),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32())
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (version < 5)
                return null;
            return await connection.CallMethodAsync(CreatePathsRequest(connection, id), ReadHostPath, id)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DBusExceptionBase or IOException or
            TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadHostPath(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        var end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            reader.AlignStruct();
            var key = reader.ReadString();
            var bytes = reader.ReadArrayOfByte();
            if (key == (string)state!)
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        return null;
    }

    private static MessageBuffer CreateVersionRequest(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Documents",
            path: "/org/freedesktop/portal/documents", @interface: "org.freedesktop.DBus.Properties",
            member: "Get", signature: "ss");
        writer.WriteString("org.freedesktop.portal.Documents");
        writer.WriteString("version");
        return writer.CreateMessage();
    }

    private static MessageBuffer CreatePathsRequest(DBusConnection connection, string id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Documents",
            path: "/org/freedesktop/portal/documents", @interface: "org.freedesktop.portal.Documents",
            member: "GetHostPaths", signature: "as");
        writer.WriteArray(new[] { id });
        return writer.CreateMessage();
    }
}
