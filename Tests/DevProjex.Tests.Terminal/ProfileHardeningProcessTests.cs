using System.Diagnostics;
using System.IO.Pipes;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class ProfileHardeningProcessTests
{
	[Fact(Timeout = 60_000)]
	public void ProfileResetReportsPartialCleanupAndRetryFinishesIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		store.SaveProfile(project, new ProjectSelectionProfile([], [".cs"], []));
		using (var heldLock = new FileStream(
			store.GetPath() + ".lock",
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None))
		{
			var partial = Run(dataRoot, "profile", "reset", project, "--language", "en", "--plain");
			Assert.Equal(CommandLineExitCodes.PolicyFailure, partial.ExitCode);
			Assert.Empty(partial.StandardOutput);
			Assert.Contains("DPX-CLI-PROFILE-PARTIAL", partial.StandardError, StringComparison.Ordinal);
			Assert.Contains("Repeat the command", partial.StandardError, StringComparison.Ordinal);
		}

		var retried = Run(dataRoot, "profile", "reset", project, "--language", "en", "--plain");
		Assert.Equal(CommandLineExitCodes.Success, retried.ExitCode);
		Assert.False(store.TryLoadProfile(project, out _));
	}

	[Fact(Timeout = 60_000)]
	public async Task ProfileSaveReportsConflictFromASeparateCliProcess()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "source.cs"), "class Source { }");
		var dataRoot = workspace.CreateDirectory("data");
		var store = new ProjectProfileStore(() => dataRoot);
		store.SaveProfile(project, new ProjectSelectionProfile([], [".cs"], []));
		var pipeName = $"dpx-{Guid.NewGuid():N}";
		using var barrier = new NamedPipeClientStream(
			".",
			pipeName,
			PipeDirection.InOut,
			PipeOptions.Asynchronous);
		using var process = StartConflictProcess(dataRoot, pipeName, project);
		var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

		await barrier.ConnectAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
		Assert.Equal(1, barrier.ReadByte());
		var current = store.LookupProfile(project, TimeSpan.FromSeconds(5));
		Assert.Equal(ProjectProfileLookupStatus.Found, current.Status);
		Assert.True(store.TrySaveProfile(
			project,
			new ProjectSelectionProfile([], [".json"], []),
			current.UpdatedUtc!.Value.AddMinutes(1)));
		barrier.WriteByte(1);
		await barrier.FlushAsync(TestContext.Current.CancellationToken);

		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(CommandLineExitCodes.PolicyFailure, process.ExitCode);
		Assert.Empty(await stdout);
		var error = await stderr;
		Assert.Contains("DPX-CLI-PROFILE-CONFLICT", error, StringComparison.Ordinal);
		Assert.Contains("Repeat the command", error, StringComparison.Ordinal);
		Assert.True(store.TryLoadProfile(project, out var persisted));
		Assert.Equal([".json"], persisted.SelectedExtensions);
	}

	private static TerminalTestProcessResult Run(string dataRoot, params string[] arguments)
	{
		var assembly = PublishedApplicationLocator.FindApplicationAssembly();
		var startInfo = new ProcessStartInfo("dotnet")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(assembly)!
		};
		startInfo.ArgumentList.Add(assembly);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		startInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		startInfo.Environment[InvocationEnvironment.InternalDataRootVariable] = dataRoot;
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		return TerminalTestProcess.Run(startInfo, TimeSpan.FromSeconds(15));
	}

	private static Process StartConflictProcess(string dataRoot, string pipeName, string project)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = PublishedApplicationLocator.FindProgressCheckpointHostExecutable(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		string[] arguments =
		[
			"--profile-conflict", dataRoot, pipeName,
			"profile", "save", project, "--extension", ".md", "--language", "en", "--plain"
		];
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}
		var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start());
		process.StandardInput.Close();
		return process;
	}
}
