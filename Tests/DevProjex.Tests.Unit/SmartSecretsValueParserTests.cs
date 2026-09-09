using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SmartSecretsValueParserTests
{
	// dotenv values follow the quoting, escaping, comment, and multiline rules documented by dotenvx.
	// Source: https://dotenvx.com/docs/env-file
	[Theory]
	[InlineData("DB_PASSWORD=\"ab #cd\" # comment", "ab #cd")]
	[InlineData("DB_PASSWORD='päss,#}]'", "päss,#}]")]
	[InlineData("DB_PASSWORD=ab,cd}ef]gh", "ab,cd}ef]gh")]
	[InlineData("DB_PASSWORD=ab#comment", "ab")]
	[InlineData("DB_PASSWORD=ab # comment", "ab")]
	[InlineData("DB_PASSWORD=\"ab\\\"cd\"", "ab\\\"cd")]
	public void DotEnv_UsesFormatSpecificQuotedAndUnquotedBoundaries(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, ".env", "environment-secret"), expected);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void DotEnv_QuotedLogicalValueMayCrossPhysicalLines(string newline)
	{
		var expected = $"ab12{newline}cd34";
		var content = $"DB_PASSWORD='{expected}'{newline}SAFE=value";

		AssertExactCoverage(content, Find(content, ".env", "environment-secret"), expected);
	}

	[Fact]
	public void DotEnv_NonSensitiveAssignmentAndQuotedCommentStayVisible()
	{
		const string content = "SAFE=ab12\n# DB_PASSWORD=comment-only";

		Assert.Empty(Find(content, ".env", "environment-secret"));
	}

	// ADO.NET uses semicolon-delimited pairs with doubled quote escaping; JDBC URL properties use '&'.
	// Sources: https://learn.microsoft.com/dotnet/framework/data/adonet/connection-strings
	//          https://docs.oracle.com/javase/8/docs/api/java/sql/DriverManager.html
	[Theory]
	[InlineData("Server=db;Password=ab&cd;Database=app", "ab&cd")]
	[InlineData("Server=db;Password=\"ab\"\"cd\";Database=app", "ab\"\"cd")]
	[InlineData("Server=db;Password='ab;cd';Database=app", "ab;cd")]
	[InlineData("Server=db;Password=ab=cd;Database=app", "ab=cd")]
	public void AdoNetConnectionString_UsesSemicolonPairGrammar(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, "settings.txt", "connection-password"), expected);
	}

	[Fact]
	public void JdbcConnectionString_UsesQueryParameterGrammar()
	{
		const string content = "jdbc:postgresql://db/app?user=admin&password=ab12&ssl=true";

		AssertExactCoverage(content, Find(content, "settings.txt", "connection-password"), "ab12");
	}

	[Fact]
	public void UriUserInfo_RemainsDelimitedByTheAuthorityGrammar()
	{
		const string content = "postgres://user:ab12@db.internal/app?safe=true";

		AssertExactCoverage(content, Find(content, "settings.txt", "credential-uri-password"), "ab12");
	}

	[Fact]
	public void ConnectionString_WithoutCredentialKeyDoesNotCreateAStructuredFinding()
	{
		const string content = "Server=db;User ID=admin;Database=app";

		Assert.Empty(Find(content, "settings.txt", "connection-password"));
	}

	// RFC 8259 strings use odd/even reverse-solidus parity and permit whitespace between tokens.
	// Source: https://www.rfc-editor.org/rfc/rfc8259
	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void Json_ValueMayFollowItsPropertyOnTheNextLine(string newline)
	{
		var content = $"{{\"Password\":{newline}  \"ab12\",{newline}\"Port\":8080}}";

		AssertExactCoverage(content, Find(content, "appsettings.json", "config-secret"), "ab12");
	}

	[Fact]
	public void Json_EvenBackslashesCloseTheValueWithoutCapturingTheNextProperty()
	{
		const string content = "{\"Password\":\"ab\\\\\",\"Host\":\"db\"}";

		AssertExactCoverage(content, Find(content, "appsettings.json", "config-secret"), "ab\\\\");
	}

	[Fact]
	public void Json_SensitiveArrayMasksScalarDescendantsOnly()
	{
		const string content = "{\"Passwords\":[\"ab12\",\"cd34\"],\"Port\":8080}";

		AssertExactCoverage(content, Find(content, "appsettings.json", "config-secret"), "ab12", "cd34");
	}

	[Fact]
	public void Json_DecodesEscapedPropertyNameBeforeSensitivityMatching()
	{
		const string content = "{\"Pass\\u0077ord\":\"päss🔐\",\"Port\":8080}";

		AssertExactCoverage(content, Find(content, "appsettings.json", "config-secret"), "päss🔐");
	}

	[Fact]
	public void Json_NonSensitiveArrayAndFollowingPropertyStayVisible()
	{
		const string content = "{\"Hosts\":[\"db-one\",\"db-two\"],\"Port\":8080}";

		Assert.Empty(Find(content, "appsettings.json", "config-secret"));
	}

	// YAML 1.2 comments require separation in plain scalars, single quotes escape by doubling,
	// and block scalar content is selected by indentation rather than the physical line.
	// Source: https://yaml.org/spec/1.2.2/
	[Theory]
	[InlineData("password: ab#cd\nsafe: keep", "ab#cd")]
	[InlineData("password: ab # comment\nsafe: keep", "ab")]
	[InlineData("password: 'ab''cd'\nsafe: keep", "ab''cd")]
	public void Yaml_UsesScalarSpecificCommentsAndQuotes(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, "application.yml", "config-secret"), expected);
	}

	[Theory]
	[InlineData("|", "\n")]
	[InlineData("|-", "\r\n")]
	[InlineData("|+", "\r")]
	[InlineData(">", "\n")]
	[InlineData("|2", "\r\n")]
	public void Yaml_BlockScalarMasksContentButNotIndicatorOrAdjacentKey(string indicator, string newline)
	{
		var content = $"password: {indicator}{newline}  ab12{newline}  cd34{newline}port: 8080";

		AssertExactCoverage(content, Find(content, "application.yml", "config-secret"), "ab12", "cd34");
	}

	[Fact]
	public void Yaml_NonSensitiveBlockScalarDoesNotCreateAStructuredFinding()
	{
		const string content = "description: |\r\n  safe text\r\nport: 8080";

		Assert.Empty(Find(content, "application.yml", "config-secret"));
	}

	[Theory]
	[InlineData("${DB_PASS}", false)]
	[InlineData("${DB_PASS:-ab12}", true)]
	[InlineData("${DB_PASS}suffix", true)]
	[InlineData("${DB_PASS?required-secret}", true)]
	[InlineData("\\${DB_PASS}", true)]
	[InlineData("${OUTER:-${INNER:-ab12}}", true)]
	public void DotEnv_OnlyPureReferencesAreExcluded(string value, bool expectedFinding)
	{
		var content = $"DB_PASSWORD={value}";
		var findings = Find(content, ".env", "environment-secret");

		if (expectedFinding)
			AssertExactCoverage(content, findings, value);
		else
			Assert.Empty(findings);
	}

	// XML character data and CDATA inherit their containing element's sensitivity; no DTD is resolved.
	// Source: https://www.w3.org/TR/xml/
	[Theory]
	[InlineData("<Password><![CDATA[ab12]]></Password>", "ab12")]
	[InlineData("<cfg:Password xmlns:cfg=\"urn:test\">ab12</cfg:Password>", "ab12")]
	[InlineData("<add key=\"Pass&#x77;ord\" value=\"ab12\" />", "ab12")]
	public void Xml_SensitiveContextFlowsToTextCdataAndDecodedKeyAttributes(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, "web.config", "config-secret"), expected);
	}

	[Fact]
	public void Xml_MixedTextAndCdataMasksOnlyTextNodes()
	{
		const string content = "<Password>left<![CDATA[mid]]>right</Password><Port>8080</Port>";

		AssertExactCoverage(content, Find(content, "web.config", "config-secret"), "left", "mid", "right");
	}

	[Fact]
	public void Xml_NonSensitiveCdataAndAdjacentElementStayVisible()
	{
		const string content = "<Description><![CDATA[safe text]]></Description>\r\n<Port>8080</Port>";

		Assert.Empty(Find(content, "web.config", "config-secret"));
	}

	// Python lexical prefixes and triple/adjacent strings follow the language lexical reference.
	// Source: https://docs.python.org/3/reference/lexical_analysis.html#string-and-bytes-literals
	[Theory]
	[InlineData("SECRET_KEY = \"\"\"ab12\"\"\"", "ab12")]
	[InlineData("SECRET_KEY = r\"ab12\"", "ab12")]
	[InlineData("SECRET_KEY = u'päss'", "päss")]
	[InlineData("SECRET_KEY = b\"ab12\"", "ab12")]
	[InlineData("SECRET_KEY = f\"ab{suffix}\"", "ab{suffix}")]
	public void Python_RecognizesPrefixesAndTripleQuotedLiterals(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, "settings.py", "config-secret"), expected);
	}

	[Fact]
	public void Python_AdjacentLiteralsAreSeparateExactFragments()
	{
		const string content = "SECRET_KEY = \"ab12\" 'cd34'\nPORT = 8080";

		AssertExactCoverage(content, Find(content, "settings.py", "config-secret"), "ab12", "cd34");
	}

	[Fact]
	public void Python_NonSensitiveLiteralDoesNotCreateAStructuredFinding()
	{
		const string content = "DESCRIPTION = r\"safe text\"\r\nPORT = 8080";

		Assert.Empty(Find(content, "settings.py", "config-secret"));
	}

	// Dockerfile instructions use logical continuation lines and the active escape directive.
	// Source: https://docs.docker.com/reference/dockerfile/#escape
	[Theory]
	[InlineData("ENV NORMAL=x \\\n    DB_PASSWORD=ab12", "ab12")]
	[InlineData("ENV NORMAL=x \\\r\n    DB_PASSWORD=ab12", "ab12")]
	[InlineData("# escape=`\r\nENV NORMAL=x `\r\n    DB_PASSWORD=ab12", "ab12")]
	[InlineData("ENV DB_PASSWORD=ab\\ cd", "ab\\ cd")]
	[InlineData("ARG DB_PASSWORD=ab12", "ab12")]
	public void Dockerfile_UsesLogicalLinesAndEscapedValueCharacters(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, "Dockerfile", "container-secret"), expected);
	}

	[Fact]
	public void Dockerfile_NonSensitiveLogicalAssignmentStaysVisible()
	{
		const string content = "ENV NAME=safe \\\r\n    PORT=8080";

		Assert.Empty(Find(content, "Dockerfile", "container-secret"));
	}

	// netrc is a whitespace token stream; a newline does not terminate a pending password field.
	// Source: https://www.gnu.org/software/inetutils/manual/html_node/The-_002enetrc-file.html
	[Theory]
	[InlineData("machine host login alice password\nab12", "ab12")]
	[InlineData("machine host login alice password\r\n\"ab 12\"", "ab 12")]
	[InlineData("machine host login alice password 'päss word'", "päss word")]
	public void Netrc_ReadsPasswordFromTheLogicalTokenStream(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, ".netrc", "netrc-password"), expected);
	}

	[Fact]
	public void Netrc_AccountTokenIsNotAPasswordField()
	{
		const string content = "machine host login alice account ab12\r\nmacdef init";

		Assert.Empty(Find(content, ".netrc", "netrc-password"));
	}

	// npm requires authentication keys to be scoped to a registry URI fragment.
	// Source: https://docs.npmjs.com/cli/v11/configuring-npm/npmrc#auth-related-configuration
	[Theory]
	[InlineData("//registry.npmjs.org/:_auth=YTpi", "YTpi")]
	[InlineData("//registry.example.com/team/:_authToken = npm-short", "npm-short")]
	[InlineData("//registry.example.com/:_password=päss", "päss")]
	[InlineData("//registry.example.com/:_auth=\"ab#cd\"", "ab#cd")]
	[InlineData("_auth=YTpi", "YTpi")]
	public void Npmrc_RecognizesScopedAndUnscopedCredentialKeys(string content, string expected)
	{
		AssertExactCoverage(content, Find(content, ".npmrc", "environment-secret"), expected);
	}

	[Theory]
	[InlineData("//registry.example.com/:email=owner@example.com")]
	[InlineData("//registry.example.com/:certfile=/safe/client.pem")]
	[InlineData("//registry.example.com/:_auth=${NPM_AUTH}")]
	public void Npmrc_DoesNotInventSecretsForMetadataOrPureReferences(string content)
	{
		Assert.Empty(Find(content, ".npmrc", "environment-secret"));
	}

	[Fact]
	public void StructuredLexers_StopBeforeRetainingMoreThanTheFindingLimit()
	{
		var content = string.Join(
			'\n',
			Enumerable.Range(0, SecretInspectionLimits.MaximumFindingsPerFile + 1)
				.Select(static index => $"PASSWORD=value-{index}"));

		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			StructuredSecretDetector.Detect(
				".env",
				content,
				SmartSecretStack.None,
				TestContext.Current.CancellationToken));
		Assert.Equal(nameof(SecretInspectionLimits.MaximumFindingsPerFile), exception.LimitName);
	}

	private static DetectedSecret[] Find(string content, string path, string ruleId) =>
		StructuredSecretDetector.Detect(
			path,
			content,
			SmartSecretStack.None,
			TestContext.Current.CancellationToken)
		.Where(finding => finding.RuleId == ruleId)
		.OrderBy(static finding => finding.Start)
		.ToArray();

	private static void AssertExactCoverage(
		string content,
		IReadOnlyList<DetectedSecret> findings,
		params string[] expectedValues)
	{
		var expectedCoverage = new bool[content.Length];
		var searchStart = 0;
		foreach (var expectedValue in expectedValues)
		{
			var start = content.IndexOf(expectedValue, searchStart, StringComparison.Ordinal);
			Assert.True(start >= 0, $"Expected value '{expectedValue}' was not present after offset {searchStart}.");
			for (var index = start; index < start + expectedValue.Length; index++)
				expectedCoverage[index] = true;
			searchStart = start + expectedValue.Length;
		}

		var actualCoverage = new bool[content.Length];
		foreach (var finding in findings)
		{
			Assert.Equal(finding.Value, content.Substring(finding.Start, finding.Length));
			for (var index = finding.Start; index < finding.Start + finding.Length; index++)
				actualCoverage[index] = true;
		}

		Assert.Equal(expectedCoverage, actualCoverage);
	}
}
