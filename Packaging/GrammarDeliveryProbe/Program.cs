using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using DevProjex.Packaging.GrammarDeliveryProbe;
using TreeSitter;

// Delivery contract check. Exits non-zero unless every curated grammar is located, loaded and
// able to produce a usable tree on the RID this binary was published for.
//
// Usage: GrammarDeliveryProbe [--baseline]
//   --baseline  the binary was published without grammars; report size only and skip loading.

var isBaseline = args.Contains("--baseline", StringComparer.Ordinal);
var bindingVersion = ResolveBindingVersion();
var runtimeIdentifier = GrammarCatalog.ResolveRuntimeIdentifier();
#pragma warning disable IL3000 // An empty Location is precisely the signal being probed for here.
var isSingleFile = string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);
#pragma warning restore IL3000

IGrammarLibraryLocator locator =
#if GRAMMAR_DELIVERY_CONTENT
	new ContentGrammarLibraryLocator();
#else
	new EmbeddedGrammarLibraryLocator(EmbeddedGrammarLibraryLocator.DefaultRootDirectory(bindingVersion));
#endif

Console.WriteLine($"rid              : {runtimeIdentifier}");
Console.WriteLine($"binding          : {bindingVersion}");
Console.WriteLine($"strategy         : {locator.StrategyName}");
Console.WriteLine($"single-file      : {isSingleFile}");
Console.WriteLine($"grammar root     : {(locator is EmbeddedGrammarLibraryLocator e ? e.RootDirectory : ((ContentGrammarLibraryLocator)locator).RootDirectory)}");
Console.WriteLine($"loader debug     : {Environment.GetEnvironmentVariable("TREESITTER_DEBUG_LOADER") ?? "<unset>"}");
Console.WriteLine();

if (isBaseline)
{
	Console.WriteLine("baseline build: no grammars embedded, load check skipped");
	Console.WriteLine($"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy=baseline loaded=0/0 result=baseline");
	return 0;
}

var failures = new List<string>();
var loaded = 0;
var materializeWatch = Stopwatch.StartNew();
var paths = new Dictionary<string, string>(StringComparer.Ordinal);

foreach (var grammar in GrammarCatalog.All)
{
	try
	{
		paths[grammar.LanguageId] = locator.Resolve(grammar.LibraryBaseName);
	}
	catch (Exception exception)
	{
		failures.Add($"{grammar.LanguageId}: locate failed - {exception.GetType().Name}: {exception.Message}");
	}
}

materializeWatch.Stop();
Console.WriteLine($"materialize {paths.Count} grammars: {materializeWatch.Elapsed.TotalMilliseconds:F1} ms");
Console.WriteLine();

// Before anything is loaded: a damaged copy on disk is repaired at the next resolve, exactly as
// it would be for a user whose file was truncated or quarantined between sessions. It has to run
// here because the binding never releases module handles, so a mapped grammar cannot be rewritten.
if (args.Contains("--verify-recovery", StringComparer.Ordinal) &&
	locator is EmbeddedGrammarLibraryLocator embedded)
{
	Console.WriteLine("recovery check: damaging a materialized grammar and re-resolving");
	failures.AddRange(VerifyRecovery(embedded));
	Console.WriteLine();
}

foreach (var grammar in GrammarCatalog.All)
{
	if (!paths.TryGetValue(grammar.LanguageId, out var path))
		continue;

	var watch = Stopwatch.StartNew();
	try
	{
		// One Language per grammar for the process lifetime: the constructor performs a fresh
		// native load every call and never releases the module handle.
		using var language = new Language(path, grammar.ExportName);
		var loadMilliseconds = watch.Elapsed.TotalMilliseconds;
		using var parser = new Parser(language);
		using var tree = parser.Parse(grammar.SampleSource)
			?? throw new InvalidOperationException("parser returned no tree");
		using var query = new Query(language, grammar.SmokeQuery);
		var captures = query.Execute(tree.RootNode).Captures.Count();
		watch.Stop();

		if (captures == 0)
		{
			failures.Add($"{grammar.LanguageId}: grammar loaded but the smoke query captured nothing");
			continue;
		}

		loaded++;
		Console.WriteLine(
			$"  OK   {grammar.LanguageId,-11} load={loadMilliseconds,6:F1}ms total={watch.Elapsed.TotalMilliseconds,6:F1}ms " +
			$"captures={captures} error={tree.RootNode.HasError}");
	}
	catch (Exception exception)
	{
		failures.Add($"{grammar.LanguageId}: {exception.GetType().Name}: {exception.Message}");
		Console.WriteLine($"  FAIL {grammar.LanguageId,-11} {exception.GetType().Name}: {exception.Message}");
	}
}

Console.WriteLine();
foreach (var failure in failures)
	Console.Error.WriteLine($"FAILURE {failure}");

var total = GrammarCatalog.All.Count;
var result = failures.Count == 0 && loaded == total ? "pass" : "fail";
Console.WriteLine(
	$"GRAMMAR-DELIVERY rid={runtimeIdentifier} strategy={locator.StrategyName} " +
	$"loaded={loaded}/{total} materialize_ms={materializeWatch.Elapsed.TotalMilliseconds:F0} result={result}");
return result == "pass" ? 0 : 1;

// A materialized grammar can be truncated by a crash, quarantined by antivirus, or damaged on
// disk. The contract is that the next resolve repairs it instead of loading damaged native code.
static List<string> VerifyRecovery(EmbeddedGrammarLibraryLocator locator)
{
	var problems = new List<string>();
	// The smallest grammar keeps the check cheap; the code path is identical for all of them.
	var grammar = GrammarCatalog.All.Single(candidate => candidate.LanguageId == "go");
	var path = locator.Resolve(grammar.LibraryBaseName);
	var expected = locator.GetEmbeddedHash(grammar.LibraryBaseName);

	// The grammar is already mapped by the load loop above, which is the realistic shape of this
	// failure: the binding never releases module handles, so the damaged copy cannot simply be
	// overwritten on Windows.
	var damaged = File.ReadAllBytes(path);
	damaged[damaged.Length / 2] ^= 0xFF;
	try
	{
		File.WriteAllBytes(path, damaged);
	}
	catch (IOException exception)
	{
		problems.Add($"recovery: could not damage the copy to run the check - {exception.Message}");
		return problems;
	}

	if (SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(expected))
	{
		problems.Add("recovery: flipping a byte did not change the file hash, the check proves nothing");
		return problems;
	}

	var repaired = locator.Resolve(grammar.LibraryBaseName);
	if (!SHA256.HashData(File.ReadAllBytes(repaired)).SequenceEqual(expected))
	{
		problems.Add("recovery: a damaged grammar was NOT re-materialized - damaged native code would be loaded");
		return problems;
	}

	try
	{
		using var language = new Language(repaired, grammar.ExportName);
		using var parser = new Parser(language);
		using var tree = parser.Parse(grammar.SampleSource)
			?? throw new InvalidOperationException("parser returned no tree");
		using var query = new Query(language, grammar.SmokeQuery);
		if (query.Execute(tree.RootNode).Captures.Count() == 0)
			problems.Add("recovery: repaired grammar loaded but captured nothing");
	}
	catch (Exception exception)
	{
		problems.Add($"recovery: repaired grammar failed to load - {exception.GetType().Name}: {exception.Message}");
	}

	if (problems.Count == 0)
		Console.WriteLine($"  OK   {grammar.LanguageId} re-materialized after corruption and loaded");
	return problems;
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
