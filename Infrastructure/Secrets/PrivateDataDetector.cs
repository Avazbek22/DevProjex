using System.Net;
using System.Net.Sockets;
using DevProjex.Application.Secrets;

namespace DevProjex.Infrastructure.Secrets;

public sealed class PrivateDataDetector : ISecretDetector
{
	internal const string RulesVersion = "private-data-v1";
	private const int BudgetCheckpointMask = 0x3FF;
	private const int EmailUriContextWindowLength = 256;
	private const int Ipv4VersionContextWindowLength = 160;
	// Conventional source license banners fit in this window without requiring a whole-file rescan.
	private const int LicenseAttributionContextWindowLength = 4096;
	private const int MaximumIpv6TextLength = 45;
	private const int EmailOrder = -250;
	private const int Ipv6Order = -225;
	private const int Ipv4Order = -200;
	private const int LocalUserOrder = -175;
	private const int MacAddressOrder = -150;
	private const int PhoneNumberOrder = -125;

	private static readonly string[] Ipv4VersionContextKeywords =
	[
		"version", "tag", "release", "build", "packages", "sec", "section", "chapter", "ch.", "ch", "rfc",
		"jvms", "spec", "syntax", "$id:", ",v ", "paragraph", "clause", "kernel"
	];

	private static readonly string[] Ipv4StandardReferenceContextKeywords =
	[
		"c99", "c11", "c17", "c23", "c++", "iso/iec", "ieee", "posix", "standard", "specification", "sctp", "ibta",
		"papr", "dwarf", "oid", "see", "isodcl", "802.", "1003.", "revision", "classification",
		"mapping", "described", "sdm", "applicable", "supplement", "std", "acl", "capabilities"
	];

	private static readonly string[] Ipv4DisabledFilePrefixes =
	[
		"CHANGELOG", "HISTORY", "RELEASES", "RELEASE_NOTES", "RELEASE-NOTES"
	];

	private static readonly string[] Ipv4DisabledFileNames =
	[
		"Gemfile.lock", "Podfile.lock", "package-lock.json", "packages.lock.json", "yarn.lock",
		"pnpm-lock.yaml", "composer.lock", "Cargo.lock", "paket.lock", "mix.lock", "flake.lock",
		"pubspec.lock", "Package.resolved"
	];

	private static readonly string[] EmailDisabledFilePrefixes =
	[
		"LICENSE", "LICENCE", "NOTICE", "AUTHORS", "CONTRIBUTORS", "COPYING", "COPYRIGHT", "PATENTS",
		"THIRD-PARTY", "CITATION", "CODE_OF_CONDUCT", "SECURITY", "MAINTAINERS", "CODEOWNERS",
		"GOVERNANCE", "SUPPORT", "go-licenses"
	];

	private static readonly string[] EmailDisabledFileNames =
	[
		"composer.json", "package.json", "pyproject.toml", "pom.xml", "Cargo.toml"
	];

	private static readonly string[] EmailDisabledFileExtensions =
	[
		".gemspec", ".podspec", ".nuspec"
	];

	private static readonly string[] PopularEmailTopLevelLabels =
	[
		"com", "net", "org", "edu", "gov", "mil", "int", "arpa", "info", "biz", "name", "pro",
		"dev", "app", "xyz", "top", "site", "online", "tech", "store", "blog", "wiki", "club", "vip",
		"fun", "art", "one", "pub", "red", "run", "win", "bet", "bio", "buzz", "cafe", "city", "cloud",
		"codes", "cool", "date", "digital", "email", "life", "live", "love", "ltd", "media", "money",
		"ninja", "page", "party", "plus", "press", "rest", "rocks", "sale", "shop", "show", "social",
		"software", "solutions", "space", "studio", "systems", "team", "today", "tools", "video", "website",
		"work", "works", "world", "zone", "agency", "center", "company", "consulting", "design", "directory",
		"education", "energy", "expert", "finance", "fitness", "gallery", "group", "guru", "house", "institute",
		"international", "land", "management", "marketing", "network", "partners", "photography", "productions",
		"properties", "services", "support", "taxi", "technology", "training", "ventures", "engineering",
		"capital", "computer", "express", "foundation", "gmbh", "moe", "travel", "museum", "aero", "coop",
		"jobs", "mobi", "cat", "tel", "post", "asia", "xxx", "local", "lan", "internal", "corp", "intra", "home"
	];

	private static readonly string[] EmailAttributionKeywords =
	[
		"author", "maintainer", "copyright", "contributor", "credits", "thanks", "acknowledg",
		"created by", "modified by", "written by", "e-mail:", "email:", "send comments", "please send",
		"please forward", "please inform", "may be reached", "package_bugreport", "last edit:",
		"conversion to float by", "sending patches"
	];

	private static readonly string[] DocumentedMacAddresses =
	[
		"001122334455", "112233445566", "0123456789AB", "AABBCCDDEEFF", "01234567890A",
		"ABCDEF012345", "BCDEF0123456", "123456789ABC"
	];

	private static readonly string[] NonPrivateLocalUsers =
	[
		"Public",
		"Default",
		"Default User",
		"All Users",
		"user",
		"username",
		"example",
		"demo",
		"test",
		"runner",
		"runneradmin",
		"ContainerAdministrator",
		"ContainerUser",
		"vagrant",
		"jenkins",
		"root",
		"name",
		"yourname",
		"your-username",
		"your_username",
		"johndoe",
		"janedoe",
		"someuser",
		"me",
		"developer",
		"devuser",
		"vsts",
		"appveyor",
		"gitlab-runner",
		"circleci",
		"travis",
		"buildbot",
		"teamcity",
		"bamboo",
		"ubuntu",
		"ec2-user",
		"azureuser",
		"centos",
		"debian",
		"fedora",
		"rocky",
		"alpine",
		"arch",
		"core",
		"opc",
		"pi",
		"node",
		"git",
		"deploy",
		"app",
		"jovyan",
		"sagemaker-user",
		"postgres",
		"mysql",
		"docker",
		"WDAGUtilityAccount",
		"defaultuser0",
		"alice",
		"bob",
		"jdoe",
		"jsmith",
		"foo", "bar", "baz", "qux", "xxx", "abc", "abcd", "asd", "nobody", "guest", "anonymous",
		"unknown", "dummy", "sample", "placeholder", "userid", "staff", "student", "contoso", "acme",
		"new", "old", "index", "main", "page", "login", "register", "search", "css", "js", "img",
		"assets", "static", "api", "docs", "blog", "faq", "help", "dashboard", "settings", "profile",
		"code", "web", "flutteruser", "pwuser", "vscode", "www", "www-data", "html", "images", "media",
		"files", "data", "shared", "common", "private", "backup", "tmp", "temp", "cache", "logs", "log",
		"srv", "ftp", "sftp", "anon", "absolute", "relative", "opam", "linuxbrew", "brew", "nginx",
		"apache", "httpd", "redis", "mongo", "mongodb", "rabbitmq", "kafka", "elastic", "elasticsearch",
		"kibana", "grafana", "prometheus", "laravel", "symfony", "django", "rails", "flask", "spring",
		"deno", "bun", "zig", "dune", "cargo", "gradle", "maven", "composer"
	];

	private static readonly string[] NumberedLocalUserPlaceholderPrefixes =
	[
		"user", "test", "demo", "guest"
	];

