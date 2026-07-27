using DevProjex.Terminal.CommandLine;
using Spectre.Console;

namespace DevProjex.Terminal.Rendering;

public sealed class StatusRenderer(
	ITerminalEnvironment environment,
	TerminalOutputOptions options)
{
	public async Task<T> RunAsync<T>(
		string description,
		Func<Task<T>> operation)
	{
		var capabilities = TerminalCapabilities.Resolve(
			environment,
			options,
			forStandardError: true);
		if (!capabilities.UseInteractiveProgress)
			return await operation().ConfigureAwait(false);

		var console = AnsiConsoleFactory.Create(environment.Error, capabilities);
		return await console.Status()
			.Spinner(Spinner.Known.Dots)
			.StartAsync(description, _ => operation())
			.ConfigureAwait(false);
	}
}
