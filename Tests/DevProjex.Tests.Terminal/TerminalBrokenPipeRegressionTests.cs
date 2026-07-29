using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class TerminalBrokenPipeRegressionTests
{
	[Theory]
	[InlineData(false, 32, true)]
	[InlineData(false, 109, false)]
	[InlineData(false, 232, false)]
	[InlineData(false, 5, false)]
	[InlineData(true, 32, false)]
	[InlineData(true, 109, true)]
	[InlineData(true, 232, true)]
	[InlineData(true, 5, false)]
	public void DetectorRecognizesOnlyPlatformNativeBrokenPipeCodes(
		bool isWindows,
		int nativeCode,
		bool expected)
	{
		var failure = new NativeCodeIOException(nativeCode);

		Assert.Equal(
			expected,
			TerminalBrokenPipeDetector.IsBrokenPipe(failure, isWindows));
	}

	[Fact]
	public void DetectorDoesNotTreatAnArbitraryInnerHResultAsNativePipeEvidence()
	{
		var failure = new IOException(
			"Unrelated write failure.",
			new NativeCodeException(109));

		Assert.False(
			TerminalBrokenPipeDetector.IsBrokenPipe(
				failure,
				isWindows: true));
	}

	[Fact]
	public void InvocationOutputTranslatesOnlyNativeBrokenPipeIoFailure()
	{
		var nativeCode = OperatingSystem.IsWindows() ? 109 : 32;
		var failure = new NativeCodeIOException(nativeCode);
		using var consoleOutput = new ConsoleOutputScope(
			new ThrowingTextWriter(failure));
		var environment = new InvocationEnvironment(hasAttachedConsole: false);

		var exception = Assert.Throws<TerminalBrokenPipeException>(
			() => environment.Output.Write("payload"));

		Assert.Same(failure, exception.InnerException);
	}

	[Fact]
	public void InvocationOutputDoesNotMaskUnrelatedIoFailure()
	{
		var failure = new NativeCodeIOException(5);
		using var consoleOutput = new ConsoleOutputScope(
			new ThrowingTextWriter(failure));
		var environment = new InvocationEnvironment(hasAttachedConsole: false);

		var exception = Assert.Throws<NativeCodeIOException>(
			() => environment.Output.Write("payload"));

		Assert.Same(failure, exception);
	}

	[Fact]
	public async Task WriterTranslatesConfirmedAsynchronousBrokenPipeFailure()
	{
		var failure = new NativeCodeIOException(109);
		using var writer = new TerminalOutputWriter(
			new AsyncThrowingTextWriter(failure),
			isWindows: true);

		var exception = await Assert.ThrowsAsync<TerminalBrokenPipeException>(
			() => writer.WriteAsync(
				"payload".AsMemory(),
				TestContext.Current.CancellationToken));

		Assert.Same(failure, exception.InnerException);
	}

	[Fact]
	public async Task CommandExecutionTreatsTranslatedBrokenPipeAsCleanTermination()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => Task.FromException<int>(new TerminalBrokenPipeException()));

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task ApplicationExitsCleanlyWhenStdoutConsumerClosesEarly()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = Path.Combine(project, "large.txt");
		await WriteLargeTextFileAsync(
			source,
			4 * 1024 * 1024,
			TestContext.Current.CancellationToken);
		var applicationAssembly = FindApplicationAssembly();
		Assert.True(
			File.Exists(applicationAssembly),
			$"Application assembly was not found: {applicationAssembly}");

		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add(applicationAssembly);
		process.StartInfo.ArgumentList.Add("export");
		process.StartInfo.ArgumentList.Add("context");
		process.StartInfo.ArgumentList.Add(project);
		process.StartInfo.ArgumentList.Add("--format");
		process.StartInfo.ArgumentList.Add("text");
		process.StartInfo.ArgumentList.Add("--git-mode");
		process.StartInfo.ArgumentList.Add("none");
		process.StartInfo.ArgumentList.Add("--exclude");
		process.StartInfo.ArgumentList.Add("none");
		process.StartInfo.ArgumentList.Add("--plain");
		process.StartInfo.ArgumentList.Add("-o");
		process.StartInfo.ArgumentList.Add("-");
		process.StartInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		process.StartInfo.Environment[InvocationEnvironment.InternalDataRootVariable] =
			workspace.CreateDirectory("app-data");
		process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";

		Assert.True(process.Start());
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		var standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
		try
		{
			var buffer = new char[128];
			var charactersRead = await process.StandardOutput.ReadAsync(
				buffer,
				timeout.Token);
			Assert.True(charactersRead > 0);
			process.StandardOutput.Dispose();

			await process.WaitForExitAsync(timeout.Token);
			var standardError = await standardErrorTask;

			Assert.Equal(CommandLineExitCodes.Success, process.ExitCode);
			Assert.Empty(standardError);
		}
		finally
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				await process.WaitForExitAsync(CancellationToken.None);
			}
		}
	}

	private static async Task WriteLargeTextFileAsync(
		string path,
		int byteCount,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[64 * 1024];
		Array.Fill(buffer, (byte)'x');
		await using var destination = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			buffer.Length,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		for (var remaining = byteCount; remaining > 0;)
		{
			var count = Math.Min(remaining, buffer.Length);
			await destination.WriteAsync(
				buffer.AsMemory(0, count),
				cancellationToken);
			remaining -= count;
		}
	}

	private static string FindApplicationAssembly()
	{
		var configuration = new DirectoryInfo(
				AppContext.BaseDirectory.TrimEnd(
					Path.DirectorySeparatorChar,
					Path.AltDirectorySeparatorChar))
			.Parent?
			.Name ?? "Debug";
		return Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
			"bin",
			configuration,
			"net10.0",
			"DevProjex.dll");
	}

	private sealed class NativeCodeIOException : IOException
	{
		public NativeCodeIOException(int nativeCode)
		{
			HResult = unchecked((int)(0x80070000u | (uint)nativeCode));
		}
	}

	private sealed class NativeCodeException : Exception
	{
		public NativeCodeException(int nativeCode)
		{
			HResult = unchecked((int)(0x80070000u | (uint)nativeCode));
		}
	}

	private sealed class ThrowingTextWriter(IOException exception) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(string? value) => throw exception;
	}

	private sealed class AsyncThrowingTextWriter(IOException exception) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;

		public override Task WriteAsync(
			ReadOnlyMemory<char> buffer,
			CancellationToken cancellationToken = default) =>
			Task.FromException(exception);
	}

	private sealed class ConsoleOutputScope : IDisposable
	{
		private readonly TextWriter _original = Console.Out;

		public ConsoleOutputScope(TextWriter replacement) =>
			Console.SetOut(replacement);

		public void Dispose() => Console.SetOut(_original);
	}
}
