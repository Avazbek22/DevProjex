using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public sealed class DesktopControlClient(
	DesktopInstanceRegistry? registry = null)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};
	private readonly DesktopInstanceRegistry _registry = registry ?? new DesktopInstanceRegistry();

	public Task<IReadOnlyList<DesktopInstanceRegistration>> ListAsync(
		CancellationToken cancellationToken = default) =>
		_registry.ListAsync(cancellationToken);

	public async Task<DesktopInstanceRegistration> ResolveTargetAsync(
		DesktopTarget target,
		CancellationToken cancellationToken = default)
	{
		var instances = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
		IEnumerable<DesktopInstanceRegistration> matches = instances;
		if (!string.IsNullOrWhiteSpace(target.InstanceId))
		{
			matches = matches.Where(instance =>
				instance.InstanceId.Equals(target.InstanceId, StringComparison.Ordinal));
		}
		if (!string.IsNullOrWhiteSpace(target.ProjectPath))
		{
			var projectPath = PathUtility.Normalize(target.ProjectPath);
			matches = matches.Where(instance =>
				instance.ProjectPath is not null &&
				PathComparer.Default.Equals(instance.ProjectPath, projectPath));
		}

		var resolved = matches.ToArray();
		if (resolved.Length == 0)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-NOT-RUNNING",
				"No matching DevProjex Desktop instance is running.");
		}
		if (resolved.Length > 1)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-AMBIGUOUS",
				"More than one DevProjex Desktop instance matches the request.");
		}

		return resolved[0];
	}

	public async Task<DesktopProtocolResponse> SendAsync(
		DesktopInstanceRegistration instance,
		string action,
		object? payload,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutSource.CancelAfter(timeout);
		var token = timeoutSource.Token;
		var requestId = Guid.NewGuid().ToString("N");
		var payloadElement = JsonSerializer.SerializeToElement(payload ?? new { }, JsonOptions);
		var request = new DesktopProtocolRequest(
			DesktopProtocol.CurrentVersion,
			requestId,
			instance.InstanceId,
			action,
			payloadElement);

		try
		{
			await using var stream = await ConnectAsync(instance, token).ConfigureAwait(false);
			var json = JsonSerializer.Serialize(request, JsonOptions);
			var bytes = Encoding.UTF8.GetBytes(json + "\n");
			await stream.WriteAsync(bytes, token).ConfigureAwait(false);
			await stream.FlushAsync(token).ConfigureAwait(false);
			var responseJson = await ReadMessageAsync(stream, token).ConfigureAwait(false);
			var response = JsonSerializer.Deserialize<DesktopProtocolResponse>(responseJson, JsonOptions) ??
			               throw new JsonException();
			if (response.ProtocolVersion != DesktopProtocol.CurrentVersion ||
			    !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
			{
				throw new DesktopControlException(
					"DPX-DESKTOP-PROTOCOL-MISMATCH",
					"The desktop returned an invalid protocol response.");
			}

			return response;
		}
		catch (DesktopControlException)
		{
			throw;
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-TIMEOUT",
				"The desktop did not respond before the timeout.",
				innerException: exception);
		}
		catch (Exception exception) when (exception is IOException or SocketException or JsonException)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-NOT-RUNNING",
				"The desktop control endpoint is unavailable.",
				innerException: exception);
		}
	}

	private static async Task<Stream> ConnectAsync(
		DesktopInstanceRegistration instance,
		CancellationToken cancellationToken)
	{
		if (instance.Transport == "pipe")
		{
			var pipe = new NamedPipeClientStream(
				".",
				instance.Endpoint,
				PipeDirection.InOut,
				PipeOptions.Asynchronous);
			try
			{
				await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
				return pipe;
			}
			catch
			{
				await pipe.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		if (instance.Transport == "unix")
		{
			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			try
			{
				await socket.ConnectAsync(
					new UnixDomainSocketEndPoint(instance.Endpoint),
					cancellationToken).ConfigureAwait(false);
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		}

		throw new DesktopControlException(
			"DPX-DESKTOP-PROTOCOL-MISMATCH",
			"The desktop transport is not supported.");
	}

	private static async Task<string> ReadMessageAsync(
		Stream stream,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[4096];
		using var message = new MemoryStream();
		while (true)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
				throw new EndOfStreamException();
			var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
			var count = newline >= 0 ? newline : read;
			if (message.Length + count > DesktopProtocol.MaximumMessageBytes)
				throw new DesktopControlException("DPX-DESKTOP-PAYLOAD-TOO-LARGE", "The desktop response exceeds the size limit.");
			message.Write(buffer, 0, count);
			if (newline >= 0)
				return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
		}
	}
}
