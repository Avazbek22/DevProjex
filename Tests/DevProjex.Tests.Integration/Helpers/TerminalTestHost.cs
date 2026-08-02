namespace DevProjex.Tests.Integration.Helpers;

internal sealed class TerminalTestHost : ITerminalEnvironment
{
	private readonly StringWriter _output = new();
	private readonly StringWriter _error = new();

	public TextReader Input { get; init; } = new StringReader(string.Empty);
	public TextWriter Output => _output;
	public TextWriter Error => _error;
	public bool IsInputInteractive { get; init; }
	public bool IsOutputInteractive { get; init; }
	public bool IsErrorInteractive { get; init; }
	public bool HasAttachedConsole { get; init; }
	public bool IsTerminalHost { get; init; }
	public bool IsCi { get; init; } = true;
	public bool IsTermDumb { get; init; }
	public bool IsNoColor { get; init; } = true;
	public bool SupportsUnicode { get; init; } = true;
	public int Width { get; init; } = 120;
	public int Height { get; init; } = 30;
	public IReadOnlyDictionary<string, string?> Variables { get; init; } =
		new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

	public string StandardOutput => _output.ToString();
	public string StandardError => _error.ToString();

	public async Task<int> RunAsync(
		IReadOnlyList<string> arguments,
		Func<string>? appDataPathProvider = null,
		CancellationToken cancellationToken = default)
	{
		var application = new TerminalApplication(
			this,
			new TerminalServiceFactory(appDataPathProvider));
		return await application.RunAsync(arguments, cancellationToken);
	}
}
