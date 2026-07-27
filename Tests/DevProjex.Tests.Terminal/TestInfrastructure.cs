namespace DevProjex.Tests.Terminal;

internal sealed class TestTerminalEnvironment : ITerminalEnvironment
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
	public bool IsCi { get; init; }
	public bool IsTermDumb { get; init; }
	public bool IsNoColor { get; init; }
	public bool SupportsUnicode { get; init; } = true;
	public int Width { get; init; } = 120;
	public int Height { get; init; } = 30;
	public IReadOnlyDictionary<string, string?> Variables { get; init; } =
		new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

	public string StandardOutput => _output.ToString();
	public string StandardError => _error.ToString();
}

internal sealed class TemporaryDirectory : IDisposable
{
	public TemporaryDirectory()
	{
		Path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"DevProjex.Tests.Terminal",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	public string CreateDirectory(string relativePath)
	{
		var path = System.IO.Path.Combine(Path, relativePath);
		Directory.CreateDirectory(path);
		return path;
	}

	public string WriteFile(string relativePath, string content)
	{
		var path = System.IO.Path.Combine(Path, relativePath);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, new UTF8Encoding(false));
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
			// Test cleanup is best effort on platforms with delayed file handle release.
		}
	}
}
