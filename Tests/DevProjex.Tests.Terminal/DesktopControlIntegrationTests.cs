using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using DevProjex.Application.DesktopControl;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class DesktopControlIntegrationTests
{
	[Fact]
	public async Task ServerRegistersHandlesAppliedStateAndRemovesRegistrationOnStop()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new RecordingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			workspace.Path,
			paths,
			TestContext.Current.CancellationToken);
		var registry = new DesktopInstanceRegistry(paths);
		var client = new DesktopControlClient(registry);

		var registration = Assert.Single(await client.ListAsync(TestContext.Current.CancellationToken));
		Assert.Equal(server.InstanceId, registration.InstanceId);
		Assert.Equal(
			OperatingSystem.IsWindows() ? "pipe" : "unix",
			registration.Transport);
		Assert.DoesNotContain("tcp", registration.Transport, StringComparison.OrdinalIgnoreCase);

		var response = await client.SendAsync(
			registration,
			"status",
			new { },
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.True(response.Ok);
		Assert.True(response.State!.ContainsKey("projectLoaded"));
		Assert.IsType<DesktopStatusRequest>(Assert.Single(handler.Requests));

		await server.DisposeAsync();
		Assert.Empty(await client.ListAsync(TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task OpenWritesTheAcceptedProjectPathForExistingAndLastProjectRequests()
	{
		using var workspace = new TemporaryDirectory();
		var explicitProject = workspace.CreateDirectory("explicit-project");
		var lastProject = workspace.CreateDirectory("last-project");
		var paths = new DesktopControlPaths(() => workspace.CreateDirectory("ipc"));
		var handler = new OpenStateDesktopHandler(lastProject);
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			explicitProject,
			paths,
			TestContext.Current.CancellationToken);
		var client = new DesktopControlClient(new DesktopInstanceRegistry(paths));

		var explicitEnvironment = new TestTerminalEnvironment();
		var explicitExitCode = await new DesktopCommandHandler(
				explicitEnvironment,
				client: client)
			.OpenAsync(
				new DesktopOpenRequest(ProjectPath: explicitProject),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, explicitExitCode);
		Assert.Equal(
			PathUtility.Normalize(explicitProject) + Environment.NewLine,
			explicitEnvironment.StandardOutput);

		var lastEnvironment = new TestTerminalEnvironment();
		var lastExitCode = await new DesktopCommandHandler(
				lastEnvironment,
				client: client)
			.OpenAsync(
				new DesktopOpenRequest(UseLastProject: true),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, lastExitCode);
		Assert.Equal(
			PathUtility.Normalize(lastProject) + Environment.NewLine,
			lastEnvironment.StandardOutput);
		Assert.DoesNotContain(server.InstanceId, lastEnvironment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SuccessIsReturnedOnlyAfterHandlerAppliedTheRequest()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new BlockingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var client = new DesktopControlClient(new DesktopInstanceRegistry(paths));
		var registration = Assert.Single(await client.ListAsync(TestContext.Current.CancellationToken));

		var sendTask = client.SendAsync(
			registration,
			"preview.open",
			new { view = "tree-content" },
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		await handler.Started.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.False(sendTask.IsCompleted);

		handler.Release.TrySetResult();
		var response = await sendTask;
		Assert.True(response.Ok);
		Assert.IsType<DesktopPreviewRequest>(handler.Request);
	}

	[Fact]
	public async Task TargetedCliActionWritesTheVersionedProtocolEnvelope()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new RecordingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var registry = new DesktopInstanceRegistry(paths);
		var registration = Assert.Single(
			await registry.ListAsync(TestContext.Current.CancellationToken));
		var environment = new TestTerminalEnvironment();

		var exitCode = await new DesktopCommandHandler(
				environment,
				new DesktopControlClient(registry))
			.SendAsync(
				new DesktopTarget(InstanceId: registration.InstanceId),
				"status",
				new { },
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(
			DesktopProtocol.CurrentVersion,
			document.RootElement.GetProperty("protocolVersion").GetInt32());
		Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(
			"C:/workspace/Project",
			document.RootElement
				.GetProperty("state")
				.GetProperty("projectPath")
				.GetString());
		Assert.Equal(
			JsonValueKind.Null,
			document.RootElement.GetProperty("error").ValueKind);
	}

	[Theory]
	[InlineData("invoke-method", "DPX-DESKTOP-UNKNOWN-ACTION")]
	[InlineData("filter.set", "DPX-DESKTOP-INVALID-PAYLOAD")]
	public async Task InvalidOrLowLevelActionIsRejectedWithoutInvokingDesktop(
		string action,
		string expectedCode)
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new RecordingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var client = new DesktopControlClient(new DesktopInstanceRegistry(paths));
		var registration = Assert.Single(await client.ListAsync(TestContext.Current.CancellationToken));

		var response = await client.SendAsync(
			registration,
			action,
			new { },
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		Assert.False(response.Ok);
		Assert.Equal(expectedCode, response.Error?.Code);
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task TimedOutClientDoesNotTerminateServerListener()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new FirstRequestBlockingHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var client = new DesktopControlClient(new DesktopInstanceRegistry(paths));
		var registration = Assert.Single(await client.ListAsync(TestContext.Current.CancellationToken));

		var timedOutSend = client.SendAsync(
			registration,
			"status",
			new { },
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);
		await handler.FirstRequestStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		DesktopControlException exception;
		try
		{
			exception = await Assert.ThrowsAsync<DesktopControlException>(() => timedOutSend);
		}
		finally
		{
			handler.ReleaseFirstRequest.TrySetResult();
		}
		Assert.Equal("DPX-DESKTOP-TIMEOUT", exception.Code);

		await handler.FirstRequestCompleted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		var response = await client.SendAsync(
			registration,
			"status",
			new { },
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);
		Assert.True(response.Ok);
	}

	[Fact]
	public async Task RegistryRemovesStaleProcessIdentity()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var registry = new DesktopInstanceRegistry(paths);
		var stale = new DesktopInstanceRegistration(
			DesktopProtocol.CurrentVersion,
			"stale",
			Environment.ProcessId,
			Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks - TimeSpan.FromHours(1).Ticks,
			workspace.Path,
			DateTimeOffset.UtcNow,
			OperatingSystem.IsWindows() ? "pipe" : "unix",
			OperatingSystem.IsWindows()
				? "devprojex-stale"
				: paths.GetSocketPath("stale"));
		await registry.RegisterAsync(stale, TestContext.Current.CancellationToken);

		Assert.Empty(await registry.ListAsync(TestContext.Current.CancellationToken));
		Assert.False(File.Exists(paths.GetRegistrationPath(stale.InstanceId)));
	}

	[Fact]
	public async Task TargetResolutionRequiresOneUnambiguousInstance()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var registry = new DesktopInstanceRegistry(paths);
		using var process = Process.GetCurrentProcess();
		var start = process.StartTime.ToUniversalTime().Ticks;
		var firstProject = workspace.CreateDirectory("first");
		var secondProject = workspace.CreateDirectory("second");
		await registry.RegisterAsync(
			CreateLiveRegistration("first", firstProject, start),
			TestContext.Current.CancellationToken);
		await registry.RegisterAsync(
			CreateLiveRegistration("second", secondProject, start),
			TestContext.Current.CancellationToken);
		var client = new DesktopControlClient(registry);

		var ambiguous = await Assert.ThrowsAsync<DesktopControlException>(() =>
			client.ResolveTargetAsync(new DesktopTarget(), TestContext.Current.CancellationToken));
		Assert.Equal("DPX-DESKTOP-AMBIGUOUS", ambiguous.Code);

		var byId = await client.ResolveTargetAsync(
			new DesktopTarget(InstanceId: "first"),
			TestContext.Current.CancellationToken);
		Assert.Equal("first", byId.InstanceId);
		var byProject = await client.ResolveTargetAsync(
			new DesktopTarget(ProjectPath: secondProject),
			TestContext.Current.CancellationToken);
		Assert.Equal("second", byProject.InstanceId);
	}

	[Fact]
	public async Task UnixEndpointAndRegistryArePrivateToCurrentUser()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Unix file modes do not apply to Windows named pipes.");

		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		await using var server = await DesktopControlServer.StartAsync(
			new RecordingDesktopHandler(),
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var registration = Assert.Single(
			await new DesktopInstanceRegistry(paths).ListAsync(TestContext.Current.CancellationToken));

#pragma warning disable CA1416
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
			File.GetUnixFileMode(paths.RegistryDirectory));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(paths.GetRegistrationPath(registration.InstanceId)));
		Assert.Equal(
			UnixFileMode.UserRead | UnixFileMode.UserWrite,
			File.GetUnixFileMode(registration.Endpoint));
#pragma warning restore CA1416
	}

	[Fact]
	public async Task ProtocolVersionMismatchReturnsTypedFailure()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		await using var server = await DesktopControlServer.StartAsync(
			new RecordingDesktopHandler(),
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var registration = Assert.Single(
			await new DesktopInstanceRegistry(paths).ListAsync(TestContext.Current.CancellationToken));
		var request = JsonSerializer.Serialize(new
		{
			protocolVersion = DesktopProtocol.CurrentVersion + 1,
			requestId = "version-test",
			instanceId = registration.InstanceId,
			action = "status",
			payload = new { }
		});

		var responseJson = await SendRawAsync(
			registration,
			request,
			TestContext.Current.CancellationToken);
		using var response = JsonDocument.Parse(responseJson);
		Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(
			"DPX-DESKTOP-PROTOCOL-MISMATCH",
			response.RootElement.GetProperty("error").GetProperty("code").GetString());
	}

	[Theory]
	[InlineData("")]
	[InlineData("{")]
	[InlineData("[]")]
	public async Task MalformedProtocolPayloadIsRejectedWithoutInvokingDesktop(string payload)
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new RecordingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var registration = Assert.Single(
			await new DesktopInstanceRegistry(paths).ListAsync(TestContext.Current.CancellationToken));

		var responseJson = await SendRawAsync(
			registration,
			payload,
			TestContext.Current.CancellationToken);
		using var response = JsonDocument.Parse(responseJson);

		Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(
			"DPX-DESKTOP-INVALID-PAYLOAD",
			response.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task OversizedPayloadIsRejectedBeforeDeserializationOrDispatch()
	{
		using var workspace = new TemporaryDirectory();
		var paths = new DesktopControlPaths(() => workspace.Path);
		var handler = new RecordingDesktopHandler();
		await using var server = await DesktopControlServer.StartAsync(
			handler,
			paths: paths,
			cancellationToken: TestContext.Current.CancellationToken);
		var registration = Assert.Single(
			await new DesktopInstanceRegistry(paths).ListAsync(TestContext.Current.CancellationToken));
		var payload = new string('x', DesktopProtocol.MaximumMessageBytes + 1);

		var responseJson = await SendRawAsync(
			registration,
			payload,
			TestContext.Current.CancellationToken);
		using var response = JsonDocument.Parse(responseJson);

		Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
		Assert.Equal(
			"DPX-DESKTOP-PAYLOAD-TOO-LARGE",
			response.RootElement.GetProperty("error").GetProperty("code").GetString());
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public void OpenWaitReadinessRequiresEveryRequestedSemanticState()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var request = new DesktopOpenRequest(
			ProjectPath: project,
			WaitForCompletion: true,
			OpenPreview: true,
			PreviewView: DesktopPreviewView.TreeContent,
			TreeFormat: TreeTextFormat.Markdown,
			Filter: "Service");
		var state = DeserializeState(new Dictionary<string, object?>
		{
			["startupReady"] = true,
			["startupError"] = null,
			["projectLoaded"] = true,
			["projectPath"] = project,
			["previewOpen"] = true,
			["previewView"] = "tree-content",
			["treeFormat"] = "markdown",
			["filter"] = "Service",
			["search"] = null
		});

		Assert.True(DesktopOpenReadiness.IsApplied(request, state));

		foreach (var key in new[]
		         {
			         "startupReady",
			         "projectLoaded",
			         "previewOpen",
			         "previewView",
			         "treeFormat",
			         "filter"
		         })
		{
			var mismatched = state.ToDictionary(static item => item.Key, static item => item.Value);
			mismatched[key] = key switch
			{
				"previewView" => JsonSerializer.SerializeToElement("content"),
				"treeFormat" => JsonSerializer.SerializeToElement("json"),
				"filter" => JsonSerializer.SerializeToElement("Other"),
				_ => JsonSerializer.SerializeToElement(false)
			};
			Assert.False(DesktopOpenReadiness.IsApplied(request, mismatched));
		}
	}

	[Fact]
	public void OpenWaitReadinessRequiresRequestedTrackedModeAndReadableIndex()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var request = new DesktopOpenRequest(
			ProjectPath: project,
			Selection: ProjectSelectionSpec.Standard with
			{
				GitMode = GitFilteringMode.TrackedFilesOnly
			});
		var state = DeserializeState(new Dictionary<string, object?>
		{
			["startupReady"] = true,
			["startupError"] = null,
			["projectLoaded"] = true,
			["projectPath"] = project,
			["previewOpen"] = false,
			["previewView"] = "tree-content",
			["treeFormat"] = "text",
			["filter"] = null,
			["search"] = null,
			["gitMode"] = "tracked",
			["trackedGitReady"] = true
		});

		Assert.True(DesktopOpenReadiness.IsApplied(request, state));

		var unavailable = state.ToDictionary(static pair => pair.Key, static pair => pair.Value);
		unavailable["trackedGitReady"] = JsonSerializer.SerializeToElement(false);
		Assert.False(DesktopOpenReadiness.IsApplied(request, unavailable));

		var wrongMode = state.ToDictionary(static pair => pair.Key, static pair => pair.Value);
		wrongMode["gitMode"] = JsonSerializer.SerializeToElement("none");
		Assert.False(DesktopOpenReadiness.IsApplied(request, wrongMode));
	}

	[Theory]
	[InlineData(false, false, false, false)]
	[InlineData(true, false, false, true)]
	[InlineData(false, true, false, true)]
	[InlineData(false, false, true, true)]
	public void OpenPreviewActivationIncludesExplicitTreeFormat(
		bool explicitPreview,
		bool hasView,
		bool hasTreeFormat,
		bool expectedOpenPreview)
	{
		var request = DesktopOpenRequestFactory.Create(
			projectPath: ".",
			useLastProject: false,
			newWindow: false,
			waitForCompletion: false,
			explicitPreview,
			previewView: hasView ? DesktopPreviewView.Tree : null,
			treeFormat: hasTreeFormat ? TreeTextFormat.Json : null,
			filter: null,
			search: null,
			selection: null,
			language: AppLanguage.En,
			elevationAttempted: false);

		Assert.Equal(expectedOpenPreview, request.OpenPreview);
		Assert.Equal(
			hasTreeFormat ? TreeTextFormat.Json : null,
			request.TreeFormat);
	}

	[Fact]
	public void OpenRequestPreservesExplicitPrivateDataSelectionThroughJsonTransport()
	{
		var selection = new ProjectSelectionSpec(HidePrivateData: true)
		{
			ApplicationIntent = new ProjectSelectionApplicationIntent(
				ProjectSelectionApplicationMode.Preserve,
				ProjectSelectionApplicationMode.Preserve,
				ProjectSelectionApplicationMode.Preserve,
				ProjectSelectionApplicationMode.Preserve,
				HidePrivateData: ProjectSelectionApplicationMode.ApplyResolvedValue)
		};
		var request = DesktopOpenRequestFactory.Create(
			projectPath: ".",
			useLastProject: false,
			newWindow: false,
			waitForCompletion: false,
			explicitPreview: false,
			previewView: null,
			treeFormat: null,
			filter: null,
			search: null,
			selection: selection,
			language: AppLanguage.En,
			elevationAttempted: false);

		var json = JsonSerializer.Serialize(request);
		var roundTrip = JsonSerializer.Deserialize<DesktopOpenRequest>(json);

		Assert.NotNull(roundTrip);
		Assert.True(roundTrip.Selection?.HidePrivateData);
		Assert.Equal(
			ProjectSelectionApplicationMode.ApplyResolvedValue,
			roundTrip.Selection?.ApplicationIntent?.HidePrivateData);
	}

	[Fact]
	public void DesktopChildUsesIsolatedStandardStreams()
	{
		var startInfo = DesktopProcessLauncher.CreateStartInfo("desktop-request.json");

		Assert.Contains(
			DesktopLaunchRequestStore.InternalRequestArgument,
			startInfo.ArgumentList);
		Assert.Contains("desktop-request.json", startInfo.ArgumentList);
		if (OperatingSystem.IsWindows())
		{
			Assert.True(startInfo.UseShellExecute);
			Assert.False(startInfo.RedirectStandardInput);
			Assert.False(startInfo.RedirectStandardOutput);
			Assert.False(startInfo.RedirectStandardError);
			Assert.NotEqual("/bin/sh", startInfo.FileName);
		}
		else
		{
			Assert.False(startInfo.UseShellExecute);
			Assert.True(startInfo.RedirectStandardInput);
			Assert.True(startInfo.RedirectStandardOutput);
			Assert.True(startInfo.RedirectStandardError);
			Assert.Equal("/bin/sh", startInfo.FileName);
			Assert.Contains("exec \"$@\" </dev/null >/dev/null 2>&1", startInfo.ArgumentList);
			Assert.False(startInfo.Environment.ContainsKey(InvocationEnvironment.TerminalHostVariable));
		}
	}

	[Fact]
	public void WindowsFrameworkDependentDesktopLaunchPrefersGuiAppHost()
	{
		var startInfo = DesktopProcessLauncher.CreateStartInfo(
			"desktop-request.json",
			@"C:\Program Files\dotnet\dotnet.exe",
			@"C:\DevProjex\DevProjex.dll",
			@"C:\DevProjex\DevProjex.exe",
			isWindows: true);

		Assert.Equal(@"C:\DevProjex\DevProjex.exe", startInfo.FileName);
		Assert.True(startInfo.UseShellExecute);
		Assert.False(startInfo.CreateNoWindow);
		Assert.Equal(
			[
				DesktopLaunchRequestStore.InternalRequestArgument,
				"desktop-request.json"
			],
			startInfo.ArgumentList);
	}

	[Fact]
	public void WindowsDotnetDesktopFallbackCannotCreateOrReattachConsoleWindow()
	{
		var startInfo = DesktopProcessLauncher.CreateStartInfo(
			"desktop-request.json",
			@"C:\Program Files\dotnet\dotnet.exe",
			@"C:\DevProjex\DevProjex.dll",
			appHostPath: null,
			isWindows: true);

		Assert.Equal(@"C:\Program Files\dotnet\dotnet.exe", startInfo.FileName);
		Assert.False(startInfo.UseShellExecute);
		Assert.True(startInfo.CreateNoWindow);
		Assert.Equal(
			[
				@"C:\DevProjex\DevProjex.dll",
				DesktopLaunchRequestStore.InternalRequestArgument,
				"desktop-request.json"
			],
			startInfo.ArgumentList);
		Assert.False(
			startInfo.Environment.ContainsKey(
				InvocationEnvironment.TerminalHostVariable));
	}

	[Fact]
	public void InternalDesktopRequestIsRemovedBeforePublicRouting()
	{
		var previousRequest = Environment.GetEnvironmentVariable(
			InvocationEnvironment.DesktopRequestVariable);
		var previousTerminalHost = Environment.GetEnvironmentVariable(
			InvocationEnvironment.TerminalHostVariable);
		try
		{
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.TerminalHostVariable,
				"1");

			var remaining = DesktopLaunchRequestStore.PromoteInternalInvocation(
				[
					DesktopLaunchRequestStore.InternalRequestArgument,
					"desktop-request.json"
				]);

			Assert.Empty(remaining);
			Assert.Equal(
				"desktop-request.json",
				Environment.GetEnvironmentVariable(
					InvocationEnvironment.DesktopRequestVariable));
			Assert.Null(
				Environment.GetEnvironmentVariable(
					InvocationEnvironment.TerminalHostVariable));
		}
		finally
		{
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.DesktopRequestVariable,
				previousRequest);
			Environment.SetEnvironmentVariable(
				InvocationEnvironment.TerminalHostVariable,
				previousTerminalHost);
		}
	}

	[Fact]
	public void PublicArgumentsAreNotConsumedAsInternalDesktopRequest()
	{
		string[] arguments = ["open", ".", "--preview"];

		var remaining = DesktopLaunchRequestStore.PromoteInternalInvocation(arguments);

		Assert.Same(arguments, remaining);
	}

	[Fact]
	public void OpenWaitReadinessSurfacesTypedStartupFailure()
	{
		var state = DeserializeState(new Dictionary<string, object?>
		{
			["startupReady"] = true,
			["startupError"] = "DPX-DESKTOP-PROJECT-OPEN-FAILED"
		});

		Assert.True(DesktopOpenReadiness.TryGetFailureCode(state, out var code));
		Assert.Equal("DPX-DESKTOP-PROJECT-OPEN-FAILED", code);
	}

	private static IReadOnlyDictionary<string, object?> DeserializeState(
		IReadOnlyDictionary<string, object?> source) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(
			JsonSerializer.Serialize(source))!;

	private static DesktopInstanceRegistration CreateLiveRegistration(
		string instanceId,
		string projectPath,
		long processStartTime) =>
		new(
			DesktopProtocol.CurrentVersion,
			instanceId,
			Environment.ProcessId,
			processStartTime,
			projectPath,
			DateTimeOffset.UtcNow,
			OperatingSystem.IsWindows() ? "pipe" : "unix",
			OperatingSystem.IsWindows()
				? $"devprojex-{instanceId}"
				: Path.Combine(Path.GetTempPath(), $"dpx-{instanceId}.sock"));

	private static async Task<string> SendRawAsync(
		DesktopInstanceRegistration registration,
		string json,
		CancellationToken cancellationToken)
	{
		await using var stream = await ConnectRawAsync(registration, cancellationToken);
		var bytes = Encoding.UTF8.GetBytes(json + "\n");
		await stream.WriteAsync(bytes, cancellationToken);
		await stream.FlushAsync(cancellationToken);
		using var reader = new StreamReader(
			stream,
			new UTF8Encoding(false),
			detectEncodingFromByteOrderMarks: false,
			leaveOpen: true);
		return await reader.ReadLineAsync(cancellationToken) ??
		       throw new EndOfStreamException();
	}

	private static async Task<Stream> ConnectRawAsync(
		DesktopInstanceRegistration registration,
		CancellationToken cancellationToken)
	{
		if (registration.Transport == "pipe")
		{
			var pipe = new NamedPipeClientStream(
				".",
				registration.Endpoint,
				PipeDirection.InOut,
				PipeOptions.Asynchronous);
			await pipe.ConnectAsync(cancellationToken);
			return pipe;
		}

		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		await socket.ConnectAsync(
			new UnixDomainSocketEndPoint(registration.Endpoint),
			cancellationToken);
		return new NetworkStream(socket, ownsSocket: true);
	}

	private sealed class RecordingDesktopHandler : IDesktopInteractionHandler
	{
		public List<DesktopInteractionRequest> Requests { get; } = [];

		public Task<DesktopInteractionResult> HandleAsync(
			DesktopInteractionRequest request,
			CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(new DesktopInteractionResult(
				true,
				State: new Dictionary<string, object?>
				{
					["projectLoaded"] = true,
					["projectPath"] = @"C:\workspace\Project"
				}));
		}
	}

	private sealed class OpenStateDesktopHandler(string lastProject) : IDesktopInteractionHandler
	{
		public Task<DesktopInteractionResult> HandleAsync(
			DesktopInteractionRequest request,
			CancellationToken cancellationToken)
		{
			var open = Assert.IsType<DesktopOpenProjectRequest>(request);
			var projectPath = open.Request.UseLastProject
				? lastProject
				: open.Request.ProjectPath;
			return Task.FromResult(new DesktopInteractionResult(
				true,
				State: new Dictionary<string, object?>
				{
					["startupReady"] = true,
					["projectLoaded"] = true,
					["projectPath"] = projectPath
				}));
		}
	}

	private sealed class BlockingDesktopHandler : IDesktopInteractionHandler
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public DesktopInteractionRequest? Request { get; private set; }

		public async Task<DesktopInteractionResult> HandleAsync(
			DesktopInteractionRequest request,
			CancellationToken cancellationToken)
		{
			Request = request;
			Started.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken);
			return new DesktopInteractionResult(true);
		}
	}

	private sealed class FirstRequestBlockingHandler : IDesktopInteractionHandler
	{
		private int _requestCount;
		public TaskCompletionSource FirstRequestStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirstRequest { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FirstRequestCompleted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<DesktopInteractionResult> HandleAsync(
			DesktopInteractionRequest request,
			CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _requestCount) == 1)
			{
				FirstRequestStarted.TrySetResult();
				try
				{
					await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
				}
				finally
				{
					FirstRequestCompleted.TrySetResult();
				}
			}

			return new DesktopInteractionResult(true);
		}
	}
}