	private static readonly string[] NonPrivateEmailLocalParts =
	[
		"git",
		"noreply",
		"no-reply",
		"your",
		"yours",
		"youremail",
		"your-email",
		"your_email",
		"your.email",
		"yourname",
		"your-name",
		"your_name",
		"name",
		"email",
		"mail",
		"someone",
		"somebody",
		"example",
		"sample",
		"demo",
		"test",
		"user",
		"username",
		"foo",
		"bar",
		"john",
		"jane",
		"john.doe",
		"jane.doe",
		"johndoe",
		"janedoe",
		"firstname.lastname",
		"first.last",
		"firstname",
		"lastname",
		"admin",
		"info",
		"support",
		"contact",
		"hello",
		"sales",
		"office",
		"help",
		"team",
		"feedback",
		"webmaster",
		"postmaster",
		"hostmaster",
		"abuse",
		"security",
		"privacy",
		"billing",
		"marketing",
		"hr",
		"careers",
		"jobs",
		"press",
		"legal",
		"notifications",
		"bot",
		"actions",
		"ci",
		"build",
		"donotreply",
		"do-not-reply",
		"to",
		"cc",
		"bcc",
		"from",
		"reply",
		"sender",
		"recipient",
		"joe",
		"devops",
		"ops",
		"owner",
		"dev",
		"developer",
		"qa",
		"staging",
		"testing"
	];

	public string RulesIdentity => RulesVersion;

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content, new SecretFileInspectionBudget(), cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(budget);
		budget.Checkpoint(cancellationToken);
		if (content.IsEmpty)
			return [];

