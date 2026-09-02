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
			"java", "kt", "kts", "scala", "php", "swift", "zig", "c", "h", "cpp", "hpp", "cc", "hh", "sh", "bash", "zsh",
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
	[InlineData("vendor/COPYRIGHT", "ivan.petrov@corp.internal")]
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
	public void Detect_AttributionFileDisablesEmailAndPhoneRulesOnly()
	{
		const string email = "ivan.petrov@corp.internal";
		const string phone = "+79261234567";
		const string address = "51.15.23.7";
		var findings = Detect("NOTICE.md", $"{email} {phone} {address}");

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "email");
		Assert.DoesNotContain(findings, static finding => finding.RuleId == "phone-number");
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

	[Theory]
	[InlineData("to")]
	[InlineData("cc")]
	[InlineData("bcc")]
	[InlineData("from")]
	[InlineData("reply")]
	[InlineData("sender")]
	[InlineData("recipient")]
	[InlineData("joe")]
	public void Detect_Email_MessageRoleMailboxesAreKept(string localPart)
	{
		Assert.DoesNotContain(
			Detect($"{localPart}@company.com"),
			static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("a@a.com")]
	[InlineData("a@b.com")]
	[InlineData("b@b.com")]
	[InlineData("d@foo.com")]
	public void Detect_Email_RejectsSingleCharacterLocalParts(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_SingleCharacterGuardPreservesTwoCharacterMailbox()
	{
		const string email = "al@company.com";

		Assert.Equal(email, FindSingle(email, "email").Value);
	}

	[Fact]
	public void Detect_Email_RolePrefixDoesNotSuppressPersonalMailbox()
	{
		const string email = "dev.person@company.com";

		Assert.Equal(email, FindSingle(email, "email").Value);
	}

	[Theory]
	[InlineData("this@LongWrapper.get")]
	[InlineData("this@myTransform.collect")]
	[InlineData("it@Flow.emit")]
	[InlineData("super@Scope.member")]
	[InlineData("%@foo.Wor")]
	[InlineData("t@IntMap.Tip(_, _)")]
	[InlineData("left@IntMap.Bin { value }")]
	[InlineData("java.util.@lib.Valid")]
	[InlineData("hell@o.wor")]
	[InlineData("thisguy@domain.abcd")]
	[InlineData("0CB459E0-0336-41DA-BC88-E6E28C697DDB@37signals.com")]
	[InlineData("\\u003cinfo@datadoghq.com")]
	public void Detect_Email_RejectsLanguageSyntaxUnknownDomainsAndMessageIdentifiers(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("member@corp.local")]
	[InlineData("person@company.systems")]
	[InlineData("ivan@mail.ru")]
	public void Detect_Email_TldPolicyKeepsOperationalAddressesDetectable(string content)
	{
		Assert.Equal(content, FindSingle(content, "email").Value);
	}

	[Theory]
	[InlineData("\" Maintainer: Jay Sitter (jay@jaysitter.com)")]
	[InlineData("* Copyright (c) 2013 Edin Dazdarevic (edin@company.com)")]
	[InlineData("// contributor ivan.petrov@company.com")]
	[InlineData("// Peter Bartok <pbartok@novell.com>")]
	[InlineData("- Alex Soto (alex.soto@company.com)")]
	[InlineData("Thanks jane.smith@company.com")]
	public void Detect_Email_RejectsAttributionContext(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("\" first created by image@lgic.co.kr")]
	[InlineData("\" Please send comments to maintainer@company.com")]
	[InlineData("// modified by Jane Doe <jane@company.com>")]
	[InlineData("// written by Jane Doe <jane@company.com>")]
	[InlineData("E-mail: jane@company.com")]
	[InlineData("Send comments to jane@company.com")]
	[InlineData("Email: cyan@fb.com")]
	[InlineData("Conversion to float by Ian Lance Taylor, Cygnus Support, ian@cygnus.com.")]
	[InlineData("#define PACKAGE_BUGREPORT \"mingw-w64-public@lists.sourceforge.net\"")]
	[InlineData("Please forward all changes to rmk@arm.linux.org.uk")]
	[InlineData("Please inform tcpdump-workers@lists.tcpdump.org if you use any")]
	[InlineData("last edit: 1999/11/05 gwyn@arl.mil")]
	public void Detect_Email_RejectsNarrowAttributionPhrases(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_RejectsLicenseBannerAttribution()
	{
		const string content =
			"/* Copyright (c) 1994 Carnegie-Mellon University. All rights reserved.\n" +
			" * Permission to use, copy, modify and distribute this software is hereby granted.\n" +
			" * Software Distribution Coordinator or Software.Distribution@CS.CMU.EDU\n */";

		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_Email_RejectsSpdxCopyrightContinuation()
	{
		const string content =
			"/* SPDX-License-Identifier: GPL-2.0\n" +
			" * Copyright 2001 Intel (first.person@intel.com,\n" +
			" * second.person@intel.com)\n */";

		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Fact]
	public void Detect_ClosedLicenseBannerDoesNotSuppressOperationalValues()
	{
		const string email = "ivan.petrov@corp.local";
		const string phone = "+79261234567";
		var findings = Detect(
			"/* Copyright 2026 Example. Permission to use this software is granted. */\n" +
			$"*ptr = \"{email}\"; phone={phone}");

		Assert.Contains(findings, finding => finding.RuleId == "email" && finding.Value == email);
		Assert.Contains(findings, finding => finding.RuleId == "phone-number" && finding.Value == phone);
	}

	[Fact]
	public void Detect_Email_NamedAttributionGuardDoesNotSuppressApplicationAssignment()
	{
		const string email = "ivan.petrov@company.com";

		Assert.Equal(email, FindSingle($"const email = \"{email}\";", "email").Value);
	}

	[Fact]
	public void Detect_Email_NamedAttributionGuardDoesNotSuppressLowercaseCommentText()
	{
		const string email = "ivan.petrov@corp.local";

		Assert.Equal(email, FindSingle($"# please contact {email} for access", "email").Value);
	}

	[Fact]
	public void Detect_Email_UserPlaceholderIsKeptWithoutSuppressingPersonalMailbox()
	{
		const string placeholder = "let parameters = [\"email\": \"user@alamofire.org\",\n";
		const string personal = "ivan.petrov@alamofire.org";

		Assert.DoesNotContain(Detect(placeholder), static finding => finding.RuleId == "email");
		Assert.Equal(personal, FindSingle(personal, "email").Value);
	}

	[Theory]
	[InlineData("sk-ssh-ed25519@openssh.com")]
	[InlineData("sk-ecdsa-sha2-nistp256@openssh.com")]
	[InlineData("hmac-sha2-256-etm@openssh.com")]
	[InlineData("algorithm@ssh.com")]
	[InlineData("algorithm@libssh.org")]
	public void Detect_Email_SshAlgorithmIdentifiersAreKept(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("CODE_OF_CONDUCT.md")]
	[InlineData("docs/SECURITY.md")]
	[InlineData("MAINTAINERS")]
	[InlineData("CODEOWNERS")]
	[InlineData("GOVERNANCE.md")]
	[InlineData("SUPPORT.txt")]
	[InlineData("third-party/go-licenses.csv")]
	[InlineData("composer.json")]
	[InlineData("package.json")]
	[InlineData("composer.lock")]
	[InlineData("pyproject.toml")]
	[InlineData("pom.xml")]
	[InlineData("Cargo.toml")]
	[InlineData("library.gemspec")]
	[InlineData("plugin.podspec")]
	[InlineData("package.nuspec")]
	[InlineData("manual.mdoc")]
	[InlineData("junitbuild.publishing-conventions.gradle.kts")]
	[InlineData("publish-android.gradle.kts")]
	[InlineData("publishing.kt")]
	public void Detect_Email_IsDisabledInContactAndPackageMetadataFiles(string path)
	{
		Assert.DoesNotContain(
			Detect(path, "ivan.petrov@company.com"),
			static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("composer.lock", "\"email\": \"j.boggiano@seld.be\",")]
	[InlineData("junitbuild.publishing-conventions.gradle.kts", "email = \"business@johanneslink.net\"")]
	[InlineData("publish-android.gradle.kts", "email.set(\"arnaud@kotzilla.io\")")]
	[InlineData("publishing.kt", "email.set(\"oleksiy.pylypenko@gmail.com\")")]
	[InlineData("testColorSinglePageManual().mdoc", ".Mt johnappleseed@apple.com")]
	public void Detect_Email_Audit3PublicMetadataExamplesAreKept(string path, string content)
	{
		Assert.DoesNotContain(Detect(path, content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData(".Mt johnappleseed@apple.com")]
	[InlineData("  .Mt appleseeds@apple.com ,")]
	public void Detect_Email_MdocMailToMacroIsKeptOutsideMdocFiles(string content)
	{
		Assert.DoesNotContain(
			Detect("manual.snapshot", content),
			static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("ssh_host=\"$CI_USER@ci-s390x.caddyserver.com\"")]
	[InlineData("git config user.email \"$GITHUB_ACTOR@users.noreply.github.com\"")]
	[InlineData("\\xe9@blah.com")]
	[InlineData("hg@bitbucket.org")]
	public void Detect_Email_RejectsShellVariablesEscapesAndDvcsUserInfo(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "email");
	}

	[Theory]
	[InlineData("appsettings.json")]
	[InlineData("Source/Startup.cs")]
	public void Detect_Email_RemainsEnabledInApplicationFiles(string path)
	{
		const string email = "ivan.petrov@company.com";

		Assert.Equal(email, FindSingle(path, email, "email").Value);
	}

	[Theory]
	[InlineData("address=93.184." + "216.34", "93.184." + "216.34")]
	[InlineData("http://151.101." + "1.69:443/path", "151.101." + "1.69")]
	[InlineData("dns=[2a02:6b8:" + ":2:242]:53", "2a02:6b8:" + ":2:242")]
	[InlineData("peer=2a00:1450:4001:81f:" + ":200e", "2a00:1450:4001:81f:" + ":200e")]
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
	[InlineData("192.0.0.25")]
	[InlineData("192.88.99.25")]
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
	[InlineData("2001:2::1")]
	[InlineData("3fff:0fff::1")]
	[InlineData("::ffff:192.168.1.1")]
	public void Detect_NonPrivateOrDocumentationIp_IsKept(string address)
	{
		Assert.DoesNotContain(
			Detect($"endpoint=[{address}]:443"),
			static finding => finding.RuleId is "ipv4" or "ipv6");
	}

	[Theory]
	[InlineData("191.255.255.254")]
	[InlineData("192.0.1.1")]
	[InlineData("192.88.98.255")]
	[InlineData("192.88.100.1")]
	public void Detect_Ipv4_IanaExclusionsDoNotCrossPrefixBoundaries(string address)
	{
		Assert.Equal(address, FindSingle($"host={address}", "ipv4").Value);
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
	[InlineData("~2.2.3.1")]
	[InlineData("^2.2.3.1")]
	[InlineData("'~> 3.17.0.2'")]
	public void Detect_Ipv4_RejectsAdjacentVersionConstraintOperators(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData(">= 4.1.7.1")]
	[InlineData("<= 4.1.7.1")]
	[InlineData("~= 4.1.7.1")]
	[InlineData("!= 4.1.7.1")]
	public void Detect_Ipv4_RejectsVersionConstraintSeparatedByOneSpace(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_SingleEqualsWithSpaceStillRedactsAddress()
	{
		const string address = "51.15.23.7";

		Assert.Equal(address, FindSingle($"ip = {address}", "ipv4").Value);
	}

	[Theory]
	[InlineData("# security allow 51.15.23.7")]
	[InlineData("inspect 51.15.23.7")]
	[InlineData("stage 51.15.23.7")]
	[InlineData("consecutive 51.15.23.7")]
	[InlineData("rebuild 51.15.23.7")]
	public void Detect_Ipv4_ShortVersionKeywordsRequireWordBoundaries(string content)
	{
		Assert.Equal("51.15.23.7", FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("tag: 1.16.0.2")]
	[InlineData("release: 1.16.0.2")]
	[InlineData("build: 1.16.0.2")]
	[InlineData("packages/serilog/2.10.0.1/lib")]
	[InlineData("jackson-databind:2.12.7 -> 2.12.7.1")]
	[InlineData("runtimeInformation: \"Mono 6.12.0.140")]
	[InlineData("otp: \"25.3.2.21\"")]
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
		Assert.Equal("51.15.23.7", FindSingle("README.md", "endpoint=51.15.23.7", "ipv4").Value);
	}

	[Theory]
	[InlineData("src/VersionSelector.php")]
	[InlineData("tests/VersionBumperTest.php")]
	[InlineData("tests/PlatformVersionTest.php")]
	[InlineData("docs/v1.34.0.md")]
	[InlineData("relnotes/V1.52.1.MD")]
	public void Detect_Ipv4_IsDisabledInVersionNamedAndVersionedReleaseFiles(string path)
	{
		Assert.DoesNotContain(
			Detect(path, "release value 3.17.0.2"),
			static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("src/Composer/Package/Version/VersionSelector.php", " *  * 1.2.1.2       -> ^1.2")]
	[InlineData("tests/Composer/Test/Package/Version/VersionBumperTest.php", "['~2.2.3.1', '2.2.4', '~2.2.4.0']")]
	[InlineData("relnotes/v1.34.0.md", "Require Parser 3.1.2.1 or higher.")]
	public void Detect_Ipv4_Audit3VersionFileExamplesAreKept(string path, string content)
	{
		Assert.DoesNotContain(Detect(path, content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("route -> 51.15.23.7")]
	[InlineData("route ->51.15.23.7")]
	[InlineData("route ->\t51.15.23.7")]
	public void Detect_Ipv4_DependencyArrowPolicyKeepsArrowTargetsVisible(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("1.2.3.4")]
	[InlineData("2.2.2.2")]
	[InlineData("88.88.88.88")]
	[InlineData("2.3.4.5")]
	[InlineData("123.124.125.126")]
	[InlineData("1.008.08.224")]
	[InlineData("2.023.001.23")]
	[InlineData("24.045.056.124")]
	[InlineData("7.12.02.5")]
	[InlineData("1.89.017.98")]
	[InlineData("Cabal-2.2.0.1")]
	[InlineData("jvms-5.4.3.3")]
	[InlineData("#section-5.2.3.3")]
	[InlineData("1.4.5.2-rc.1")]
	[InlineData("GAS ref manual sec 3.6.2.1")]
	[InlineData("JVMS Sec. 4.7.16.1")]
	[InlineData("ch. 2.13.3.2")]
	[InlineData("rfc6819#section-5.2.3.3")]
	[InlineData("The HTML 5.2 syntax 8.2.4.41")]
	public void Detect_Ipv4_RejectsCanonicalExamplesAndVersionSyntax(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("51.15.23.7")]
	[InlineData("104.26.14.6")]
	[InlineData("host=1.2.3.5")]
	public void Detect_Ipv4_NewSyntaxGuardsPreserveOperationalAddresses(string content)
	{
		var expected = content[(content.LastIndexOf('=') + 1)..];

		Assert.Equal(expected, FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("2.5.4.3")]
	[InlineData("2.5.4.11")]
	[InlineData("2.5.29.17")]
	[InlineData("1.3.6.1")]
	[InlineData("2.23.140.1")]
	[InlineData("1.3.101.112")]
	[InlineData("1.3.132.7")]
	[InlineData("1.3.14.3")]
	[InlineData("1.3.36.1")]
	[InlineData("1.2.840.113549")]
	public void Detect_Ipv4_RejectsAsn1ObjectIdentifiers(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_Asn1ObjectIdentifierRangesDoNotSuppressAdjacentPublicRange()
	{
		const string address = "2.6.4.3";

		Assert.Equal(address, FindSingle($"ip {address}", "ipv4").Value);
	}

	[Theory]
	[InlineData("50.19.103.36:5000", "50.19.103.36")]
	[InlineData("65.54.227.120", "65.54.227.120")]
	public void Detect_Ipv4_Audit3GuardsPreserveOperationalAddresses(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("rails (7.1.3.2)")]
	[InlineData("Require Parser 3.1.2.1 or")]
	[InlineData("Present in 1.4.2.1 (Oct 2021)")]
	[InlineData("# \"5.0.0.1\"   --> \"5.0.1\"")]
	[InlineData("['1.2.3a', '1.2.3.1']")]
	[InlineData("logger.rb/1.5.2.9")]
	[InlineData("23.41.13.37")]
	public void Detect_Ipv4_AllSmallOctetsWithoutNetworkContextAreKept(string content)
	{
		Assert.DoesNotContain(
			Detect("examples/config.txt", content),
			static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("\"ip\": \"3.8.37.2\"", "3.8.37.2")]
	[InlineData("ping 23.41.13.37", "23.41.13.37")]
	[InlineData("allow 23.41.13.37/32", "23.41.13.37")]
	[InlineData("23.41.13.37:8080", "23.41.13.37")]
	[InlineData("ssh root@40.30.20.10", "40.30.20.10")]
	[InlineData("http://23.41.13.37/path", "23.41.13.37")]
	[InlineData("51.15.23.7", "51.15.23.7")]
	public void Detect_Ipv4_NetworkContextPreservesAddresses(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("ip")]
	[InlineData("host")]
	[InlineData("addr")]
	[InlineData("address")]
	[InlineData("server")]
	[InlineData("dns")]
	[InlineData("proxy")]
	[InlineData("gateway")]
	[InlineData("endpoint")]
	[InlineData("remote")]
	[InlineData("connect")]
	[InlineData("listen")]
	[InlineData("bind")]
	[InlineData("ping")]
	[InlineData("ssh")]
	[InlineData("curl")]
	[InlineData("http")]
	[InlineData("network")]
	[InlineData("subnet")]
	[InlineData("netmask")]
	[InlineData("mask")]
	[InlineData("route")]
	[InlineData("nat")]
	[InlineData("firewall")]
	[InlineData("vpn")]
	[InlineData("port")]
	[InlineData("socket")]
	[InlineData("nameserver")]
	[InlineData("resolver")]
	[InlineData("peer")]
	[InlineData("client")]
	[InlineData("upstream")]
	[InlineData("forwarded")]
	public void Detect_Ipv4_EachNetworkKeywordPreservesAllSmallAddress(string keyword)
	{
		const string address = "23.41.13.37";

		Assert.Equal(address, FindSingle($"{keyword} {address}", "ipv4").Value);
	}

	[Theory]
	[InlineData("23.41.13.37 upstream")]
	[InlineData("23.41.13.37 DNS")]
	[InlineData("23.41.13.37/8")]
	public void Detect_Ipv4_NetworkSignalsAreBidirectionalAndCaseInsensitive(string content)
	{
		Assert.Equal("23.41.13.37", FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("43.41.13.37", false)]
	[InlineData("44.41.13.37", true)]
	public void Detect_Ipv4_VersionFormThresholdIsInclusive(string address, bool shouldDetect)
	{
		Assert.Equal(
			shouldDetect,
			Detect(address).Any(static finding => finding.RuleId == "ipv4"));
	}

	[Fact]
	public void Detect_Ipv4_NetworkSignalDoesNotOverrideVersionGuard()
	{
		Assert.DoesNotContain(
			Detect("version ip 3.8.37.2"),
			static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("shipping 23.41.13.37")]
	[InlineData("ghost 23.41.13.37")]
	[InlineData("http://example.invalid\n23.41.13.37")]
	[InlineData("23.41.13.37/320")]
	public void Detect_Ipv4_NetworkSignalsRespectTokenAndLineBoundaries(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_NetworkSignalIsBounded()
	{
		var content = $"host {new string('x', 161)} 23.41.13.37";

		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("$Id: lvm.h,v 2.5.1.1 2007/12/27 13:02:25 roberto Exp $")]
	[InlineData("paragraph 4.7.20.1")]
	[InlineData("clause 27.7.2.6")]
	[InlineData("4.7.20.1 paragraph details")]
	[InlineData("27.7.2.6 clause requirements")]
	public void Detect_Ipv4_RejectsRevisionAndBidirectionalStandardContexts(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("// 27.7.2.6 Formatted output")]
	[InlineData("/* 20.7.2.4 [util.smartptr]")]
	[InlineData(" * 4.7.20.1 JVMS type annotation")]
	[InlineData("/* C99 7.18.1.1 Exact-width integer types.")]
	[InlineData("* functions. IEEE 802.3-2022 33.2.4.4 Variables")]
	[InlineData("/* 7.12.3.1 */")]
	[InlineData("// 2.2.1.2 - Mapped character")]
	[InlineData("- [2.3.1.1. System PATH fallback](#2311-system-path-fallback)")]
	[InlineData("As with [3.2.2.1](#3221-usable-as-tools), use the dependency")]
	[InlineData(" * 4.7.20.1</a>).")]
	[InlineData("Kernels 2.6.32.60 and newer are supported")]
	[InlineData("* in an overload condition (see 33.2.7.6) for at least TCUT")]
	[InlineData("char creation_date [ISODCL (814, 830)]; /* 8.4.26.1 */")]
	[InlineData("#define szOID_DSALG_CRPT \"2.5.8.1\"")]
	[InlineData("/* 23.1.6.2 (1) ACL Entry manipulation */")]
	[InlineData("// 23.3.5.1 constructors:")]
	[InlineData("/* 7.12.3.1 int fpclassify(real-floating x) */")]
	[InlineData("// [iterator.traits]/3.2.3.4")]
	[InlineData("// [format.string.escaped]/2.2.1.2")]
	[InlineData("// [network.reference]/65.1.3.2")]
	[InlineData("// 20.4.1.4, tuple helper classes:")]
	[InlineData("// 20.4.1.4: tuple helper classes")]
	[InlineData("/* usb 3.1 ch 9.6.2.5 */")]
	public void Detect_Ipv4_RejectsCommentedStandardSectionHeadings(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("# server 51.15.23.7 Production")]
	[InlineData("51.15.23.7 Production")]
	[InlineData("see 51.15.23.7 Production")]
	[InlineData("standard endpoint 51.15.23.7 Production")]
	[InlineData("ACL gateway 51.15.23.7 Production")]
	[InlineData("http://x/51.15.23.7")]
	[InlineData("branch 51.15.23.7")]
	public void Detect_Ipv4_SectionHeadingGuardPreservesOperationalAddresses(string content)
	{
		const string address = "51.15.23.7";

		Assert.Equal(address, FindSingle(content, "ipv4").Value);
	}

	[Theory]
	[InlineData("9.8.7.6")]
	[InlineData("43.42.41.40")]
	public void Detect_Ipv4_RejectsStrictlyDescendingExamples(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_DescendingExampleGuardPreservesNonSequentialAddress()
	{
		const string address = "9.8.7.5";

		Assert.Equal(address, FindSingle($"host={address}", "ipv4").Value);
	}

	[Theory]
	[InlineData("path M .45,71 51.15.23.7 Z")]
	[InlineData("port .5 near 51.15.23.7")]
	public void Detect_Ipv4_RejectsCandidatesNearNakedFractions(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_PeriodAfterAddressDoesNotTriggerSvgGuard()
	{
		const string address = "51.15.23.7";

		Assert.Equal(address, FindSingle($"server {address}.", "ipv4").Value);
	}

	[Theory]
	[InlineData("Gemfile.lock")]
	[InlineData("Podfile.lock")]
	[InlineData("package-lock.json")]
	[InlineData("packages.lock.json")]
	[InlineData("yarn.lock")]
	[InlineData("pnpm-lock.yaml")]
	[InlineData("composer.lock")]
	[InlineData("Cargo.lock")]
	[InlineData("paket.lock")]
	[InlineData("mix.lock")]
	[InlineData("flake.lock")]
	[InlineData("pubspec.lock")]
	[InlineData("Package.resolved")]
	public void Detect_Ipv4_IsDisabledInLockFiles(string path)
	{
		Assert.DoesNotContain(Detect(path, "activesupport (7.2.3.1)"), static finding => finding.RuleId == "ipv4");
	}

	[Theory]
	[InlineData("public/assets/logo.svg", "M0 12.3-71.5 26.6 8.5 11 4.8.3.2 9.7-8")]
	[InlineData("options/fileicon/material-icon-svgs.json", "33-.05-.47-.07 0 .02-.02.05-.02.09l-.04.17 1.13.37.99")]
	public void Detect_Ipv4_IsDisabledInSvgAssetFiles(string path, string content)
	{
		Assert.DoesNotContain(Detect(path, content), static finding => finding.RuleId == "ipv4");
	}

	[Fact]
	public void Detect_Ipv4_RemainsEnabledInOrdinaryJson()
	{
		const string address = "51.15.23.7";

		Assert.Equal(address, FindSingle("assets/config.json", $"{{\"endpoint\":\"{address}\"}}", "ipv4").Value);
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
	[InlineData("/home/sweet/project")]
	[InlineData("/home/build/project")]
	[InlineData("/home/you/project")]
	[InlineData("/home/projects/project")]
	[InlineData("/home/project/source")]
	[InlineData("/home/vapor/project")]
	[InlineData("/home/foobar/project")]
	[InlineData("/Users/John/project")]
	[InlineData("/Users/Jane/project")]
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
			"mysql", "docker", "WDAGUtilityAccount", "defaultuser0", "alice", "bob", "jdoe", "jsmith",
			"www", "www-data", "html", "images", "media", "img", "files", "data", "shared", "common",
			"private", "backup", "tmp", "temp", "cache", "logs", "log", "srv", "ftp", "sftp", "anon",
			"absolute", "relative", "opam", "linuxbrew", "brew", "nginx", "apache", "httpd", "redis",
			"mongo", "mongodb", "rabbitmq", "kafka", "elastic", "elasticsearch", "kibana", "grafana",
			"prometheus", "laravel", "symfony", "django", "rails", "flask", "spring", "deno", "bun",
			"zig", "dune", "cargo", "gradle", "maven", "composer"
		};

		foreach (var identity in identities)
		{
			Assert.DoesNotContain(
				Detect($"/home/{identity}/project"),
				static finding => finding.RuleId == "local-user");
		}
	}

	[Theory]
	[InlineData("/home/polls.com/project")]
	[InlineData("/home/special.polls.com/project")]
	public void Detect_LocalUser_RejectsDomainLikeSegments(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "local-user");
	}

	[Theory]
	[InlineData("/home/avazb", "avazb")]
	[InlineData("/Users/j.doe", "j.doe")]
	public void Detect_LocalUser_DomainGuardPreservesUserNames(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "local-user").Value);
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
	[InlineData("../../home/exceptions.md#notes")]
	[InlineData("./home/alice/project")]
	[InlineData("/home/.config/dart/pub-credentials.json")]
	[InlineData(@"C:\Users\R&D")]
	[InlineData(@"C:\Users\username.")]
	[InlineData("/home/Projects.txt")]
	[InlineData("/home/main.zig")]
	[InlineData("/home/user1/project")]
	[InlineData("/home/demo42/project")]
	public void Detect_LocalUser_RejectsRelativeLinksFilesAndNumberedPlaceholders(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "local-user");
	}

	[Fact]
	public void Detect_LocalUser_IsDisabledInsideDocsetBundle()
	{
		Assert.DoesNotContain(
			Detect("docs/Alamofire.docset/Contents/Resources/page.html", "/home/maintainer/project"),
			static finding => finding.RuleId == "local-user");
	}

	[Fact]
	public void Detect_LocalUser_IsDisabledInJazzyUndocumentedArtifact()
	{
		Assert.DoesNotContain(
			Detect("docs/undocumented.json", "{\"file\":\"/Users/jshier/Code/Alamofire.swift\"}"),
			static finding => finding.RuleId == "local-user");
	}

	[Theory]
	[InlineData("/home/avazb", "avazb")]
	[InlineData("path=\"/home/avazb/project\"", "avazb")]
	[InlineData(@"C:\Users\ivanov\source", "ivanov")]
	public void Detect_LocalUser_NewAmbiguityGuardsPreserveRealPaths(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "local-user").Value);
	}

	[Theory]
	[InlineData("/home/shark/project", "shark")]
	[InlineData("/home/rad/project", "rad")]
	[InlineData("/home/oleksiyp/project", "oleksiyp")]
	public void Detect_LocalUser_Audit3KeepListPreservesRealUserNames(string content, string expected)
	{
		Assert.Equal(expected, FindSingle(content, "local-user").Value);
	}

	[Theory]
	[InlineData("a::4")]
	[InlineData("b::4")]
	[InlineData("c1::8")]
	[InlineData("unquote(name)(c1)::16")]
	[InlineData("rwh_06::{...")]
	[InlineData("dxgi1_2::*")]
	[InlineData("*::8*")]
	[InlineData("::warning file=foo/bar.php,line=2,col=4")]
	[InlineData(":dep a::b:1")]
	[InlineData(":dep c::d:2")]
	[InlineData("::4711")]
	[InlineData("::12")]
	[InlineData("::21")]
	[InlineData("256::")]
	[InlineData("1::8")]
	[InlineData("1:0::")]
	public void Detect_Ipv6_RejectsLanguageAndFixtureShorthand(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv6");
	}

	[Theory]
	[InlineData("2a00:1450:4001:81f::200e")]
	[InlineData("234e:0:4567::3d")]
	[InlineData("2607:f8b0::1")]
	public void Detect_Ipv6_StructuralMinimumPreservesGlobalAddresses(string address)
	{
		Assert.Equal(address, FindSingle(address, "ipv6").Value);
	}

	[Theory]
	[InlineData("64:ff9b::")]
	[InlineData("64:ff9b:1::1234")]
	[InlineData("100::1")]
	[InlineData("2001::1")]
	[InlineData("2001:10::1")]
	[InlineData("2001:20::1")]
	[InlineData("2002::1")]
	[InlineData("5f00::1")]
	[InlineData("FE00::1")]
	[InlineData("fec0::1")]
	[InlineData("3ffe::1")]
	[InlineData("2001:4860:4860::8888")]
	[InlineData("2001:4860:4860::8844")]
	[InlineData("2606:4700:4700::1111")]
	[InlineData("2606:4700:4700::1001")]
	[InlineData("2620:fe::fe")]
	[InlineData("2620:fe::9")]
	public void Detect_Ipv6_RejectsIanaSpecialPurposeRanges(string address)
	{
		Assert.DoesNotContain(Detect(address), static finding => finding.RuleId == "ipv6");
	}

	[Fact]
	public void Detect_Ipv6_SpecialPurposeGuardsPreserveOperationalAddress()
	{
		const string address = "2a02:6b8::2:242";

		Assert.Equal(address, FindSingle(address, "ipv6").Value);
	}

	[Theory]
	[InlineData("2001:1:ffff::1")]
	[InlineData("2001:3::1")]
	public void Detect_Ipv6_BenchmarkingExclusionDoesNotCrossPrefixBoundaries(string address)
	{
		Assert.Equal(address, FindSingle(address, "ipv6").Value);
	}

	[Fact]
	public void Detect_Ipv6_MixedFixtureAndGlobalAddressFindsOnlyGlobalAddress()
	{
		const string address = "2a00:1450:4001:81f::200e";
		var finding = Assert.Single(Detect($"a::4 endpoint={address}"), static item => item.RuleId == "ipv6");

		Assert.Equal(address, finding.Value);
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

	[Fact]
	public void Detect_Ipv6_RejectsRstYearTarget()
	{
		const string content = "entries published in 2008::";

		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "ipv6");
	}

	[Theory]
	[InlineData("2600::")]
	[InlineData("2008::1")]
	public void Detect_Ipv6_RstYearGuardPreservesOperationalAddresses(string address)
	{
		Assert.Equal(address, FindSingle(address, "ipv6").Value);
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
	[InlineData("02:00:00:00:00:00")]
	[InlineData("16-00-00-00-00-00")]
	[InlineData("01:23:45:67:89:0A")]
	[InlineData("AB-CD-EF-01-23-45")]
	[InlineData("BC:DE:F0:12:34:56")]
	[InlineData("12-34-56-78-9A-BC")]
	public void Detect_MacAddress_RejectsFixturePatterns(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "mac-address");
	}

	[Fact]
	public void Detect_MacAddress_FixtureGuardsPreserveDeviceAddress()
	{
		const string address = "3C:22:FB:11:5A:C4";

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
	[InlineData("+00123456")]
	[InlineData("+0000 2007")]
	[InlineData("+0300 2012-04-13 01")]
	[InlineData("+015155555785")]
	[InlineData("+011234567890")]
	[InlineData("+01-123-456-7890")]
	[InlineData("+0001 1000")]
	[InlineData("+2015-01-23")]
	[InlineData("iex> Calendar.ISO.parse_naive_datetime(\"+2015-01-23 23:50:07\")")]
	[InlineData("+10 -10 0090 -090")]
	[InlineData("+18005551212")]
	[InlineData("+1-800-555-1212")]
	[InlineData("+45-22-555-1212")]
	[InlineData("+19725551212")]
	[InlineData("+11234567")]
	[InlineData("+112345678")]
	public void Detect_Phone_RejectsDatesSeparatedFieldsAndCanonicalFixtures(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "phone-number");
	}

	[Theory]
	[InlineData("+11234567891")]
	[InlineData("+112345678912")]
	[InlineData("+1123456789123")]
	[InlineData("+11234567891234")]
	[InlineData("+112345678912345")]
	[InlineData("+33612345678")]
	[InlineData("+1411111111")]
	public void Detect_Phone_RejectsCyclicAscendingAndUniformSuffixPlaceholders(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "phone-number");
	}

	[Theory]
	[InlineData("+79261234567")]
	[InlineData("+33687221934")]
	[InlineData("+12025434958")]
	[InlineData("+201001234567")]
	public void Detect_Phone_PlaceholderAndDateGuardsPreserveOperationalNumbers(string content)
	{
		Assert.Equal(content, FindSingle(content, "phone-number").Value);
	}

	[Fact]
	public void Detect_Phone_DateLikePrefixDoesNotSuppressMalformedDateShape()
	{
		const string content = "+2015-012-3456";

		Assert.Equal(content, FindSingle(content, "phone-number").Value);
	}

	[Theory]
	[InlineData("+79261234567")]
	[InlineData("+33687654321")]
	[InlineData("+1 (555) 867-5309")]
	public void Detect_Phone_NewFixtureGuardsPreserveOperationalNumbers(string content)
	{
		Assert.Equal(content, FindSingle(content, "phone-number").Value);
	}

	[Theory]
	[InlineData("+2147483647")]
	[InlineData("+2147483648")]
	[InlineData("+100000000")]
	public void Detect_Phone_RejectsNumericTypeBoundaries(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "phone-number");
	}

	[Fact]
	public void Detect_Phone_NumericBoundaryGuardPreservesOperationalNumber()
	{
		const string phone = "+79261234567";

		Assert.Equal(phone, FindSingle(phone, "phone-number").Value);
	}

	[Theory]
	[InlineData(
		"/* Copyright 1998 Marshall Kirk McKusick. All Rights Reserved.\n" +
		" * Further information can be obtained from:\n" +
		" * Berkeley, CA 94709-1608 +1-510-843-9542\n */")]
	[InlineData(
		"/* Copyright (c) 1990 Regents of The University of Michigan.\n" +
		" * Permission to use, copy, modify, and distribute this software is hereby granted.\n" +
		" * Research Systems Unix Group\n * +1-313-764-2278\n */")]
	public void Detect_Phone_RejectsLicenseBannerAttribution(string content)
	{
		Assert.DoesNotContain(Detect(content), static finding => finding.RuleId == "phone-number");
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
	[InlineData("legal/COPYRIGHT", "ivan.petrov@corp.internal", "email")]
	[InlineData("legal/NOTICE.txt", "+79261234567", "phone-number")]
	[InlineData("changes/update.patch", "+79261234567", "phone-number")]
	[InlineData("dependencies/package-lock.json", "1.2.3.5", "ipv4")]
	[InlineData("src/VersionSelector.php", "51.15.23.7", "ipv4")]
	[InlineData("relnotes/v1.34.0.md", "51.15.23.7", "ipv4")]
	[InlineData("composer.lock", "ivan.petrov@corp.local", "email")]
	[InlineData("build/publishing.kt", "ivan.petrov@corp.local", "email")]
	[InlineData("snapshots/manual.mdoc", "ivan.petrov@corp.local", "email")]
	[InlineData("docs/Framework.docset/page.html", "/home/maintainer", "local-user")]
	[InlineData("docs/undocumented.json", "/Users/jshier/Code", "local-user")]
	[InlineData("package.json", "ivan.petrov@corp.local", "email")]
	[InlineData("assets/logo.svg", "M0 1.13.37.99", "ipv4")]
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
			"local-user" => PrivateDataFeatureMask.LocalUser,
			_ => throw new ArgumentOutOfRangeException(nameof(excludedRuleId))
		};

		Assert.Equal(PrivateDataFeatureMask.None, features & excludedFeature);
		Assert.Equal(withoutPrescan, withPrescan);
	}

	[Fact]
	public void Detect_PathAwareRulesUseOnlyTheLastPathSegment()
	{
		const string email = "ivan.petrov@corp.internal";
		const string address = "51.15.23.7";
		var findings = Detect("CHANGELOG/LICENSE-archive/version/publish/README.md", $"{email} {address}");

		Assert.Contains(findings, finding => finding.RuleId == "email" && finding.Value == email);
		Assert.Contains(findings, finding => finding.RuleId == "ipv4" && finding.Value == address);
	}

	[Theory]
	[InlineData("docs/vulnerability.md")]
	[InlineData("docs/victory.md")]
	public void Detect_Ipv4_VersionedReleaseNotePatternRequiresVFollowedByDigit(string path)
	{
		Assert.Equal("51.15.23.7", FindSingle(path, "endpoint=51.15.23.7", "ipv4").Value);
	}

	[Theory]
	[InlineData("manual.mdoc.snapshot", "ivan.petrov@corp.internal")]
	[InlineData("manual.snapshot", ".Mtx ivan.petrov@corp.internal")]
	public void Detect_Email_MdocPoliciesRequireExactExtensionOrMacro(string path, string content)
	{
		Assert.Equal("ivan.petrov@corp.internal", FindSingle(path, content, "email").Value);
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
			("amy" + "@corp.io ben" + "@corp.io 93.184." + "216.34").AsSpan(),
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
