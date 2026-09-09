using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DevProjex.Tests.Terminal;

public sealed class SelectionPathGitProcessTests
{
	[Fact]
	public async Task GitQuotePathFalsePipelineSelectsUnicodeAndLiteralQuoteNames()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var unicodePath = Path.Combine(project, "Пример.cs");
		var quoteName = OperatingSystem.IsWindows() ? "literal'quote.cs" : "literal\"quote.cs";
		var quotePath = Path.Combine(project, quoteName);
		await File.WriteAllTextAsync(unicodePath, "class Пример {}\n", TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(quotePath, "class Quote {}\n", TestContext.Current.CancellationToken);
		await RunGitAsync(project, "init");
		await RunGitAsync(project, "config", "user.email", "test@example.invalid");
		await RunGitAsync(project, "config", "user.name", "Test");
		await RunGitAsync(project, "add", "--", ".");
		await RunGitAsync(project, "commit", "-m", "initial");
		await File.AppendAllTextAsync(unicodePath, "// changed\n", TestContext.Current.CancellationToken);
		await File.AppendAllTextAsync(quotePath, "// changed\n", TestContext.Current.CancellationToken);
		var selected = await RunGitAsync(
			project,
			"-c", "core.quotepath=false", "diff", "--name-only");

		var applicationAssembly = PublishedApplicationLocator.FindApplicationAssembly();
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardInputEncoding = new UTF8Encoding(false),
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			}
		};
		foreach (var argument in new[]
		         {
			         applicationAssembly, "export", "context", project,
			         "--view", "content", "--format", "json", "--git-mode", "none",
			         "--exclude", "none", "--select-from", "-", "--plain", "-o", "-"
		         })
			process.StartInfo.ArgumentList.Add(argument);
		process.StartInfo.Environment[InvocationEnvironment.TerminalHostVariable] = "1";
		process.StartInfo.Environment[InvocationEnvironment.InternalDataRootVariable] =
			workspace.CreateDirectory("app-data");
		Assert.True(process.Start());
		await process.StandardInput.WriteAsync(selected);
		process.StandardInput.Close();
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, process.ExitCode);
		Assert.Empty(await errorTask);
		using var document = JsonDocument.Parse(await outputTask);
		var paths = document.RootElement.GetProperty("files")
			.EnumerateArray().Select(file => file.GetProperty("path").GetString()!).ToArray();
		Assert.Equal(2, paths.Length);
		Assert.Contains(paths, path => path.EndsWith('/' + quoteName, StringComparison.Ordinal));
		Assert.Contains(paths, path => path.EndsWith("/Пример.cs", StringComparison.Ordinal));
	}

	private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = "git",
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false)
			}
		};
		foreach (var argument in arguments)
			process.StartInfo.ArgumentList.Add(argument);
		try
		{
			Assert.True(process.Start());
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is unavailable for the process contract.");
			return string.Empty;
		}
		var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		Assert.True(process.ExitCode == 0, await errorTask);
		return await outputTask;
	}
}
