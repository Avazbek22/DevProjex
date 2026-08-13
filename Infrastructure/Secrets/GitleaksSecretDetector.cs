using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DevProjex.Application.Secrets;
using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Infrastructure.Secrets;

/// <summary>
/// Managed port of the pinned Gitleaks default configuration. The TOML stays verbatim-updatable;
/// this adapter only bridges RE2 syntax and the finding/allowlist semantics used by Gitleaks.
/// </summary>
public sealed class GitleaksSecretDetector : ISecretDetector
{
	public const string RulesVersion = "v8.30.1";
	public const int ExpectedRuleCount = 222;
	public const int ExpectedContentRuleCount = 221;
	public const string PathOnlyRuleId = "pkcs12-file";
	public const string ConfigurationSha256 = "0CEEB4F9C567F9F80EE05E8E37EEBA4646DF809F69C736A64D5B8B1398EB3E4C";
	private static readonly TimeSpan NonBacktrackingRegexTimeout = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan BacktrackingRegexTimeout = TimeSpan.FromMilliseconds(250);
	private const string ResourceSuffix = ".Secrets.Rules.gitleaks-v8.30.1.toml";
	private static readonly string EmbeddedConfigurationFileName = $"gitleaks-{RulesVersion}.toml";
	private const string GitleaksAllowSignature = "gitleaks:allow";
	private const string GenericApiKeyRuleId = "generic-api-key";
	private const string PrivateKeyRuleId = "private-key";
	// Reviewed override for the upstream private-key rule. The upstream body pattern accepts any
	// character, so in a file that merely mentions PEM markers - test fixtures, documentation -
	// a match can start at one marker and run across arbitrary source code to the next "KEY-----"
	// occurrence, redacting everything between as one giant secret. The override permits only
	// characters a PEM payload can contain (base64, armor headers, whitespace, and escaped
	// newlines inside string literals), bounds the body, and requires a real END marker. Real
	// keys still match byte-for-byte; marker mentions can no longer bridge unrelated text.
	private const string UpstreamPrivateKeyPattern =
		@"(?i)-----BEGIN[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----[\s\S-]{64,}?KEY(?: BLOCK)?-----";
	// The body bound must stay below the non-backtracking engine's automaton size limit while
	// still covering an RSA-8192 PEM body (~6.5K characters including newlines).
	private const string BoundedPrivateKeyPattern =
		@"(?i)-----BEGIN[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----" +
		@"[a-z0-9+/=\\\s:.,_-]{64,8192}?" +
		@"-----END[ A-Z0-9_-]{0,100}PRIVATE KEY(?: BLOCK)?-----";
	private const string TwitterBearerPrefix = "AAAAAAAAAAAAAAAAAAAAAA";
	private const string RegexWarmUpProbe =
		"apiKey = \"A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h\"; /src/config.json";
	private const string RegexWarmUpPath = "src/config.json";
	private static readonly WarmUpRule[] CommonWarmUpRules =
	[
		new(GenericApiKeyRuleId, RegexWarmUpProbe),
		new(
			"vault-service-token",
			"vaultToken = \"hvs.A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL4aS6dF8gH0jK2mN4qR6tU8wX0zB2cD4eF6hJ8kL0nP2rT4vX6zB7yQ\""),
		new(
			"curl-auth-user",
			"curl -u \"warmup-user:A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h\" https://example.invalid")
	];
	private static readonly string[] GenericApiKeySignals =
	[
		"key", "api", "token", "secret", "client", "passwd", "password", "auth", "access",
		"credential", "creds"
	];
	private readonly Lazy<CompiledConfiguration> _configuration;

	public GitleaksSecretDetector()
		: this(LoadEmbeddedConfiguration)
	{
	}

	internal GitleaksSecretDetector(Func<string> configurationLoader)
	{
		ArgumentNullException.ThrowIfNull(configurationLoader);
		_configuration = new Lazy<CompiledConfiguration>(
			() => Compile(configurationLoader()),
			LazyThreadSafetyMode.ExecutionAndPublication);
	}

	public int RuleCount => _configuration.Value.Rules.Count;
	// The "+pkb" marker records the reviewed private-key override so cached findings produced by
	// the unbounded upstream pattern are never mistaken for results of the bounded one.
	public string RulesIdentity => $"gitleaks:{RulesVersion}:{ConfigurationSha256}+pkb";

