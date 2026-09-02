using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

// Delivery contract check. Exits non-zero unless every grammar this build carries can be located,
// loaded and used to parse on the RID the binary was published for.
//
// It drives the production locator rather than a copy, and derives the grammar list from what is
// actually embedded, so a language added to the product is covered here without touching this file.
//
// Usage: GrammarDeliveryProbe [--baseline] [--content] [--verify-recovery]
//        GrammarDeliveryProbe --materialize-only --root <directory>

var isBaseline = args.Contains("--baseline", StringComparer.Ordinal);
var useContent = args.Contains("--content", StringComparer.Ordinal);
var materializeOnly = args.Contains("--materialize-only", StringComparer.Ordinal);
var bindingVersion = ResolveBindingVersion();
var runtimeIdentifier = GrammarPlatform.RuntimeIdentifier;
var explicitRoot = ReadOption(args, "--root");

#pragma warning disable IL3000 // An empty Location is precisely the signal being probed for here.
var isSingleFile = string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);
#pragma warning restore IL3000

var embedded = new EmbeddedGrammarLibraryLocator(
	typeof(GrammarPlatform).Assembly,
	"DevProjex.Grammars/",
	explicitRoot is null
		? EmbeddedGrammarLibraryLocator.DefaultRootDirectory(bindingVersion)
		: Path.GetFullPath(explicitRoot));
IGrammarLibraryLocator locator = useContent ? new ContentGrammarLibraryLocator() : embedded;
using var catalog = new TreeSitterCodeCompressor(locator);
var expectedGrammars = catalog.Languages
	.ToDictionary(
		static language => language.GrammarLibrary,
		static language => language.GrammarExport,
		StringComparer.Ordinal);

Console.WriteLine($"rid              : {runtimeIdentifier}");
Console.WriteLine($"binding          : {bindingVersion}");
Console.WriteLine($"strategy         : {locator.StrategyName}");
Console.WriteLine($"single-file      : {isSingleFile}");
Console.WriteLine($"loader debug     : {Environment.GetEnvironmentVariable("TREESITTER_DEBUG_LOADER") ?? "<unset>"}");
Console.WriteLine();

if (isBaseline)
{
	Console.WriteLine("baseline build: no grammars embedded, load check skipped");
	Console.WriteLine($"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy=baseline loaded=0/0 result=baseline");
	return 0;
}

if (!useContent && !materializeOnly && explicitRoot is null)
{
	var pruned = embedded.PruneAbandonedDirectories();
	Console.WriteLine($"pruned abandoned grammar directories: {pruned.Count}");
	foreach (var directory in pruned)
		Console.WriteLine($"   removed {directory}");
	Console.WriteLine();
}

var libraries = locator.EnumerateLibraries();
Console.WriteLine($"grammars carried by this build: {libraries.Count}");
var missing = expectedGrammars.Keys.Except(libraries, StringComparer.Ordinal).Order().ToArray();
var unexpected = libraries.Except(expectedGrammars.Keys, StringComparer.Ordinal).Order().ToArray();
if (missing.Length > 0 || unexpected.Length > 0)
{
	Console.Error.WriteLine(
		$"FAILURE grammar catalog mismatch; missing=[{string.Join(", ", missing)}], " +
		$"unexpected=[{string.Join(", ", unexpected)}]");
	Console.WriteLine(
		$"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy={locator.StrategyName} " +
		$"loaded=0/{expectedGrammars.Count} result=fail");
	return 1;
}

if (materializeOnly)
{
	if (useContent)
	{
		Console.Error.WriteLine("FAILURE --materialize-only is only valid for embedded delivery");
		return 2;
	}

	foreach (var library in libraries)
	{
		var path = locator.Resolve(library);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"FAILURE materialized grammar is missing: {path}");
			return 1;
		}
	}

	Console.WriteLine(
		$"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy={locator.StrategyName} " +
		$"materialized={libraries.Count}/{libraries.Count} result=pass");
	return 0;
}

