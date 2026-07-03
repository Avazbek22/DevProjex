namespace DevProjex.Infrastructure.ResourceStore;

public sealed class CommandLineHelpContentProvider
{
	private const string EnglishResourceName = "DevProjex.Assets.CommandLineHelp.help.en.txt";
	private const string EnglishResourceSuffix = ".CommandLineHelp.help.en.txt";

	private const string FallbackEnglishHelpText =
		"""
		DevProjex

		Help content resource is unavailable.
		Try: DevProjex --path <folder> --report
		Utility options: --help, -h, /?, --version
		""";

	private readonly Lazy<string> _englishHelp;

	public CommandLineHelpContentProvider()
		: this(typeof(Marker).Assembly)
	{
	}

	internal CommandLineHelpContentProvider(Assembly assembly)
	{
		_englishHelp = new Lazy<string>(() => LoadEnglishHelp(assembly));
	}

	public string GetHelpText() => _englishHelp.Value;

	private static string LoadEnglishHelp(Assembly assembly)
	{
		using var stream = OpenResourceStream(assembly);
		if (stream is null)
			return FallbackEnglishHelpText;

		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		var text = reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd();

		// CLI help runs before Avalonia and normal DI. A minimal fallback keeps --help usable even
		// when packaging accidentally drops the embedded text resource.
		return string.IsNullOrWhiteSpace(text) ? FallbackEnglishHelpText : text;
	}

	private static Stream? OpenResourceStream(Assembly assembly)
	{
		var stream = assembly.GetManifestResourceStream(EnglishResourceName);
		if (stream is not null)
			return stream;

		var fallbackName = assembly.GetManifestResourceNames()
			.FirstOrDefault(name => name.EndsWith(EnglishResourceSuffix, StringComparison.OrdinalIgnoreCase));

		return fallbackName is null ? null : assembly.GetManifestResourceStream(fallbackName);
	}
}
