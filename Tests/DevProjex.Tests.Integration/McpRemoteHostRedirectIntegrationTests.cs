using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class McpRemoteHostRedirectIntegrationTests
{
	[Fact]
	public async Task AllowlistedRemoteCloneDoesNotFollowRedirectToAnotherHost()
	{
		using var temporary = new TemporaryDirectory();
		var localRoot = temporary.CreateDirectory("local-project");
		await using var server = new RedirectingHttpsGitServer();
		var caFile = Path.Combine(temporary.Path, "redirect-test-ca.pem");
		await File.WriteAllTextAsync(
			caFile,
			server.CertificateAuthority.ExportCertificatePem(),
			TestContext.Current.CancellationToken);
		var priorCaFile = Environment.GetEnvironmentVariable("GIT_SSL_CAINFO");
		var priorNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");
		try
		{
			Environment.SetEnvironmentVariable("GIT_SSL_CAINFO", caFile);
			Environment.SetEnvironmentVariable(
				"NO_PROXY",
				string.IsNullOrWhiteSpace(priorNoProxy)
					? "localhost,127.0.0.1"
					: $"{priorNoProxy},localhost,127.0.0.1");
			using var resolver = new McpProjectSourceResolver(
				new McpRootRegistry([localRoot]),
				allowRemote: true,
				() => McpRemoteProjectServices.Create(() => temporary.Path),
				remoteHosts: new HashSet<string>(["localhost"], StringComparer.OrdinalIgnoreCase));

			await Assert.ThrowsAsync<McpToolException>(() => resolver.ResolveAsync(
				server.Url,
				branch: null,
				TestContext.Current.CancellationToken));

			Assert.True(server.InitialRequests > 0, server.LastError ?? "The initial HTTPS request was not received.");
			Assert.Equal(0, server.RedirectedRequests);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GIT_SSL_CAINFO", priorCaFile);
			Environment.SetEnvironmentVariable("NO_PROXY", priorNoProxy);
		}
	}

	[Fact]
	public void UnrestrictedNetworkGitKeepsDefaultRedirectConfiguration()
	{
		using var temporary = new TemporaryDirectory();
		var operation = GitProcessOperation.CloneRepository(
			"https://github.com/owner/repo.git",
			Path.Combine(temporary.Path, "clone"));
		var startInfo = GitProcessStartInfoFactory.Create(null, operation);

		Assert.DoesNotContain("http.followRedirects=false", startInfo.ArgumentList);
	}

	private sealed class RedirectingHttpsGitServer : IAsyncDisposable
	{
		private const string CertificatePassword = "DevProjex redirect fixture";
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _shutdown = new();
		private readonly X509Certificate2 _serverCertificate;
		private readonly Task _serveTask;
		private int _initialRequests;
		private int _redirectedRequests;
		private string? _lastError;

		public RedirectingHttpsGitServer()
		{
			(CertificateAuthority, _serverCertificate) = CreateCertificates();
			_listener.Start();
			var endpoint = (IPEndPoint)_listener.LocalEndpoint;
			Url = $"https://localhost:{endpoint.Port}/repository.git";
			RedirectUrl = $"https://127.0.0.1:{endpoint.Port}/renamed.git";
			_serveTask = ServeAsync();
		}

		public string Url { get; }
		private string RedirectUrl { get; }
		public X509Certificate2 CertificateAuthority { get; }
		public int InitialRequests => Volatile.Read(ref _initialRequests);
		public int RedirectedRequests => Volatile.Read(ref _redirectedRequests);
		public string? LastError => Volatile.Read(ref _lastError);

		private async Task ServeAsync()
		{
			var clients = new List<Task>();
			try
			{
				while (!_shutdown.IsCancellationRequested)
					clients.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_shutdown.Token)));
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
			}
			catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
			{
			}
			await Task.WhenAll(clients);
		}

		private async Task HandleAsync(TcpClient client)
		{
			try
			{
				using (client)
				await using (var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
				{
					await stream.AuthenticateAsServerAsync(
						new SslServerAuthenticationOptions { ServerCertificate = _serverCertificate },
						_shutdown.Token);
					var request = await ReadHeadersAsync(stream, _shutdown.Token);
					if (request.Contains($"Host: localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}", StringComparison.OrdinalIgnoreCase))
					{
						Interlocked.Increment(ref _initialRequests);
						await WriteResponseAsync(stream, "302 Found", $"Location: {RedirectUrl}\r\n", _shutdown.Token);
					}
					else
					{
						Interlocked.Increment(ref _redirectedRequests);
						await WriteResponseAsync(stream, "404 Not Found", string.Empty, _shutdown.Token);
					}
				}
			}
			catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException or SocketException or OperationCanceledException)
			{
				if (!_shutdown.IsCancellationRequested)
					Volatile.Write(ref _lastError, exception.ToString());
			}
		}

		private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
		{
			using var headers = new MemoryStream();
			var one = new byte[1];
			while (headers.Length < 64 * 1024)
			{
				if (await stream.ReadAsync(one, cancellationToken) == 0)
					break;
				headers.WriteByte(one[0]);
				var buffer = headers.GetBuffer();
				var length = (int)headers.Length;
				if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
				    buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
					break;
			}
			return Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length);
		}

		private static async Task WriteResponseAsync(
			Stream stream,
			string status,
			string extraHeaders,
			CancellationToken cancellationToken)
		{
			var response = Encoding.ASCII.GetBytes(
				$"HTTP/1.1 {status}\r\nContent-Length: 0\r\n{extraHeaders}Connection: close\r\n\r\n");
			await stream.WriteAsync(response, cancellationToken);
			await stream.FlushAsync(cancellationToken);
		}

		private static (X509Certificate2 Authority, X509Certificate2 Server) CreateCertificates()
		{
			using var authorityKey = RSA.Create(2048);
			var authorityRequest = new CertificateRequest(
				"CN=DevProjex Redirect Test CA",
				authorityKey,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);
			authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
			authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
				true));
			var signer = X509SignatureGenerator.CreateForRSA(authorityKey, RSASignaturePadding.Pkcs1);
			var authority = authorityRequest.Create(
				authorityRequest.SubjectName,
				signer,
				DateTimeOffset.UtcNow.AddMinutes(-5),
				DateTimeOffset.UtcNow.AddDays(2),
				RandomNumberGenerator.GetBytes(16));

			using var serverKey = RSA.Create(2048);
			var serverRequest = new CertificateRequest(
				"CN=localhost",
				serverKey,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);
			serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
			serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
				true));
			serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
				[new Oid("1.3.6.1.5.5.7.3.1")],
				true));
			var names = new SubjectAlternativeNameBuilder();
			names.AddDnsName("localhost");
			names.AddIpAddress(IPAddress.Loopback);
			serverRequest.CertificateExtensions.Add(names.Build());
			using var publicCertificate = serverRequest.Create(
				authority.SubjectName,
				signer,
				DateTimeOffset.UtcNow.AddMinutes(-5),
				DateTimeOffset.UtcNow.AddDays(1),
				RandomNumberGenerator.GetBytes(16));
			using var exportable = publicCertificate.CopyWithPrivateKey(serverKey);
			var storage = OperatingSystem.IsMacOS()
				? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
				: OperatingSystem.IsWindows()
					? X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable
					: X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
			var certificate = X509CertificateLoader.LoadPkcs12(
				exportable.Export(X509ContentType.Pfx, CertificatePassword),
				CertificatePassword,
				storage);
			return (authority, certificate);
		}

		public async ValueTask DisposeAsync()
		{
			_shutdown.Cancel();
			_listener.Stop();
			await _serveTask;
			_serverCertificate.Dispose();
			CertificateAuthority.Dispose();
			_shutdown.Dispose();
		}
	}
}
