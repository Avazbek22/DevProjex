using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SmartSecretsConnectionValueGrammarTests
{
	// Pair and quoting rules follow the ADO.NET, JDBC URL, libpq, and HTTP field grammars.
	// Sources: https://learn.microsoft.com/dotnet/framework/data/adonet/connection-strings
	//          https://docs.oracle.com/javase/8/docs/api/java/sql/DriverManager.html
	//          https://www.postgresql.org/docs/current/libpq-connect.html#LIBPQ-PARAMKEYWORDS
	//          https://www.rfc-editor.org/rfc/rfc9110#section-5
	public static TheoryData<string, string> AcceptedRegions => new()
	{
		{ "Server=db;User ID=sa;Password=Admin123!;Database=app", "Admin123!" },
		{ "Host=db;Username=admin;Password=Admin123!;Database=app", "Admin123!" },
		{ "jdbc:postgresql://db/app?user=admin&password=short&ssl=true", "short" },
		{ "postgresql://db/app?username=admin&password=query-password&ssl=true", "query-password" },
		{ "Server=db;User ID=sa;Password=ab&cd;Database=app", "ab&cd" },
		{ "Server=db;User ID=sa;Password=\"ab\"\"cd\";Database=app", "ab\"\"cd" },
		{ "Server=db;User ID=sa;Password='ab;cd';Database=app", "ab;cd" },
		{ "Server=db;User ID=sa;Password=ab=cd;Database=app", "ab=cd" },
		{ "{\"Connection\":\"Server=db;Password=ab&cd;Database=app\"}", "ab&cd" },
		{ "const string value = \"Server=db;Password=ab&cd;Database=app\";", "ab&cd" },
		{ "<add value=\"Server=db;Password=ab&amp;cd;Database=app\" />", "ab&amp;cd" },
		{ "host=localhost port=5432 dbname=app user=postgres password=Admin123!", "Admin123!" },
		{ "Cookie: Server=db; Password=cookie-password", "cookie-password" },
		{ "  cOoKiE: Server=db; Password=header-password", "header-password" },
		{ "Server=db;User ID=sa;Password='päss;🔐'", "päss;🔐" },
		{ "safe\r\nServer=db;User ID=sa;Password=line-password;Database=app\r\nsafe", "line-password" }
	};

	[Theory]
	[MemberData(nameof(AcceptedRegions))]
	public void CompletePairRegionsExposeOnlyThePasswordValue(string content, string expected)
	{
		AssertExactCoverage(content, Match(content), expected);
	}

	public static TheoryData<string> RejectedRegions => new()
	{
		{ "return BasicAuth(username=username, password=password)" },
		{ "return BasicAuth(username=auth[0], password=auth[1])" },
		{ "password=self._password" },
		{ "password=kwargs.get(\"password\")" },
		{ "password=os.environ[\"PW\"]" },
		{ "password=config.password" },
		{ "Connect(host = host, password = password);" },
		{ "connect({ host = host, password = password });" },
		{ "Cookie: str = \"value\"" },
		{ "Cookie: name=value" },
		{ "Foo: Server=db; Password=x" },
		{ "Server=db" },
		{ "Password=x" },
		{ "jdbc:postgresql://db/app?user=admin&ssl=true" },
		{ "host=localhost  password=Admin123!" },
		{ "Server=db;Password=;Database=app" }
	};

	[Theory]
	[MemberData(nameof(RejectedRegions))]
	public void TextOutsideACompletePairRegionRemainsUnchanged(string content)
	{
		Assert.Empty(Match(content));
	}

	[Theory]
	[InlineData("Cookie")]
	[InlineData("Set-Cookie")]
	[InlineData("Authorization")]
	[InlineData("Proxy-Authorization")]
	public void ExistingProtectedHeaderNamesMayOpenACompletePairRegion(string header)
	{
		var content = $"{header}: Server=db; Password=header-password";

		AssertExactCoverage(content, Match(content), "header-password");
	}

	[Theory]
	[InlineData("return BasicAuth(username=username, password=password)")]
	[InlineData("return BasicAuth(username=auth[0], password=auth[1])")]
	public void PythonCallTextRemainsByteForByteUnchanged(string content)
	{
		var matches = SmartSecretsDetectorTests.Detector.Detect(
			"httpx/_client.py",
			content,
			TestContext.Current.CancellationToken);

		Assert.Equal(content, Apply(content, matches));
	}

	[Fact(Timeout = 5_000)]
	public void LongCompleteRegionAtTheInspectionBoundaryKeepsExactCoverage()
	{
		var password = string.Create(2 * 1024 * 1024, 0, static (buffer, _) =>
		{
			for (var index = 0; index < buffer.Length; index++)
				buffer[index] = (index & 1) == 0 ? 'a' : 'B';
		});
		var content = $"Server=db;User ID=sa;Password={password};Database=app";

		AssertExactCoverage(content, Match(content), password);
	}

	private static DetectedSecret[] Match(string content) =>
		StructuredSecretDetector.Detect(
			"settings.txt",
			content,
			SmartSecretStack.None,
			TestContext.Current.CancellationToken)
		.Where(static match => match.RuleId == "connection-password")
		.OrderBy(static match => match.Start)
		.ToArray();

	private static string Apply(string content, IReadOnlyList<DetectedSecret> matches)
	{
		var result = content;
		foreach (var match in matches.OrderByDescending(static match => match.Start))
		{
			result = string.Concat(
				result.AsSpan(0, match.Start),
				$"DEVPROJEX_REDACTED[{match.RuleId}#1]",
				result.AsSpan(match.Start + match.Length));
		}
		return result;
	}

	private static void AssertExactCoverage(
		string content,
		IReadOnlyList<DetectedSecret> matches,
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
		foreach (var match in matches)
		{
			Assert.Equal(match.Value, content.Substring(match.Start, match.Length));
			for (var index = match.Start; index < match.Start + match.Length; index++)
				actualCoverage[index] = true;
		}

		Assert.Equal(expectedCoverage, actualCoverage);
	}
}
