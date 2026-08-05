using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Infrastructure.SmartIgnore;

namespace DevProjex.Tests.Unit;

public sealed class SmartSecretsDetectorTests
{
	private static readonly SmartSecretsDetector Detector = CreateDetector();

	[Theory]
	[InlineData("postgres://admin:pass@db.local/app")]
	[InlineData("mysql://admin:Admin123!@db.local/app")]
	[InlineData("mongodb+srv://service:change-me-now@cluster.example/app")]
	[InlineData("redis://default:redis-pass@cache.local:6379")]
	[InlineData("amqp://worker:rabbit-pass@queue.local/vhost")]
	[InlineData("https://deploy:http-pass@example.test/artifact")]
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
			$"postgres://admin:{value}@db.local/app",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "credential-uri-password");
	}

	[Fact]
	public void Detect_CredentialUriValueContainingExample_IsStillRedacted()
	{
		var finding = Assert.Single(
			Detector.Detect(
				"settings.txt",
				"postgres://admin:live-example-password@db.local/app",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "credential-uri-password");

		Assert.Equal("live-example-password", finding.Value);
	}

	[Theory]
	[InlineData("Host=db;Username=admin;Password=postgres;Database=app", "postgres")]
	[InlineData("Server=db;User Id=sa;Pwd=Admin123!;Initial Catalog=app", "Admin123!")]
	[InlineData("Host = db; Username = admin; Password = phrase with spaces; Database = app", "phrase with spaces")]
	[InlineData("jdbc:postgresql://db/app?user=admin&password=short&ssl=true", "short")]
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
			$"Host=db;Username=admin;Password={value};Database=app",
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
		var content = "DB_PASSWORD=" + new string('a', 2 * 1024 * 1024);

		var finding = Assert.Single(
			Detector.Detect(".env", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");

		Assert.Equal(content.Length - "DB_PASSWORD=".Length, finding.Length);
	}

	private static string Replace(string content, DetectedSecret finding) =>
		string.Concat(
			content.AsSpan(0, finding.Start),
			$"DEVPROJEX_REDACTED[{finding.RuleId}#1]",
			content.AsSpan(finding.Start + finding.Length));

	private static SmartSecretsDetector CreateDetector()
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
