using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public sealed class DesktopControlServer : IAsyncDisposable
{
	internal static readonly TimeSpan RequestReceiveTimeout = TimeSpan.FromSeconds(5);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly IDesktopInteractionHandler _handler;
	private readonly DesktopInstanceRegistry _registry;
	private readonly DesktopControlPaths _paths;
	private readonly CancellationTokenSource _shutdown = new();
	private readonly object _stateLock = new();
	private DesktopInstanceRegistration _registration;
	private Socket? _unixListener;
	private Task? _listenerTask;
	private int _disposed;

	private DesktopControlServer(
		IDesktopInteractionHandler handler,
		DesktopInstanceRegistry registry,
		DesktopControlPaths paths,
		DesktopInstanceRegistration registration)
	{
		_handler = handler;
		_registry = registry;
		_paths = paths;
		_registration = registration;
	}

	public string InstanceId => _registration.InstanceId;

	public static async Task<DesktopControlServer> StartAsync(
		IDesktopInteractionHandler handler,
		string? projectPath = null,
		DesktopControlPaths? paths = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(handler);
		paths ??= new DesktopControlPaths();
		var registry = new DesktopInstanceRegistry(paths);
		var instanceId = Guid.NewGuid().ToString("N");
		var transport = OperatingSystem.IsWindows() ? "pipe" : "unix";
		var endpoint = OperatingSystem.IsWindows()
			? $"devprojex-{instanceId}"
			: paths.GetSocketPath(instanceId);
		using var process = Process.GetCurrentProcess();
		var registration = new DesktopInstanceRegistration(
			DesktopProtocol.CurrentVersion,
			instanceId,
			Environment.ProcessId,
			process.StartTime.ToUniversalTime().Ticks,
			NormalizeOptionalPath(projectPath),
			DateTimeOffset.UtcNow,
			transport,
			endpoint);
		var server = new DesktopControlServer(handler, registry, paths, registration);
		try
		{
			if (transport == "unix")
				server.InitializeUnixListener();
			await registry.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
			server._listenerTask = transport == "pipe"
				? server.RunPipeListenerAsync(server._shutdown.Token)
				: server.RunUnixListenerAsync(server._shutdown.Token);
			return server;
		}
		catch
		{
			await server.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public async Task UpdateProjectAsync(
		string? projectPath,
		CancellationToken cancellationToken = default)
	{
		DesktopInstanceRegistration registration;
		lock (_stateLock)
		{
			_registration = _registration with
			{
				ProjectPath = NormalizeOptionalPath(projectPath),
				LastActiveUtc = DateTimeOffset.UtcNow
			};
			registration = _registration;
		}

		await _registry.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		if (!_shutdown.IsCancellationRequested)
			_shutdown.Cancel();
		_unixListener?.Dispose();
		if (_listenerTask is not null)
		{
			try
			{
				await _listenerTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
			catch
			{
				// Shutdown must still remove the endpoint and registration.
			}
		}

		await _registry.UnregisterAsync(_registration.InstanceId).ConfigureAwait(false);
		if (_registration.Transport == "unix")
			DesktopInstanceRegistry.TryDelete(_registration.Endpoint);
		_shutdown.Dispose();
	}

	private void InitializeUnixListener()
	{
		DesktopInstanceRegistry.EnsurePrivateDirectory(_paths.SocketDirectory);
		DesktopInstanceRegistry.TryDelete(_registration.Endpoint);
		_unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		_unixListener.Bind(new UnixDomainSocketEndPoint(_registration.Endpoint));
		_unixListener.Listen(8);
		DesktopInstanceRegistry.SetPrivateFileMode(_registration.Endpoint);
	}

	private async Task RunPipeListenerAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await using var pipe = new NamedPipeServerStream(
				_registration.Endpoint,
				PipeDirection.InOut,
				1,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (
				!cancellationToken.IsCancellationRequested &&
				exception is IOException or ObjectDisposedException)
			{
				// A client may time out or disconnect after the UI operation was accepted.
				// The next connection must still get a fresh server pipe.
			}
		}
	}

	private async Task RunUnixListenerAsync(CancellationToken cancellationToken)
	{
		var listener = _unixListener ??
		               throw new InvalidOperationException("Unix listener was not initialized.");
		while (!cancellationToken.IsCancellationRequested)
		{
			var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
			await using var stream = new NetworkStream(socket, ownsSocket: true);
			try
			{
				await HandleConnectionAsync(stream, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (
				!cancellationToken.IsCancellationRequested &&
				exception is IOException or SocketException or ObjectDisposedException)
			{
				// Isolate a disconnected client from the long-lived Unix listener.
			}
		}
	}

	private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
	{
		DesktopProtocolResponse response;
		string requestId = string.Empty;
		try
		{
			var json = await ReadMessageAsync(
				stream,
				RequestReceiveTimeout,
				cancellationToken).ConfigureAwait(false);
			var request = JsonSerializer.Deserialize<DesktopProtocolRequest>(json, JsonOptions) ??
			              throw new JsonException();
			requestId = request.RequestId;
			if (request.ProtocolVersion != DesktopProtocol.CurrentVersion)
			{
				throw new DesktopControlException(
					"DPX-DESKTOP-PROTOCOL-MISMATCH",
					"The desktop control protocol version is not supported.");
			}
			if (!string.IsNullOrWhiteSpace(request.InstanceId) &&
			    !request.InstanceId.Equals(_registration.InstanceId, StringComparison.Ordinal))
			{
				throw new DesktopControlException(
					"DPX-DESKTOP-INSTANCE-MISMATCH",
					"The desktop instance does not match the request.");
			}

			var interaction = DesktopProtocolMapper.Map(request);
			var result = await _handler.HandleAsync(interaction, cancellationToken).ConfigureAwait(false);
			response = result.Success
				? new DesktopProtocolResponse(
					DesktopProtocol.CurrentVersion,
					request.RequestId,
					true,
					result.State,
					null)
				: new DesktopProtocolResponse(
					DesktopProtocol.CurrentVersion,
					request.RequestId,
					false,
					result.State,
					new DesktopProtocolError(
						result.ErrorCode ?? "DPX-DESKTOP-REQUEST-FAILED",
						"The desktop could not apply the requested state."));
			await TouchRegistrationAsync(result.State, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (DesktopControlException exception)
		{
			response = Failure(requestId, exception.Code, exception.Message);
		}
		catch
		{
			response = Failure(
				requestId,
				"DPX-DESKTOP-INVALID-PAYLOAD",
				"The desktop request is invalid.");
		}

		var responseJson = JsonSerializer.Serialize(response, JsonOptions);
		var bytes = Encoding.UTF8.GetBytes(responseJson + "\n");
		await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task TouchRegistrationAsync(
		IReadOnlyDictionary<string, object?>? state,
		CancellationToken cancellationToken)
	{
		DesktopInstanceRegistration registration;
		lock (_stateLock)
		{
			var projectPath = state is not null &&
			                  state.TryGetValue("projectPath", out var value) &&
			                  value is string path
				? NormalizeOptionalPath(path)
				: _registration.ProjectPath;
			_registration = _registration with
			{
				ProjectPath = projectPath,
				LastActiveUtc = DateTimeOffset.UtcNow
			};
			registration = _registration;
		}

		await _registry.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
	}

	internal static async Task<string> ReadMessageAsync(
		Stream stream,
		TimeSpan receiveTimeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if (receiveTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(receiveTimeout));
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(receiveTimeout);
		var buffer = new byte[4096];
		using var message = new MemoryStream();
		try
		{
			while (true)
			{
				var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
				if (read == 0)
					throw new EndOfStreamException();

				var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
				var count = newline >= 0 ? newline : read;
				if (message.Length + count > DesktopProtocol.MaximumMessageBytes)
				{
					throw new DesktopControlException(
						"DPX-DESKTOP-PAYLOAD-TOO-LARGE",
						"The desktop request exceeds the size limit.",
						CommandLineExitCodes.UsageError);
				}

				message.Write(buffer, 0, count);
				if (newline >= 0)
					return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
			}
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			throw new DesktopControlException(
				"DPX-DESKTOP-TIMEOUT",
				"The desktop request was not received before the timeout.",
				innerException: exception);
		}
	}

	private static DesktopProtocolResponse Failure(
		string requestId,
		string code,
		string message) =>
		new(
			DesktopProtocol.CurrentVersion,
			requestId,
			false,
			null,
			new DesktopProtocolError(code, message));

	private static string? NormalizeOptionalPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			return PathUtility.Normalize(path);
		}
		catch
		{
			return null;
		}
	}
}
