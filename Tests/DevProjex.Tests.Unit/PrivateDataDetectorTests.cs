using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PrivateDataDetectorTests
{
	private static readonly PrivateDataDetector Detector = new();

	[Fact]
	public void RulesIdentity_IsVersionedForCacheIsolation()
	{
		Assert.Equal("private-data-v1", Detector.RulesIdentity);
	}

	[Theory]
	[InlineData("Contact alice.smith" + "@company.io", "alice.smith" + "@company.io")]
	[InlineData("mailto:engineer+alerts" + "@CORP.ExampleHost.COM", "engineer+alerts" + "@CORP.ExampleHost.COM")]
	[InlineData("const mail = \"DEV.USER" + "@internal.cloud\";", "DEV.USER" + "@internal.cloud")]
	[InlineData("(first_last%tag" + "@service.co.uk)", "first_last%tag" + "@service.co.uk")]
	[InlineData("Contact member" + "@company.io.", "member" + "@company.io")]
	public void Detect_Email_RedactsAsciiAddressOnly(string content, string expected)
	{
		var finding = FindSingle(content, "email");

		Assert.Equal(expected, finding.Value);
		Assert.Equal(expected, content.Substring(finding.Start, finding.Length));
		Assert.Equal(RedactionFindingCategory.PrivateData, finding.Category);
	}

	[Theory]
	[InlineData("user@example.com")]
	[InlineData("a@b.test")]
	[InlineData("user@localhost")]
	[InlineData("git@github.com")]
	[InlineData("noreply@company.com")]
	[InlineData("no-reply@company.com")]
	[InlineData("${owner@company.com}")]
	[InlineData("<owner@company.com>")]
	[InlineData("{{owner@company.com}}")]
	[InlineData("abc@defmail")]
	[InlineData("préfixeowner@company.comsuffix")]
	[InlineData("пользователь@company.com")]
	[InlineData("owner@xn--bcher-kva.de")]
	[InlineData("cp Assets/AppIcon/MacOS/AppIconSet/32.png Assets/AppIcon/MacOS/app.iconset/icon_16x16@2x.png")]
	[InlineData("--apple-id \"your@email.com\" \\")]
	[InlineData("photo@3x.webp")]
	[InlineData("readme@old.md")]
	[InlineData("admin@company.com")]
	[InlineData("info@firma.de")]
	[InlineData("your-team@corp.com")]
	[InlineData("owner@company.com")]
	[InlineData("owner+alerts@company.com")]
	[InlineData("tests@devprojex.local")]
	[InlineData("terminal-tests@devprojex.local")]
	[InlineData("postgres://admin:pass@db.local/app")]
	[InlineData("https://token@github.com/user/repo.git")]
	[InlineData("https:" + "//service:change-me-now@cluster.internal/app")]
	[InlineData("owner@corp.internal@prod.internal")]
	public void Detect_Email_RejectsDocumentationServicesPlaceholdersAndInvalidBoundaries(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_RejectsEveryFileLikeTopLevelLabel()
	{
		var extensions = new[]
		{
			"png", "jpg", "jpeg", "gif", "svg", "webp", "avif", "ico", "bmp", "tif", "tiff", "heic", "psd",
			"css", "scss", "less", "js", "mjs", "cjs", "jsx", "ts", "tsx", "map", "json", "xml", "yml", "yaml",
			"toml", "ini", "cfg", "conf", "lock", "cs", "csproj", "sln", "props", "targets", "py", "rb", "go", "rs",
			"java", "kt", "kts", "scala", "php", "swift", "c", "h", "cpp", "hpp", "cc", "hh", "sh", "bash", "zsh",
			"ps1", "psm1", "psd1", "bat", "cmd", "md", "markdown", "rst", "txt", "log", "pdf", "docx", "xlsx", "pptx",
			"csv", "tsv", "zip", "gz", "tgz", "tar", "bz2", "xz", "7z", "rar", "jar", "exe", "dll", "so", "dylib",
			"pdb", "nupkg", "snupkg", "dmg", "pkg", "msi", "msix", "deb", "rpm", "ttf", "otf", "woff", "woff2",
			"eot", "mp3", "mp4", "wav", "ogg", "flac", "mov", "avi", "mkv", "webm", "wasm", "bin", "iso", "sql",
			"db", "sqlite", "bak", "tmp"
		};

		foreach (var extension in extensions)
		{
			Assert.DoesNotContain(
				Detect($"asset@variant.{extension}"),
				static finding => finding.RuleId == "email");
		}
	}

	[Fact]
	public void Detect_Email_RejectsEveryPlaceholderAndRoleLocalPart()
	{
		var localParts = new[]
		{
			"your", "yours", "youremail", "your-email", "your_email", "your.email", "yourname", "your-name",
			"your_name", "name", "email", "mail", "someone", "somebody", "example", "sample", "demo", "test",
			"user", "username", "foo", "bar", "john", "jane", "john.doe", "jane.doe", "johndoe", "janedoe",
			"firstname.lastname", "first.last", "firstname", "lastname", "admin", "info", "support", "contact",
			"hello", "sales", "office", "help", "team", "feedback", "webmaster", "postmaster", "hostmaster",
			"abuse", "security", "privacy", "billing", "marketing", "hr", "careers", "jobs", "press", "legal",
			"notifications", "bot", "actions", "ci", "build", "donotreply", "do-not-reply", "devops", "ops",
			"git", "noreply", "no-reply"
		};

		foreach (var localPart in localParts)
		{
			Assert.DoesNotContain(
				Detect($"{localPart}@company.com"),
				static finding => finding.RuleId == "email");
		}
	}

	[Theory]
	[InlineData("sprite@2x.assets.io")]
	[InlineData("icon@12Z.cdn.dev")]
	public void Detect_Email_RejectsRetinaDomainLabelsIndependentlyOfFileExtension(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("ivan.petrov" + "@gmail.com")]
	[InlineData("person" + "@company.ai")]
	[InlineData("person" + "@company.app")]
	[InlineData("person" + "@company.dev")]
	[InlineData("person" + "@company.me")]
	[InlineData("person" + "@company.co")]
	[InlineData("person" + "@company.tv")]
	public void Detect_Email_DoesNotBlockCommonMailTopLevelLabels(string content)
	{
		Assert.Equal(content, FindSingle(content, "email").Value);
	}

	[Fact]
	public void Detect_EmailInsideUriQuery_IsNotMistakenForAuthorityUserInfo()
	{
		const string content = "https://company.test/path?email=ivan.petrov" + "@gmail.com";

		Assert.Equal("ivan.petrov" + "@gmail.com", FindSingle(content, "email").Value);
	}

	[Theory]
	[InlineData("legal/LICENSE", "ivan.petrov@corp.internal")]
	[InlineData("legal/licence.md", "ivan.petrov@corp.internal")]
	[InlineData("NOTICE.txt", "ivan.petrov@corp.internal")]
	[InlineData("AUTHORS", "ivan.petrov@corp.internal")]
	[InlineData("CONTRIBUTORS.md", "ivan.petrov@corp.internal")]
	[InlineData("COPYING.third-party", "ivan.petrov@corp.internal")]
	[InlineData("PATENTS", "ivan.petrov@corp.internal")]
	[InlineData("THIRD-PARTY-NOTICES", "ivan.petrov@corp.internal")]
	[InlineData("Citation.cff", "ivan.petrov@corp.internal")]
	[InlineData(".mailmap", "ivan.petrov@corp.internal")]
	public void Detect_Email_IsDisabledInAttributionFiles(string path, string content)
	{
		Assert.DoesNotContain(Detect(path, content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_InReadmeRemainsPrivate()
	{
		const string email = "ivan.petrov@corp.internal";

		Assert.Equal(email, FindSingle("README.md", email, "email").Value);
	}

	[Fact]
	public void Detect_AttributionFileDisablesOnlyEmailRule()
	{
		const string email = "ivan.petrov@corp.internal";
		const string address = "51.15.23.7";
		var findings = Detect("NOTICE.md", $"{email} {address}");

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "email");
		Assert.Contains(findings, finding => finding.RuleId == "ipv4" && finding.Value == address);
	}

	[Theory]
	[InlineData("dev")]
	[InlineData("developer")]
	[InlineData("qa")]
	[InlineData("staging")]
	[InlineData("testing")]
	public void Detect_Email_AdditionalRoleMailboxesAreKept(string localPart)
	{
		Assert.DoesNotContain(
			Detect($"{localPart}@company.com"),
			static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_RolePrefixDoesNotSuppressPersonalMailbox()
	{
		const string email = "dev.person@company.com";

		Assert.Equal(email, FindSingle(email, "email").Value);
	}

	[Theory]
	[InlineData("address=93.184." + "216.34", "93.184." + "216.34")]
	[InlineData("http://151.101." + "1.69:443/path", "151.101." + "1.69")]
	[InlineData("dns=[2001:4860:4860:" + ":8888]:53", "2001:4860:4860:" + ":8888")]
	[InlineData("peer=2606:4700:4700:" + ":1111", "2606:4700:4700:" + ":1111")]
	[InlineData("mapped=[::ffff:93.184." + "216.34]:443", "::ffff:93.184." + "216.34")]
	[InlineData("peer=[3fff:1000:" + ":1]:443", "3fff:1000:" + ":1")]
	[InlineData("peer=3ff0:" + ":1", "3ff0:" + ":1")]
	public void Detect_GlobalIp_RedactsOnlyAddress(string content, string expected)
	{
		var finding = Assert.Single(
			Detect(content),
			finding => finding.RuleId is "ipv4" or "ipv6");

		Assert.Equal(expected, finding.Value);
	}

	[Theory]
	[InlineData("0.1.2.3")]
	[InlineData("127.9.8.7")]
	[InlineData("169.254.4.5")]
	[InlineData("10.1.2.3")]
	[InlineData("172.31.255.254")]
	[InlineData("192.168.1.2")]
	[InlineData("100.127.1.2")]
	[InlineData("192.0.2.25")]
	[InlineData("198.51.100.25")]
	[InlineData("203.0.113.25")]
	[InlineData("233.252.0.25")]
	[InlineData("198.19.1.2")]
	[InlineData("224.1.2.3")]
	[InlineData("240.1.2.3")]
	[InlineData("255.255.255.255")]
	[InlineData("8.8.8.8")]
	[InlineData("8.8.4.4")]
	[InlineData("1.1.1.1")]
	[InlineData("1.0.0.1")]
	[InlineData("9.9.9.9")]
	[InlineData("149.112.112.112")]
	[InlineData("208.67.222.222")]
	[InlineData("208.67.220.220")]
	[InlineData("::")]
	[InlineData("::1")]
	[InlineData("fe80::1%eth0")]
	[InlineData("fc00::1")]
	[InlineData("fd12:3456::1")]
	[InlineData("ff02::1")]
	[InlineData("2001:db8::1")]
	[InlineData("3fff:0fff::1")]
	[InlineData("::ffff:192.168.1.1")]
	public void Detect_NonPrivateOrDocumentationIp_IsKept(string address)
	{
		Assert.DoesNotContain(
			Detect($"endpoint=[{address}]:443"),
			static finding => finding.RuleId is "ipv4" or "ipv6");
	}

	[Theory]
	[InlineData("999.1.1.1")]
	[InlineData("1.2.3.4.5")]
	[InlineData("v1.2.3.4")]
	[InlineData("10.0.26200.1234")]
	public void Detect_InvalidOrEmbeddedIpv4_IsNotMatched(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("Version=\"5.0.0.0\"")]
	[InlineData("AssemblyVersion(\"1.0.0.0\")")]
	[InlineData("<FileVersion>5.0.0.0</FileVersion>")]
	[InlineData("\"version\": \"1.2.3.4\"")]
	[InlineData("Mozilla/5.0 … Chrome/120.0.0.0 Safari/537.36")]
	[InlineData("network=93.184.216.0")]
	public void Detect_Ipv4_RejectsVersionContextsAndTrailingZero(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_VersionContextDoesNotCrossLineBoundary()
	{
		const string content = "version=1.2.3.4 tag=2.3.4.5\nssh 51.15." + "23.7";

		Assert.Equal("51.15." + "23.7", FindSingle(content, "ipv4").Value);
	}

	[Fact]
	public void Detect_Ipv4_VersionContextIsBounded()
	{
		var content = $"version {new string('x', 161)} ssh 51.15." + "23.7";

		Assert.Equal("51.15." + "23.7", FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("package==2.31.0.1")]
	[InlineData("package>=2.31.0.1")]
	[InlineData("package<=2.31.0.1")]
	[InlineData("package~=2.31.0.1")]
	[InlineData("package!=2.31.0.1")]
	public void Detect_Ipv4_RejectsAdjacentVersionConstraintOperators(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_SpacedEqualityContextStillRedactsAddress()
	{
		const string address = "51.15.23.7";

		Assert.Equal(address, FindSingle($"ip == {address}", "ipv4").Value);
	}

	[Theory]
	[InlineData("tag: 1.16.0.2")]
	[InlineData("release: 1.16.0.2")]
	[InlineData("build: 1.16.0.2")]
	[InlineData("packages/serilog/2.10.0.1/lib")]
	public void Detect_Ipv4_RejectsExpandedVersionContexts(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("CHANGELOG.md")]
	[InlineData("docs/history.txt")]
	[InlineData("Releases")]
	[InlineData("docs/RELEASE_NOTES-v2.md")]
	[InlineData("docs/RELEASE-NOTES.md")]
	public void Detect_Ipv4_IsDisabledInVersionHistoryFiles(string path)
	{
		Assert.DoesNotContain(
			Detect(path, "## 1.2.3.4 (2026-08-16)"),
			static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_InOrdinaryMarkdownRemainsPrivate()
	{
		Assert.Equal("1.2.3.4", FindSingle("README.md", "endpoint=1.2.3.4", "ipv4").Value);
	}

	[Fact]
	public void Detect_VersionHistoryFileDisablesOnlyIpv4Rule()
	{
		const string email = "ivan.petrov@corp.internal";
		var findings = Detect("CHANGELOG.md", $"## 1.2.3.4 - {email}");

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "ipv4");
		Assert.Contains(findings, finding => finding.RuleId == "email" && finding.Value == email);
	}

	[Theory]
	[InlineData("C:\\Users\\" + "avazb\\Projects\\DevProjex", "avazb")]
	[InlineData("C:/Users/" + "Olivia/Projects/App", "Olivia")]
	[InlineData("\"C:\\\\Users\\\\build-owner\\\\source\"", "build-owner")]
	[InlineData("/home/" + "alexei/project", "alexei")]
	[InlineData("/Users/" + "MacOwner/Code", "MacOwner")]
	[InlineData("file:///home/" + "olivia/.config", "olivia")]
	[InlineData("(/Users/" + "octocat)", "octocat")]
	[InlineData("/home/" + "алиса/.config", "алиса")]
	public void Detect_LocalUser_RedactsOnlyUserSegment(string content, string expected)
	{
		var finding = FindSingle(content, "local-user");

		Assert.Equal(expected, finding.Value);
		var redacted = Replace(content, finding);
		Assert.Contains("DEVPROJEX_REDACTED[local-user#1]", redacted, StringComparison.Ordinal);
		Assert.Equal(content[..finding.Start], redacted[..finding.Start]);
		Assert.EndsWith(content[(finding.Start + finding.Length)..], redacted, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(@"C:\Users\Public\Documents")]
	[InlineData(@"C:\Users\Default\Documents")]
	[InlineData(@"C:\Users\Default User\Documents")]
	[InlineData("\"C:\\Users\\Default User\"")]
	[InlineData(@"C:\Users\All Users\Documents")]
	[InlineData(@"C:\Users\user\Documents")]
	[InlineData(@"C:\Users\username\Documents")]
	[InlineData(@"C:\Users\example\Documents")]
	[InlineData(@"C:\Users\test\Documents")]
	[InlineData(@"C:\Users\runner\work")]
	[InlineData(@"C:\Users\runneradmin\work")]
	[InlineData(@"C:\Users\ContainerAdministrator\work")]
	[InlineData(@"C:\Users\ContainerUser\work")]
	[InlineData(@"C:\Users\vagrant\work")]
	[InlineData(@"C:\Users\jenkins\work")]
	[InlineData("/home/root/project")]
	[InlineData("/Users/demo/Code")]
	[InlineData("%USERPROFILE%/project")]
	[InlineData("~/project")]
	[InlineData("'''^/Users/(?i)[a-z0-9]+/[\\w .-/]+$'''")]
	[InlineData("/Users/42")]
	[InlineData("example.com/home/dashboard")]
	[InlineData("api/Users/octocat")]
	[InlineData("/home/name/project")]
	[InlineData("/home/yourname/project")]
	[InlineData("/home/your-username/project")]
	[InlineData("/home/your_username/project")]
	[InlineData("/home/johndoe/project")]
	[InlineData("/home/janedoe/project")]
	[InlineData("/home/someuser/project")]
	[InlineData(@"C:\Users\me\AppData\Local")]
	[InlineData("/home/developer/project")]
	[InlineData("/home/devuser/project")]
	public void Detect_LocalUser_KeepListAndIndirectHomesAreNotMatched(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "local-user");
	}

	[Fact]
	public void Detect_LocalUser_KeepsCiCloudContainerAndDocumentationIdentities()
	{
		var identities = new[]
		{
			"vsts", "appveyor", "gitlab-runner", "circleci", "travis", "buildbot", "teamcity", "bamboo",
			"ubuntu", "ec2-user", "azureuser", "centos", "debian", "fedora", "rocky", "alpine", "arch",
			"core", "opc", "pi", "node", "git", "deploy", "app", "jovyan", "sagemaker-user", "postgres",
			"mysql", "docker", "WDAGUtilityAccount", "defaultuser0", "alice", "bob"
		};

		foreach (var identity in identities)
		{
			Assert.DoesNotContain(
				Detect($"/home/{identity}/project"),
				static finding => finding.RuleId == "local-user");
		}
	}

	[Theory]
	[InlineData("/home/yourusername/project")]
	[InlineData(@"C:\Users\my-account\project")]
	public void Detect_LocalUser_KeepsYourAndMyPrefixes(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "local-user");
	}

	[Theory]
	[InlineData("/home/avazb/project", "avazb")]
	[InlineData(@"C:\Users\avazb\project", "avazb")]
	public void Detect_LocalUser_PrefixRulesDoNotSuppressRealUser(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "local-user").Value);
	}

	[Theory]
	[InlineData("DB::Add(...)")]
	[InlineData("[List]::Add")]
	[InlineData("::Add")]
	[InlineData("face:cafe::beef")]
	public void Detect_Ipv6_RejectsScopeOperatorsAndLetterOnlyCandidates(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv6");
	}

	[Theory]
	[InlineData("device=AA:bb:CC" + ":01:23:fE", "AA:bb:CC" + ":01:23:fE")]
	[InlineData("device=0a-1B-2c" + "-3D-4e-5F", "0a-1B-2c" + "-3D-4e-5F")]
	[InlineData("prefix-AA:bb:CC" + ":01:23:fE", "AA:bb:CC" + ":01:23:fE")]
	[InlineData("prefix:0a-1B-2c" + "-3D-4e-5F", "0a-1B-2c" + "-3D-4e-5F")]
	public void Detect_MacAddress_RedactsConsistentSixPairForm(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "mac-address").Value);
	}

	[Theory]
	[InlineData("00:00:00:00:00:00")]
	[InlineData("FF-FF-FF-FF-FF-FF")]
	[InlineData("2001:db8:aa:bb:cc:dd:ee:ff")]
	[InlineData("123e4567-e89b-12d3-a456-426614174000")]
	[InlineData("aabb.ccdd.eeff")]
	[InlineData("AA:BB-CC:DD:EE:FF")]
	public void Detect_MacAddress_RejectsSentinelsAndEmbeddedForms(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "mac-address");
	}

	[Theory]
	[InlineData("00:11:22:33:44:55")]
	[InlineData("00-11-22-33-44-55")]
	[InlineData("11-22-33-44-55-66")]
	[InlineData("01:23:45:67:89:ab")]
	[InlineData("AA-BB-CC-DD-EE-FF")]
	[InlineData("DE:AD:BE:EF:00:01")]
	[InlineData("de-ad-be-ef-aa-bb")]
	public void Detect_MacAddress_KeepsCanonicalDocumentationValues(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "mac-address");
	}

	[Fact]
	public void Detect_MacAddress_OrdinaryDeviceAddressRemainsPrivate()
	{
		const string address = "3C:22:FB:12:34:56";

		Assert.Equal(address, FindSingle(address, "mac-address").Value);
	}

	[Theory]
	[InlineData("call +" + "79261234567", "+" + "79261234567")]
	[InlineData("tel:+49 (30) " + "1234-5678", "+49 (30) " + "1234-5678")]
	[InlineData("phone=+33 1 42 " + "68 53 00", "+33 1 42 " + "68 53 00")]
	public void Detect_InternationalPhone_RedactsBoundedValue(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "phone-number").Value);
	}

	[Theory]
	[InlineData("+1-202-555-0142")]
	[InlineData("+44 7700 900123")]
	[InlineData("+44 20 7946 0123")]
	[InlineData("@@ -1,5 +1,7 @@")]
	[InlineData("+0500")]
	[InlineData("C++11")]
	[InlineData("+1234567890123456")]
	[InlineData("x+12345678")]
	[InlineData("x + 12345678")]
	[InlineData("+12345678.90")]
	[InlineData("+87654321.90")]
	[InlineData("+1.2025550142")]
	[InlineData("+99999999999")]
	[InlineData("+1234567890")]
	public void Detect_Phone_RejectsDocumentationAndAmbiguousTokens(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "phone-number");
	}

	[Theory]
	[InlineData("change.patch")]
	[InlineData("changes.DIFF")]
	public void Detect_Phone_LeadingPlusInDiffFileIsKept(string path)
	{
		Assert.DoesNotContain(
			Detect(path, "+79261234567"),
			static finding => finding.RuleId == "phone-number");
	}

	[Fact]
	public void Detect_Phone_NonLeadingValueInDiffFileRemainsPrivate()
	{
		Assert.Equal(
			"+79261234567",
			FindSingle("change.patch", "phone=+79261234567", "phone-number").Value);
	}

	[Fact]
	public void Detect_Phone_LeadingValueInTextFileRemainsPrivate()
	{
		Assert.Equal(
			"+79261234567",
			FindSingle("phones.txt", "+79261234567", "phone-number").Value);
	}

	[Theory]
	[InlineData("${ 93.184.216.34 }", "ipv4")]
	[InlineData("$(2001:4860::1)", "ipv6")]
	[InlineData("{{AA:BB:CC:12:34:56}}", "mac-address")]
	[InlineData("<+79261234567>", "phone-number")]
	[InlineData("%owner@corp.io%", "email")]
	[InlineData(@"C:\Users\placeholder\source", "local-user")]
	public void Detect_SharedPlaceholderGuardRejectsCandidatesFromEveryScanner(
		string content,
		string ruleId)
	{
		Assert.DoesNotContain(Detect(content), finding => finding.RuleId == ruleId);
	}

	[Fact]
	public void Prescan_SkipsEmailWithoutAtAndRecognizesAnchorAtEnd()
	{
		var noEmail = PrivateDataDetector.ComputeFeatureMaskForAnalysis("plain text only");
		var trailingAnchor = PrivateDataDetector.ComputeFeatureMaskForAnalysis("prefix /home/");
		var windowsPath = PrivateDataDetector.ComputeFeatureMaskForAnalysis("C:\\Users\\" + "alice");
		var unrelatedWindowsPath = PrivateDataDetector.ComputeFeatureMaskForAnalysis(@"C:\Projects\app");
		var mac = PrivateDataDetector.ComputeFeatureMaskForAnalysis("AA-BB-CC" + "-12-34-56");

		Assert.Equal(PrivateDataFeatureMask.None, noEmail & PrivateDataFeatureMask.Email);
		Assert.NotEqual(PrivateDataFeatureMask.None, trailingAnchor & PrivateDataFeatureMask.LocalUser);
		Assert.Equal(PrivateDataFeatureMask.None, windowsPath & PrivateDataFeatureMask.Ipv6);
		Assert.Equal(PrivateDataFeatureMask.None, unrelatedWindowsPath & PrivateDataFeatureMask.LocalUser);
		Assert.NotEqual(PrivateDataFeatureMask.None, mac & PrivateDataFeatureMask.MacAddress);
	}

	[Fact]
	public void Prescan_ProducesSameFindingsAsRunningEveryRuleScanner()
	{
		const string content = "owner@corp.io icon@2x.png 93.184." + "216.34 Version=5.0.0.0 [2001:4860:" +
		                       ":1] DB::Add C:\\Users\\" + "alice\\src /Users/(?i) AA:BB:CC" +
		                       ":12:34:56 +" + "79261234567";

		var withPrescan = Detect(content);
		var withoutPrescan = PrivateDataDetector.DetectWithoutPrescanForAnalysis(content);

		Assert.Equal(withoutPrescan, withPrescan);
	}

	[Theory]
	[InlineData("docs/CHANGELOG.md", "## 1.2.3.4", "ipv4")]
	[InlineData("legal/LICENSE.txt", "ivan.petrov@corp.internal", "email")]
	[InlineData("changes/update.patch", "+79261234567", "phone-number")]
	public void Prescan_PathAwareRulesMatchRunningEveryEligibleScanner(
		string path,
		string content,
		string excludedRuleId)
	{
		var withPrescan = Detect(path, content);
		var withoutPrescan = PrivateDataDetector.DetectWithoutPrescanForAnalysis(path, content);
		var features = PrivateDataDetector.ComputeFeatureMaskForAnalysis(path, content);
		var excludedFeature = excludedRuleId switch
		{
			"ipv4" => PrivateDataFeatureMask.Ipv4,
			"email" => PrivateDataFeatureMask.Email,
			"phone-number" => PrivateDataFeatureMask.PhoneNumber,
			_ => throw new ArgumentOutOfRangeException(nameof(excludedRuleId))
		};

		Assert.Equal(PrivateDataFeatureMask.None, features & excludedFeature);
		Assert.Equal(withoutPrescan, withPrescan);
	}

	[Fact]
	public void Detect_PathAwareRulesUseOnlyTheLastPathSegment()
	{
		const string email = "ivan.petrov@corp.internal";
		const string address = "1.2.3.4";
		var findings = Detect("CHANGELOG/LICENSE-archive/README.md", $"{email} {address}");

		Assert.Contains(findings, finding => finding.RuleId == "email" && finding.Value == email);
		Assert.Contains(findings, finding => finding.RuleId == "ipv4" && finding.Value == address);
	}

	[Fact]
	public void Detect_MailmapSuffixDoesNotDisableEmailRule()
	{
		const string email = "ivan.petrov@corp.internal";

		Assert.Equal(email, FindSingle(".mailmap.bak", email, "email").Value);
	}

	[Fact]
	public void Detect_RegistersFindingsAgainstSharedBudget()
	{
		var budget = new SecretFileInspectionBudget();
		var findings = Detector.Detect(
			"data.txt",
			("a" + "@corp.io b" + "@corp.io 93.184." + "216.34").AsSpan(),
			budget,
			TestContext.Current.CancellationToken);

		Assert.Equal(3, findings.Count);
		Assert.Throws<SecretInspectionBudgetExceededException>(() =>
		{
			for (var index = findings.Count; index <= SecretInspectionLimits.MaximumFindingsPerFile; index++)
				budget.RegisterFinding(CancellationToken.None);
		});
	}

	[Fact]
	public void Detect_CanceledTokenStopsPrescan()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() =>
			Detector.Detect("data.txt", new string('x', 4096), cancellation.Token));
	}

	[Fact]
	public void Detect_RepeatedInputIsDeterministic()
	{
		const string content = "owner@corp.io 93.184." + "216.34 /home/" + "alice +" +
		                       "79261234567 AA:BB:CC" + ":12:34:56";

		var first = Detect(content);
		var second = Detect(content);

		Assert.Equal(first, second);
	}

	private static IReadOnlyList<DetectedSecret> Detect(string content) =>
		Detector.Detect("data.txt", content, TestContext.Current.CancellationToken);

	private static IReadOnlyList<DetectedSecret> Detect(string path, string content) =>
		Detector.Detect(path, content, TestContext.Current.CancellationToken);

	private static DetectedSecret FindSingle(string content, string ruleId) =>
		Assert.Single(Detect(content), finding => finding.RuleId == ruleId);

	private static DetectedSecret FindSingle(string path, string content, string ruleId) =>
		Assert.Single(Detect(path, content), finding => finding.RuleId == ruleId);

	private static string Replace(string content, DetectedSecret finding) =>
		string.Concat(
			content.AsSpan(0, finding.Start),
			$"DEVPROJEX_REDACTED[{finding.RuleId}#1]",
			content.AsSpan(finding.Start + finding.Length));
}
