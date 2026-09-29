using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// Keeps the DPX-* error code catalogs (CLI-Output-Contract.md, TerminalWorkspace.md,
/// McpServer.md) in sync with the codes the source actually raises.
/// </summary>
public sealed partial class ErrorCodeCatalogContractTests
{
	private const string SourceRootsUnderTest = "Apps, Application, Infrastructure, Kernel";

	// Apps/Avalonia/MainWindow.StoreScreenshotCapture.cs writes these into the Store-listing
	// screenshot tool's own failure.json, an internal release utility that is neither the CLI,
	// the Terminal Workspace, nor MCP. They have no catalog among the three this test checks.
	private static readonly string[] OutOfScopeCodes =
	[
		"DPX-STORE-CAPTURE-CANCELED",
		"DPX-STORE-CAPTURE-FAILED"
	];

	// DoctorCommandHandler.cs builds each doctor check's code as $"DPX-DOCTOR-{Name}" from a
	// fixed but runtime-enumerated check-name list, so no complete "DPX-DOCTOR-<NAME>" literal
	// exists in source for this scan to find (only the "DPX-DOCTOR-" prefix, itself dropped by
	// the trailing-hyphen rule below). CLI-Output-Contract.md documents the whole family as one
	// pattern plus the current check names, and its JSON example names one concrete instance;
	// that instance would otherwise look stale here, so the family is exempt from that check.
	private const string InterpolatedCodeFamilyPrefix = "DPX-DOCTOR-";

	[GeneratedRegex(@"DPX-[A-Z0-9-]+", RegexOptions.CultureInvariant)]
	private static partial Regex CodeTokenPattern();

	[Fact]
	public void EveryRaisedErrorCodeIsDocumentedAndEveryDocumentedCodeIsStillRaised()
	{
		var rootPath = PublishedApplicationLocator.FindRepositoryRoot();

		var sourceCodes = CollectCodes(EnumerateSourceText(rootPath))
			.Except(OutOfScopeCodes, StringComparer.Ordinal)
			.ToHashSet(StringComparer.Ordinal);
		Assert.True(sourceCodes.Count > 100, $"Expected well over 100 raised codes under {SourceRootsUnderTest}; found {sourceCodes.Count}.");

		var cliDocPath = Path.Combine(rootPath, "Docs", "CLI-Output-Contract.md");
		var tuiDocPath = Path.Combine(rootPath, "Docs", "TerminalWorkspace.md");
		var mcpDocPath = Path.Combine(rootPath, "Docs", "McpServer.md");
		var documentedCodes = CollectCodes(
			[
				File.ReadAllText(cliDocPath),
				File.ReadAllText(tuiDocPath),
				File.ReadAllText(mcpDocPath)
			]);

		var missing = sourceCodes
			.Where(code => !documentedCodes.Contains(code))
			.OrderBy(code => code, StringComparer.Ordinal)
			.ToArray();
		Assert.True(
			missing.Length == 0,
			"Raised but undocumented in CLI-Output-Contract.md / TerminalWorkspace.md / McpServer.md: " +
			string.Join(", ", missing));

		var stale = documentedCodes
			.Where(code => !sourceCodes.Contains(code) &&
						   !code.StartsWith(InterpolatedCodeFamilyPrefix, StringComparison.Ordinal))
			.OrderBy(code => code, StringComparer.Ordinal)
			.ToArray();
		Assert.True(
			stale.Length == 0,
			"Documented but no longer raised anywhere under " + SourceRootsUnderTest + ": " +
			string.Join(", ", stale));
	}

	private static IEnumerable<string> EnumerateSourceText(string rootPath)
	{
		foreach (var folder in new[] { "Apps", "Application", "Infrastructure", "Kernel" })
		{
			var folderPath = Path.Combine(rootPath, folder);
			if (!Directory.Exists(folderPath))
				continue;

			foreach (var file in Directory.EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
					file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				yield return File.ReadAllText(file);
			}
		}
	}

	/// <summary>
	/// A raised or documented match that ends in a bare hyphen is a prefix, not a concrete code:
	/// either a `StartsWith` fallback matcher (e.g. "DPX-DESKTOP-"), a code family built by string
	/// interpolation from a runtime name (e.g. "DPX-DOCTOR-{Name}" in DoctorCommandHandler.cs,
	/// documented in CLI-Output-Contract.md as the `DPX-DOCTOR-&lt;CHECK-NAME&gt;` pattern instead
	/// of one row per check), or a code-detection regex's own pattern text. None of these is a
	/// single stable code that documentation could list, so trailing-hyphen matches are dropped
	/// from both the raised set and the documented set alike.
	/// </summary>
	private static HashSet<string> CollectCodes(IEnumerable<string> texts)
	{
		var codes = new HashSet<string>(StringComparer.Ordinal);
		foreach (var text in texts)
			foreach (Match match in CodeTokenPattern().Matches(text))
				if (!match.Value.EndsWith('-'))
					codes.Add(match.Value);

		return codes;
	}
}
