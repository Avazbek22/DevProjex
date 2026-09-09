using System.Diagnostics;
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
}
