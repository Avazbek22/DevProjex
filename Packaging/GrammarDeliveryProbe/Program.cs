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

var isBaseline = args.Contains("--baseline", StringComparer.Ordinal);
var useContent = args.Contains("--content", StringComparer.Ordinal);
var bindingVersion = ResolveBindingVersion();
var runtimeIdentifier = GrammarPlatform.RuntimeIdentifier;

#pragma warning disable IL3000 // An empty Location is precisely the signal being probed for here.
var isSingleFile = string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);
#pragma warning restore IL3000

var embedded = new EmbeddedGrammarLibraryLocator(
	typeof(GrammarPlatform).Assembly,
	"DevProjex.Grammars/",
	EmbeddedGrammarLibraryLocator.DefaultRootDirectory(bindingVersion));
IGrammarLibraryLocator locator = useContent ? new ContentGrammarLibraryLocator() : embedded;

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

if (!useContent)
{
	var pruned = embedded.PruneAbandonedDirectories();
	Console.WriteLine($"pruned abandoned grammar directories: {pruned.Count}");
	foreach (var directory in pruned)
		Console.WriteLine($"   removed {directory}");
	Console.WriteLine();
}

var libraries = locator.EnumerateLibraries();
Console.WriteLine($"grammars carried by this build: {libraries.Count}");

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
		using var language = new Language(locator.Resolve(library), ExportFor(library));
		var loadMilliseconds = watch.Elapsed.TotalMilliseconds;
		using var parser = new Parser(language);
		using var tree = parser.Parse("x") ?? throw new InvalidOperationException("parser returned no tree");
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

// The binding maps ids by lowercasing and swapping '-' for '_', so tree-sitter-c-sharp exports
// tree_sitter_c_sharp. Deriving it here keeps the probe independent of the language packs.
static string ExportFor(string libraryBaseName) =>
	libraryBaseName.Replace('-', '_');

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
