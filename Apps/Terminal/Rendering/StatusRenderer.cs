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
		if (ShouldRenderStaticStatus())
		{
			environment.Error.WriteLine(description);
			return await operation().ConfigureAwait(false);
		}

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

	private bool ShouldRenderStaticStatus() =>
		options.Progress == TerminalProgressMode.Always &&
		options.Verbosity is not (
			TerminalVerbosity.Quiet or
			TerminalVerbosity.Minimal) &&
		(options.Plain ||
		 environment.IsTermDumb ||
		 !environment.IsErrorInteractive);
}
