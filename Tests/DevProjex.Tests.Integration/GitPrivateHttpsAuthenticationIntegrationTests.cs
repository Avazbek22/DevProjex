using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class GitPrivateHttpsAuthenticationIntegrationTests
{
	[Fact]
	public async Task New044_PrivateCloneBranchFetchAndUpdateReuseAskPassSession()
	{
		using var temporary = new TemporaryDirectory();
		var source = temporary.CreateDirectory("source");
		RunGit(source, "init", "--initial-branch=main");
		RunGit(source, "config", "user.name", "DevProjex Tests");
		RunGit(source, "config", "user.email", "tests@devprojex.local");
		await File.WriteAllTextAsync(
			Path.Combine(source, "main.txt"),
			"main\n",
			TestContext.Current.CancellationToken);
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "main");
		RunGit(source, "checkout", "-b", "private-feature");
		await File.WriteAllTextAsync(
			Path.Combine(source, "feature.txt"),
			"first\n",
			TestContext.Current.CancellationToken);
		RunGit(source, "add", ".");
		RunGit(source, "commit", "-m", "feature");
		RunGit(source, "checkout", "main");
		var bare = Path.Combine(temporary.Path, "private.git");
		RunGit(temporary.Path, "clone", "--bare", source, bare);
		RunGit(bare, "symbolic-ref", "HEAD", "refs/heads/main");
		RunGit(bare, "update-server-info");

		await using var server = await AuthenticatedGitHttpsServer.StartAsync(
			bare,
			"private-user",
			"private-password");
		var caFile = Path.Combine(temporary.Path, "test-ca.pem");
		await File.WriteAllTextAsync(
			caFile,
			server.CertificateAuthority.ExportCertificatePem(),
			TestContext.Current.CancellationToken);
		var previousCa = Environment.GetEnvironmentVariable("GIT_SSL_CAINFO");
		Environment.SetEnvironmentVariable("GIT_SSL_CAINFO", caFile);
		try
		{
			using var service = new GitRepositoryService();
			var target = Path.Combine(temporary.Path, "managed", RepositoryCacheLayout.BaseDirectoryName);
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			var authenticatedUrl = server.Url.Replace(
				"https://",
				"https://private-user:private-password@",
				StringComparison.Ordinal);
			var clone = await service.CloneAsync(
				authenticatedUrl,
				target,
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.True(
				clone.Success,
				$"{clone.ErrorMessage}; authorized requests: {server.AuthorizedRequestCount}; " +
				$"requests: {server.RequestCount}; server error: {server.LastError}");
			Assert.DoesNotContain("private-password", clone.RepositoryUrl, StringComparison.Ordinal);
			Assert.DoesNotContain(
				"private-password",
				await service.GetRemoteUrlAsync(target, TestContext.Current.CancellationToken) ?? string.Empty,
				StringComparison.Ordinal);
			await File.WriteAllTextAsync(
				Path.Combine(Path.GetDirectoryName(target)!, RepositoryCacheLayout.MarkerFileName),
				"git",
				TestContext.Current.CancellationToken);

			var branches = await service.GetBranchesAsync(target, TestContext.Current.CancellationToken);
			Assert.True(
				branches.Any(static branch => branch.Name == "private-feature"),
				$"Branches: {string.Join(',', branches.Select(static branch => branch.Name))}; " +
				$"authorized requests: {server.AuthorizedRequestCount}; server: {server.LastBackendResponse}");
			Assert.True(await service.SwitchBranchAsync(
				target,
				"private-feature",
				cancellationToken: TestContext.Current.CancellationToken));

			RunGit(source, "checkout", "private-feature");
			await File.AppendAllTextAsync(
				Path.Combine(source, "feature.txt"),
				"second\n",
				TestContext.Current.CancellationToken);
			RunGit(source, "add", ".");
			RunGit(source, "commit", "-m", "feature update");
			RunGit(source, "push", bare, "private-feature");
			RunGit(bare, "update-server-info");

			Assert.True(await service.PullUpdatesAsync(
				target,
				cancellationToken: TestContext.Current.CancellationToken));
			Assert.Equal(
				"first\nsecond\n",
				(await File.ReadAllTextAsync(
					Path.Combine(target, "feature.txt"),
					TestContext.Current.CancellationToken)).ReplaceLineEndings("\n"));
			Assert.True(server.AuthorizedRequestCount >= 3, server.AuthorizedRequestCount.ToString());
		}
		finally
		{
			Environment.SetEnvironmentVariable("GIT_SSL_CAINFO", previousCa);
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo(GitRuntime.GitExecutable)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo);
		Assert.NotNull(process);
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		Assert.True(process.WaitForExit(30_000), "Git fixture command timed out.");
		Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}{output}");
	}

	private sealed class AuthenticatedGitHttpsServer : IAsyncDisposable
	{
		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly CancellationTokenSource _shutdown = new();
		private readonly string _repositoryRoot;
		private readonly string _expectedAuthorization;
		private readonly X509Certificate2 _serverCertificate;
		private readonly Task _serveTask;
		private int _authorizedRequestCount;
		private int _requestCount;
		private string? _lastError;
		private string? _lastBackendResponse;

		private AuthenticatedGitHttpsServer(
			string repositoryRoot,
			string userName,
			string password,
			X509Certificate2 certificateAuthority,
			X509Certificate2 serverCertificate)
		{
			_repositoryRoot = Path.GetFullPath(repositoryRoot);
			_expectedAuthorization = "Basic " + Convert.ToBase64String(
				Encoding.UTF8.GetBytes($"{userName}:{password}"));
			CertificateAuthority = certificateAuthority;
			_serverCertificate = serverCertificate;
			_listener.Start();
			var endpoint = (IPEndPoint)_listener.LocalEndpoint;
			Url = $"https://127.0.0.1:{endpoint.Port}/private.git";
			_serveTask = ServeAsync();
		}

		public string Url { get; }
		public X509Certificate2 CertificateAuthority { get; }
		public int AuthorizedRequestCount => Volatile.Read(ref _authorizedRequestCount);
		public int RequestCount => Volatile.Read(ref _requestCount);
		public string? LastError => Volatile.Read(ref _lastError);
		public string? LastBackendResponse => Volatile.Read(ref _lastBackendResponse);

		public static Task<AuthenticatedGitHttpsServer> StartAsync(
			string repositoryRoot,
			string userName,
			string password)
		{
			var certificates = CreateCertificates();
			return Task.FromResult(new AuthenticatedGitHttpsServer(
				repositoryRoot,
				userName,
				password,
				certificates.Authority,
				certificates.Server));
		}

		private async Task ServeAsync()
		{
			var clients = new List<Task>();
			try
			{
				while (!_shutdown.IsCancellationRequested)
				{
					var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
					clients.Add(HandleSafelyAsync(client));
				}
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
			{
			}
			catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
			{
			}
			await Task.WhenAll(clients);
		}

		private async Task HandleSafelyAsync(TcpClient client)
		{
			try
			{
				await HandleAsync(client);
			}
			catch (Exception exception) when (
				exception is IOException or AuthenticationException && !_shutdown.IsCancellationRequested)
			{
				Volatile.Write(ref _lastError, exception.ToString());
				client.Dispose();
			}
		}

		private async Task HandleAsync(TcpClient client)
		{
			using (client)
			await using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
			{
				await tls.AuthenticateAsServerAsync(
					new SslServerAuthenticationOptions
					{
						ServerCertificate = _serverCertificate,
						EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
					},
					_shutdown.Token);
				var request = await ReadRequestAsync(tls, _shutdown.Token);
				if (request is null)
					return;
				Interlocked.Increment(ref _requestCount);
				if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
				    !string.Equals(authorization, _expectedAuthorization, StringComparison.Ordinal))
				{
					await WriteResponseAsync(
						tls,
						"401 Unauthorized",
						[],
						"WWW-Authenticate: Basic realm=\"DevProjex test\"\r\n");
					return;
				}
				Interlocked.Increment(ref _authorizedRequestCount);
				await RunGitHttpBackendAsync(tls, request, _shutdown.Token);
			}
		}

		private async Task RunGitHttpBackendAsync(
			Stream responseStream,
			HttpRequest request,
			CancellationToken cancellationToken)
		{
			var targetParts = request.Target.Split('?', 2);
			var startInfo = new ProcessStartInfo(GitRuntime.GitExecutable)
			{
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			startInfo.ArgumentList.Add("http-backend");
			startInfo.Environment["GIT_PROJECT_ROOT"] = Path.GetDirectoryName(_repositoryRoot)!;
			startInfo.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
			startInfo.Environment["PATH_INFO"] = targetParts[0];
			startInfo.Environment["QUERY_STRING"] = targetParts.Length > 1 ? targetParts[1] : string.Empty;
			startInfo.Environment["REQUEST_METHOD"] = request.Method;
			startInfo.Environment["CONTENT_TYPE"] = request.Headers.GetValueOrDefault("Content-Type") ?? string.Empty;
			startInfo.Environment["CONTENT_LENGTH"] = request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
			using var process = Process.Start(startInfo);
			Assert.NotNull(process);
			await process.StandardInput.BaseStream.WriteAsync(request.Body, cancellationToken);
			process.StandardInput.Close();
			using var output = new MemoryStream();
			var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			await outputTask;
			var error = await errorTask;
			Assert.True(process.ExitCode == 0, error);
			var bytes = output.ToArray();
			var separator = FindHeaderSeparator(bytes, out var separatorLength);
			Assert.True(separator >= 0, "git http-backend returned no CGI header boundary.");
			var headerText = Encoding.ASCII.GetString(bytes, 0, separator);
			var responseHeaders = headerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
			var status = "200 OK";
			var forwarded = new StringBuilder();
			foreach (var header in responseHeaders)
			{
				if (header.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
					status = header["Status:".Length..].Trim();
				else
					forwarded.Append(header).Append("\r\n");
			}
			var body = bytes.AsSpan(separator + separatorLength).ToArray();
			Volatile.Write(
				ref _lastBackendResponse,
				headerText + " | " + Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 512))));
			await WriteResponseAsync(responseStream, status, body, forwarded.ToString());
		}

		private static async Task<HttpRequest?> ReadRequestAsync(
			Stream stream,
			CancellationToken cancellationToken)
		{
			using var headerBuffer = new MemoryStream();
			var one = new byte[1];
			while (headerBuffer.Length < 64 * 1024)
			{
				var read = await stream.ReadAsync(one, cancellationToken);
				if (read == 0)
					return null;
				headerBuffer.WriteByte(one[0]);
				var bytes = headerBuffer.GetBuffer();
				var length = (int)headerBuffer.Length;
				if (length >= 4 && bytes[length - 4] == '\r' && bytes[length - 3] == '\n' &&
				    bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
					break;
			}
			var headerText = Encoding.ASCII.GetString(headerBuffer.GetBuffer(), 0, (int)headerBuffer.Length);
			var lines = headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);
			if (lines.Length == 0)
				return null;
			var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (requestLine.Length < 2)
				return null;
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var line in lines.Skip(1))
			{
				var colon = line.IndexOf(':');
				if (colon > 0)
					headers[line[..colon]] = line[(colon + 1)..].Trim();
			}
			var contentLength = headers.TryGetValue("Content-Length", out var value) &&
			                    int.TryParse(value, out var parsed)
				? parsed
				: 0;
			var body = new byte[contentLength];
			await stream.ReadExactlyAsync(body, cancellationToken);
			return new HttpRequest(requestLine[0], requestLine[1], headers, body);
		}

		private static int FindHeaderSeparator(byte[] bytes, out int length)
		{
			for (var index = 0; index <= bytes.Length - 4; index++)
			{
				if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
				    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
				{
					length = 4;
					return index;
				}
			}
			for (var index = 0; index <= bytes.Length - 2; index++)
			{
				if (bytes[index] == '\n' && bytes[index + 1] == '\n')
				{
					length = 2;
					return index;
				}
			}
			length = 0;
			return -1;
		}

		private static async Task WriteResponseAsync(
			Stream stream,
			string status,
			byte[] body,
			string additionalHeaders = "")
		{
			var header = Encoding.ASCII.GetBytes(
				$"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\n" +
				additionalHeaders +
				(additionalHeaders.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase)
					? string.Empty
					: "Content-Type: application/octet-stream\r\n") +
				"Connection: close\r\n\r\n");
			await stream.WriteAsync(header);
			if (body.Length > 0)
				await stream.WriteAsync(body);
			await stream.FlushAsync();
		}

		private static (X509Certificate2 Authority, X509Certificate2 Server) CreateCertificates()
		{
			using var authorityKey = RSA.Create(2048);
			var authorityRequest = new CertificateRequest(
				"CN=DevProjex Test CA",
				authorityKey,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);
			authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
			authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
				true));
			var authorityGenerator = X509SignatureGenerator.CreateForRSA(
				authorityKey,
				RSASignaturePadding.Pkcs1);
			var authority = authorityRequest.Create(
				authorityRequest.SubjectName,
				authorityGenerator,
				DateTimeOffset.UtcNow.AddMinutes(-5),
				DateTimeOffset.UtcNow.AddDays(2),
				RandomNumberGenerator.GetBytes(16));

			using var serverKey = RSA.Create(2048);
			var serverRequest = new CertificateRequest(
				"CN=127.0.0.1",
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
			var san = new SubjectAlternativeNameBuilder();
			san.AddIpAddress(IPAddress.Loopback);
			serverRequest.CertificateExtensions.Add(san.Build());
			var serial = RandomNumberGenerator.GetBytes(16);
			var publicServer = serverRequest.Create(
				authority.SubjectName,
				authorityGenerator,
				DateTimeOffset.UtcNow.AddMinutes(-5),
				DateTimeOffset.UtcNow.AddDays(1),
				serial);
			var ephemeralServer = publicServer.CopyWithPrivateKey(serverKey);
			publicServer.Dispose();
			if (!OperatingSystem.IsWindows())
				return (authority, ephemeralServer);

			using (ephemeralServer)
			{
				var server = X509CertificateLoader.LoadPkcs12(
					ephemeralServer.Export(X509ContentType.Pfx),
					password: null,
					X509KeyStorageFlags.MachineKeySet |
					X509KeyStorageFlags.Exportable);
				return (authority, server);
			}
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

		private sealed record HttpRequest(
			string Method,
			string Target,
			IReadOnlyDictionary<string, string> Headers,
			byte[] Body);
	}
}
