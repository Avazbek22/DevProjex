using Tmds.DBus.Protocol;

namespace DevProjex.Avalonia.Services;

internal static class FlatpakDocumentPortal
{
    public static Task<string?> GetHostPathAsync(string id, CancellationToken cancellationToken) =>
        QueryHostPathAsync(DBusAddress.Session, id, static address => new Session(address), cancellationToken);

    internal static async Task<string?> QueryHostPathAsync(string? address, string id,
        Func<string, IFlatpakDocumentPortalSession> createSession, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
            using var session = createSession(address);
            await session.ConnectAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            var version = await session.GetVersionAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            if (version < 5)
                return null;
            var host = await session.GetHostPathAsync(id).WaitAsync(deadline.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(host) ? null : host;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is DBusExceptionBase or IOException or
            OperationCanceledException or TimeoutException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class Session(string address) : IFlatpakDocumentPortalSession
    {
        private readonly DBusConnection _connection = new(address);
        public Task ConnectAsync() => _connection.ConnectAsync().AsTask();
        public Task<uint> GetVersionAsync() => _connection.CallMethodAsync(CreateVersionRequest(_connection),
            static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32());
        public Task<string?> GetHostPathAsync(string id) =>
            _connection.CallMethodAsync(CreatePathsRequest(_connection, id), ReadHostPath, id);
        public void Dispose() => _connection.Dispose();
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

internal interface IFlatpakDocumentPortalSession : IDisposable
{
    Task ConnectAsync();
    Task<uint> GetVersionAsync();
    Task<string?> GetHostPathAsync(string id);
}
