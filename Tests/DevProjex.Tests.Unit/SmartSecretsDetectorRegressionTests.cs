using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SmartSecretsDetectorRegressionTests
{
	private static readonly SmartSecretsDetector Detector = SmartSecretsDetectorTests.Detector;

	public static TheoryData<string> LiteralPlaceholders => new()
	{
		"changeme",
		"change_me",
		"change-me",
		"your-password-here",
		"your_password_here",
		"replace_me",
		"replaceme",
		"todo",
		"tbd",
		"placeholder",
		"n/a",
		"na",
		"null",
		"none",
		"unset",
		"CHANGE_ME",
		"XXXXXXXX",
		"...."
	};

	[Theory]
	[MemberData(nameof(LiteralPlaceholders))]
	public void Detect_LiteralPlaceholderValue_DoesNotCreateStructuredFinding(string value)
	{
		var findings = Detector.Detect(
			".env.example",
			$"DB_PASSWORD={value}",
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, IsStructuredFinding);
	}

	[Theory]
	[InlineData("secret")]
	[InlineData("password")]
	[InlineData("admin")]
	[InlineData("0000")]
	[InlineData("123456")]
	public void Detect_WeakLiteralValue_RemainsASecret(string value)
	{
		var finding = Assert.Single(
			Detector.Detect(
				".env.example",
				$"DB_PASSWORD={value}",
				TestContext.Current.CancellationToken),
			static match => match.RuleId == "environment-secret");

		Assert.Equal(value, finding.Value);
	}

	[Fact]
	public void Detect_PlaceholderSubstringInsideCredential_DoesNotSuppressFinding()
	{
		const string value = "prod-placeholder-7f4c91a2";

		var finding = Assert.Single(
			Detector.Detect(
				".env.example",
				$"DB_PASSWORD={value}",
				TestContext.Current.CancellationToken),
			static match => match.RuleId == "environment-secret");

		Assert.Equal(value, finding.Value);
	}

	public static TheoryData<string> GeneralSensitiveEnvironmentKeys => new()
	{
		"AWS_KEY",
		"ENCRYPTION_KEY",
		"encryptionKey",
		"API_KEY",
		"ACCESS_KEY",
		"PRIVATE_KEY",
		"SIGNING_KEY",
		"DB_PASSWORD",
		"PASSWD",
		"PWD",
		"CLIENT_SECRET",
		"AUTH_TOKEN",
		"API_TOKEN",
		"SERVICE_CREDENTIAL"
	};

	[Theory]
	[MemberData(nameof(GeneralSensitiveEnvironmentKeys))]
	public void Detect_SensitiveEnvironmentKey_StopwordInsideValueDoesNotChangeScopedVerdict(string key)
	{
		const string cleanValue = "A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h";
		const string valueContainingStopword = "A7d9mQ2xEXAMPLEK4vN8sR6tY3uW5zB1cE0fG2h";

		var cleanFinding = Assert.Single(
			Detector.Detect(
				".env",
				$"{key}={cleanValue}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");
		var stopwordFinding = Assert.Single(
			Detector.Detect(
				".env",
				$"{key}={valueContainingStopword}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");

		Assert.Equal(cleanValue, cleanFinding.Value);
		Assert.Equal(valueContainingStopword, stopwordFinding.Value);
	}

	[Theory]
	[InlineData("PUBLIC_KEY")]
	[InlineData("JWT_PUBLIC_KEY")]
	[InlineData("publicKey")]
	[InlineData("KEY")]
	[InlineData("MONKEY")]
	public void Detect_NonSecretKeySuffix_DoesNotCreateStructuredFinding(string key)
	{
		const string value = "A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h";

		Assert.DoesNotContain(
			Detector.Detect(
				".env",
				$"{key}={value}",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "environment-secret");
	}

	[Theory]
	[InlineData(".env.example", "DB_PASSWORD=changeme\nAPI_TOKEN=replace_me\nJWT_SECRET=your-password-here")]
	[InlineData(".env.sample", "DB_PASSWORD=none\nAPI_TOKEN=todo")]
	[InlineData(".env.template", "DB_PASSWORD=XXXXXXXX\nAPI_TOKEN=....")]
	[InlineData(".env.dist", "DB_PASSWORD=null\nAPI_TOKEN=unset")]
	[InlineData("appsettings.Example.json", "{ \"Password\": \"changeme\", \"ApiKey\": \"placeholder\" }")]
	[InlineData("appsettings.template.json", "{ \"Password\": \"your_password_here\", \"ClientSecret\": \"tbd\" }")]
	public void Detect_RealisticTemplateFilledWithPlaceholders_ProducesNoFindings(
		string path,
		string content)
	{
		Assert.Empty(Detector.Detect(path, content, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData(".env.example", "DB_PASSWORD=changeme\nAPI_TOKEN=Admin123!")]
	[InlineData("appsettings.Example.json", "{ \"Password\": \"placeholder\", \"ClientSecret\": \"short live value\" }")]
	public void Detect_TemplateContainingOneCredential_ProducesExactlyOneStructuredFinding(
		string path,
		string content)
	{
		Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			IsStructuredFinding);
	}

	[Theory]
	[InlineData("Dockerfile", "ENV DB_PASSWORD=S3cr3tPass", "S3cr3tPass")]
	[InlineData("Dockerfile.dev", "ARG API_TOKEN=short-token", "short-token")]
	[InlineData("service.dockerfile", "ENV DB_PASSWORD=\"phrase with spaces\"", "phrase with spaces")]
	[InlineData("Containerfile", "ARG CLIENT_SECRET='quoted-secret'", "quoted-secret")]
	[InlineData("Dockerfile", "ENV DB_PASSWORD legacy password", "legacy password")]
	public void Detect_DockerfileSensitiveAssignment_RedactsOnlyValue(
		string path,
		string content,
		string expectedValue)
	{
		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "container-secret");

		Assert.Equal(expectedValue, finding.Value);
		Assert.Equal(expectedValue, content.Substring(finding.Start, finding.Length));
	}

	[Theory]
	[InlineData("ENV APP_NAME=production")]
	[InlineData("ARG BUILD_CONFIGURATION=Release")]
	[InlineData("ENV DB_PASSWORD=changeme")]
	[InlineData("ARG API_TOKEN=XXXXXXXX")]
	[InlineData("RUN echo DB_PASSWORD=Admin123!")]
	public void Detect_DockerfileNonSecretOrPlaceholder_DoesNotCreateContainerFinding(string content)
	{
		var findings = Detector.Detect("Dockerfile", content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static match => match.RuleId == "container-secret");
	}

	[Theory]
	[InlineData("request.http", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc.def", "authorization-bearer", "Bearer")]
	[InlineData("requests.rest", "Authorization: Basic dXNlcjpwYXNz", "authorization-basic", "Basic")]
	[InlineData("request.http", "Authorization: Token gh-test-token-value", "authorization-token", "Token")]
	[InlineData("request.http", "Proxy-Authorization: Bearer proxy-token-value", "authorization-bearer", "Bearer")]
	[InlineData("request.http", "Authorization: Bearer live-token # request note", "authorization-bearer", "Bearer")]
	public void Detect_AuthorizationHeader_RedactsCredentialAndPreservesScheme(
		string path,
		string content,
		string ruleId,
		string scheme)
	{
		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			match => match.RuleId == ruleId);
		var redacted = Replace(content, finding);

		Assert.Contains($": {scheme} DEVPROJEX_REDACTED[{ruleId}#1]", redacted, StringComparison.Ordinal);
		Assert.DoesNotContain(finding.Value, redacted, StringComparison.Ordinal);
		Assert.EndsWith(content[(finding.Start + finding.Length)..], redacted, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("Authorization: Bearer ${ACCESS_TOKEN}")]
	[InlineData("Authorization: Bearer ${ACCESS_TOKEN} # resolved by the client")]
	[InlineData("Authorization: Basic {{ basic_auth }}")]
	[InlineData("Authorization: Basic {{ basic_auth }} # resolved by the client")]
	[InlineData("Proxy-Authorization: Token changeme")]
	[InlineData("X-Authorization: Bearer real-token")]
	[InlineData("Authorization: Digest response=value")]
	public void Detect_AuthorizationHeaderReferenceOrUnsupportedShape_DoesNotCreateFinding(string content)
	{
		var findings = Detector.Detect("request.http", content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(
			findings,
			static match => match.RuleId.StartsWith("authorization-", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData(".pgpass", "db.local:5432:app:admin:Admin123!", "Admin123!")]
	[InlineData("pgpass.conf", "db.local:5432:app:admin:p\\:ass", "p\\:ass")]
	public void Detect_PgPass_RedactsOnlyPasswordField(
		string path,
		string content,
		string expectedValue)
	{
		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "pgpass-password");

		Assert.Equal(expectedValue, finding.Value);
		Assert.StartsWith("db.local:5432:app:admin:", Replace(content, finding), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("# db.local:5432:app:admin:Admin123!")]
	[InlineData("db.local:5432:app:admin:changeme")]
	public void Detect_PgPass_CommentOrPlaceholder_DoesNotCreateFinding(string content)
	{
		Assert.DoesNotContain(
			Detector.Detect(".pgpass", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "pgpass-password");
	}

	[Theory]
	[InlineData(".netrc", "machine api.example login build password Admin123!", "Admin123!")]
	[InlineData("_netrc", "machine api.example password \"phrase with spaces\"", "phrase with spaces")]
	public void Detect_Netrc_RedactsOnlyPasswordToken(
		string path,
		string content,
		string expectedValue)
	{
		var finding = Assert.Single(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "netrc-password");

		Assert.Equal(expectedValue, finding.Value);
		Assert.Contains("machine api.example", Replace(content, finding), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("# machine api.example password Admin123!")]
	[InlineData("machine api.example login build password placeholder")]
	[InlineData("machine api.example login build")]
	public void Detect_Netrc_CommentPlaceholderOrMissingPassword_DoesNotCreateFinding(string content)
	{
		Assert.DoesNotContain(
			Detector.Detect(".netrc", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "netrc-password");
	}

	public static TheoryData<string, string, string> VerifiedDetectionBaseline => new()
	{
		{ "appsettings.json", "{ \"Smtp\": { \"Password\": \"mail pass\" } }", "config-secret" },
		{ "appsettings.Production.json", "{ \"ClientSecret\": \"client value\" }", "config-secret" },
		{ "Web.config", "<add key=\"Password\" value=\"Admin123!\" />", "config-secret" },
		{ "db.txt", "Server=db;User ID=sa;Password=Admin123!;Database=app", "connection-password" },
		{ "db.txt", "jdbc:postgresql://db/app?user=admin&password=short&ssl=true", "connection-password" },
		{ ".env.local", "export DB_PASSWORD=Admin123!", "environment-secret" },
		{ "docker-compose.yml", "environment:\n  - POSTGRES_PASSWORD=Admin123!", "config-secret" },
		{ ".npmrc", "//registry.npmjs.org/:_authToken=npm-test-token", "environment-secret" },
		{ "application.yml", "spring:\n  datasource:\n    password: Admin123!", "config-secret" },
		{ "terraform.tfvars", "db_password = \"Admin123!\"", "config-secret" },
		{ "settings.py", "SECRET_KEY = \"short signing phrase\"", "config-secret" },
		{ "secret.yaml", "kind: Secret\ndata:\n  password: " + "c2FuaXRpemVkLXBhc3N3b3Jk", "kubernetes-secret-yaml" },
		{
			"private.pem",
			"-----BEGIN " + "PRIVATE KEY-----\n" +
			"MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDAC4AWkdwKYSd8\n" +
			"Ks14IReLcYgADhoXk56ZzXI=\n" +
			"-----END " + "PRIVATE KEY-----",
			"private-key"
		},
		{ "database.txt", "postgres://admin:Admin123!@db.local/app", "credential-uri-password" }
	};

	[Theory]
	[MemberData(nameof(VerifiedDetectionBaseline))]
	public void Detect_VerifiedBaseline_RemainsCovered(string path, string content, string ruleId)
	{
		Assert.Contains(
			Detector.Detect(path, content, TestContext.Current.CancellationToken),
			match => match.RuleId == ruleId);
	}

	[Fact]
	public void Detect_SanitizedRealProjectShapes_RemainCovered()
	{
		var cases = new[]
		{
			("appsettings.json", "Host=db;Username=admin;Password=Admin123!;Database=app", "connection-password"),
			("appsettings.json", "{ \"Jwt\": { \"Secret\": \"this is my signing key\" } }", "config-secret"),
			("appsettings.json", "{ \"AdminPassword\": \"0000\" }", "config-secret")
		};

		foreach (var (path, content, ruleId) in cases)
		{
			Assert.Contains(
				Detector.Detect(path, content, TestContext.Current.CancellationToken),
				match => match.RuleId == ruleId);
		}
	}

	private static bool IsStructuredFinding(DetectedSecret finding) =>
		finding.RuleId is
			"environment-secret" or
			"config-secret" or
			"container-secret" or
			"pgpass-password" or
			"netrc-password";

	private static string Replace(string content, DetectedSecret finding) =>
		string.Concat(
			content.AsSpan(0, finding.Start),
			$"DEVPROJEX_REDACTED[{finding.RuleId}#1]",
			content.AsSpan(finding.Start + finding.Length));
}
