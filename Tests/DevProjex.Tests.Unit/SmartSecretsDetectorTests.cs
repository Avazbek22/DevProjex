using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SmartSecretsDetectorTests
{
	internal static readonly SmartSecretsDetector Detector = CreateDetector();

	[Fact]
	public void RulesIdentity_UsesVersionFourForStructuredCacheInvalidation()
	{
		Assert.EndsWith(":smart-secrets-v4", Detector.RulesIdentity, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("postgres:" + "//admin:pass@db.local/app")]
	[InlineData("mysql:" + "//admin:Admin123!@db.local/app")]
	[InlineData("mongodb+srv:" + "//service:change-me-now@cluster.internal/app")]
	[InlineData("redis:" + "//default:redis-pass@cache.local:6379")]
	[InlineData("amqp:" + "//worker:rabbit-pass@queue.local/vhost")]
	[InlineData("https:" + "//deploy:http-pass@downloads.internal/artifact")]
	public void Detect_CredentialUri_RedactsOnlyPassword(string uri)
	{
		var finding = Assert.Single(
			Detector.Detect("config/settings.txt", uri, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "credential-uri-password");

		var redacted = Replace(uri, finding);
		Assert.Contains(":DEVPROJEX_REDACTED[credential-uri-password#1]@", redacted, StringComparison.Ordinal);
		Assert.Contains(uri[..uri.IndexOf("://", StringComparison.Ordinal)], redacted, StringComparison.Ordinal);
		Assert.Contains("@", redacted, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("example.com")]
	[InlineData("EXAMPLE.NET:443")]
	[InlineData("api.example.org")]
	[InlineData("db.test:5432")]
	[InlineData("service.example")]
	[InlineData("cache.invalid")]
	public void Detect_CredentialUriOnRfc2606DocumentationHost_DoesNotRedact(string host)
	{
		var findings = Detector.Detect(
			"settings.txt",
			$"postgres://admin:live-password-42@{host}/app",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "credential-uri-password");
	}

	[Theory]
	[InlineData("localhost")]
	[InlineData("db.localhost:5432")]
	[InlineData("example.com.evil.io")]
	[InlineData("prod.internal:443")]
	[InlineData("test")]
	[InlineData("[::1]:5432")]
	public void Detect_CredentialUriOnOperationalHost_RedactsPassword(string host)
	{
		var finding = Assert.Single(
			Detector.Detect(
				"settings.txt",
				$"postgres://admin:live-password-42@{host}/app",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "credential-uri-password");

		Assert.Equal("live-password-42", finding.Value);
	}

	[Theory]
	[InlineData("example.com", true)]
	[InlineData("EXAMPLE.NET:443", true)]
	[InlineData("api.example.org:8443", true)]
	[InlineData("db.test", true)]
	[InlineData("service.EXAMPLE", true)]
	[InlineData("cache.invalid:6379", true)]
	[InlineData("example.com.:443", true)]
	[InlineData("test", false)]
	[InlineData("example", false)]
	[InlineData("invalid:6379", false)]
	[InlineData("localhost:5432", false)]
	[InlineData("db.localhost", false)]
	[InlineData("example.com.evil.io:443", false)]
	[InlineData("prod.internal", false)]
	[InlineData("[2001:db8::1]:443", false)]
	public void IsRfc2606DocumentationHost_NormalizesPortsAndIpv6Brackets(
		string host,
		bool expected)
	{
		Assert.Equal(expected, StructuredSecretDetector.IsRfc2606DocumentationHost(host));
	}

	[Theory]
	[InlineData("${DB_PASSWORD}")]
	[InlineData("$(DbPassword)")]
	[InlineData("%DB_PASSWORD%")]
	[InlineData("{{ secret }}")]
	[InlineData("<password>")]
	[InlineData("")]
	public void Detect_CredentialUriReferenceOrPlaceholder_DoesNotRedact(string value)
	{
		var findings = Detector.Detect(
			"settings.txt",
			$"postgres:" + $"//admin:{value}@db.local/app",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "credential-uri-password");
	}

	[Fact]
	public void Detect_CredentialUriValueContainingExample_IsStillRedacted()
	{
		var finding = Assert.Single(
			Detector.Detect(
				"settings.txt",
				"postgres:" + "//admin:live-example-password@db.local/app",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "credential-uri-password");

		Assert.Equal("live-example-password", finding.Value);
	}

	[Theory]
	[InlineData("requests.http")]
	[InlineData("REQUEST.REST")]
	public void Detect_HttpCookieHeader_RedactsEveryNonEmptyValueAndPreservesNames(string path)
	{
		const string content =
			"GET https://localhost/health\r\ncOoKiE : session = alpha-cookie ; theme=dark; encoded=abc==; empty= ; flag\r\n";

		var findings = Detector.Detect(path, content, TestContext.Current.CancellationToken)
			.Where(static finding => finding.RuleId == "http-cookie")
			.OrderBy(static finding => finding.Start)
			.ToArray();

		Assert.Equal(["alpha-cookie", "dark", "abc=="], findings.Select(static finding => finding.Value));
		Assert.All(findings, static finding => Assert.Equal(-325, finding.RuleOrder));
		foreach (var finding in findings)
			Assert.Equal(finding.Value, content.Substring(finding.Start, finding.Length));
		Assert.DoesNotContain(findings, static finding => finding.Value.Contains('=') && finding.Value != "abc==");
	}

	[Fact]
	public void Detect_HttpSetCookieHeader_RedactsOnlyTheInitialPairValue()
	{
		const string content =
			"Set-Cookie: session = abc== ; Path=/private; HttpOnly; Max-Age=3600; SameSite=Strict; Partitioned\n";

		var finding = Assert.Single(
			Detector.Detect("requests.http", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "http-cookie");

		Assert.Equal("abc==", finding.Value);
		Assert.Equal(content.IndexOf("abc==", StringComparison.Ordinal), finding.Start);
		Assert.DoesNotContain("Path", finding.Value, StringComparison.Ordinal);
		Assert.DoesNotContain("3600", finding.Value, StringComparison.Ordinal);
	}

	[Fact]
	public void Detect_HttpCookieReferencesPlaceholdersAndEmptyValues_AreSkipped()
	{
		const string content =
			"Cookie: reference={{token}}; literal=YOUR-API-KEY-HERE; empty= ; live=real-cookie-value\n";

		var finding = Assert.Single(
			Detector.Detect("requests.rest", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "http-cookie");

		Assert.Equal("real-cookie-value", finding.Value);
	}

	[Fact]
	public void Detect_CookieHeaderOutsideHttpRequestFile_IsNotStructuredSecret()
	{
		var findings = Detector.Detect(
			"notes.txt",
			"Cookie: session=real-cookie-value",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "http-cookie");
	}

	[Fact]
	public void Detect_HttpCookieHeaders_EnforcesPerFileFindingBudget()
	{
		var pairs = Enumerable.Range(0, SecretInspectionLimits.MaximumFindingsPerFile + 1)
			.Select(static index => $"name{index}=value{index}");
		var content = $"Cookie: {string.Join(';', pairs)}";

		var exception = Assert.Throws<SecretInspectionBudgetExceededException>(() =>
			StructuredSecretDetector.Detect(
				"requests.http",
				content,
				SmartSecretStack.None,
				new SecretFileInspectionBudget(),
				TestContext.Current.CancellationToken));

		Assert.Equal(nameof(SecretInspectionLimits.MaximumFindingsPerFile), exception.LimitName);
	}

	[Fact]
	public void Detect_HttpCookieRuleWinsAnExactConnectionStringOverlap()
	{
		const string content = "Cookie: Server=db; Password=cookie-password";
		var rawFindings = StructuredSecretDetector.Detect(
			"requests.http",
			content,
			SmartSecretStack.None,
			TestContext.Current.CancellationToken);
		var passwordStart = content.IndexOf("cookie-password", StringComparison.Ordinal);

		Assert.Contains(rawFindings, finding =>
			finding.RuleId == "connection-password" && finding.Start == passwordStart);
		Assert.Contains(rawFindings, finding =>
			finding.RuleId == "http-cookie" && finding.Start == passwordStart);
		var resolved = SecretRedactionScope.ResolveNonOverlappingMatches(rawFindings);

		Assert.DoesNotContain(resolved, static finding => finding.RuleId == "connection-password");
		Assert.Equal(2, resolved.Count(static finding => finding.RuleId == "http-cookie"));
	}

	[Theory]
	[InlineData("Host=db;Username=admin;Pass" + "word=postgres;Database=app", "postgres")]
	[InlineData("Server=db;User Id=sa;P" + "wd=Admin123!;Initial Catalog=app", "Admin123!")]
	[InlineData("Host = db; Username = admin; Pass" + "word = phrase with spaces; Database = app", "phrase with spaces")]
	[InlineData("jdbc:postgresql://db/app?user=admin&pass" + "word=short&ssl=true", "short")]
	public void Detect_ConnectionString_RedactsOnlyPassword(string connectionString, string password)
	{
		var finding = Assert.Single(
			Detector.Detect("appsettings.json", connectionString, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "connection-password");

		Assert.Equal(password, finding.Value);
		var redacted = Replace(connectionString, finding);
		Assert.DoesNotContain(password, redacted, StringComparison.Ordinal);
		Assert.Equal(connectionString[..finding.Start], redacted[..finding.Start]);
		Assert.EndsWith(
			connectionString[(finding.Start + finding.Length)..],
			redacted,
			StringComparison.Ordinal);
	}

	[Fact]
	public void Detect_ConnectionStringOnDocumentationHost_StillRedactsPassword()
	{
		var finding = Assert.Single(
			Detector.Detect(
				"appsettings.json",
				"Host=example.com;Username=admin;Password=admin;Database=app",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "connection-password");

		Assert.Equal("admin", finding.Value);
	}

	[Theory]
	[InlineData("${DB_PASSWORD}")]
	[InlineData("$(DbPassword)")]
	[InlineData("%DB_PASSWORD%")]
	[InlineData("{{ secret }}")]
	[InlineData("<password>")]
	[InlineData("")]
	public void Detect_ConnectionStringReferenceOrPlaceholder_DoesNotRedact(string value)
	{
		var findings = Detector.Detect(
			"settings.txt",
			$"Host=db;Username=admin;Pass" + $"word={value};Database=app",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "connection-password");
	}

	[Theory]
	[InlineData("appsettings.json", "{ \"Jwt\": { \"Secret\": \"this is my signing key\" } }")]
	[InlineData("application.yml", "security:\n  password: Admin123!")]
	[InlineData("production.tfvars", "api_secret = \"tiny\"")]
	[InlineData("settings.py", "SECRET_KEY = \"phrase with spaces\"")]
	[InlineData("docker-compose.yml", "environment:\n  DB_PASSWORD: postgres")]
	[InlineData("web.config", "<appSettings><add key=\"Jwt:Secret\" value=\"this is my signing key\" /></appSettings>")]
	[InlineData("web.config", "<database password=\"Admin123!\" host=\"db\" />")]
	[InlineData("web.config", "<password>phrase with spaces</password>")]
	public void Detect_ConfigurationValue_RedactsLowEntropyAndSpacedValues(
		string path,
		string content)
	{
		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "config-secret");

		Assert.NotEmpty(finding.Value);
		Assert.Equal(finding.Value, content.Substring(finding.Start, finding.Length));
	}

	[Theory]
	[InlineData("${DB_PASSWORD}")]
	[InlineData("$(DbPassword)")]
	[InlineData("%DB_PASSWORD%")]
	[InlineData("{{ secret }}")]
	[InlineData("<password>")]
	[InlineData("")]
	public void Detect_EnvironmentReferenceOrPlaceholder_DoesNotRedact(string value)
	{
		var findings = Detector.Detect(
			".env",
			$"DB_PASSWORD=\"{value}\"",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "environment-secret");
	}

	[Theory]
	[InlineData("appsettings.json", "{ \"Password\": \"${DB_PASSWORD}\" }")]
	[InlineData("application.yml", "password: $(DbPassword)")]
	[InlineData("production.tfvars", "password = \"%DB_PASSWORD%\"")]
	[InlineData("docker-compose.yml", "DB_PASSWORD: {{ secret }}")]
	[InlineData("web.config", "<add key=\"Password\" value=\"<password>\" />")]
	public void Detect_ConfigurationReferenceOrPlaceholder_DoesNotRedact(string path, string content)
	{
		var findings = Detector.Detect(path, content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "config-secret");
	}

	[Theory]
	[InlineData("your-api-key-here")]
	[InlineData("your_api_key_here")]
	[InlineData("your-token-here")]
	[InlineData("your_token_here")]
	[InlineData("insert-key-here")]
	[InlineData("insert_key_here")]
	[InlineData("enter-password-here")]
	[InlineData("enter_password_here")]
	public void Detect_NewWholeValueConfigurationPlaceholder_DoesNotRedact(string value)
	{
		var findings = Detector.Detect(
			"appsettings.json",
			$"{{ \"Password\": \"{value}\" }}",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "config-secret");
	}

	[Theory]
	[InlineData("test")]
	[InlineData("admin")]
	public void Detect_WeakConfigurationLiteral_IsNotTreatedAsPlaceholder(string value)
	{
		var finding = Assert.Single(
			Detector.Detect(
				"appsettings.json",
				$"{{ \"Password\": \"{value}\" }}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "config-secret");

		Assert.Equal(value, finding.Value);
	}

	[Theory]
	[InlineData("SECRET_KEY = os.environ[\"DJANGO_SECRET_KEY\"]")]
	[InlineData("SECRET_KEY = os.getenv(\"DJANGO_SECRET_KEY\")")]
	[InlineData("SECRET_KEY = settings.SECRET_KEY")]
	public void Detect_PythonConfigurationReference_DoesNotRedact(string content)
	{
		var findings = Detector.Detect("settings.py", content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "config-secret");
	}

	[Fact]
	public void Detect_EnvPrefixVariant_UsesEnvironmentRules()
	{
		var finding = Assert.Single(
			Detector.Detect(
				".envrc",
				"DB_PASSWORD=Admin123!",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");

		Assert.Equal("Admin123!", finding.Value);
	}

	[Fact]
	public void Detect_PlaceholderWordInsideRealCredential_DoesNotSuppressTheWholeValue()
	{
		const string key = "AKIAIOSFODNN7EXAMPLE";
		var finding = Assert.Single(
			Detector.Detect(
				".env",
				$"AWS_ACCESS_KEY_ID={key}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");

		Assert.Equal(key, finding.Value);
	}

	[Fact]
	public void Detect_ExampleSubstringInsideConfigurationValue_DoesNotSuppressTheWholeValue()
	{
		const string value = "real-example-production-password";

		var finding = Assert.Single(
			Detector.Detect(
				"appsettings.json",
				$"{{ \"Password\": \"{value}\" }}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "config-secret");

		Assert.Equal(value, finding.Value);
	}

	[Fact]
	public void Detect_OrdinarySourceAssignment_IsOutsideStructuredTier()
	{
		const string source = "const string password = \"Admin123!\";";

		var findings = Detector.Detect("src/Example.cs", source, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(
			findings,
			static finding => finding.RuleId is "config-secret" or "environment-secret");
	}

	[Theory]
	[InlineData("go.mod")]
	[InlineData("package-lock.json")]
	[InlineData("vendor/private/config.txt")]
	public void Detect_ProviderAllowlistedPathStillRunsHighConfidenceStructuredTier(string path)
	{
		const string content = "https://service:production-password@packages.internal/repository";

		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "credential-uri-password");

		Assert.Equal("production-password", finding.Value);
	}

	[Theory]
	[InlineData("go.mod")]
	[InlineData("package-lock.json")]
	[InlineData("vendor/module/config.txt")]
	public void Detect_ProviderRulesStillRespectProviderPathAllowlist(string path)
	{
		const string providerToken = "AKIAIOSFODNN7EXAMPLE";

		var findings = Detector.Detect(path, providerToken, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "aws-access-token");
	}

	[Theory]
	[InlineData("go.mod", "module example.test/project")]
	[InlineData("package-lock.json", "{ \"lockfileVersion\": 3 }")]
	[InlineData("vendor/module/config.txt", "endpoint=https://example.test/public")]
	public void Detect_AllowlistedStructuredTierDoesNotCreateNoise(string path, string content)
	{
		var findings = Detector.Detect(path, content, TestContext.Current.CancellationToken);

		Assert.Empty(findings);
	}

	[Fact]
	public void DetectionScope_NearestProjectMarkerOwnsEnvironmentVocabulary()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("package.json", "{}");
		var nodeEnvironment = workspace.CreateFile(".env", "NPM_AUTH=opaque");
		workspace.CreateFile("nested/pyproject.toml", "[project]");
		var pythonEnvironment = workspace.CreateFile("nested/.env", "NPM_AUTH=opaque");
		var scope = Detector.CreateScope(workspace.Path);

		var nodeFindings = scope.Detect(
			nodeEnvironment,
			".env",
			"NPM_AUTH=opaque",
			TestContext.Current.CancellationToken);
		var pythonFindings = scope.Detect(
			pythonEnvironment,
			"nested/.env",
			"NPM_AUTH=opaque",
			TestContext.Current.CancellationToken);

		Assert.Contains(nodeFindings, static finding => finding.RuleId == "environment-secret");
		Assert.DoesNotContain(pythonFindings, static finding => finding.RuleId == "environment-secret");
	}

	[Fact]
	public void DetectionScope_NewNearestMarkerInvalidatesScopedRuleIdentity()
	{
		using var workspace = new TemporaryDirectory();
		var environment = workspace.CreateFile("nested/.env", "NPM_AUTH=opaque");
		var beforeScope = Detector.CreateScope(workspace.Path);

		var before = beforeScope.Detect(
			environment,
			"nested/.env",
			"NPM_AUTH=opaque",
			TestContext.Current.CancellationToken);
		var beforeIdentity = beforeScope.GetRulesIdentity(environment, "nested/.env");
		workspace.CreateFile("nested/package.json", "{}");
		var afterScope = Detector.CreateScope(workspace.Path);
		var after = afterScope.Detect(
			environment,
			"nested/.env",
			"NPM_AUTH=opaque",
			TestContext.Current.CancellationToken);
		var afterIdentity = afterScope.GetRulesIdentity(environment, "nested/.env");

		Assert.DoesNotContain(before, static finding => finding.RuleId == "environment-secret");
		Assert.Contains(after, static finding => finding.RuleId == "environment-secret");
		Assert.NotEqual(beforeIdentity, afterIdentity);
	}

	[Fact(Timeout = 5_000)]
	public void Detect_PathologicalStructuredInput_CompletesWithinBound()
	{
		var value = new string('a', 2 * 1024 * 1024 - 1) + "b";
		var content = "DB_PASSWORD=" + value;

		var finding = Assert.Single(
			Detector.Detect(".env", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");

		Assert.Equal(value.Length, finding.Length);
	}

	private static string Replace(string content, DetectedSecret finding) =>
		string.Concat(
			content.AsSpan(0, finding.Start),
			$"DEVPROJEX_REDACTED[{finding.RuleId}#1]",
			content.AsSpan(finding.Start + finding.Length));

	internal static SmartSecretsDetector CreateDetector()
	{
		var smartIgnore = new SmartIgnoreService(
		[
			new CommonSmartIgnoreRule(),
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule(),
			new PythonArtifactsIgnoreRule(),
			new JvmArtifactsIgnoreRule(),
			new RustArtifactsIgnoreRule(),
			new GoArtifactsIgnoreRule(),
			new PhpArtifactsIgnoreRule(),
			new RubyArtifactsIgnoreRule()
		]);
		return new SmartSecretsDetector(new GitleaksSecretDetector(), smartIgnore);
	}
}