if (args.Contains("--verify-recovery", StringComparer.Ordinal) && libraries.Count > 0 && !useContent)
{
	// Before anything is loaded: a damaged copy on disk must be repaired at the next resolve,
	// exactly as it would be for a user whose file was truncated between sessions. It has to run
	// here because the binding never releases module handles, so a mapped grammar cannot be
	// rewritten on Windows.
	Console.WriteLine();
	Console.WriteLine("recovery check: damaging a materialized grammar and re-resolving");
	var failure = VerifyRecovery(embedded, libraries[0]);
	if (failure is not null)
	{
		Console.Error.WriteLine($"FAILURE {failure}");
		Console.WriteLine($"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy={locator.StrategyName} loaded=0/{libraries.Count} result=fail");
		return 1;
	}

	Console.WriteLine("  OK   repaired after corruption");
}

Console.WriteLine();
var loaded = 0;
var failures = new List<string>();
foreach (var library in libraries)
{
	var watch = Stopwatch.StartNew();
	try
	{
		using var language = new Language(locator.Resolve(library), expectedGrammars[library]);
		var loadMilliseconds = watch.Elapsed.TotalMilliseconds;
		using var parser = new Parser(language);
		var (probeSource, requireCleanParse) = ResolveProbe(library);
		using var tree = parser.Parse(probeSource) ?? throw new InvalidOperationException("parser returned no tree");
		if (requireCleanParse && tree.RootNode.HasError)
			throw new InvalidOperationException($"{library} could not parse its delivery fixture");
		_ = tree.RootNode.Type;
		watch.Stop();
		loaded++;
		Console.WriteLine($"  OK   {library,-26} load={loadMilliseconds,6:F1}ms total={watch.Elapsed.TotalMilliseconds,6:F1}ms");
	}
	catch (Exception exception)
	{
		failures.Add($"{library}: {exception.GetType().Name}: {exception.Message}");
		Console.WriteLine($"  FAIL {library,-26} {exception.GetType().Name}: {exception.Message}");
	}
}

Console.WriteLine();
foreach (var failure in failures)
	Console.Error.WriteLine($"FAILURE {failure}");

var result = failures.Count == 0 && loaded == libraries.Count && loaded > 0 ? "pass" : "fail";
Console.WriteLine(
	$"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy={locator.StrategyName} " +
	$"loaded={loaded}/{libraries.Count} result={result}");
return result == "pass" ? 0 : 1;

static (string Source, bool RequireCleanParse) ResolveProbe(string libraryBaseName) =>
	libraryBaseName switch
	{
		"tree-sitter-kotlin" => ("fun main() { println(\"grammar delivery\") }", true),
		"tree-sitter-toml" => ("title = \"grammar delivery\"\n[probe]\nenabled = true\n", true),
		"tree-sitter-xml" => ("<?xml version=\"1.0\"?><root><!-- grammar delivery --><![CDATA[kept]]></root>", true),
		"tree-sitter-yaml" => ("---\nprobe: &probe\n  enabled: true # grammar delivery\ncopy: *probe\n", true),
		_ => ("x", false)
	};

static string? VerifyRecovery(EmbeddedGrammarLibraryLocator locator, string libraryBaseName)
{
	var path = locator.Resolve(libraryBaseName);
	var expected = locator.GetEmbeddedHash(libraryBaseName);

	var damaged = File.ReadAllBytes(path);
	damaged[damaged.Length / 2] ^= 0xFF;
	try
	{
		File.WriteAllBytes(path, damaged);
	}
	catch (IOException exception)
	{
		return $"recovery: could not damage the copy to run the check - {exception.Message}";
	}

	var repaired = locator.Resolve(libraryBaseName);
	return SHA256.HashData(File.ReadAllBytes(repaired)).SequenceEqual(expected)
		? null
		: "recovery: a damaged grammar was NOT re-materialized - damaged native code would be loaded";
}

static string ResolveBindingVersion()
{
	var informational = typeof(Language).Assembly
		.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
	if (string.IsNullOrWhiteSpace(informational))
		return typeof(Language).Assembly.GetName().Version?.ToString() ?? "unknown";
	var plus = informational.IndexOf('+');
	return plus < 0 ? informational : informational[..plus];
}

static string? ReadOption(string[] arguments, string option)
{
	for (var index = 0; index < arguments.Length; index++)
	{
		if (!arguments[index].Equals(option, StringComparison.Ordinal))
			continue;
		if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
			throw new ArgumentException($"Missing value for {option}.");
		return arguments[index + 1];
	}
	return null;
}