	public void WarmUp(CancellationToken cancellationToken = default)
	{
		try
		{
			var configuration = _configuration.Value;
			foreach (var allowlist in configuration.GlobalAllowlists)
			{
				cancellationToken.ThrowIfCancellationRequested();
				allowlist.WarmUp();
			}
			// Warming all provider-specific expressions allocates hundreds of megabytes and
			// delays the first result. This list contains only the generic rule plus expressions
			// measured as first-content costs on the fixed real-project profiles. Every other
			// provider rule stays lazy until its distinctive value shape is actually present.
			foreach (var warmUpRule in CommonWarmUpRules)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var rule = configuration.Rules.Single(candidate =>
					candidate.Id.Equals(warmUpRule.RuleId, StringComparison.Ordinal));
				if (rule.ContentRegex?.Value.IsMatch(warmUpRule.Probe) != true)
				{
					throw new SecretDetectionException(
						$"Warm-up probe no longer exercises rule '{warmUpRule.RuleId}'.");
				}
				_ = rule.PathRegex?.Value.IsMatch(RegexWarmUpPath);
				foreach (var allowlist in rule.Allowlists)
					allowlist.WarmUp(warmUpRule.Probe);
			}
		}
		catch (SecretDetectionException)
		{
			throw;
		}
		catch (RegexMatchTimeoutException exception)
		{
			throw new SecretDetectionException("Secret detector warm-up timed out.", exception);
		}
	}

	public bool ShouldInspectPath(string repositoryRelativePath)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		try
		{
			var normalizedPath = repositoryRelativePath.Replace('\\', '/');
			return ShouldInspectPath(_configuration.Value, normalizedPath);
		}
		catch (RegexMatchTimeoutException exception)
		{
			throw new SecretDetectionException("Secret path policy evaluation timed out.", exception);
		}
	}

	internal GitleaksCandidateStatistics InspectCandidates(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		var configuration = _configuration.Value;
		var normalizedPath = repositoryRelativePath.Replace('\\', '/');
		Span<ulong> candidates = stackalloc ulong[GetCandidateWordCount(configuration.Rules.Count)];
		configuration.KeywordPrefilter.FindCandidates(content, candidates, cancellationToken);
		var candidateCount = 0;
		foreach (var ruleOrder in EnumerateCandidateRuleOrders(candidates, configuration.Rules.Count))
		{
			var rule = configuration.Rules[ruleOrder];
			if (rule.ContentRegex is not null &&
			    rule.AppliesToPath(normalizedPath))
			{
				candidateCount++;
			}
		}

		return new GitleaksCandidateStatistics(candidateCount);
	}

	internal IReadOnlyList<string> InspectCandidateRuleIds(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		var configuration = _configuration.Value;
		var normalizedPath = repositoryRelativePath.Replace('\\', '/');
		Span<ulong> candidates = stackalloc ulong[GetCandidateWordCount(configuration.Rules.Count)];
		configuration.KeywordPrefilter.FindCandidates(content, candidates, cancellationToken);
		var ids = new List<string>();
		foreach (var ruleOrder in EnumerateCandidateRuleOrders(candidates, configuration.Rules.Count))
		{
			var rule = configuration.Rules[ruleOrder];
			if (rule.ContentRegex is not null && rule.AppliesToPath(normalizedPath))
				ids.Add(rule.Id);
		}
		return ids;
	}

	internal IReadOnlyList<string> InspectRunnableRuleIds(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		var configuration = _configuration.Value;
		var normalizedPath = repositoryRelativePath.Replace('\\', '/');
		Span<ulong> candidates = stackalloc ulong[GetCandidateWordCount(configuration.Rules.Count)];
		configuration.KeywordPrefilter.FindCandidates(content, candidates, cancellationToken);
		var ids = new List<string>();
		foreach (var ruleOrder in EnumerateCandidateRuleOrders(candidates, configuration.Rules.Count))
		{
			var rule = configuration.Rules[ruleOrder];
			if (rule.ContentRegex is null ||
			    rule.Id.Equals(GenericApiKeyRuleId, StringComparison.Ordinal) &&
			    !HasGenericApiKeyEvidence(content) ||
			    !HasRuleSpecificEvidence(rule.Id, content) ||
			    !rule.AppliesToPath(normalizedPath))
			{
				continue;
			}
			ids.Add(rule.Id);
		}
		return ids;
	}

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		Detect(
			repositoryRelativePath,
			content,
			new SecretFileInspectionBudget(),
			cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		ArgumentNullException.ThrowIfNull(budget);
		budget.Checkpoint(cancellationToken);
		if (content.Length == 0)
			return [];

		try
		{
			return DetectCore(repositoryRelativePath, content, budget, cancellationToken);
		}
		catch (SecretDetectionException)
		{
			throw;
		}
		catch (RegexMatchTimeoutException exception)
		{
			// Path and allowlist expressions use the same bounded engine as content rules.
			// Any timeout is a failed inspection, never permission to continue unredacted.
			throw new SecretDetectionException("Secret detection timed out.", exception);
		}
	}

	private IReadOnlyList<DetectedSecret> DetectCore(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{

		var configuration = _configuration.Value;
		var normalizedPath = repositoryRelativePath.Replace('\\', '/');
		if (!ShouldInspectPath(configuration, normalizedPath))
			return [];
		Span<ulong> candidateRules = stackalloc ulong[GetCandidateWordCount(configuration.Rules.Count)];
		configuration.KeywordPrefilter.FindCandidates(content, candidateRules, cancellationToken);

		var findings = new List<DetectedSecret>();
		foreach (var ruleOrder in EnumerateCandidateRuleOrders(candidateRules, configuration.Rules.Count))
		{
			var rule = configuration.Rules[ruleOrder];
			budget.Checkpoint(cancellationToken);
			if (rule.ContentRegex is null)
				continue;
			if (rule.Id.Equals(GenericApiKeyRuleId, StringComparison.Ordinal) &&
			    !HasGenericApiKeyEvidence(content))
			{
				continue;
			}
			if (!HasRuleSpecificEvidence(rule.Id, content))
				continue;
			// Value-shape gates are path-independent necessary conditions. Running them first
			// avoids constructing a provider rule's lazy path DFA for ordinary keyword noise.
			if (!rule.AppliesToPath(normalizedPath))
				continue;
			try
			{
				var contentRegex = rule.ContentRegex.Value;
				foreach (var valueMatch in contentRegex.EnumerateMatches(content))
				{
					budget.Checkpoint(cancellationToken);
					// ValueMatch deliberately omits capture groups. Re-running the expression over
					// the already bounded full-match slice keeps the full file allocation-free while
					// preserving the reviewed Gitleaks secretGroup semantics.
					var matchText = content.Slice(valueMatch.Index, valueMatch.Length).ToString();
					var captureMatch = contentRegex.Match(matchText);
					if (!captureMatch.Success ||
					    !TryExtractSecret(rule, captureMatch, out var secretGroup))
						continue;

					var line = GetContainingLine(content, valueMatch.Index, valueMatch.Length);
					if (line.Contains(GitleaksAllowSignature, StringComparison.Ordinal))
						continue;
					var secret = secretGroup.Value;
					if (rule.Entropy > 0 && CalculateShannonEntropy(secret) <= rule.Entropy)
						continue;

					var context = new AllowlistContext(
						normalizedPath,
						secret,
						matchText,
						line);
					if (configuration.GlobalAllowlists.Any(allowlist => allowlist.Allows(context)) ||
					    rule.Allowlists.Any(allowlist => allowlist.Allows(context)))
					{
						continue;
					}

					budget.RegisterFinding(cancellationToken);
					findings.Add(new DetectedSecret(
						rule.Id,
						checked(valueMatch.Index + secretGroup.Index),
						secretGroup.Length,
						secret,
						rule.Order));
				}
			}
			catch (RegexMatchTimeoutException exception)
			{
				throw new SecretDetectionException(
					$"Secret detection timed out for rule '{rule.Id}'.",
					exception);
			}
		}

		budget.Checkpoint(cancellationToken);
		return findings;
	}

	private static bool ShouldInspectPath(
		CompiledConfiguration configuration,
		string normalizedPath) =>
		!IsEmbeddedConfigurationPath(normalizedPath) &&
		!configuration.GlobalAllowlists.Any(
			allowlist => allowlist.AllowsWholeFileByPath(normalizedPath));

	private static bool IsEmbeddedConfigurationPath(string normalizedPath) =>
		normalizedPath.Equals(EmbeddedConfigurationFileName, StringComparison.OrdinalIgnoreCase) ||
		normalizedPath.EndsWith('/' + EmbeddedConfigurationFileName, StringComparison.OrdinalIgnoreCase);

	private static bool HasGenericApiKeyEvidence(ReadOnlySpan<char> content)
	{
		for (var delimiterStart = 0; delimiterStart < content.Length; delimiterStart++)
		{
			var delimiterLength = GetGenericDelimiterLength(content, delimiterStart);
			if (delimiterLength == 0)
				continue;

			if (!TryFindGenericApiKeyCandidate(
					content,
					delimiterStart + delimiterLength,
					out var candidate) ||
			    CalculateShannonEntropy(candidate) <= 3.5d)
				continue;

			// Values reject the overwhelming majority of source-code delimiters. Only a
			// plausible literal pays for the bounded key-vocabulary probe.
			var lineStart = content[..delimiterStart].LastIndexOf('\n') + 1;
			var keyWindowStart = Math.Max(lineStart, delimiterStart - 40);
			var keyWindow = content[keyWindowStart..delimiterStart];
			if (!HasCompatibleGenericKey(keyWindow))
				continue;
			// UserSecretsId is project metadata, and the pinned Gitleaks generic rule already
			// allowlists it. Recognising it before lazy regex construction preserves that
			// upstream decision without paying for the large generic allowlist on every csproj.
			if (!keyWindow.Contains("UserSecretsId", StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private static bool TryFindGenericApiKeyCandidate(
		ReadOnlySpan<char> content,
		int valueStart,
		out ReadOnlySpan<char> candidate)
	{
		// Gitleaks permits up to five separators before the captured value. Try each
		// legal boundary instead of consuming them greedily: '=' is both a separator
		// and a legal first character of the provider-neutral value grammar.
		for (var skipped = 0; skipped <= 5 && valueStart < content.Length; skipped++, valueStart++)
		{
			if (TryReadGenericApiKeyCandidate(content, valueStart, out candidate))
				return true;
			if (skipped == 5 ||
			    !char.IsWhiteSpace(content[valueStart]) &&
			    content[valueStart] is not ('=' or '\'' or '"' or '`'))
			{
				break;
			}
		}

		candidate = default;
		return false;
	}

	private static int GetGenericDelimiterLength(ReadOnlySpan<char> content, int start)
	{
		return content[start] switch
		{
			'=' => start + 1 < content.Length && content[start + 1] == '>' ? 2 : 1,
			'>' => 1,
			'|' when start + 1 < content.Length && content[start + 1] == '|' => 2,
			'?' when start + 1 < content.Length && content[start + 1] == '=' => 2,
			',' => 1,
			':' => GetColonDelimiterLength(content, start),
			_ => 0
		};
	}

	private static int GetColonDelimiterLength(ReadOnlySpan<char> content, int start)
	{
		var length = 1;
		while (length < 3 && start + length < content.Length && content[start + length] == ':')
			length++;
		if (start + length < content.Length && content[start + length] == '=')
			length++;
		return length;
	}

	private static bool HasCompatibleGenericKey(ReadOnlySpan<char> keyWindow)
	{
		var suffixEnd = keyWindow.Length;
		var punctuationCount = 0;
		while (suffixEnd > 0 && punctuationCount < 3 &&
		       (char.IsWhiteSpace(keyWindow[suffixEnd - 1]) || keyWindow[suffixEnd - 1] is '\'' or '"'))
		{
			suffixEnd--;
			punctuationCount++;
		}
		var key = keyWindow[..suffixEnd];
		foreach (var signal in GenericApiKeySignals)
		{
			var searchEnd = key.Length;
			while (searchEnd >= signal.Length)
			{
				var signalStart = key[..searchEnd].LastIndexOf(signal, StringComparison.OrdinalIgnoreCase);
				if (signalStart < 0)
					break;
				var suffix = key[(signalStart + signal.Length)..];
				if (suffix.Length <= 20 && IsGenericKeySuffix(suffix))
					return true;
				searchEnd = signalStart;
			}
		}
		return false;
	}

	private static bool IsGenericKeySuffix(ReadOnlySpan<char> suffix)
	{
		foreach (var character in suffix)
		{
			if (!char.IsWhiteSpace(character) &&
			    !char.IsLetterOrDigit(character) &&
			    character is not ('_' or '.' or '-'))
			{
				return false;
			}
		}
		return true;
	}

	private static bool TryReadGenericApiKeyCandidate(
		ReadOnlySpan<char> content,
		int valueStart,
		out ReadOnlySpan<char> candidate)
	{
		var wordEnd = valueStart;
		while (wordEnd < content.Length && wordEnd - valueStart <= 150 &&
		       IsGenericApiKeyCharacter(content[wordEnd]))
		{
			wordEnd++;
		}
		if (wordEnd - valueStart is >= 10 and <= 150 && IsGenericValueTerminator(content, wordEnd))
		{
			candidate = content[valueStart..wordEnd];
			return true;
		}

		var base64End = valueStart;
		while (base64End < content.Length && IsGenericBase64Character(content[base64End]))
			base64End++;
		var paddingStart = base64End;
		while (base64End < content.Length && base64End - paddingStart < 3 && content[base64End] == '=')
			base64End++;
		if (paddingStart - valueStart >= 12 && IsGenericValueTerminator(content, base64End))
		{
			candidate = content[valueStart..base64End];
			return true;
		}

		candidate = default;
		return false;
	}

	private static bool IsGenericValueTerminator(ReadOnlySpan<char> content, int index) =>
		index == content.Length ||
		content[index] is '`' or '\'' or '"' or ';' ||
		char.IsWhiteSpace(content[index]) ||
		content[index] == '\\' && index + 1 < content.Length && content[index + 1] is 'n' or 'r';

	private static bool IsGenericBase64Character(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '+' or '/';

	private static bool HasRuleSpecificEvidence(string ruleId, ReadOnlySpan<char> content) =>
		ruleId switch
		{
			"1password-secret-key" => HasOnePasswordSecretKeyEvidence(content),
			"1password-service-account-token" =>
				HasPrefixedRun(content, "ops_", 250, int.MaxValue, IsBase64Character),
			"cohere-api-token" => HasRun(content, 40, char.IsAsciiLetterOrDigit),
			"lob-pub-api-key" =>
				content.Contains("test_pub_", StringComparison.OrdinalIgnoreCase) ||
				content.Contains("live_pub_", StringComparison.OrdinalIgnoreCase),
			"sendgrid-api-token" =>
				HasPrefixedRun(content, "SG.", 66, 66, IsProviderTokenCharacter),
			"sentry-access-token" => HasRun(content, 64, char.IsAsciiHexDigit),
			"square-access-token" =>
				HasPrefixedRun(content, "EAAA", 22, 60, IsWordOrHyphen) ||
				HasPrefixedRun(content, "sq0atp-", 22, 60, IsWordOrHyphen),
			"telegram-bot-api-token" => HasTelegramTokenEvidence(content),
			"twitter-access-secret" => HasRun(content, 45, char.IsAsciiLetterOrDigit),
			"twitter-access-token" => HasTwitterAccessTokenEvidence(content),
			"twitter-api-key" => HasRun(content, 25, char.IsAsciiLetterOrDigit),
			"twitter-api-secret" => HasRun(content, 50, char.IsAsciiLetterOrDigit),
			"twitter-bearer-token" =>
				HasPrefixedRun(content, TwitterBearerPrefix, 80, 100, IsTwitterBearerCharacter),
			"vault-service-token" =>
				HasPrefixedRun(content, "hvs.", 90, 120, IsVaultTokenCharacter, IsGitleaksValueTerminator) ||
				HasPrefixedRun(content, "s.", 24, 24, char.IsAsciiLetterOrDigit, IsGitleaksValueTerminator),
			"twilio-api-key" => HasPrefixedRun(content, "SK", 32, 32, char.IsAsciiHexDigit),
			"jwt" => HasJwtEvidence(content),
			"yandex-access-token" => content.Contains("t1.", StringComparison.Ordinal),
			"yandex-api-key" =>
				HasPrefixedRun(
					content,
					"AQVN",
					35,
					38,
					IsWordOrHyphen,
					comparison: StringComparison.OrdinalIgnoreCase),
			"yandex-aws-access-token" =>
				HasPrefixedRun(
					content,
					"YC",
					38,
					38,
					IsWordOrHyphen,
					comparison: StringComparison.OrdinalIgnoreCase),
			"linear-client-secret" =>
				content.Contains("linear", StringComparison.OrdinalIgnoreCase) && HasHexRun(content, 32),
			"curl-auth-header" =>
				content.Contains("curl", StringComparison.Ordinal) &&
				(content.Contains("-H", StringComparison.Ordinal) ||
				 content.Contains("--header", StringComparison.Ordinal)),
			"curl-auth-user" =>
				content.Contains("curl", StringComparison.Ordinal) &&
				(content.Contains("-u", StringComparison.Ordinal) ||
				 content.Contains("--user", StringComparison.Ordinal)),
			"discord-api-token" => HasRun(content, 64, char.IsAsciiHexDigit),
			"discord-client-id" => HasRun(content, 18, char.IsAsciiDigit),
			"discord-client-secret" => HasRun(content, 32, IsWordHyphenOrEquals),
			"facebook-page-access-token" =>
				HasPrefixedRun(content, "EAAM", 100, int.MaxValue, char.IsAsciiLetterOrDigit) ||
				HasPrefixedRun(content, "EAAC", 100, int.MaxValue, char.IsAsciiLetterOrDigit),
			"octopus-deploy-api-key" =>
				HasPrefixedRun(content, "API-", 26, 26, char.IsAsciiLetterOrDigit),
			_ => true
		};

	private static bool HasPrefixedRun(
		ReadOnlySpan<char> content,
		string prefix,
		int minimumLength,
		int maximumLength,
		Func<char, bool> isAllowed,
		Func<char, bool>? isTerminator = null,
		StringComparison comparison = StringComparison.Ordinal)
	{
		var searchStart = 0;
		while (searchStart <= content.Length - prefix.Length - minimumLength)
		{
			var relativeStart = content[searchStart..].IndexOf(prefix, comparison);
			if (relativeStart < 0)
				return false;
			var runStart = searchStart + relativeStart + prefix.Length;
			var runLength = 0;
			while (runStart + runLength < content.Length &&
			       runLength <= maximumLength &&
			       isAllowed(content[runStart + runLength]))
			{
				runLength++;
			}
			if (runLength >= minimumLength && runLength <= maximumLength &&
			    (runStart + runLength == content.Length ||
			     isTerminator is null || isTerminator(content[runStart + runLength])))
				return true;
			searchStart = runStart;
		}
		return false;
	}

	private static bool HasOnePasswordSecretKeyEvidence(ReadOnlySpan<char> content)
	{
		var searchStart = 0;
		while (searchStart <= content.Length - 39)
		{
			var relativeStart = content[searchStart..].IndexOf("A3-", StringComparison.Ordinal);
			if (relativeStart < 0)
				return false;
			var start = searchStart + relativeStart;
			if ((start == 0 || !IsWordCharacter(content[start - 1])) &&
			    (MatchesOnePasswordSecretKey(content, start, middleWithSeparator: false, out var end) ||
			     MatchesOnePasswordSecretKey(content, start, middleWithSeparator: true, out end)) &&
			    (end == content.Length || !IsWordCharacter(content[end])))
			{
				return true;
			}
			searchStart = start + 3;
		}
		return false;
	}

	private static bool MatchesOnePasswordSecretKey(
		ReadOnlySpan<char> content,
		int start,
		bool middleWithSeparator,
		out int end)
	{
		var cursor = start + 3;
		if (!ConsumeUpperAlphaNumeric(content, ref cursor, 6) || !Consume(content, ref cursor, '-'))
		{
			end = cursor;
			return false;
		}
		if (middleWithSeparator)
		{
			if (!ConsumeUpperAlphaNumeric(content, ref cursor, 6) ||
			    !Consume(content, ref cursor, '-') ||
			    !ConsumeUpperAlphaNumeric(content, ref cursor, 5))
			{
				end = cursor;
				return false;
			}
		}
		else if (!ConsumeUpperAlphaNumeric(content, ref cursor, 11))
		{
			end = cursor;
			return false;
		}
		for (var segment = 0; segment < 3; segment++)
		{
			if (!Consume(content, ref cursor, '-') || !ConsumeUpperAlphaNumeric(content, ref cursor, 5))
			{
				end = cursor;
				return false;
			}
		}
		end = cursor;
		return true;
	}

	private static bool ConsumeUpperAlphaNumeric(ReadOnlySpan<char> content, ref int cursor, int length)
	{
		if (cursor > content.Length - length)
			return false;
		for (var index = 0; index < length; index++)
		{
			var character = content[cursor + index];
			if (!char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'Z'))
				return false;
		}
		cursor += length;
		return true;
	}

	private static bool Consume(ReadOnlySpan<char> content, ref int cursor, char expected)
	{
		if (cursor >= content.Length || content[cursor] != expected)
			return false;
		cursor++;
		return true;
	}

	private static bool HasRun(
		ReadOnlySpan<char> content,
		int requiredLength,
		Func<char, bool> isAllowed)
	{
		var runLength = 0;
		foreach (var character in content)
		{
			runLength = isAllowed(character) ? runLength + 1 : 0;
			if (runLength >= requiredLength)
				return true;
		}
		return false;
	}

	private static bool HasTelegramTokenEvidence(ReadOnlySpan<char> content)
	{
		for (var colon = 5; colon < content.Length - 35; colon++)
		{
			if (content[colon] != ':' || content[colon + 1] != 'A')
				continue;
			var digitStart = colon;
			while (digitStart > 0 && char.IsAsciiDigit(content[digitStart - 1]))
				digitStart--;
			if (colon - digitStart is < 5 or > 16)
				continue;
			var suffixLength = 0;
			while (colon + 2 + suffixLength < content.Length &&
			       suffixLength < 34 &&
			       IsWordOrHyphen(content[colon + 2 + suffixLength]))
			{
				suffixLength++;
			}
			if (suffixLength == 34)
				return true;
		}
		return false;
	}

	private static bool HasTwitterAccessTokenEvidence(ReadOnlySpan<char> content)
	{
		for (var hyphen = 15; hyphen < content.Length - 20; hyphen++)
		{
			if (content[hyphen] != '-')
				continue;
			var digitStart = hyphen;
			while (digitStart > 0 && char.IsAsciiDigit(content[digitStart - 1]))
				digitStart--;
			if (hyphen - digitStart is < 15 or > 25)
				continue;
			var suffixLength = 0;
			while (hyphen + 1 + suffixLength < content.Length &&
			       suffixLength < 40 &&
			       char.IsAsciiLetterOrDigit(content[hyphen + 1 + suffixLength]))
			{
				suffixLength++;
			}
			if (suffixLength >= 20)
				return true;
		}
		return false;
	}

	private static bool HasJwtEvidence(ReadOnlySpan<char> content)
	{
		var searchStart = 0;
		while (searchStart <= content.Length - 3)
		{
			var relativeSeparator = content[searchStart..].IndexOf(".ey", StringComparison.Ordinal);
			if (relativeSeparator < 0)
				return false;
			var separator = searchStart + relativeSeparator;
			var firstStart = separator;
			while (firstStart > 0 && char.IsAsciiLetterOrDigit(content[firstStart - 1]))
				firstStart--;
			if (separator - firstStart >= 19 &&
			    content[firstStart] == 'e' &&
			    firstStart + 1 < separator && content[firstStart + 1] == 'y')
			{
				return true;
			}
			searchStart = separator + 3;
		}
		return false;
	}

	private static bool HasHexRun(ReadOnlySpan<char> content, int requiredLength)
	{
		var runLength = 0;
		foreach (var character in content)
		{
			runLength = char.IsAsciiHexDigit(character) ? runLength + 1 : 0;
			if (runLength >= requiredLength)
				return true;
		}
		return false;
	}

	private static bool IsVaultTokenCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

	private static bool IsGitleaksValueTerminator(char character) =>
		character is '`' or '\'' or '"' or ';' or '\\' || char.IsWhiteSpace(character);

	private static bool IsWordCharacter(char character) =>
		SecretTokenBoundary.IsContinuation(character);

	private static bool IsBase64Character(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=';

	private static bool IsProviderTokenCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '=' or '_' or '-' or '.';

	private static bool IsWordOrHyphen(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '_' or '-';

	private static bool IsWordHyphenOrEquals(char character) =>
		IsWordOrHyphen(character) || character == '=';

	private static bool IsTwitterBearerCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character == '%';

	private static bool IsGenericApiKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '.' or '=' or '-';

	private static double CalculateShannonEntropy(ReadOnlySpan<char> value)
	{
		Span<int> frequencies = stackalloc int[128];
		foreach (var character in value)
		{
			if (character >= frequencies.Length)
				return CalculateShannonEntropy(value.ToString());
			frequencies[character]++;
		}

		var entropy = 0d;
		foreach (var frequency in frequencies)
		{
			if (frequency == 0)
				continue;
			var probability = (double)frequency / value.Length;
			entropy -= probability * Math.Log2(probability);
		}
		return entropy;
	}

	private static int GetCandidateWordCount(int ruleCount) => (ruleCount + 63) / 64;

	private static CandidateRuleOrderEnumerable EnumerateCandidateRuleOrders(
		ReadOnlySpan<ulong> candidates,
		int ruleCount) =>
		new(candidates, ruleCount);

	private static bool TryExtractSecret(
		CompiledRule rule,
		Match match,
		out Group secretGroup)
	{
		if (rule.SecretGroup > 0)
		{
			if (rule.SecretGroup >= match.Groups.Count || !match.Groups[rule.SecretGroup].Success)
			{
				secretGroup = match.Groups[0];
				return false;
			}
			secretGroup = match.Groups[rule.SecretGroup];
			return secretGroup.Length > 0;
		}

		for (var index = 1; index < match.Groups.Count; index++)
		{
			if (!match.Groups[index].Success || match.Groups[index].Length == 0)
				continue;
			secretGroup = match.Groups[index];
			return true;
		}

		secretGroup = match.Groups[0];
		return secretGroup.Length > 0;
	}

	private static string GetContainingLine(ReadOnlySpan<char> content, int matchStart, int matchLength)
	{
		var lineStart = content[..Math.Max(0, matchStart)].LastIndexOf('\n') + 1;
		var matchEnd = Math.Min(content.Length, matchStart + matchLength);
		var relativeLineEnd = content[matchEnd..].IndexOf('\n');
		var lineEnd = relativeLineEnd < 0 ? content.Length : matchEnd + relativeLineEnd;
		if (lineEnd > lineStart && content[lineEnd - 1] == '\r')
			lineEnd--;
		return content[lineStart..lineEnd].ToString();
	}

	internal static double CalculateShannonEntropy(string value)
	{
		if (value.Length == 0)
			return 0;
		var frequencies = new Dictionary<char, int>();
		foreach (var character in value)
			frequencies[character] = frequencies.GetValueOrDefault(character) + 1;

		var entropy = 0d;
		foreach (var frequency in frequencies.Values)
		{
			var probability = (double)frequency / value.Length;
			entropy -= probability * Math.Log2(probability);
		}
		return entropy;
	}

	internal static bool IsPathSufficientForAllowlist(
		bool requireAll,
		bool pathMatches,
		bool hasFindingCriteria) =>
		pathMatches && (!requireAll || !hasFindingCriteria);

	private static CompiledConfiguration Compile(string source)
	{
		var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\r', '\n')
			.TrimEnd('\n') + "\n";
		var configurationHash = Convert.ToHexString(
			SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSource)));
		if (!configurationHash.Equals(ConfigurationSha256, StringComparison.Ordinal))
		{
			throw new SecretDetectionException(
				$"The embedded Gitleaks configuration does not match the reviewed {RulesVersion} source.");
		}

		TomlTable root;
		try
		{
			root = TomlSerializer.Deserialize<TomlTable>(source) ??
			       throw new SecretDetectionException("The embedded Gitleaks configuration is empty.");
		}
		catch (SecretDetectionException)
		{
			throw;
		}
		catch (Exception exception)
		{
			throw new SecretDetectionException(
				"The embedded Gitleaks rule configuration is invalid.",
				exception);
		}

		if (!root.TryGetValue("rules", out var rulesValue) ||
		    rulesValue is not TomlTableArray ruleTables)
		{
			throw new SecretDetectionException("The embedded Gitleaks configuration contains no rules.");
		}

		var globalAllowlists = root.TryGetValue("allowlist", out var globalAllowlistValue) &&
		                       globalAllowlistValue is TomlTable globalAllowlist
			? new[] { CompileAllowlist(globalAllowlist) }
			: [];
		var rules = new List<CompiledRule>(ruleTables.Count);
		for (var order = 0; order < ruleTables.Count; order++)
			rules.Add(CompileRule(ruleTables[order], order));

		if (rules.Count != ExpectedRuleCount)
		{
			throw new SecretDetectionException(
				$"Expected {ExpectedRuleCount} Gitleaks rules from {RulesVersion}, but loaded {rules.Count}.");
		}
		if (rules.Select(static rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
			throw new SecretDetectionException("The Gitleaks configuration contains duplicate rule identifiers.");
		var pathOnlyRules = rules
			.Where(static rule => rule.ContentRegex is null)
			.Select(static rule => rule.Id)
			.ToArray();
		if (pathOnlyRules is not [PathOnlyRuleId] ||
		    rules.Count(static rule => rule.ContentRegex is not null) != ExpectedContentRuleCount)
		{
			throw new SecretDetectionException(
				"The reviewed Gitleaks content-rule boundary changed and requires an explicit port decision.");
		}

		return new CompiledConfiguration(
			rules,
			globalAllowlists,
			KeywordPrefilter.Build(rules));
	}

	private static CompiledRule CompileRule(TomlTable table, int order)
	{
		var id = GetRequiredString(table, "id");
		var patternSource = GetOptionalString(table, "regex");
		if (string.Equals(id, PrivateKeyRuleId, StringComparison.Ordinal))
		{
			if (!string.Equals(patternSource, UpstreamPrivateKeyPattern, StringComparison.Ordinal))
			{
				throw new SecretDetectionException(
					"The upstream private-key rule changed and its bounded override needs a new review.");
			}

			patternSource = BoundedPrivateKeyPattern;
		}

		var regex = patternSource is { Length: > 0 } pattern
			? CreateDeferredRegex(pattern, $"rule '{id}'")
			: null;
		var pathRegex = GetOptionalString(table, "path") is { Length: > 0 } path
			? CreateDeferredRegex(path, $"path for rule '{id}'")
			: null;
		var allowlists = table.TryGetValue("allowlists", out var allowlistValue) &&
		                 allowlistValue is TomlTableArray allowlistTables
			? allowlistTables.Select(CompileAllowlist).ToArray()
			: [];
		return new CompiledRule(
			id,
			regex,
			pathRegex,
			GetDouble(table, "entropy"),
			GetInt32(table, "secretGroup"),
			GetStringArray(table, "keywords"),
			allowlists,
			order);
	}

	// Provider detectors stay non-backtracking. Allowlists inspect only a path or an already
	// bounded finding context under the same hard timeout; using the interpreted engine here
	// avoids retaining tens of megabytes of lazy symbolic DFA state for exclusion predicates.
	private static CompiledAllowlist CompileAllowlist(TomlTable table) =>
		new(
			GetStringArray(table, "paths").Select(pattern => CreateDeferredRegex(
				pattern,
				"allowlist path",
				useNonBacktracking: false)).ToArray(),
			GetStringArray(table, "regexes").Select(pattern => CreateDeferredRegex(
				pattern,
				"allowlist expression",
				useNonBacktracking: false)).ToArray(),
			GetStringArray(table, "stopwords"),
			GetOptionalString(table, "regexTarget") switch
			{
				"match" => AllowlistRegexTarget.Match,
				"line" => AllowlistRegexTarget.Line,
				_ => AllowlistRegexTarget.Secret
			},
			string.Equals(GetOptionalString(table, "condition"), "AND", StringComparison.OrdinalIgnoreCase));

	private static Regex CompileRegex(string pattern, string context, bool useNonBacktracking)
	{
		var translated = pattern
			.Replace("[[:alnum:]]", "[A-Za-z0-9]", StringComparison.Ordinal)
			.Replace("(?P<", "(?<", StringComparison.Ordinal);
		try
		{
			var options = RegexOptions.CultureInvariant;
			if (useNonBacktracking)
				options |= RegexOptions.NonBacktracking;
			return new Regex(
				translated,
				options,
				useNonBacktracking ? NonBacktrackingRegexTimeout : BacktrackingRegexTimeout);
		}
		catch (ArgumentException exception)
		{
			throw new SecretDetectionException($"Invalid {context} expression.", exception);
		}
	}

	private static Lazy<Regex> CreateDeferredRegex(
		string pattern,
		string context,
		bool useNonBacktracking = true) =>
		new(
			() => CompileRegex(pattern, context, useNonBacktracking),
			LazyThreadSafetyMode.ExecutionAndPublication);

	private static string GetRequiredString(TomlTable table, string key) =>
		GetOptionalString(table, key) is { Length: > 0 } value
			? value
			: throw new SecretDetectionException($"A Gitleaks rule is missing '{key}'.");

	private static string? GetOptionalString(TomlTable table, string key) =>
		table.TryGetValue(key, out var value) ? value as string : null;

	private static double GetDouble(TomlTable table, string key) =>
		table.TryGetValue(key, out var value)
			? Convert.ToDouble(value, CultureInfo.InvariantCulture)
			: 0;

	private static int GetInt32(TomlTable table, string key) =>
		table.TryGetValue(key, out var value)
			? Convert.ToInt32(value, CultureInfo.InvariantCulture)
			: 0;

	private static string[] GetStringArray(TomlTable table, string key)
	{
		if (!table.TryGetValue(key, out var value) || value is not TomlArray array)
			return [];
		return array.OfType<string>().ToArray();
	}

	private static string LoadEmbeddedConfiguration()
	{
		var assembly = typeof(GitleaksSecretDetector).Assembly;
		var resourceName = assembly.GetManifestResourceNames()
			.SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
		if (resourceName is null)
			throw new SecretDetectionException("The embedded Gitleaks rule configuration was not found.");
		using var stream = assembly.GetManifestResourceStream(resourceName) ??
		                   throw new SecretDetectionException("The embedded Gitleaks rule configuration could not be opened.");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private sealed record CompiledConfiguration(
		IReadOnlyList<CompiledRule> Rules,
		IReadOnlyList<CompiledAllowlist> GlobalAllowlists,
		KeywordPrefilter KeywordPrefilter);

	private readonly record struct WarmUpRule(string RuleId, string Probe);

	private sealed record CompiledRule(
		string Id,
		Lazy<Regex>? ContentRegex,
		Lazy<Regex>? PathRegex,
		double Entropy,
		int SecretGroup,
		IReadOnlyList<string> Keywords,
		IReadOnlyList<CompiledAllowlist> Allowlists,
		int Order)
	{
		public bool AppliesToPath(string path) => PathRegex?.Value.IsMatch(path) ?? true;
	}

	/// <summary>
	/// Matches every configured keyword in one pass over the file. Running up to 222 separate
	/// substring searches would multiply memory bandwidth on large selected files; the automaton
	/// keeps the same case-insensitive candidate semantics with O(content + matches) work.
	/// </summary>
	private sealed class KeywordPrefilter
	{
		private readonly List<Node> _nodes;
		private readonly IReadOnlyList<int> _rulesWithoutKeywords;

		private KeywordPrefilter(List<Node> nodes, IReadOnlyList<int> rulesWithoutKeywords)
		{
			_nodes = nodes;
			_rulesWithoutKeywords = rulesWithoutKeywords;
		}

		public static KeywordPrefilter Build(IReadOnlyList<CompiledRule> rules)
		{
			var nodes = new List<Node> { new() };
			var rulesWithoutKeywords = new List<int>();
			foreach (var rule in rules)
			{
				if (rule.Keywords.Count == 0)
				{
					rulesWithoutKeywords.Add(rule.Order);
					continue;
				}

				foreach (var keyword in rule.Keywords)
				{
					var state = 0;
					foreach (var character in keyword)
					{
						var normalized = char.ToLowerInvariant(character);
						if (!nodes[state].Transitions.TryGetValue(normalized, out var next))
						{
							next = nodes.Count;
							nodes[state].Transitions.Add(normalized, next);
							nodes.Add(new Node());
						}
						state = next;
					}
					nodes[state].RuleOrders.Add(rule.Order);
				}
			}

			var queue = new Queue<int>();
			foreach (var child in nodes[0].Transitions.Values)
				queue.Enqueue(child);
			while (queue.TryDequeue(out var state))
			{
				foreach (var (character, next) in nodes[state].Transitions)
				{
					queue.Enqueue(next);
					var fallback = nodes[state].Failure;
					while (fallback != 0 && !nodes[fallback].Transitions.ContainsKey(character))
						fallback = nodes[fallback].Failure;
					if (nodes[fallback].Transitions.TryGetValue(character, out var target) && target != next)
						nodes[next].Failure = target;
					nodes[next].RuleOrders.AddRange(nodes[nodes[next].Failure].RuleOrders);
				}
			}

			return new KeywordPrefilter(nodes, rulesWithoutKeywords);
		}

		public void FindCandidates(
			ReadOnlySpan<char> content,
			Span<ulong> candidates,
			CancellationToken cancellationToken)
		{
			candidates.Clear();
			foreach (var ruleOrder in _rulesWithoutKeywords)
				MarkCandidate(candidates, ruleOrder);

			var state = 0;
			for (var index = 0; index < content.Length; index++)
			{
				if ((index & 0xFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var character = char.ToLowerInvariant(content[index]);
				while (state != 0 && !_nodes[state].Transitions.ContainsKey(character))
					state = _nodes[state].Failure;
				if (_nodes[state].Transitions.TryGetValue(character, out var next))
					state = next;
				foreach (var ruleOrder in _nodes[state].RuleOrders)
					MarkCandidate(candidates, ruleOrder);
			}
		}

		private static void MarkCandidate(Span<ulong> candidates, int ruleOrder) =>
			candidates[ruleOrder >> 6] |= 1UL << (ruleOrder & 63);

		private sealed class Node
		{
			public Dictionary<char, int> Transitions { get; } = [];
			public List<int> RuleOrders { get; } = [];
			public int Failure { get; set; }
		}
	}

	private readonly ref struct CandidateRuleOrderEnumerable
	{
		private readonly ReadOnlySpan<ulong> _candidates;
		private readonly int _ruleCount;

		public CandidateRuleOrderEnumerable(ReadOnlySpan<ulong> candidates, int ruleCount)
		{
			_candidates = candidates;
			_ruleCount = ruleCount;
		}

		public Enumerator GetEnumerator() => new(_candidates, _ruleCount);

		public ref struct Enumerator
		{
			private readonly ReadOnlySpan<ulong> _candidates;
			private readonly int _ruleCount;
			private int _wordIndex = -1;
			private ulong _remaining;

			public Enumerator(ReadOnlySpan<ulong> candidates, int ruleCount)
			{
				_candidates = candidates;
				_ruleCount = ruleCount;
			}

			public int Current { get; private set; }

			public bool MoveNext()
			{
				while (_remaining == 0)
				{
					_wordIndex++;
					if (_wordIndex >= _candidates.Length)
						return false;
					_remaining = _candidates[_wordIndex];
				}

				var bit = BitOperations.TrailingZeroCount(_remaining);
				_remaining &= _remaining - 1;
				Current = (_wordIndex << 6) + bit;
				return Current < _ruleCount;
			}
		}
	}

	private sealed record CompiledAllowlist(
		IReadOnlyList<Lazy<Regex>> Paths,
		IReadOnlyList<Lazy<Regex>> Regexes,
		IReadOnlyList<string> Stopwords,
		AllowlistRegexTarget RegexTarget,
		bool RequireAll)
	{
		public void WarmUp(string probe = RegexWarmUpProbe)
		{
			foreach (var path in Paths)
				_ = path.Value.IsMatch(RegexWarmUpPath);
			foreach (var regex in Regexes)
				_ = regex.Value.IsMatch(probe);
		}

		public bool AllowsPath(string path) => Paths.Any(regex => regex.Value.IsMatch(path));

		public bool AllowsWholeFileByPath(string path) =>
			IsPathSufficientForAllowlist(
				RequireAll,
				AllowsPath(path),
				Regexes.Count > 0 || Stopwords.Count > 0);

		public bool Allows(AllowlistContext context)
		{
			var target = RegexTarget switch
			{
				AllowlistRegexTarget.Match => context.Match,
				AllowlistRegexTarget.Line => context.Line,
				_ => context.Secret
			};
			if (!RequireAll)
			{
				return Paths.Count > 0 && AllowsPath(context.Path) ||
				       Stopwords.Count > 0 && Stopwords.Any(stopword =>
					       context.Secret.Contains(stopword, StringComparison.OrdinalIgnoreCase)) ||
				       Regexes.Count > 0 && Regexes.Any(regex => regex.Value.IsMatch(target));
			}

			var hasCriterion = false;
			if (Paths.Count > 0)
			{
				hasCriterion = true;
				if (!AllowsPath(context.Path))
					return false;
			}
			if (Stopwords.Count > 0)
			{
				hasCriterion = true;
				if (!Stopwords.Any(stopword =>
					    context.Secret.Contains(stopword, StringComparison.OrdinalIgnoreCase)))
				{
					return false;
				}
			}
			if (Regexes.Count > 0)
			{
				hasCriterion = true;
				if (!Regexes.Any(regex => regex.Value.IsMatch(target)))
					return false;
			}
			return hasCriterion;
		}
	}

	private sealed record AllowlistContext(
		string Path,
		string Secret,
		string Match,
		string Line);

	private enum AllowlistRegexTarget
	{
		Secret,
		Match,
		Line
	}
}

internal readonly record struct GitleaksCandidateStatistics(int CandidateRuleCount);
