using System.Globalization;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	public static TheoryData<string, string, string, string> MemberNavigationCases => new()
	{
		{
			"Members.cs",
			"namespace P;\nclass A { string Run() { return \"member-marker-a\"; } }\nclass B { string Run() { return \"member-marker-b\"; } }\n// fallback-marker\n",
			"P.A.Run",
			"P.B.Run"
		},
		{
			"members.js",
			"class A { run() { return 'member-marker-a'; } }\nclass B { run() { return 'member-marker-b'; } }\n(function () { return 'fallback-marker'; })();\n",
			"A.run",
			"B.run"
		},
		{
			"members.ts",
			"class A { run(): string { return 'member-marker-a'; } }\nclass B { run(): string { return 'member-marker-b'; } }\n(function (): string { return 'fallback-marker'; })();\n",
			"A.run",
			"B.run"
		},
		{
			"members.go",
			"package sample\ntype A struct{}\ntype B struct{}\nfunc (a A) Run() string { return \"member-marker-a\" }\nfunc (b B) Run() string { return \"member-marker-b\" }\nvar fallback = func() string { return \"fallback-marker\" }\n",
			"A.Run",
			"B.Run"
		},
		{
			"members.py",
			"class A:\n    def run(self):\n        return 'member-marker-a'\nclass B:\n    def run(self):\n        return 'member-marker-b'\n(lambda: 'fallback-marker')()\n",
			"A.run",
			"B.run"
		},
		{
			"Members.java",
			"package sample;\nclass A { String run() { return \"member-marker-a\"; } }\nclass B { String run() { return \"member-marker-b\"; } }\n// fallback-marker\n",
			"sample.A.run",
			"sample.B.run"
		},
		{
			"members.rs",
			"struct A;\nstruct B;\nimpl A { fn run(&self) -> &'static str { \"member-marker-a\" } }\nimpl B { fn run(&self) -> &'static str { \"member-marker-b\" } }\n// fallback-marker\n",
			"members::impl<A>::run",
			"members::impl<B>::run"
		},
		{
			"Members.kt",
			"package sample\nclass A {\n fun run(): String { return \"member-marker-a\" }\n}\nclass B {\n fun run(): String { return \"member-marker-b\" }\n}\n// fallback-marker\n",
			"sample.A.run",
			"sample.B.run"
		},
		{
			"members.rb",
			"module A\n def run\n  'member-marker-a'\n end\nend\nmodule B\n def run\n  'member-marker-b'\n end\nend\n# fallback-marker\n",
			"A#run",
			"B#run"
		},
		{
			"Members.php",
			"<?php\nnamespace Sample;\nclass A {\n function run() { return 'member-marker-a'; }\n}\nclass B {\n function run() { return 'member-marker-b'; }\n}\n// fallback-marker\n",
			"Sample.A.run",
			"Sample.B.run"
		}
	};

	[Theory]
	[MemberData(nameof(MemberNavigationCases))]
	public async Task RealProcessUsesTheNearestNamedMemberAcrossSupportedLanguages(
		string fileName,
		string source,
		string firstSymbol,
		string secondSymbol)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("member-project");
		workspace.WriteFile($"member-project/{fileName}", source);
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var search = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "member-marker|fallback-marker",
				["context_lines"] = 0
			})));
		Assert.Contains($"in {firstSymbol}\n", search, StringComparison.Ordinal);
		Assert.Contains($"in {secondSymbol}\n", search, StringComparison.Ordinal);
		Assert.Contains("in (no declaration)\n", search, StringComparison.Ordinal);

		var first = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = fileName, ["symbol"] = firstSymbol })));
		Assert.Contains("member-marker-a", first, StringComparison.Ordinal);
		Assert.DoesNotContain("member-marker-b", first, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessNamesTheDeclarationEachSearchHitSitsInsideWithoutBeingAsked()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("symbol-project");
		workspace.WriteFile(
			"symbol-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n");
		workspace.WriteFile(
			"symbol-project/src/Helper.cs",
			"namespace P;\n\npublic sealed class Helper\n{\n\tpublic int Assist() => 2;\n}\n");
		workspace.WriteFile("symbol-project/README.md", "# Notes\n\nmarker line\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// No flag is passed: naming is what the tool does.
		var code = AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "=> [0-9]", ["context_lines"] = 0 }));

		// The declaration heads its hits inside the file's own block, the way the path does, and
		// location is spelled exactly once: no row anywhere repeats the path beside a line number.
		Assert.Contains("src/App.cs\nin P.App.Run\n5:", Normalize(code), StringComparison.Ordinal);
		Assert.Contains("src/Helper.cs\nin P.Helper.Assist\n5:", Normalize(code), StringComparison.Ordinal);
		Assert.DoesNotContain("src/App.cs:5:", code, StringComparison.Ordinal);
		Assert.DoesNotContain("src/Helper.cs:5:", code, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=2 · files-without-declarations=0.", code, StringComparison.Ordinal);

		// A declaration name is project text, so it stays inside the untrusted block with the match
		// lines, and only the counts are trusted.
		var untrustedEnd = code.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(untrustedEnd > 0);
		Assert.True(
			code.IndexOf("in P.App.Run", StringComparison.Ordinal) < untrustedEnd,
			"The declaration name must not be reported outside the untrusted block.");
		Assert.True(code.IndexOf("[Symbols]", StringComparison.Ordinal) > untrustedEnd);
	}

	[Fact]
	public async Task RealProcessDoesNotAttributeALateMethodHitToItsLargeOwnerType()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("large-owner-project");
		var source = new StringBuilder("public sealed class Logger\n{\n");
		for (var line = 0; line < 1_430; line++)
			source.Append("    // padding\n");
		source.Append("    public void Write()\n    {\n        var text = \"late-method-marker\";\n    }\n}\n");
		workspace.WriteFile("large-owner-project/Logger.cs", source.ToString());
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "late-method-marker", ["context_lines"] = 0 })));

		Assert.Contains("in Logger.Write\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin Logger\n", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessUsesOneTransformedSnapshotForSearchAndNamedReads()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("transformed-navigation-project");
		const string privateKey =
			"-----BEGIN " + "PRIVATE KEY-----\n" +
			"MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDAC4AWkdwKYSd8\n" +
			"Ks14IReLcYgADhoXk56ZzXI=\n" +
			"-----END " + "PRIVATE KEY-----";
		var source =
			"sealed class Multi\n{\n    private const string Key = \"\"\"\n" + privateKey +
			"\n\"\"\";\n\n    string Locate()\n    {\n        return \"coordinate-marker\";\n    }\n}\n";
		workspace.WriteFile("transformed-navigation-project/Multi.cs", source);
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var searched = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "coordinate-marker", ["context_lines"] = 0 })));
		Assert.Contains("in Multi.Locate\n", searched, StringComparison.Ordinal);
		Assert.Contains("9:        return \"coordinate-marker\";", searched, StringComparison.Ordinal);
		Assert.Contains("Multi.cs Multi.Locate 7", searched, StringComparison.Ordinal);

		var named = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "Multi.cs", ["symbol"] = "Multi.Locate" })));
		Assert.Contains("File: Multi.cs\nLines: 7-10 of 12\n", named, StringComparison.Ordinal);
		Assert.Contains("coordinate-marker", named, StringComparison.Ordinal);
		Assert.DoesNotContain("PRIVATE KEY", named, StringComparison.Ordinal);

		workspace.WriteFile(
			"transformed-navigation-project/Multi.cs",
			"sealed class Multi\n{\n\n\n    string Locate()\n    {\n        return \"updated-coordinate-marker\";\n    }\n}\n");
		var updated = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "Multi.cs", ["symbol"] = "Multi.Locate" })));
		Assert.Contains("File: Multi.cs\nLines: 5-8 of 10\n", updated, StringComparison.Ordinal);
		Assert.Contains("updated-coordinate-marker", updated, StringComparison.Ordinal);
		Assert.DoesNotContain("PRIVATE KEY", updated, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessWritesOneHeaderForARunOfHitsInTheSameDeclaration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("run-project");
		workspace.WriteFile(
			"run-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int One()\n\t{\n\t\tvar first = 1;\n\t\treturn 2;\n\t}\n}\n\npublic sealed class Other\n{\n\tpublic int Three() => 3;\n}\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "var first|return 2|=> 3",
				["context_lines"] = 0
			})));

		// Two hits share a declaration and are headed once; the third changes declaration and is
		// headed again. That collapsing is the whole saving over a row per hit.
		Assert.Equal(1, CountOccurrences(text, "in P.App.One\n"));
		Assert.Equal(1, CountOccurrences(text, "in P.Other.Three\n"));
		Assert.Contains("[Symbols] annotated=3 · files-without-declarations=0.", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessCountsAHitInAFileThatDeclaresNothingInsteadOfNamingIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("prose-project");
		workspace.WriteFile("prose-project/README.md", "# Notes\n\nmarker line\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var prose = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "marker", ["context_lines"] = 0 })));

		// Matches are grouped under their path, so the file heads its own block and the
		// matched line carries only its number. Nothing declares anything here, so no header
		// is written and the coverage notice says so rather than going silent.
		Assert.Contains("README.md\n3:marker line", prose, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin ", prose, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=0 · files-without-declarations=1.", prose, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessSpendsNoResponseCharactersOnNamingWhenTheSearchCapAlreadyFired()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("wide-symbol-project");
		for (var file = 0; file < 40; file++)
		{
			var lines = new StringBuilder();
			lines.Append("namespace P;\n\npublic sealed class Wide");
			lines.Append(file.ToString("D2", CultureInfo.InvariantCulture));
			lines.Append("\n{\n");
			for (var member = 0; member < 20; member++)
			{
				// Long enough that twenty of them in forty files cannot fit the search cap.
				lines.Append("\tpublic string Needle");
				lines.Append(member.ToString("D2", CultureInfo.InvariantCulture));
				lines.Append("() => \"");
				lines.Append(new string('p', 280));
				lines.Append("\";\n");
			}

			lines.Append("}\n");
			workspace.WriteFile(
				$"wide-symbol-project/src/Wide{file:D2}.cs",
				lines.ToString());
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var wide = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "public string Needle",
				["context_lines"] = 3,
				["max_results"] = 200
			})));

		// The cap fired, so the remaining characters went to matches and no header was written at
		// all: turning naming on cannot push a capped response past the size it already had.
		Assert.Contains("[Search truncated]", wide, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin P.Wide", wide, StringComparison.Ordinal);
		// The selector list still ships on a cut response: that is exactly when a caller would
		// otherwise open a whole file to find a declaration it is already holding.
		Assert.Contains("Declarations found (path, symbol, line):", wide, StringComparison.Ordinal);
		Assert.Contains("[Symbols] annotated=0", wide, StringComparison.Ordinal);
		Assert.True(wide.Length <= 18_000, $"Capped search returned {wide.Length} characters.");
	}

	[Fact]
	public async Task RealProcessNeverEndsASearchBlockWithADeclarationHeader()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("tail-project");
		for (var file = 0; file < 30; file++)
		{
			var lines = new StringBuilder();
			lines.Append("namespace P;\n\npublic sealed class Tail");
			lines.Append(file.ToString("D2", CultureInfo.InvariantCulture));
			lines.Append("\n{\n");
			for (var member = 0; member < 12; member++)
			{
				lines.Append("\tpublic string Marker");
				lines.Append(member.ToString("D2", CultureInfo.InvariantCulture));
				lines.Append("() => \"");
				lines.Append(new string('q', 240));
				lines.Append("\";\n");
			}

			lines.Append("}\n");
			workspace.WriteFile($"tail-project/src/Tail{file:D2}.cs", lines.ToString());
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		// One search that fits and one the cap cuts: a header is never the last thing either one
		// leaves inside the untrusted block, so no caller is shown a name with nothing under it.
		foreach (var arguments in new[]
		{
			new Dictionary<string, object?> { ["pattern"] = "Marker00", ["context_lines"] = 0 },
			new Dictionary<string, object?>
			{
				["pattern"] = "public string Marker",
				["context_lines"] = 2,
				["max_results"] = 200
			}
		})
		{
			var body = SpotlightBody(Normalize(AllProcessText(await CallAsync(server, "search_project", arguments))));
			var lastLine = body
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.LastOrDefault(static line => line.Trim().Length > 0);
			Assert.NotNull(lastLine);
			Assert.False(
				lastLine!.StartsWith("in ", StringComparison.Ordinal),
				$"A search block ended with a declaration header: {lastLine}");
		}
	}

	[Fact]
	public async Task RealProcessStopsAHeaderFromClaimingAHitThatBelongsToNoDeclaration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("outside-project");
		workspace.WriteFile(
			"outside-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int Run() => 1;\n}\n\n// tail marker note\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "marker|=> 1",
				["context_lines"] = 0
			})));

		// The comment on the last line is inside no declaration. Without a closing header it would
		// render under the one above it and read as part of that type.
		var named = text.IndexOf("in P.App.Run\n", StringComparison.Ordinal);
		var closed = text.IndexOf("in (no declaration)\n", StringComparison.Ordinal);
		var outside = text.IndexOf("8:// tail marker note", StringComparison.Ordinal);
		Assert.True(named >= 0, text);
		Assert.True(closed > named, $"The run was never closed before the unnamed hit: {text}");
		Assert.True(outside > closed, $"The closing header must precede the hit it frees: {text}");
	}

	[Fact]
	public async Task RealProcessNamesNothingAndSaysSoWhenTheHeadersWouldNotFit()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("crowded-project");
		for (var file = 0; file < 60; file++)
		{
			// One match per file, and a type name long enough that sixty headers cannot fit beside
			// the matches even though the matches themselves are far under the cap.
			var name = "W" + new string('x', 200) + file.ToString("D2", CultureInfo.InvariantCulture);
			workspace.WriteFile(
				$"crowded-project/src/F{file:D2}.cs",
				$"namespace P;\n\npublic sealed class {name}\n{{\n\tpublic string Needle() => \"{new string('q', 120)}\";\n}}\n");
		}

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "Needle",
				["context_lines"] = 0,
				["max_results"] = 200
			})));

		// Placement is all or nothing, so nothing is named. The response says that rather than
		// going silent about naming it computed and then refused to spend.
		Assert.DoesNotContain("[Search truncated]", text, StringComparison.Ordinal);
		Assert.DoesNotContain("\nin P.W", text, StringComparison.Ordinal);
		Assert.Contains(
			"[Symbols] annotated=0 · files-without-declarations=0; the names did not fit the " +
			"16000-character search cap, so none were written.",
			text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessListsEachDeclarationOnceAsSomethingTheCallerCanPassBack()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("selector-project");
		workspace.WriteFile(
			"selector-project/src/App.cs",
			"namespace P;\n\npublic sealed class App\n{\n\tpublic int One() => 1;\n\n\tpublic int Two() => 1;\n}\n");
		workspace.WriteFile("selector-project/README.md", "# Notes\n\nmentions 1 here\n");
		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var text = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?> { ["pattern"] = "1", ["context_lines"] = 0 })));

		// Two hits share one declaration, so the list carries it once: this is what a caller reads
		// back, and a declaration touched twice is still one thing to open.
		Assert.Contains("Declarations found (path, symbol, line):", text, StringComparison.Ordinal);
		Assert.Equal(1, CountOccurrences(text, "src/App.cs P.App.One 5"));
		Assert.Equal(1, CountOccurrences(text, "src/App.cs P.App.Two 7"));

		// The sentence that turns the list into a call is a constant and sits outside the block,
		// while the paths and names inside it are project text and stay in.
		var untrustedEnd = text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		Assert.True(text.IndexOf("src/App.cs P.App.One 5", StringComparison.Ordinal) < untrustedEnd);
		Assert.True(
			text.IndexOf("[Read declarations]", StringComparison.Ordinal) > untrustedEnd,
			"The instruction must be trusted text, outside the untrusted block.");
		Assert.Contains(
			"[Read declarations] To read any declaration listed above in full, call get_file with " +
			"its path and symbol; for several of them, one get_file requests call.",
			text,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RealProcessPrintedFollowUpRoutesExecuteWithoutAdditionalArguments()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("route-project");
		workspace.WriteFile(
			"route-project/src/Alpha.cs",
			"sealed class Alpha\n{\n    string Read() => \"route-marker-alpha\";\n}\n");
		workspace.WriteFile(
			"route-project/src/Beta.cs",
			"sealed class Beta\n{\n    string Read() => \"route-marker-beta\";\n}\n");
		workspace.WriteFile(
			"route-project/Large.txt",
			string.Join('\n', Enumerable.Range(1, 3_200).Select(static line => $"stored-route-{line:D4}")));
		workspace.WriteFile("route-project/LongLine.txt", new string('x', 60_000) + "tail-route-marker\n");
		for (var index = 0; index < 3; index++)
			workspace.WriteFile($"route-project/Stored{index}.txt", $"stored-search-route-{index}\n");

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			workspace.CreateDirectory("data"));

		var searched = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "route-marker-(alpha|beta)",
				["context_lines"] = 0
			})));
		Assert.Contains("[Read declarations]", searched, StringComparison.Ordinal);
		var selectors = Regex.Matches(
			SpotlightBody(searched),
			@"^(?<path>src/[^ ]+\.cs) (?<symbol>[^ ]+) (?<line>[0-9]+)$",
			RegexOptions.Multiline,
			TimeSpan.FromSeconds(2));
		Assert.Equal(2, selectors.Count);

		var scalarArguments = new Dictionary<string, object?>
		{
			["path"] = selectors[0].Groups["path"].Value,
			["symbol"] = selectors[0].Groups["symbol"].Value
		};
		var scalar = Normalize(AllProcessText(await CallAsync(server, "get_file", scalarArguments)));
		Assert.Contains("route-marker-alpha", scalar, StringComparison.Ordinal);
		Assert.DoesNotContain("route-marker-beta", scalar, StringComparison.Ordinal);

		var requests = selectors.Select(selector => (object)new
		{
			path = selector.Groups["path"].Value,
			symbol = selector.Groups["symbol"].Value
		}).ToArray();
		var batch = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["requests"] = requests })));
		Assert.Contains("route-marker-alpha", batch, StringComparison.Ordinal);
		Assert.Contains("route-marker-beta", batch, StringComparison.Ordinal);

		var packed = Normalize(AllProcessText(await CallAsync(
			server,
			"pack_context",
			new Dictionary<string, object?>
			{
				["paths"] = new[] { "Large.txt" },
				["view"] = "content",
				["format"] = "text"
			})));
		var packId = Regex.Match(packed, @"Pack stored as '(?<id>[^']+)'", RegexOptions.None,
			TimeSpan.FromSeconds(2)).Groups["id"].Value;
		Assert.False(string.IsNullOrWhiteSpace(packId), packed);
		var firstPage = Normalize(AllProcessText(await CallAsync(
			server,
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = packId })));
		var nextLine = Regex.Match(firstPage, @"continue with start_line=(?<line>[0-9]+)", RegexOptions.None,
			TimeSpan.FromSeconds(2)).Groups["line"].Value;
		Assert.False(string.IsNullOrWhiteSpace(nextLine), firstPage);
		var nextPage = Normalize(AllProcessText(await CallAsync(
			server,
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = packId, ["start_line"] = nextLine })));
		Assert.Contains($"stored-route-{int.Parse(nextLine, CultureInfo.InvariantCulture) - 2:D4}", nextPage,
			StringComparison.Ordinal);

		var storedSearch = Normalize(AllProcessText(await CallAsync(
			server,
			"search_project",
			new Dictionary<string, object?>
			{
				["pattern"] = "stored-search-route",
				["context_lines"] = 0,
				["max_results"] = 1
			})));
		var searchPackId = Regex.Match(storedSearch, @"\[Search stored\] pack_id=(?<id>[0-9a-f]+)",
			RegexOptions.None, TimeSpan.FromSeconds(2)).Groups["id"].Value;
		Assert.False(string.IsNullOrWhiteSpace(searchPackId), storedSearch);
		var storedSearchPage = Normalize(AllProcessText(await CallAsync(
			server,
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = searchPackId })));
		Assert.Contains("stored-search-route", storedSearchPage, StringComparison.Ordinal);

		var longPage = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?> { ["path"] = "LongLine.txt" })));
		var nextColumn = Regex.Match(longPage, @"continue with start_line=1 start_column=(?<column>[0-9]+)",
			RegexOptions.None, TimeSpan.FromSeconds(2)).Groups["column"].Value;
		Assert.False(string.IsNullOrWhiteSpace(nextColumn), longPage);
		var longContinuation = Normalize(AllProcessText(await CallAsync(
			server,
			"get_file",
			new Dictionary<string, object?>
			{
				["path"] = "LongLine.txt",
				["start_line"] = 1,
				["start_column"] = nextColumn
			})));
		Assert.Contains("tail-route-marker", longContinuation, StringComparison.Ordinal);
	}

	private static string Normalize(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static int CountOccurrences(string text, string value)
	{
		var count = 0;
		for (var index = text.IndexOf(value, StringComparison.Ordinal);
			 index >= 0;
			 index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
		{
			count++;
		}

		return count;
	}

	private static string SpotlightBody(string text)
	{
		var open = text.IndexOf("<untrusted-data-", StringComparison.Ordinal);
		var openEnd = open < 0 ? -1 : text.IndexOf('\n', open);
		var close = text.LastIndexOf("</untrusted-data-", StringComparison.Ordinal);
		return openEnd < 0 || close < openEnd ? text : text[(openEnd + 1)..close];
	}
}
