using System.Reflection;
using Avalonia.VisualTree;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowElevationUiTests
{
	[AvaloniaFact]
	public async Task OpeningReparseRootExplainsTheAliasWithoutElevation()
	{
		using var project = UiTestProject.CreateDefault();
		using var aliases = new TemporaryTestDirectory();
		var target = aliases.CreateFolder("target");
		File.WriteAllText(Path.Combine(target, "App.cs"), "internal sealed class App { }\n");
		var alias = Path.Combine(aliases.Path, "alias");
		if (!TryCreateDirectoryAlias(alias, target))
			Assert.Skip("Directory aliases are unavailable in this test environment.");
		var elevation = new RecordingElevationService(isAdministrator: false);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { Elevation = elevation });
		Window? dialog = null;

		try
		{
			var openTask = await UiTestDriver.BeginOpenFolderAsync(window, alias);
			dialog = await WaitForOwnedDialogAsync(window);

			Assert.Contains(
				"This folder is a junction or symbolic link. Open the target folder instead.",
				GetDialogText(dialog),
				StringComparison.Ordinal);
			Assert.Equal(0, elevation.RelaunchCount);

			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			dialog = null;
			await openTask;
			Assert.Equal(0, elevation.RelaunchCount);
		}
		finally
		{
			if (dialog?.IsVisible == true)
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task BackgroundReloadOfAReparseRootNeverElevates()
	{
		using var project = UiTestProject.CreateDefault();
		var elevation = new RecordingElevationService(isAdministrator: false);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { Elevation = elevation });
		var physicalRoot = project.RootPath + "-physical";
		Window? dialog = null;

		try
		{
			Directory.Move(project.RootPath, physicalRoot);
			if (!TryCreateDirectoryAlias(project.RootPath, physicalRoot))
				Assert.Skip("Directory aliases are unavailable in this test environment.");

			await UiTestDriver.RefreshProjectAsync(window);
			dialog = await WaitForOwnedDialogAsync(window);

			Assert.Contains(
				"This folder is a junction or symbolic link. Open the target folder instead.",
				GetDialogText(dialog),
				StringComparison.Ordinal);
			Assert.Equal(0, elevation.RelaunchCount);
		}
		finally
		{
			if (dialog?.IsVisible == true)
				await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			await UiTestDriver.CloseWindowAsync(window);
			RestorePhysicalRoot(project.RootPath, physicalRoot);
		}
	}

	[AvaloniaTheory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task ElevationGuardRejectsAdministratorsAndRepeatedAttempts(
		bool isAdministrator,
		bool elevationAttempted)
	{
		using var project = UiTestProject.CreateDefault();
		var elevation = new RecordingElevationService(isAdministrator);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with { Elevation = elevation });

		try
		{
			if (elevationAttempted)
				SetPrivateField(window, "_elevationAttempted", true);

			var result = await window.Dispatcher.InvokeAsync(() =>
				InvokeTryElevateAndRestart(window, project.RootPath));

			Assert.False(result);
			Assert.Equal(0, elevation.RelaunchCount);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private static async Task<Window> WaitForOwnedDialogAsync(MainWindow window)
	{
		await UiTestDriver.WaitForConditionAsync(
			window,
			() => window.OwnedWindows.Count == 1,
			"the root-access dialog to open");
		return Assert.Single(window.OwnedWindows);
	}

	private static string GetDialogText(Window dialog) =>
		string.Join(
			"\n",
			dialog.GetVisualDescendants()
				.OfType<TextBlock>()
				.Select(static text => text.Text));

	private static bool InvokeTryElevateAndRestart(MainWindow window, string path)
	{
		var method = typeof(MainWindow).GetMethod(
			"TryElevateAndRestart",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<bool>(method!.Invoke(window, [path]));
	}

	private static void SetPrivateField(MainWindow window, string name, object value)
	{
		var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(window, value);
	}

	private static bool TryCreateDirectoryAlias(string alias, string target)
	{
		try
		{
			if (!OperatingSystem.IsWindows())
			{
				Directory.CreateSymbolicLink(alias, target);
				return true;
			}

			var startInfo = new ProcessStartInfo(
				Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
				$"/d /c mklink /J \"{alias}\" \"{target}\"")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			using var process = Process.Start(startInfo);
			process?.WaitForExit();
			return process?.ExitCode == 0 && Directory.Exists(alias);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void RestorePhysicalRoot(string alias, string physicalRoot)
	{
		try
		{
			if (Directory.Exists(alias))
				Directory.Delete(alias);
			if (Directory.Exists(physicalRoot))
				Directory.Move(physicalRoot, alias);
		}
		catch
		{
			// The shared UI fixture performs a final best-effort cleanup of its instance root.
		}
	}

	private sealed class RecordingElevationService(bool isAdministrator) : IElevationService
	{
		public bool IsAdministrator { get; } = isAdministrator;
		public int RelaunchCount { get; private set; }

		public bool TryRelaunchAsAdministrator(IReadOnlyList<string> arguments)
		{
			RelaunchCount++;
			return false;
		}
	}

	private sealed class TemporaryTestDirectory : IDisposable
	{
		public TemporaryTestDirectory()
		{
			Path = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(),
				"DevProjexTests",
				Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string CreateFolder(string name)
		{
			var path = System.IO.Path.Combine(Path, name);
			Directory.CreateDirectory(path);
			return path;
		}

		public void Dispose()
		{
			try
			{
				if (Directory.Exists(Path))
					Directory.Delete(Path, recursive: true);
			}
			catch
			{
				// Test cleanup must not mask the assertion that failed.
			}
		}
	}
}
