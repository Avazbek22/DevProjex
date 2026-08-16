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
	private const int MaximumIpv6TextLength = 45;
	private const int EmailOrder = -250;
	private const int Ipv6Order = -225;
	private const int Ipv4Order = -200;
	private const int LocalUserOrder = -175;
	private const int MacAddressOrder = -150;
	private const int PhoneNumberOrder = -125;

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
		"devuser"
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
		"devops",
		"ops",
		"owner"
	];

	private static readonly string[] FileLikeEmailTopLevelLabels =
	[
		"png", "jpg", "jpeg", "gif", "svg", "webp", "avif", "ico", "bmp", "tif", "tiff", "heic", "psd",
		"css", "scss", "less", "js", "mjs", "cjs", "jsx", "ts", "tsx", "map", "json", "xml", "yml", "yaml",
		"toml", "ini", "cfg", "conf", "lock", "cs", "csproj", "sln", "props", "targets", "py", "rb", "go", "rs",
		"java", "kt", "kts", "scala", "php", "swift", "c", "h", "cpp", "hpp", "cc", "hh", "sh", "bash", "zsh",
		"ps1", "psm1", "psd1", "bat", "cmd", "md", "markdown", "rst", "txt", "log", "pdf", "docx", "xlsx", "pptx",
		"csv", "tsv", "zip", "gz", "tgz", "tar", "bz2", "xz", "7z", "rar", "jar", "exe", "dll", "so", "dylib",
		"pdb", "nupkg", "snupkg", "dmg", "pkg", "msi", "msix", "deb", "rpm", "ttf", "otf", "woff", "woff2",
		"eot", "mp3", "mp4", "wav", "ogg", "flac", "mov", "avi", "mkv", "webm", "wasm", "bin", "iso", "sql",
		"db", "sqlite", "bak", "tmp"
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

		var features = ComputeFeatureMask(content, budget, cancellationToken);
		if (features == PrivateDataFeatureMask.None)
			return [];
		return DetectEnabledFeatures(content, features, budget, cancellationToken);
	}

	private static IReadOnlyList<DetectedSecret> DetectEnabledFeatures(
		ReadOnlySpan<char> content,
		PrivateDataFeatureMask features,
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
			DetectPhoneNumbers(content, findings, budget, cancellationToken);
		budget.Checkpoint(cancellationToken);
		return findings;
	}

	internal static IReadOnlyList<DetectedSecret> DetectWithoutPrescanForAnalysis(
		ReadOnlySpan<char> content) =>
		DetectEnabledFeatures(
			content,
			PrivateDataFeatureMask.All,
			new SecretFileInspectionBudget(),
			CancellationToken.None);

	internal static PrivateDataFeatureMask ComputeFeatureMaskForAnalysis(ReadOnlySpan<char> content) =>
		ComputeFeatureMask(content, new SecretFileInspectionBudget(), CancellationToken.None);

	private static PrivateDataFeatureMask ComputeFeatureMask(
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var result = PrivateDataFeatureMask.None;
		var numericDots = 0;
		var inNumericSequence = false;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			var character = content[index];
			if (character == '@')
				result |= PrivateDataFeatureMask.Email;
			if (character == '+' && index + 1 < content.Length && char.IsAsciiDigit(content[index + 1]))
				result |= PrivateDataFeatureMask.PhoneNumber;
			if (character == ':' && index + 1 < content.Length &&
			    (IsHex(content[index + 1]) || content[index + 1] == ':'))
			{
				result |= PrivateDataFeatureMask.Ipv6;
			}
			if (character is ':' or '-' && index >= 2 && index + 2 < content.Length &&
			    IsHex(content[index - 2]) && IsHex(content[index - 1]) &&
			    IsHex(content[index + 1]) && IsHex(content[index + 2]))
			{
				result |= PrivateDataFeatureMask.MacAddress;
			}

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

			if (TryFindLocalUserStart(content, index, out _))
			{
				result |= PrivateDataFeatureMask.LocalUser;
			}
		}
		return result;
	}

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

			if (localStart < absoluteAt && domainEnd > absoluteAt + 1 &&
			    (localStart == 0 ||
			     !char.IsLetterOrDigit(content[localStart - 1]) && content[localStart - 1] != '@') &&
			    (domainEnd == content.Length ||
			     !char.IsLetterOrDigit(content[domainEnd]) && content[domainEnd] != '@') &&
			    !IsEmailInUriAuthority(content, localStart) &&
			    IsEligibleEmailLocalPart(content[localStart..absoluteAt]) &&
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
		if (localPart.StartsWith("your", StringComparison.OrdinalIgnoreCase))
			return false;
		var plus = localPart.IndexOf('+');
		var mailbox = plus > 0 ? localPart[..plus] : localPart;
		if (ContainsEmailTestSegment(mailbox))
			return false;
		foreach (var candidate in NonPrivateEmailLocalParts)
		{
			if (mailbox.Equals(candidate, StringComparison.OrdinalIgnoreCase))
				return false;
		}
		return true;
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
		if (SecretDetectionTextPolicy.IsRfc2606DocumentationHost(domain))
			return false;
		var lastDot = domain.LastIndexOf('.');
		if (lastDot <= 0 || lastDot >= domain.Length - 2)
			return false;
		if (IsRetinaEmailDomain(domain) ||
		    IsFileLikeEmailTopLevelLabel(domain[(lastDot + 1)..]))
		{
			return false;
		}
		for (var index = lastDot + 1; index < domain.Length; index++)
		{
			if (!char.IsAsciiLetter(domain[index]))
				return false;
		}
		return HasValidDomainLabels(domain, budget, cancellationToken);
	}

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

	private static bool IsFileLikeEmailTopLevelLabel(ReadOnlySpan<char> topLevelLabel)
	{
		foreach (var extension in FileLikeEmailTopLevelLabels)
		{
			if (topLevelLabel.Equals(extension, StringComparison.OrdinalIgnoreCase))
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
			    (char.IsLetterOrDigit(content[start + length]) || content[start + length] == '.'))
			{
				continue;
			}
			if ((address & byte.MaxValue) != 0 &&
			    !HasIpv4VersionContext(content, start) &&
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

	private static bool HasIpv4VersionContext(ReadOnlySpan<char> content, int candidateStart)
	{
		var windowStart = Math.Max(0, candidateStart - Ipv4VersionContextWindowLength);
		for (var index = candidateStart - 1; index >= windowStart; index--)
		{
			if (content[index] is not ('\r' or '\n'))
				continue;
			windowStart = index + 1;
			break;
		}
		return content[windowStart..candidateStart].IndexOf(
			"version",
			StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool TryParseIpv4(ReadOnlySpan<char> content, out int length, out uint address)
	{
		address = 0;
		length = 0;
		for (var part = 0; part < 4; part++)
		{
			var digits = 0;
			var value = 0;
			while (length < content.Length && char.IsAsciiDigit(content[length]))
			{
				if (++digits > 3)
					return false;
				value = value * 10 + content[length++] - '0';
			}
			if (digits == 0 || value > byte.MaxValue)
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
		    bytes[0] == 0x3F && bytes[1] == 0xFF && (bytes[2] & 0xF0) == 0)
		{
			return false;
		}
		return true;
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
			if (userEnd == userStart)
				continue;

			if (IsNonPrivateMultiWordLocalUser(content, userStart, userEnd))
			{
				continue;
			}
			var value = content[userStart..userEnd];
			if (IsAsciiDigitsOnly(value) || IsNonPrivateLocalUser(value))
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
		    index > 0 && char.IsLetterOrDigit(content[index - 1]))
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
		foreach (var candidate in NonPrivateLocalUsers)
		{
			if (value.Equals(candidate, StringComparison.OrdinalIgnoreCase))
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
			if (IsUniformMac(candidate, '0') || IsUniformMac(candidate, 'F'))
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

	private static void DetectPhoneNumbers(
		ReadOnlySpan<char> content,
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
			    start > 0 && (char.IsLetterOrDigit(content[start - 1]) || content[start - 1] == '+'))
			{
				continue;
			}

			var end = start + 1;
			var digitCount = 0;
			while (end < content.Length && IsPhoneCharacter(content[end]))
			{
				CheckpointPeriodically(ref checkpoint, budget, cancellationToken);
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
			if (end - start > 20 || digitCount is < 8 or > 15 ||
			    end < content.Length && char.IsLetterOrDigit(content[end]) ||
			    IsDocumentedPhoneNumber(digits[..Math.Min(digitCount, digits.Length)]))
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

	private static bool IsDocumentedPhoneNumber(ReadOnlySpan<char> digits) =>
		digits.Length == 11 && digits[0] == '1' &&
		digits.Slice(4, 3).SequenceEqual("555") && digits.Slice(7, 2).SequenceEqual("01") ||
		digits.StartsWith("447700900", StringComparison.Ordinal) ||
		digits.StartsWith("442079460", StringComparison.Ordinal);

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
		char.IsAsciiDigit(character) || character is ' ' or '-' or '.' or '(' or ')';
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