		var fileRules = ResolveFileRules(repositoryRelativePath.AsSpan());
		var features = ComputeFeatureMask(content, fileRules, budget, cancellationToken);
		if (features == PrivateDataFeatureMask.None)
			return [];
		return DetectEnabledFeatures(content, features, fileRules, budget, cancellationToken);
	}

	private static IReadOnlyList<DetectedSecret> DetectEnabledFeatures(
		ReadOnlySpan<char> content,
		PrivateDataFeatureMask features,
		PrivateDataFileRules fileRules,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var findings = new List<DetectedSecret>();
		if ((features & PrivateDataFeatureMask.Email) != 0)
			DetectEmails(content, findings, budget, cancellationToken);
		if ((features & PrivateDataFeatureMask.Ipv4) != 0)
			DetectIpv4Addresses(content, findings, budget, cancellationToken);
		if ((features & PrivateDataFeatureMask.Ipv6) != 0)
			DetectIpv6Addresses(content, findings, budget, cancellationToken);
		if ((features & PrivateDataFeatureMask.MacAddress) != 0)
			DetectMacAddresses(content, findings, budget, cancellationToken);
		if ((features & PrivateDataFeatureMask.LocalUser) != 0)
			DetectLocalUsers(content, findings, budget, cancellationToken);
		if ((features & PrivateDataFeatureMask.PhoneNumber) != 0)
			DetectPhoneNumbers(
				content,
				fileRules.LeadingPlusIsDiffMarker,
				findings,
				budget,
				cancellationToken);
		budget.Checkpoint(cancellationToken);
		return findings;
	}

	internal static IReadOnlyList<DetectedSecret> DetectWithoutPrescanForAnalysis(
		ReadOnlySpan<char> content) =>
		DetectWithoutPrescanForAnalysis("data.txt", content);

	internal static IReadOnlyList<DetectedSecret> DetectWithoutPrescanForAnalysis(
		string repositoryRelativePath,
		ReadOnlySpan<char> content)
	{
		var fileRules = ResolveFileRules(repositoryRelativePath.AsSpan());
		return DetectEnabledFeatures(
			content,
			fileRules.EnabledFeatures,
			fileRules,
			new SecretFileInspectionBudget(),
			CancellationToken.None);
	}

	internal static PrivateDataFeatureMask ComputeFeatureMaskForAnalysis(ReadOnlySpan<char> content) =>
		ComputeFeatureMaskForAnalysis("data.txt", content);

	internal static PrivateDataFeatureMask ComputeFeatureMaskForAnalysis(
		string repositoryRelativePath,
		ReadOnlySpan<char> content)
	{
		var fileRules = ResolveFileRules(repositoryRelativePath.AsSpan());
		return ComputeFeatureMask(
			content,
			fileRules,
			new SecretFileInspectionBudget(),
			CancellationToken.None);
	}

	private static PrivateDataFeatureMask ComputeFeatureMask(
		ReadOnlySpan<char> content,
		PrivateDataFileRules fileRules,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var result = PrivateDataFeatureMask.None;
		var enabledFeatures = fileRules.EnabledFeatures;
		var scanEmail = (enabledFeatures & PrivateDataFeatureMask.Email) != 0;
		var scanPhoneNumber = (enabledFeatures & PrivateDataFeatureMask.PhoneNumber) != 0;
		var scanIpv4 = (enabledFeatures & PrivateDataFeatureMask.Ipv4) != 0;
		var scanIpv6 = (enabledFeatures & PrivateDataFeatureMask.Ipv6) != 0;
		var scanMacAddress = (enabledFeatures & PrivateDataFeatureMask.MacAddress) != 0;
		var scanLocalUser = (enabledFeatures & PrivateDataFeatureMask.LocalUser) != 0;
		var numericDots = 0;
		var inNumericSequence = false;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			var character = content[index];
			if (scanEmail && character == '@')
				result |= PrivateDataFeatureMask.Email;
			if (scanPhoneNumber && character == '+' && index + 1 < content.Length &&
			    char.IsAsciiDigit(content[index + 1]) &&
			    (!fileRules.LeadingPlusIsDiffMarker || !IsLineStart(content, index)))
			{
				result |= PrivateDataFeatureMask.PhoneNumber;
			}
			if (scanIpv6 && character == ':' && index + 1 < content.Length &&
			    (IsHex(content[index + 1]) || content[index + 1] == ':'))
			{
				result |= PrivateDataFeatureMask.Ipv6;
			}
			if (scanMacAddress && character is ':' or '-' && index >= 2 && index + 2 < content.Length &&
			    IsHex(content[index - 2]) && IsHex(content[index - 1]) &&
			    IsHex(content[index + 1]) && IsHex(content[index + 2]))
			{
				result |= PrivateDataFeatureMask.MacAddress;
			}

			if (scanIpv4)
			{
				if (char.IsAsciiDigit(character))
				{
					inNumericSequence = true;
				}
				else if (character == '.' && inNumericSequence &&
				         index + 1 < content.Length && char.IsAsciiDigit(content[index + 1]))
				{
					if (++numericDots >= 3)
						result |= PrivateDataFeatureMask.Ipv4;
					inNumericSequence = false;
				}
				else
				{
					numericDots = 0;
					inNumericSequence = false;
				}
			}

			if (scanLocalUser && TryFindLocalUserStart(content, index, out _))
			{
				result |= PrivateDataFeatureMask.LocalUser;
			}
		}
		return result;
	}

	private static PrivateDataFileRules ResolveFileRules(ReadOnlySpan<char> repositoryRelativePath)
	{
		var fileName = GetRepositoryFileName(repositoryRelativePath);
		var enabledFeatures = PrivateDataFeatureMask.All;
		if (fileName.Equals(".mailmap", StringComparison.OrdinalIgnoreCase) ||
		    StartsWithAny(fileName, EmailDisabledFilePrefixes) ||
		    EqualsAny(fileName, EmailDisabledFileNames) ||
		    EndsWithAny(fileName, EmailDisabledFileExtensions))
		{
			enabledFeatures &= ~(PrivateDataFeatureMask.Email | PrivateDataFeatureMask.PhoneNumber);
		}
		if (StartsWithAny(fileName, Ipv4DisabledFilePrefixes) || EqualsAny(fileName, Ipv4DisabledFileNames) ||
		    IsSvgAssetFile(fileName))
			enabledFeatures &= ~PrivateDataFeatureMask.Ipv4;
		if (fileName.Equals("undocumented.json", StringComparison.OrdinalIgnoreCase) ||
		    ContainsPathSegmentWithSuffix(repositoryRelativePath, ".docset"))
			enabledFeatures &= ~PrivateDataFeatureMask.LocalUser;

		var leadingPlusIsDiffMarker =
			fileName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) ||
			fileName.EndsWith(".diff", StringComparison.OrdinalIgnoreCase);
		return new PrivateDataFileRules(enabledFeatures, leadingPlusIsDiffMarker);
	}

	private static ReadOnlySpan<char> GetRepositoryFileName(ReadOnlySpan<char> repositoryRelativePath)
	{
		for (var index = repositoryRelativePath.Length - 1; index >= 0; index--)
		{
			if (repositoryRelativePath[index] is '/' or '\\')
				return repositoryRelativePath[(index + 1)..];
		}
		return repositoryRelativePath;
	}

	private static bool StartsWithAny(ReadOnlySpan<char> value, string[] prefixes)
	{
		foreach (var prefix in prefixes)
		{
			if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool EndsWithAny(ReadOnlySpan<char> value, string[] suffixes)
	{
		foreach (var suffix in suffixes)
		{
			if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool EqualsAny(ReadOnlySpan<char> value, string[] candidates)
	{
		foreach (var candidate in candidates)
		{
			if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool ContainsPathSegmentWithSuffix(ReadOnlySpan<char> path, string suffix)
	{
		var segmentStart = 0;
		for (var index = 0; index <= path.Length; index++)
		{
			if (index < path.Length && path[index] is not ('/' or '\\'))
				continue;
			if (path[segmentStart..index].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				return true;
			segmentStart = index + 1;
		}
		return false;
	}

	private static bool IsSvgAssetFile(ReadOnlySpan<char> fileName) =>
		fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
		fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
		fileName.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0;

	private static void DetectEmails(
		ReadOnlySpan<char> content,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		for (var at = content.IndexOf('@'); at >= 0;)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			var absoluteAt = at;
			var localStart = absoluteAt;
			while (localStart > 0 && IsEmailLocalCharacter(content[localStart - 1]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
				localStart--;
			}
			var domainEnd = absoluteAt + 1;
			while (domainEnd < content.Length && IsEmailDomainCharacter(content[domainEnd]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
				domainEnd++;
			}
			while (domainEnd > absoluteAt + 1 && content[domainEnd - 1] == '.')
				domainEnd--;
			var localPart = content[localStart..absoluteAt];
			var eligibleLocalPart = localStart > 0 && content[localStart - 1] == '\\' &&
			                        localPart.StartsWith("u003c", StringComparison.OrdinalIgnoreCase)
				? localPart["u003c".Length..]
				: localPart;

			if (localStart < absoluteAt && domainEnd > absoluteAt + 1 &&
			    (localStart == 0 ||
			     !char.IsLetterOrDigit(content[localStart - 1]) && content[localStart - 1] != '@') &&
			    (domainEnd == content.Length ||
			     !char.IsLetterOrDigit(content[domainEnd]) && content[domainEnd] != '@') &&
			    !IsEmailInUriAuthority(content, localStart) &&
			    !HasEmailCallOrLambdaSuffix(content, domainEnd) &&
			    !HasEmailAttributionContext(content, localStart) &&
			    !HasLicenseAttributionContext(content, localStart) &&
			    IsEligibleEmailLocalPart(eligibleLocalPart) &&
			    IsEligibleEmailDomain(
				    content[(absoluteAt + 1)..domainEnd],
				    budget,
				    cancellationToken))
			{
				AddFinding(
					content,
					localStart,
					domainEnd - localStart,
					"email",
					EmailOrder,
					findings,
					budget,
					cancellationToken);
			}

			var next = content[(absoluteAt + 1)..].IndexOf('@');
			if (next < 0)
				break;
			at = absoluteAt + 1 + next;
		}
	}

	private static bool IsEligibleEmailLocalPart(ReadOnlySpan<char> localPart)
	{
		if (!ContainsAsciiLetterOrDigit(localPart))
			return false;
		if (localPart.StartsWith("your", StringComparison.OrdinalIgnoreCase))
			return false;
		var plus = localPart.IndexOf('+');
		var mailbox = plus > 0 ? localPart[..plus] : localPart;
		if (mailbox.Length == 1)
			return false;
		if (IsUuidEmailLocalPart(mailbox))
			return false;
		if (ContainsEmailTestSegment(mailbox))
			return false;
		foreach (var candidate in NonPrivateEmailLocalParts)
		{
			if (mailbox.Equals(candidate, StringComparison.OrdinalIgnoreCase))
				return false;
		}
		return true;
	}

	private static bool ContainsAsciiLetterOrDigit(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (char.IsAsciiLetterOrDigit(character))
				return true;
		}
		return false;
	}

	private static bool IsUuidEmailLocalPart(ReadOnlySpan<char> value)
	{
		if (value.Length != 36)
			return false;
		for (var index = 0; index < value.Length; index++)
		{
			if (index is 8 or 13 or 18 or 23)
			{
				if (value[index] != '-')
					return false;
			}
			else if (!IsHex(value[index]))
			{
				return false;
			}
		}
		return true;
	}

	private static bool HasEmailCallOrLambdaSuffix(ReadOnlySpan<char> content, int domainEnd) =>
		domainEnd < content.Length && content[domainEnd] == '(' ||
		domainEnd < content.Length && content[domainEnd] == '{' ||
		domainEnd + 1 < content.Length && content[domainEnd] == ' ' && content[domainEnd + 1] == '{';

	private static bool HasEmailAttributionContext(ReadOnlySpan<char> content, int candidateStart)
	{
		var windowStart = Math.Max(0, candidateStart - Ipv4VersionContextWindowLength);
		for (var index = candidateStart - 1; index >= windowStart; index--)
		{
			if (content[index] is not ('\r' or '\n'))
				continue;
			windowStart = index + 1;
			break;
		}
		var context = content[windowStart..candidateStart];
		foreach (var keyword in EmailAttributionKeywords)
		{
			if (context.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
		}
		return HasNamedAttributionPrefix(context);
	}

	private static bool HasLicenseAttributionContext(ReadOnlySpan<char> content, int candidateStart)
	{
		if (!IsInsideSourceComment(content, candidateStart))
			return false;
		var windowStart = Math.Max(0, candidateStart - LicenseAttributionContextWindowLength);
		var context = content[windowStart..candidateStart];
		if (context.IndexOf("copyright", StringComparison.OrdinalIgnoreCase) < 0)
			return false;
		return context.IndexOf("all rights reserved", StringComparison.OrdinalIgnoreCase) >= 0 ||
		       context.IndexOf("permission to use", StringComparison.OrdinalIgnoreCase) >= 0 ||
		       context.IndexOf("permission is granted", StringComparison.OrdinalIgnoreCase) >= 0 ||
		       context.IndexOf("redistribution and use", StringComparison.OrdinalIgnoreCase) >= 0 ||
		       context.IndexOf("licensed under", StringComparison.OrdinalIgnoreCase) >= 0 ||
		       context.IndexOf("spdx-license-identifier", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool IsInsideSourceComment(ReadOnlySpan<char> content, int candidateStart)
	{
		var lineStart = candidateStart;
		while (lineStart > 0 && content[lineStart - 1] is not ('\r' or '\n'))
			lineStart--;
		var linePrefix = content[lineStart..candidateStart].TrimStart();
		if (linePrefix.StartsWith("//", StringComparison.Ordinal) ||
		    !linePrefix.IsEmpty && linePrefix[0] == '*' &&
		    (linePrefix.Length == 1 || linePrefix[1] == '*' || char.IsWhiteSpace(linePrefix[1])) ||
		    linePrefix.StartsWith("--", StringComparison.Ordinal))
		{
			return true;
		}

		var context = content[..candidateStart];
		var openComment = context.LastIndexOf("/*", StringComparison.Ordinal);
		return openComment >= 0 && openComment > context.LastIndexOf("*/", StringComparison.Ordinal);
	}

	private static bool HasNamedAttributionPrefix(ReadOnlySpan<char> context)
	{
		context = context.Trim();
		if (context.IsEmpty || context[^1] is not ('<' or '('))
			return false;
		context = context[..^1].TrimEnd();

		var wordCount = 0;
		for (var index = context.Length - 1; index >= 0;)
		{
			while (index >= 0 && char.IsWhiteSpace(context[index]))
				index--;
			var wordEnd = index + 1;
			while (index >= 0 && (char.IsLetter(context[index]) || context[index] is '.' or '-' or '\''))
				index--;
			if (wordEnd == index + 1)
				return false;
			if (!char.IsUpper(context[index + 1]))
				return false;
			if (++wordCount == 2)
				return true;
		}
		return false;
	}

	private static bool ContainsEmailTestSegment(ReadOnlySpan<char> mailbox)
	{
		var segmentStart = 0;
		for (var index = 0; index <= mailbox.Length; index++)
		{
			if (index < mailbox.Length && mailbox[index] is not ('.' or '_' or '-'))
				continue;
			var segment = mailbox[segmentStart..index];
			if (segment.Equals("test", StringComparison.OrdinalIgnoreCase) ||
			    segment.Equals("tests", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			segmentStart = index + 1;
		}
		return false;
	}

	private static bool IsEmailInUriAuthority(ReadOnlySpan<char> content, int localStart)
	{
		var windowStart = Math.Max(0, localStart - EmailUriContextWindowLength);
		for (var index = localStart - 1; index >= windowStart; index--)
		{
			if (content[index] is not ('\r' or '\n'))
				continue;
			windowStart = index + 1;
			break;
		}

		var prefix = content[windowStart..localStart];
		var slashPair = prefix.LastIndexOf("//", StringComparison.Ordinal);
		if (slashPair <= 0)
			return false;
		var authorityPrefix = prefix[(slashPair + 2)..];
		foreach (var character in authorityPrefix)
		{
			if (character is '/' or '\\' or '?' or '#' or '"' or '\'' or '<' or '>' ||
			    char.IsWhiteSpace(character))
			{
				return false;
			}
		}

		var colon = slashPair - 1;
		while (colon >= 0 && IsUriLiteralBridgeCharacter(prefix[colon]))
			colon--;
		if (colon <= 0 || prefix[colon] != ':')
			return false;
		var schemeEnd = colon;
		var schemeStart = schemeEnd;
		while (schemeStart > 0 && IsUriSchemeCharacter(prefix[schemeStart - 1]))
			schemeStart--;
		return schemeStart < schemeEnd &&
		       char.IsAsciiLetter(prefix[schemeStart]);
	}

	private static bool IsUriLiteralBridgeCharacter(char character) =>
		char.IsWhiteSpace(character) || character is '"' or '\'' or '+';

	private static bool IsUriSchemeCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.';

	private static bool IsEligibleEmailDomain(
		ReadOnlySpan<char> domain,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		budget.Checkpoint(cancellationToken);
		if (IsSshAlgorithmDomain(domain))
			return false;
		if (SecretDetectionTextPolicy.IsRfc2606DocumentationHost(domain))
			return false;
		var lastDot = domain.LastIndexOf('.');
		if (lastDot <= 0 || lastDot >= domain.Length - 2)
			return false;
		if (IsRetinaEmailDomain(domain) ||
		    SecretDetectionTextPolicy.IsFileLikeTopLevelLabel(domain[(lastDot + 1)..]))
		{
			return false;
		}
		var topLevelLabel = domain[(lastDot + 1)..];
		var hasUpper = false;
		var hasLower = false;
		for (var index = lastDot + 1; index < domain.Length; index++)
		{
			if (!char.IsAsciiLetter(domain[index]))
				return false;
			hasUpper |= char.IsAsciiLetterUpper(domain[index]);
			hasLower |= char.IsAsciiLetterLower(domain[index]);
		}
		if (hasUpper && hasLower || topLevelLabel.Length > 2 && !IsPopularEmailTopLevelLabel(topLevelLabel))
			return false;
		return HasValidDomainLabels(domain, budget, cancellationToken);
	}

	private static bool IsSshAlgorithmDomain(ReadOnlySpan<char> domain) =>
		domain.Equals("openssh.com", StringComparison.OrdinalIgnoreCase) ||
		domain.Equals("ssh.com", StringComparison.OrdinalIgnoreCase) ||
		domain.Equals("libssh.org", StringComparison.OrdinalIgnoreCase);

	private static bool IsRetinaEmailDomain(ReadOnlySpan<char> domain)
	{
		var firstDot = domain.IndexOf('.');
		if (firstDot < 2)
			return false;
		var firstLabel = domain[..firstDot];
		if (!char.IsAsciiLetter(firstLabel[^1]))
			return false;
		for (var index = 0; index < firstLabel.Length - 1; index++)
		{
			if (!char.IsAsciiDigit(firstLabel[index]))
				return false;
		}
		return true;
	}

	private static bool IsPopularEmailTopLevelLabel(ReadOnlySpan<char> topLevelLabel)
	{
		foreach (var candidate in PopularEmailTopLevelLabels)
		{
			if (topLevelLabel.Equals(candidate, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool HasValidDomainLabels(
		ReadOnlySpan<char> domain,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var labelStart = 0;
		for (var index = 0; index <= domain.Length; index++)
		{
			if ((index & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			if (index < domain.Length && domain[index] != '.')
				continue;
			var label = domain[labelStart..index];
			if (label.IsEmpty || label[0] == '-' || label[^1] == '-' ||
			    label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
				return false;
			labelStart = index + 1;
		}
		return true;
	}

	private static void DetectIpv4Addresses(
		ReadOnlySpan<char> content,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		for (var start = 0; start < content.Length; start++)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			if (!char.IsAsciiDigit(content[start]) ||
			    start > 0 && (char.IsLetterOrDigit(content[start - 1]) || content[start - 1] is '.' or ':'))
			{
				continue;
			}

			if (!TryParseIpv4(content[start..], out var length, out var address) ||
			    start + length < content.Length &&
			    (char.IsLetterOrDigit(content[start + length]) ||
			     content[start + length] == '.' && start + length + 1 < content.Length &&
			     char.IsAsciiDigit(content[start + length + 1])))
			{
				continue;
			}
			if ((address & byte.MaxValue) != 0 &&
			    !HasAdjacentVersionConstraintOperator(content, start) &&
			    !HasIpv4VersionContext(content, start, length, (byte)(address >> 24)) &&
			    !IsIpv4StandardSectionHeading(content, start, length, (byte)(address >> 24)) &&
			    !HasIpv4NakedFractionContext(content, start, length) &&
			    !HasIpv4HyphenatedPrefix(content, start) &&
			    !HasIpv4PrereleaseSuffix(content, start + length) &&
			    IsGlobalIpv4(address))
			{
				AddFinding(
					content,
					start,
					length,
					"ipv4",
					Ipv4Order,
					findings,
					budget,
					cancellationToken);
			}
			start += length - 1;
		}
	}

	private static bool HasAdjacentVersionConstraintOperator(ReadOnlySpan<char> content, int candidateStart)
	{
		var operatorEnd = candidateStart;
		if (operatorEnd > 0 && content[operatorEnd - 1] == ' ')
			operatorEnd--;
		return operatorEnd >= 2 &&
		       content[operatorEnd - 1] == '=' &&
		       content[operatorEnd - 2] is '=' or '>' or '<' or '~' or '!';
	}

	private static bool HasIpv4VersionContext(
		ReadOnlySpan<char> content,
		int candidateStart,
		int candidateLength,
		byte firstOctet)
	{
		var windowStart = Math.Max(0, candidateStart - Ipv4VersionContextWindowLength);
		for (var index = candidateStart - 1; index >= windowStart; index--)
		{
			if (content[index] is not ('\r' or '\n'))
				continue;
			windowStart = index + 1;
			break;
		}
		var candidateEnd = candidateStart + candidateLength;
		var windowEnd = Math.Min(content.Length, candidateEnd + Ipv4VersionContextWindowLength);
		for (var index = candidateEnd; index < windowEnd; index++)
		{
			if (content[index] is not ('\r' or '\n'))
				continue;
			windowEnd = index;
			break;
		}
		var prefix = content[windowStart..candidateStart];
		var suffix = content[candidateEnd..windowEnd];
		foreach (var keyword in Ipv4VersionContextKeywords)
		{
			if (ContainsIpv4VersionKeyword(prefix, keyword) || ContainsIpv4VersionKeyword(suffix, keyword))
				return true;
		}
		if (firstOctet <= 43)
		{
			foreach (var keyword in Ipv4StandardReferenceContextKeywords)
			{
				if (ContainsIpv4VersionKeyword(prefix, keyword) || ContainsIpv4VersionKeyword(suffix, keyword))
					return true;
			}
		}
		return false;
	}

	private static bool IsIpv4StandardSectionHeading(
		ReadOnlySpan<char> content,
		int candidateStart,
		int candidateLength,
		byte firstOctet)
	{
		if (candidateStart >= 2 && content[candidateStart - 2] == ']' && content[candidateStart - 1] == '/')
			return true;
		if (firstOctet > 43)
			return false;
		var candidateEnd = candidateStart + candidateLength;
		if (IsMarkdownSectionReference(content, candidateStart, candidateEnd))
			return true;

		var lineStart = candidateStart;
		while (lineStart > 0 && content[lineStart - 1] is not ('\r' or '\n'))
			lineStart--;
		var prefix = content[lineStart..candidateStart];
		var hasCommentMarker = false;
		foreach (var character in prefix)
		{
			if (char.IsWhiteSpace(character))
				continue;
			if (character is not ('/' or '*' or '#' or ';' or '-' or '!' or '['))
				return false;
			hasCommentMarker = true;
		}
		if (!hasCommentMarker)
			return false;

		var suffixStart = candidateEnd;
		if (content[suffixStart..].StartsWith("</a>", StringComparison.OrdinalIgnoreCase))
			return true;
		while (suffixStart < content.Length && content[suffixStart] is '.' or ',' or ':' or ')')
			suffixStart++;
		if (suffixStart >= content.Length || content[suffixStart] is '\r' or '\n')
			return true;
		if (!char.IsWhiteSpace(content[suffixStart]))
		{
			return false;
		}
		while (suffixStart < content.Length && content[suffixStart] is ' ' or '\t')
			suffixStart++;
		if (suffixStart >= content.Length || content[suffixStart] is '\r' or '\n')
			return true;
		if (content[suffixStart..].StartsWith("*/", StringComparison.Ordinal))
			return true;
		if (content[suffixStart] == '-')
		{
			suffixStart++;
			while (suffixStart < content.Length && content[suffixStart] is ' ' or '\t')
				suffixStart++;
		}
		return suffixStart < content.Length &&
		       (char.IsAsciiLetter(content[suffixStart]) || content[suffixStart] == '[' ||
		        content[suffixStart] == '(' && suffixStart + 1 < content.Length &&
		        char.IsAsciiDigit(content[suffixStart + 1]));
	}

	private static bool IsMarkdownSectionReference(
		ReadOnlySpan<char> content,
		int candidateStart,
		int candidateEnd)
	{
		if (candidateStart == 0 || content[candidateStart - 1] != '[' || candidateEnd >= content.Length)
			return false;
		if (content[candidateEnd..].StartsWith("](", StringComparison.Ordinal))
			return true;
		var suffix = content[candidateEnd..];
		return suffix.Length >= 3 && suffix[0] == '.' && char.IsWhiteSpace(suffix[1]) &&
		       char.IsAsciiLetterUpper(suffix[2]);
	}

	private static bool ContainsIpv4VersionKeyword(ReadOnlySpan<char> context, string keyword)
	{
		var searchStart = 0;
		while (searchStart <= context.Length - keyword.Length)
		{
			var relativeIndex = context[searchStart..].IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
			if (relativeIndex < 0)
				return false;
			var start = searchStart + relativeIndex;
			var end = start + keyword.Length;
			if (!RequiresIpv4KeywordBoundary(keyword) ||
			    (start == 0 || !char.IsLetter(context[start - 1])) &&
			    (end == context.Length || !char.IsLetter(context[end])))
			{
				return true;
			}
			searchStart = start + 1;
		}
		return false;
	}

	private static bool RequiresIpv4KeywordBoundary(ReadOnlySpan<char> keyword) =>
		keyword.Equals("sec", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("tag", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("rfc", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("ch.", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("ch", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("build", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("see", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("std", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("acl", StringComparison.OrdinalIgnoreCase) ||
		keyword.Equals("oid", StringComparison.OrdinalIgnoreCase);

	private static bool HasIpv4NakedFractionContext(ReadOnlySpan<char> content, int start, int length)
	{
		var windowStart = Math.Max(0, start - 32);
		var windowEnd = Math.Min(content.Length, start + length + 32);
		for (var index = start - 1; index >= windowStart; index--)
		{
			if (content[index] is '\r' or '\n')
			{
				windowStart = index + 1;
				break;
			}
		}
		for (var index = start + length; index < windowEnd; index++)
		{
			if (content[index] is '\r' or '\n')
			{
				windowEnd = index;
				break;
			}
		}
		for (var index = windowStart; index + 1 < windowEnd; index++)
		{
			if (content[index] == '.' &&
			    (index == windowStart || !char.IsAsciiDigit(content[index - 1])) &&
			    char.IsAsciiDigit(content[index + 1]))
			{
				return true;
			}
		}
		return false;
	}

	private static bool HasIpv4HyphenatedPrefix(ReadOnlySpan<char> content, int candidateStart) =>
		candidateStart >= 2 && content[candidateStart - 1] == '-' && char.IsLetter(content[candidateStart - 2]);

	private static bool HasIpv4PrereleaseSuffix(ReadOnlySpan<char> content, int candidateEnd) =>
		candidateEnd + 1 < content.Length && content[candidateEnd] == '-' && char.IsLetter(content[candidateEnd + 1]);

	private static bool TryParseIpv4(ReadOnlySpan<char> content, out int length, out uint address)
	{
		address = 0;
		length = 0;
		for (var part = 0; part < 4; part++)
		{
			var digits = 0;
			var value = 0;
			var partStart = length;
			while (length < content.Length && char.IsAsciiDigit(content[length]))
			{
				if (++digits > 3)
					return false;
				value = value * 10 + content[length++] - '0';
			}
			if (digits == 0 || value > byte.MaxValue || digits > 1 && content[partStart] == '0')
				return false;
			address = (address << 8) | (uint)value;
			if (part == 3)
				return true;
			if (length >= content.Length || content[length] != '.')
				return false;
			length++;
		}
		return false;
	}

	private static bool IsGlobalIpv4(uint address)
	{
		if (address is 0x08080808 or 0x08080404 or 0x01010101 or 0x01000001 or
		    0x09090909 or 0x95707070 or 0xD043DEDE or 0xD043DCDC)
		{
			return false;
		}
		var first = (byte)(address >> 24);
		var second = (byte)(address >> 16);
		var third = (byte)(address >> 8);
		var fourth = (byte)address;
		if (first == second && second == third && third == fourth ||
		    second == first + 1 && third == second + 1 && fourth == third + 1 ||
		    first == second + 1 && second == third + 1 && third == fourth + 1)
		{
			return false;
		}
		return !IsInIpv4Prefix(address, 0x00000000, 8) &&
		       !IsInIpv4Prefix(address, 0x7F000000, 8) &&
		       !IsInIpv4Prefix(address, 0xA9FE0000, 16) &&
		       !IsInIpv4Prefix(address, 0x0A000000, 8) &&
		       !IsInIpv4Prefix(address, 0xAC100000, 12) &&
		       !IsInIpv4Prefix(address, 0xC0A80000, 16) &&
		       !IsInIpv4Prefix(address, 0x64400000, 10) &&
		       !IsInIpv4Prefix(address, 0xC0000200, 24) &&
		       !IsInIpv4Prefix(address, 0xC6336400, 24) &&
		       !IsInIpv4Prefix(address, 0xCB007100, 24) &&
		       !IsInIpv4Prefix(address, 0xE9FC0000, 24) &&
		       !IsInIpv4Prefix(address, 0xC6120000, 15) &&
		       !IsInIpv4Prefix(address, 0x02050400, 24) &&
		       !IsInIpv4Prefix(address, 0x02050800, 24) &&
		       !IsInIpv4Prefix(address, 0x02051D00, 24) &&
		       !IsInIpv4Prefix(address, 0x01030600, 24) &&
		       !IsInIpv4Prefix(address, 0x02178C00, 24) &&
		       !IsInIpv4Prefix(address, 0xE0000000, 4) &&
		       !IsInIpv4Prefix(address, 0xF0000000, 4);
	}

	private static bool IsInIpv4Prefix(uint address, uint network, int prefixLength)
	{
		var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
		return (address & mask) == network;
	}

	private static void DetectIpv6Addresses(
		ReadOnlySpan<char> content,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		for (var start = 0; start < content.Length; start++)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			var bracketed = content[start] == '[';
			var addressStart = bracketed ? start + 1 : start;
			if (addressStart >= content.Length || !IsIpv6Start(content[addressStart]))
				continue;
			if (!bracketed && start > 0 && (char.IsLetterOrDigit(content[start - 1]) || content[start - 1] == ':'))
				continue;

			var addressEnd = addressStart;
			while (addressEnd < content.Length && IsIpv6CandidateCharacter(content[addressEnd]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
				addressEnd++;
			}
			var zoneStart = content[addressStart..addressEnd].IndexOf('%');
			var parseEnd = zoneStart < 0 ? addressEnd : addressStart + zoneStart;
			if (parseEnd <= addressStart || parseEnd - addressStart > MaximumIpv6TextLength ||
			    !ContainsAtLeastTwoColons(content[addressStart..parseEnd], budget, cancellationToken))
				continue;
			if (bracketed)
			{
				if (addressEnd >= content.Length || content[addressEnd] != ']')
					continue;
			}
			else if (addressEnd < content.Length &&
			         (char.IsLetterOrDigit(content[addressEnd]) || content[addressEnd] == ':'))
			{
				continue;
			}

			var candidate = content[addressStart..parseEnd];
			if (!ContainsAsciiDigit(candidate) ||
			    IsRstYearTarget(candidate) ||
			    !HasIpv6StructuralMinimum(candidate) ||
			    LooksLikeMacAddress(candidate) ||
			    !IPAddress.TryParse(candidate, out var address) ||
			    address.AddressFamily != AddressFamily.InterNetworkV6)
			{
				continue;
			}
			if (IsGlobalIpv6(address))
			{
				AddFinding(
					content,
					addressStart,
					parseEnd - addressStart,
					"ipv6",
					Ipv6Order,
					findings,
					budget,
					cancellationToken);
			}
			start = Math.Max(start, addressEnd - 1);
		}
	}

	private static bool IsRstYearTarget(ReadOnlySpan<char> candidate)
	{
		if (candidate.Length != 6 || candidate[4] != ':' || candidate[5] != ':')
			return false;
		var year = 0;
		for (var index = 0; index < 4; index++)
		{
			if (!char.IsAsciiDigit(candidate[index]))
				return false;
			year = year * 10 + candidate[index] - '0';
		}
		return year is >= 1900 and <= 2100;
	}

	private static bool HasIpv6StructuralMinimum(ReadOnlySpan<char> candidate)
	{
		var nonEmptyGroups = 0;
		var hasLongGroup = false;
		var groupStart = 0;
		for (var index = 0; index <= candidate.Length; index++)
		{
			if (index < candidate.Length && candidate[index] != ':')
				continue;
			var groupLength = index - groupStart;
			if (groupLength > 0)
			{
				nonEmptyGroups++;
				hasLongGroup |= groupLength >= 3;
			}
			groupStart = index + 1;
		}
		return nonEmptyGroups >= 4 || hasLongGroup;
	}

	private static bool IsGlobalIpv6(IPAddress address)
	{
		if (address.IsIPv4MappedToIPv6)
		{
			var mappedBytes = address.MapToIPv4().GetAddressBytes();
			var value = ((uint)mappedBytes[0] << 24) | ((uint)mappedBytes[1] << 16) |
			            ((uint)mappedBytes[2] << 8) | mappedBytes[3];
			return IsGlobalIpv4(value);
		}

		Span<byte> bytes = stackalloc byte[16];
		if (!address.TryWriteBytes(bytes, out var written) || written != 16)
			return false;
		var allZero = true;
		for (var index = 0; index < bytes.Length; index++)
			allZero &= bytes[index] == 0;
		if (allZero || bytes[..15].IndexOfAnyExcept((byte)0) < 0 && bytes[15] == 1)
			return false;
		if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80 ||
		    (bytes[0] & 0xFE) == 0xFC ||
		    bytes[0] == 0xFF ||
		    bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8 ||
		    bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0 ||
		    IsInIpv6Prefix(bytes, [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 96) ||
		    IsInIpv6Prefix(bytes, [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01], 48) ||
		    IsInIpv6Prefix(bytes, [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], 64) ||
		    IsInIpv6Prefix(bytes, [0x20, 0x01, 0x00, 0x00], 32) ||
		    IsInIpv6Prefix(bytes, [0x20, 0x01, 0x00, 0x10], 28) ||
		    IsInIpv6Prefix(bytes, [0x20, 0x01, 0x00, 0x20], 28) ||
		    IsInIpv6Prefix(bytes, [0x20, 0x02], 16) ||
		    IsInIpv6Prefix(bytes, [0x5F, 0x00], 16) ||
		    IsInIpv6Prefix(bytes, [0xFE, 0x00], 9) ||
		    IsInIpv6Prefix(bytes, [0xFE, 0xC0], 10))
		{
			return false;
		}
		return (bytes[0] & 0xE0) == 0x20;
	}

	private static bool IsInIpv6Prefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> network, int prefixLength)
	{
		var fullBytes = prefixLength / 8;
		if (!address[..fullBytes].SequenceEqual(network[..fullBytes]))
			return false;
		var remainingBits = prefixLength % 8;
		if (remainingBits == 0)
			return true;
		var mask = (byte)(byte.MaxValue << (8 - remainingBits));
		return (address[fullBytes] & mask) == (network[fullBytes] & mask);
	}

	private static void DetectLocalUsers(
		ReadOnlySpan<char> content,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		for (var index = 0; index < content.Length; index++)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			if (!TryFindLocalUserStart(content, index, out var userStart))
				continue;
			var userEnd = userStart;
			while (userEnd < content.Length && IsLocalUserSegmentCharacter(content[userEnd]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
				userEnd++;
			}
			if (userEnd - userStart <= 1 || content[userStart] == '.')
				continue;

			if (IsNonPrivateMultiWordLocalUser(content, userStart, userEnd))
			{
				continue;
			}
			var value = content[userStart..userEnd];
			var comparisonValue = TrimLocalUserPlaceholderSuffix(value);
			if (comparisonValue.IsEmpty || IsAsciiDigitsOnly(value) ||
			    IsFileLikeLocalUser(value) || IsNonPrivateLocalUser(comparisonValue))
				continue;
			AddFinding(
				content,
				userStart,
				userEnd - userStart,
				"local-user",
				LocalUserOrder,
				findings,
				budget,
				cancellationToken);
			index = userEnd - 1;
		}
	}

	private static bool TryFindLocalUserStart(ReadOnlySpan<char> content, int index, out int userStart)
	{
		userStart = 0;
		if (index + 2 < content.Length && char.IsAsciiLetter(content[index]) && content[index + 1] == ':' &&
		    (index == 0 || !char.IsLetterOrDigit(content[index - 1])))
		{
			var position = index + 2;
			if (!TryConsumeWindowsSeparator(content, ref position) ||
			    !content[position..].StartsWith("Users", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			position += "Users".Length;
			if (!TryConsumeWindowsSeparator(content, ref position))
				return false;
			userStart = position;
			return true;
		}

		if (content[index] != '/' ||
		    index > 0 && (char.IsLetterOrDigit(content[index - 1]) || content[index - 1] == '.'))
			return false;
		if (content[index..].StartsWith("/home/", StringComparison.Ordinal))
		{
			userStart = index + "/home/".Length;
			return true;
		}
		if (content[index..].StartsWith("/Users/", StringComparison.Ordinal))
		{
			userStart = index + "/Users/".Length;
			return true;
		}
		return false;
	}

	private static bool TryConsumeWindowsSeparator(ReadOnlySpan<char> content, ref int position)
	{
		if (position >= content.Length || content[position] is not ('\\' or '/'))
			return false;
		var separator = content[position++];
		if (position < content.Length && content[position] == separator)
			position++;
		return true;
	}

	private static bool IsNonPrivateLocalUser(ReadOnlySpan<char> value)
	{
		if (value.StartsWith("your", StringComparison.OrdinalIgnoreCase) ||
		    value.StartsWith("my", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (HasNumberedLocalUserPlaceholderPrefix(value))
			return true;
		foreach (var candidate in NonPrivateLocalUsers)
		{
			if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static ReadOnlySpan<char> TrimLocalUserPlaceholderSuffix(ReadOnlySpan<char> value)
	{
		while (!value.IsEmpty && value[^1] is '.' or '-' or '_')
			value = value[..^1];
		return value;
	}

	private static bool IsFileLikeLocalUser(ReadOnlySpan<char> value)
	{
		var separator = value.LastIndexOf('.');
		return separator > 0 && separator < value.Length - 1 &&
		       (SecretDetectionTextPolicy.IsFileLikeTopLevelLabel(value[(separator + 1)..]) ||
		        IsPopularEmailTopLevelLabel(value[(separator + 1)..]));
	}

	private static bool HasNumberedLocalUserPlaceholderPrefix(ReadOnlySpan<char> value)
	{
		foreach (var prefix in NumberedLocalUserPlaceholderPrefixes)
		{
			if (value.Length <= prefix.Length || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;
			if (IsAsciiDigitsOnly(value[prefix.Length..]))
				return true;
		}
		return false;
	}

	private static bool IsAsciiDigitsOnly(ReadOnlySpan<char> value)
	{
		if (value.IsEmpty)
			return false;
		foreach (var character in value)
		{
			if (!char.IsAsciiDigit(character))
				return false;
		}
		return true;
	}

	private static bool IsNonPrivateMultiWordLocalUser(
		ReadOnlySpan<char> content,
		int userStart,
		int firstWordEnd)
	{
		var candidateLength = content[userStart..].StartsWith(
			"Default User",
			StringComparison.OrdinalIgnoreCase)
			? "Default User".Length
			: content[userStart..].StartsWith("All Users", StringComparison.OrdinalIgnoreCase)
				? "All Users".Length
				: 0;
		if (candidateLength == 0 || firstWordEnd <= userStart)
			return false;
		var candidateEnd = userStart + candidateLength;
		return candidateEnd == content.Length ||
		       IsLocalPathSegmentTerminator(content[candidateEnd]);
	}

	private static void DetectMacAddresses(
		ReadOnlySpan<char> content,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		for (var start = 0; start + 17 <= content.Length; start++)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			if (!IsHex(content[start]) || start > 0 && char.IsLetterOrDigit(content[start - 1]))
				continue;
			var candidate = content.Slice(start, 17);
			if (!LooksLikeMacAddress(candidate))
				continue;
			var separator = candidate[2];
			if (start > 0 && content[start - 1] == separator)
				continue;
			if (start + 17 < content.Length &&
			    (char.IsLetterOrDigit(content[start + 17]) || content[start + 17] == separator))
			{
				continue;
			}
			if (IsUniformMac(candidate, '0') || IsUniformMac(candidate, 'F') ||
			    MacHexEndsWithTenZeros(candidate) ||
			    IsDocumentedMacAddress(candidate))
				continue;
			AddFinding(
				content,
				start,
				17,
				"mac-address",
				MacAddressOrder,
				findings,
				budget,
				cancellationToken);
			start += 16;
		}
	}

	private static bool LooksLikeMacAddress(ReadOnlySpan<char> value)
	{
		if (value.Length != 17 || value[2] is not (':' or '-'))
			return false;
		var separator = value[2];
		for (var index = 0; index < value.Length; index++)
		{
			if (index % 3 == 2)
			{
				if (value[index] != separator)
					return false;
			}
			else if (!IsHex(value[index]))
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsUniformMac(ReadOnlySpan<char> value, char expected)
	{
		for (var index = 0; index < value.Length; index++)
		{
			if (index % 3 != 2 && char.ToUpperInvariant(value[index]) != expected)
				return false;
		}
		return true;
	}

	private static bool IsDocumentedMacAddress(ReadOnlySpan<char> value)
	{
		foreach (var documentedAddress in DocumentedMacAddresses)
		{
			if (MacHexEquals(value, documentedAddress))
				return true;
		}
		return MacHexStartsWith(value, "DEADBEEF");
	}

	private static bool MacHexEndsWithTenZeros(ReadOnlySpan<char> value)
	{
		var hexIndex = 0;
		for (var index = 0; index < value.Length; index++)
		{
			if (index % 3 == 2)
				continue;
			if (hexIndex++ >= 2 && value[index] != '0')
				return false;
		}
		return true;
	}

	private static bool MacHexEquals(ReadOnlySpan<char> value, ReadOnlySpan<char> normalized) =>
		MacHexStartsWith(value, normalized) && normalized.Length == 12;

	private static bool MacHexStartsWith(ReadOnlySpan<char> value, ReadOnlySpan<char> normalizedPrefix)
	{
		var normalizedIndex = 0;
		for (var index = 0; index < value.Length && normalizedIndex < normalizedPrefix.Length; index++)
		{
			if (index % 3 == 2)
				continue;
			if (char.ToUpperInvariant(value[index]) != normalizedPrefix[normalizedIndex++])
				return false;
		}
		return normalizedIndex == normalizedPrefix.Length;
	}

	private static void DetectPhoneNumbers(
		ReadOnlySpan<char> content,
		bool leadingPlusIsDiffMarker,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpoint = 0;
		Span<char> digits = stackalloc char[15];
		for (var start = 0; start < content.Length - 1; start++)
		{
			CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
			if (content[start] != '+' || !char.IsAsciiDigit(content[start + 1]) ||
			    start > 0 && (char.IsLetterOrDigit(content[start - 1]) || content[start - 1] == '+') ||
			    leadingPlusIsDiffMarker && IsLineStart(content, start))
			{
				continue;
			}

			var end = start + 1;
			var digitCount = 0;
			while (end < content.Length && IsPhoneCharacter(content[end]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
				if (HasDisallowedPhoneSeparatorPair(content, end))
					break;
				if (char.IsAsciiDigit(content[end]))
				{
					if (digitCount < digits.Length)
						digits[digitCount] = content[end];
					digitCount++;
				}
				end++;
			}
			while (end > start + 1 && !char.IsAsciiDigit(content[end - 1]))
				end--;
			var normalizedDigits = digits[..Math.Min(digitCount, digits.Length)];
			if (end - start > 20 || digitCount is < 8 or > 15 ||
			    normalizedDigits[0] == '0' || IsDateLikePhoneNumber(content[start..end]) ||
			    end < content.Length && char.IsLetterOrDigit(content[end]) ||
			    end + 1 < content.Length && content[end] == '.' && char.IsAsciiDigit(content[end + 1]) ||
			    HasEmailAttributionContext(content, start) ||
			    HasLicenseAttributionContext(content, start) ||
			    IsDocumentedPhoneNumber(normalizedDigits))
			{
				continue;
			}
			AddFinding(
				content,
				start,
				end - start,
				"phone-number",
				PhoneNumberOrder,
				findings,
				budget,
				cancellationToken);
			start = end - 1;
		}
	}

	private static bool HasDisallowedPhoneSeparatorPair(ReadOnlySpan<char> content, int index) =>
		index > 0 && (content[index - 1], content[index]) is (' ', '-') or ('-', ' ') or (' ', ' ');

	private static bool IsDateLikePhoneNumber(ReadOnlySpan<char> candidate)
	{
		const int datePrefixLength = 11;
		if (candidate.Length < datePrefixLength ||
		    candidate[0] != '+' || candidate[5] != '-' || candidate[8] != '-')
			return false;
		for (var index = 1; index < datePrefixLength; index++)
		{
			if (index is 5 or 8)
				continue;
			if (!char.IsAsciiDigit(candidate[index]))
				return false;
		}
		var month = (candidate[6] - '0') * 10 + candidate[7] - '0';
		var day = (candidate[9] - '0') * 10 + candidate[10] - '0';
		return month is >= 1 and <= 12 && day is >= 1 and <= 31;
	}

	private static bool IsDocumentedPhoneNumber(ReadOnlySpan<char> digits) =>
		digits.Length == 11 && digits[0] == '1' &&
		digits.Slice(4, 3).SequenceEqual("555") && digits.Slice(7, 2).SequenceEqual("01") ||
		digits.IndexOf("55501", StringComparison.Ordinal) >= 0 ||
		digits.Length is >= 10 and <= 12 && digits[^7..^4].SequenceEqual("555") ||
		digits.StartsWith("447700900", StringComparison.Ordinal) ||
		digits.StartsWith("442079460", StringComparison.Ordinal) ||
		IsNumericTypeBoundary(digits) ||
		IsPlaceholderPhoneNumber(digits);

	private static bool IsNumericTypeBoundary(ReadOnlySpan<char> digits)
	{
		ulong value = 0;
		foreach (var digit in digits)
			value = value * 10 + (uint)(digit - '0');
		const ulong maximumBoundary = 1UL << 50;
		if (value <= maximumBoundary &&
		    (IsPowerOfTwo(value) || value < maximumBoundary && IsPowerOfTwo(value + 1)))
		{
			return true;
		}
		for (ulong powerOfTen = 10; powerOfTen <= maximumBoundary; powerOfTen *= 10)
		{
			if (value == powerOfTen)
				return true;
		}
		return false;
	}

	private static bool IsPowerOfTwo(ulong value) => value != 0 && (value & (value - 1)) == 0;

	private static bool IsPlaceholderPhoneNumber(ReadOnlySpan<char> digits)
	{
		return IsCyclicAscendingDigits(digits) ||
		       digits.Length > 1 && digits[0] == '1' && IsCyclicAscendingDigits(digits[1..]) ||
		       HasStrictAscendingDigitSuffix(digits, 8) ||
		       HasUniformDigitSuffix(digits, 7);
	}

	private static bool IsCyclicAscendingDigits(ReadOnlySpan<char> digits)
	{
		if (digits.Length < 2)
			return false;
		for (var index = 1; index < digits.Length; index++)
		{
			var previous = digits[index - 1];
			if (previous == '9')
			{
				if (digits[index] is not ('0' or '1'))
					return false;
			}
			else if (digits[index] != previous + 1)
			{
				return false;
			}
		}
		return true;
	}

	private static bool HasStrictAscendingDigitSuffix(ReadOnlySpan<char> digits, int minimumLength)
	{
		var length = 1;
		for (var index = digits.Length - 1; index > 0 && digits[index] == digits[index - 1] + 1; index--)
			length++;
		return length >= minimumLength && digits[^length] != '0';
	}

	private static bool HasUniformDigitSuffix(ReadOnlySpan<char> digits, int minimumLength)
	{
		var length = 1;
		for (var index = digits.Length - 1; index > 0 && digits[index] == digits[index - 1]; index--)
			length++;
		return length >= minimumLength;
	}

	private static bool ContainsAtLeastTwoColons(
		ReadOnlySpan<char> candidate,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var colonCount = 0;
		for (var index = 0; index < candidate.Length; index++)
		{
			if ((index & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			if (candidate[index] == ':' && ++colonCount == 2)
				return true;
		}
		return false;
	}

	private static bool ContainsAsciiDigit(ReadOnlySpan<char> candidate)
	{
		foreach (var character in candidate)
		{
			if (char.IsAsciiDigit(character))
				return true;
		}
		return false;
	}

	private static void AddFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string ruleId,
		int ruleOrder,
		List<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (length <= 0 || start < 0 || start > content.Length - length)
			return;
		var value = content.Slice(start, length);
		if (SecretDetectionTextPolicy.IsReferenceOrPlaceholder(content, start, length))
			return;
		budget.RegisterFinding(cancellationToken);
		findings.Add(new DetectedSecret(
			ruleId,
			start,
			length,
			value.ToString(),
			ruleOrder,
			Category: RedactionFindingCategory.PrivateData));
	}

	private static void CheckpointPeriodically(
		ref int counter,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if ((counter++ & BudgetCheckpointMask) == 0)
			budget.Checkpoint(cancellationToken);
	}

	private static bool IsEmailLocalCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '%' or '+' or '-';

	private static bool IsEmailDomainCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '.' or '-';

	private static bool IsHex(char character) => char.IsAsciiHexDigit(character);

	private static bool IsIpv6Start(char character) => IsHex(character) || character == ':';

	private static bool IsIpv6CandidateCharacter(char character) =>
		IsHex(character) || character is ':' or '.' || character == '%' ||
		char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

	private static bool IsLocalPathSegmentTerminator(char character) =>
		IsLocalPathSeparator(character) || character is '"' or '\'' || char.IsWhiteSpace(character);

	private static bool IsLocalUserSegmentCharacter(char character) =>
		char.IsLetter(character) || char.IsAsciiDigit(character) || character is '.' or '_' or '-';

	private static bool IsLocalPathSeparator(char character) => character is '\\' or '/';

	private static bool IsPhoneCharacter(char character) =>
		char.IsAsciiDigit(character) || character is ' ' or '-' or '(' or ')';

	private static bool IsLineStart(ReadOnlySpan<char> content, int index) =>
		index == 0 || content[index - 1] is '\r' or '\n';

	private readonly record struct PrivateDataFileRules(
		PrivateDataFeatureMask EnabledFeatures,
		bool LeadingPlusIsDiffMarker);
}

[Flags]
internal enum PrivateDataFeatureMask : byte
{
	None = 0,
	Email = 1 << 0,
	PhoneNumber = 1 << 1,
	Ipv4 = 1 << 2,
	Ipv6 = 1 << 3,
	MacAddress = 1 << 4,
	LocalUser = 1 << 5,
	All = Email | PhoneNumber | Ipv4 | Ipv6 | MacAddress | LocalUser
}
