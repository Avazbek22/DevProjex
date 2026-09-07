using System.Buffers;
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
	private static readonly SearchValues<char> GenericDelimiters = SearchValues.Create("=>|:?,");
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
			var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
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
		var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
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
		var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
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

	internal IReadOnlyList<string> InspectCandidateRuleIdsByLinearSearch(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		var configuration = _configuration.Value;
		var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
		var ids = new List<string>();
		foreach (var rule in configuration.Rules)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (rule.ContentRegex is null || !rule.AppliesToPath(normalizedPath))
				continue;
			var matches = rule.Keywords.Count == 0;
			foreach (var keyword in rule.Keywords)
			{
				if (!ContainsCaseFolded(content, keyword.AsSpan()))
					continue;
				matches = true;
				break;
			}
			if (matches)
			{
				ids.Add(rule.Id);
			}
		}
		return ids;
	}

	internal GitleaksKeywordPrefilterStatistics InspectKeywordPrefilterStatistics() =>
		_configuration.Value.KeywordPrefilter.GetStatistics();

	internal bool InspectRuleSpecificEvidence(string ruleId, ReadOnlySpan<char> content) =>
		HasRuleSpecificEvidence(ruleId, content);

	internal GitleaksRuleMatchProbe InspectRuleMatch(string ruleId, string content)
	{
		var rule = _configuration.Value.Rules.Single(candidate =>
			candidate.Id.Equals(ruleId, StringComparison.Ordinal));
		if (rule.ContentRegex?.Value.Match(content) is not { Success: true } match ||
		    !TryExtractSecret(rule, match, out var secret))
		{
			return default;
		}

		return new GitleaksRuleMatchProbe(
			IsMatch: true,
			match.Index,
			match.Length,
			secret.Index,
			secret.Length);
	}

	internal IReadOnlyList<string> InspectRunnableRuleIds(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		var configuration = _configuration.Value;
		var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
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

		var configuration = budget.RunRuleInitialization(() => _configuration.Value);
		foreach (var allowlist in configuration.GlobalAllowlists)
			budget.RunRuleInitialization(allowlist.EnsureCompiled);
		budget.Checkpoint(cancellationToken);
		var normalizedPath = PathUtility.NormalizeSeparators(repositoryRelativePath);
		if (!ShouldInspectPath(configuration, normalizedPath))
			return [];
		Span<ulong> candidateRules = stackalloc ulong[GetCandidateWordCount(configuration.Rules.Count)];
		configuration.KeywordPrefilter.FindCandidates(content, candidateRules, cancellationToken);

		var findings = new List<DetectedSecret>();
		var lineIndex = new LineRangeIndex(content);
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
			budget.RunRuleInitialization(rule.EnsurePathRegexCompiled);
			if (!rule.AppliesToPath(normalizedPath))
				continue;
			budget.RunRuleInitialization(rule.EnsureContentAndAllowlistsCompiled);
			budget.Checkpoint(cancellationToken);
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

					var lineRange = lineIndex.GetContainingLine(valueMatch.Index, valueMatch.Length);
					var line = content.Slice(lineRange.Start, lineRange.Length);
					if (line.Contains(GitleaksAllowSignature, StringComparison.Ordinal))
						continue;
					var secret = secretGroup.Value;
					if (rule.Entropy > 0 && CalculateShannonEntropy(secret) <= rule.Entropy)
						continue;

					var context = new AllowlistContext(
						normalizedPath,
						secret.AsSpan(),
						matchText.AsSpan(),
						line);
					if (Allows(configuration.GlobalAllowlists, context) ||
					    Allows(rule.Allowlists, context))
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
		var searchStart = 0;
		while (searchStart < content.Length)
		{
			var relativeDelimiter = content[searchStart..].IndexOfAny(GenericDelimiters);
			if (relativeDelimiter < 0)
				return false;
			var delimiterStart = searchStart + relativeDelimiter;
			searchStart = delimiterStart + 1;
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
			var lineStart = content[..delimiterStart].LastIndexOfAny('\r', '\n') + 1;
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

	private static bool ContainsCaseFolded(ReadOnlySpan<char> content, ReadOnlySpan<char> value)
	{
		if (value.Length == 0)
			return content.Length > 0;
		for (var offset = 0; offset <= content.Length - value.Length; offset++)
		{
			var matches = true;
			for (var index = 0; index < value.Length; index++)
			{
				if (char.ToLowerInvariant(content[offset + index]) ==
				    char.ToLowerInvariant(value[index]))
				{
					continue;
				}
				matches = false;
				break;
			}
			if (matches)
				return true;
		}
		return false;
	}

	private static bool HasRuleSpecificEvidence(string ruleId, ReadOnlySpan<char> content) =>
		ruleId switch
		{
			"authress-service-client-access-key" =>
				content.Contains(".acc_", StringComparison.Ordinal) ||
				content.Contains(".acc-", StringComparison.Ordinal),
			"databricks-api-token" =>
				HasPrefixedRun(content, "dapi", 32, 32, char.IsAsciiHexDigit),
			"gocardless-api-token" =>
				HasPrefixedRun(
					content,
					"live_",
					40,
					40,
					IsWordHyphenOrEquals,
					comparison: StringComparison.OrdinalIgnoreCase),
			"harness-api-key" => HasHarnessApiKeyEvidence(content),
			"intra42-client-secret" =>
				HasPrefixedRun(content, "s-s4t2ud-", 64, 64, char.IsAsciiHexDigit) ||
				HasPrefixedRun(content, "s-s4t2af-", 64, 64, char.IsAsciiHexDigit),
			"lob-api-key" =>
				HasPrefixedRun(
					content,
					"test_",
					35,
					35,
					char.IsAsciiHexDigit,
					comparison: StringComparison.OrdinalIgnoreCase) ||
				HasPrefixedRun(
					content,
					"live_",
					35,
					35,
					char.IsAsciiHexDigit,
					comparison: StringComparison.OrdinalIgnoreCase),
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
				HasPrefixedRun(
					content,
					TwitterBearerPrefix,
					80,
					100,
					IsTwitterBearerCharacter,
					comparison: StringComparison.OrdinalIgnoreCase),
			"vault-service-token" =>
				HasPrefixedRun(content, "hvs.", 90, 120, IsVaultTokenCharacter, IsGitleaksValueTerminator) ||
				HasPrefixedRun(content, "s.", 24, 24, char.IsAsciiLetterOrDigit, IsGitleaksValueTerminator),
			"twilio-api-key" => HasPrefixedRun(content, "SK", 32, int.MaxValue, char.IsAsciiHexDigit),
			"jwt" => HasJwtEvidence(content),
			"yandex-access-token" => content.Contains("t1.", StringComparison.OrdinalIgnoreCase),
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

	private static bool HasHarnessApiKeyEvidence(ReadOnlySpan<char> content) =>
		HasSegmentedToken(content, "pat.") || HasSegmentedToken(content, "sat.");

	private static bool HasSegmentedToken(ReadOnlySpan<char> content, string prefix)
	{
		var searchStart = 0;
		while (searchStart <= content.Length - prefix.Length - 68)
		{
			var relativeStart = content[searchStart..].IndexOf(prefix, StringComparison.Ordinal);
			if (relativeStart < 0)
				return false;
			var cursor = searchStart + relativeStart + prefix.Length;
			if (HasExactRun(content, ref cursor, 22, IsWordOrHyphen) &&
			    Consume(content, ref cursor, '.') &&
			    HasExactRun(content, ref cursor, 24, char.IsAsciiLetterOrDigit) &&
			    Consume(content, ref cursor, '.') &&
			    HasExactRun(content, ref cursor, 20, char.IsAsciiLetterOrDigit))
			{
				return true;
			}
			searchStart += relativeStart + prefix.Length;
		}
		return false;
	}

	private static bool HasExactRun(
		ReadOnlySpan<char> content,
		ref int cursor,
		int length,
		Func<char, bool> isAllowed)
	{
		if (cursor > content.Length - length)
			return false;
		for (var index = 0; index < length; index++)
		{
			if (!isAllowed(content[cursor + index]))
				return false;
		}
		cursor += length;
		return true;
	}

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
		character == '-' || IsRegexWordCharacter(character);

	private static bool IsRegexWordCharacter(char character) =>
		char.GetUnicodeCategory(character) is
			UnicodeCategory.UppercaseLetter or
			UnicodeCategory.LowercaseLetter or
			UnicodeCategory.TitlecaseLetter or
			UnicodeCategory.ModifierLetter or
			UnicodeCategory.OtherLetter or
			UnicodeCategory.NonSpacingMark or
			UnicodeCategory.DecimalDigitNumber or
			UnicodeCategory.ConnectorPunctuation;

	private static bool IsWordHyphenOrEquals(char character) =>
		IsWordOrHyphen(character) || character == '=';

	private static bool IsTwitterBearerCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character == '%';

	private static bool IsGenericApiKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '.' or '=' or '-';

	private static double CalculateShannonEntropy(ReadOnlySpan<char> value)
	{
		if (value.Length == 0)
			return 0;
		Span<int> frequencies = stackalloc int[128];
		foreach (var character in value)
		{
			if (character >= frequencies.Length)
				return CalculateNonAsciiShannonEntropy(value);
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

	private static bool Allows(IReadOnlyList<CompiledAllowlist> allowlists, AllowlistContext context)
	{
		foreach (var allowlist in allowlists)
		{
			if (allowlist.Allows(context))
				return true;
		}
		return false;
	}

	internal ref struct LineRangeIndex(ReadOnlySpan<char> content)
	{
		private const int IndexedContentThreshold = 64 * 1024;
		private readonly ReadOnlySpan<char> _content = content;
		private int[]? _lineStarts;
		private int _cachedStart = -1;
		private int _cachedEnd;
		private bool _cachedRangeIsSingleLine;

		public LineRange GetContainingLine(int matchStart, int matchLength)
		{
			var matchEnd = Math.Min(_content.Length, checked(matchStart + matchLength));
			if (_cachedRangeIsSingleLine &&
			    _cachedStart >= 0 &&
			    matchStart >= _cachedStart &&
			    matchEnd <= _cachedEnd)
				return new LineRange(_cachedStart, _cachedEnd - _cachedStart);

			LineRange range;
			if (_content.Length >= IndexedContentThreshold)
			{
				_lineStarts ??= BuildLineStarts(_content);
				var startLine = FindLine(_lineStarts, matchStart);
				var endLine = FindLine(_lineStarts, matchEnd);
				var start = _lineStarts[startLine];
				var end = endLine + 1 < _lineStarts.Length ? _lineStarts[endLine + 1] : _content.Length;
				while (end > start && _content[end - 1] is '\r' or '\n')
					end--;
				range = new LineRange(start, end - start);
			}
			else
			{
				var start = _content[..Math.Max(0, matchStart)].LastIndexOfAny('\r', '\n') + 1;
				var relativeEnd = _content[matchEnd..].IndexOfAny('\r', '\n');
				var end = relativeEnd < 0 ? _content.Length : matchEnd + relativeEnd;
				range = new LineRange(start, end - start);
			}

			_cachedStart = range.Start;
			_cachedEnd = range.End;
			_cachedRangeIsSingleLine = _content.Slice(range.Start, range.Length).IndexOfAny('\r', '\n') < 0;
			return range;
		}

		private static int[] BuildLineStarts(ReadOnlySpan<char> text)
		{
			var starts = new List<int> { 0 };
			for (var index = 0; index < text.Length; index++)
			{
				if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
					index++;
				else if (text[index] is not ('\r' or '\n'))
					continue;
				if (index + 1 < text.Length)
					starts.Add(index + 1);
			}
			return starts.ToArray();
		}

		private static int FindLine(int[] starts, int offset)
		{
			var found = Array.BinarySearch(starts, Math.Clamp(offset, 0, int.MaxValue));
			return found >= 0 ? found : Math.Max(0, ~found - 1);
		}
	}

	internal readonly record struct LineRange(int Start, int Length)
	{
		public int End => checked(Start + Length);
	}

	internal static double CalculateShannonEntropy(string value) =>
		CalculateShannonEntropy(value.AsSpan());

	private static double CalculateNonAsciiShannonEntropy(ReadOnlySpan<char> value)
	{
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
		public void EnsurePathRegexCompiled()
		{
			if (PathRegex is { IsValueCreated: false })
				_ = PathRegex.Value;
		}

		public void EnsureContentAndAllowlistsCompiled()
		{
			if (ContentRegex is { IsValueCreated: false })
				_ = ContentRegex.Value;
			foreach (var allowlist in Allowlists)
				allowlist.EnsureCompiled();
		}

		public bool AppliesToPath(string path) => PathRegex?.Value.IsMatch(path) ?? true;
	}

	/// <summary>
	/// Matches every configured keyword in one pass over the file. Running up to 222 separate
	/// substring searches would multiply memory bandwidth on large selected files; the automaton
	/// keeps the same case-insensitive candidate semantics with O(content + matches) work.
	/// </summary>
	private sealed class KeywordPrefilter
	{
		private readonly RuleMask[] _outputs;
		private readonly ushort[] _transitions;
		private readonly ushort[] _asciiSymbols;
		private readonly char[] _alphabet;
		private readonly RuleMask _rulesWithoutKeywords;
		private readonly int _transitionCount;

		private KeywordPrefilter(
			RuleMask[] outputs,
			ushort[] transitions,
			ushort[] asciiSymbols,
			char[] alphabet,
			RuleMask rulesWithoutKeywords,
			int transitionCount)
		{
			_outputs = outputs;
			_transitions = transitions;
			_asciiSymbols = asciiSymbols;
			_alphabet = alphabet;
			_rulesWithoutKeywords = rulesWithoutKeywords;
			_transitionCount = transitionCount;
		}

		public static KeywordPrefilter Build(IReadOnlyList<CompiledRule> rules)
		{
			if (rules.Count > RuleMask.Capacity)
				throw new SecretDetectionException(
					$"The keyword prefilter supports at most {RuleMask.Capacity} rules.");
			var nodes = new List<MutableNode> { new() };
			var rulesWithoutKeywords = default(RuleMask);
			foreach (var rule in rules)
			{
				if (rule.Keywords.Count == 0)
				{
					rulesWithoutKeywords = rulesWithoutKeywords.Add(rule.Order);
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
							nodes.Add(new MutableNode());
						}
						state = next;
					}
					nodes[state].Outputs = nodes[state].Outputs.Add(rule.Order);
				}
			}

			var queue = new Queue<int>();
			var breadthFirstOrder = new List<int>(nodes.Count - 1);
			foreach (var child in nodes[0].Transitions.Values)
				queue.Enqueue(child);
			while (queue.TryDequeue(out var state))
			{
				breadthFirstOrder.Add(state);
				foreach (var (character, next) in nodes[state].Transitions)
				{
					queue.Enqueue(next);
					var fallback = nodes[state].Failure;
					while (fallback != 0 && !nodes[fallback].Transitions.ContainsKey(character))
						fallback = nodes[fallback].Failure;
					if (nodes[fallback].Transitions.TryGetValue(character, out var target) && target != next)
						nodes[next].Failure = target;
					nodes[next].Outputs = nodes[next].Outputs.Or(nodes[nodes[next].Failure].Outputs);
				}
			}

			var alphabet = nodes.SelectMany(static node => node.Transitions.Keys)
				.Distinct().Order().ToArray();
			if (alphabet.Length > ushort.MaxValue)
				throw new SecretDetectionException("The keyword prefilter alphabet is too large.");
			var symbolByCharacter = alphabet
				.Select((character, index) => (character, index))
				.ToDictionary(static item => item.character, static item => checked((ushort)item.index));
			var asciiSymbols = new ushort[128];
			for (var character = 0; character < asciiSymbols.Length; character++)
			{
				if (symbolByCharacter.TryGetValue((char)character, out var symbol))
					asciiSymbols[character] = checked((ushort)(symbol + 1));
			}
			var transitionCount = 0;
			foreach (var node in nodes)
				transitionCount += node.Transitions.Count;
			var transitions = new ushort[checked(nodes.Count * alphabet.Length)];
			foreach (var (character, target) in nodes[0].Transitions)
				transitions[symbolByCharacter[character]] = checked((ushort)target);
			foreach (var state in breadthFirstOrder)
			{
				var row = state * alphabet.Length;
				var fallbackRow = nodes[state].Failure * alphabet.Length;
				for (var symbol = 0; symbol < alphabet.Length; symbol++)
				{
					transitions[row + symbol] = nodes[state].Transitions.TryGetValue(alphabet[symbol], out var target)
						? checked((ushort)target)
						: transitions[fallbackRow + symbol];
				}
			}
			return new KeywordPrefilter(
				nodes.Select(static node => node.Outputs).ToArray(),
				transitions,
				asciiSymbols,
				alphabet,
				rulesWithoutKeywords,
				transitionCount);
		}

		public void FindCandidates(
			ReadOnlySpan<char> content,
			Span<ulong> candidates,
			CancellationToken cancellationToken)
		{
			candidates.Clear();
			_rulesWithoutKeywords.Apply(candidates);

			var state = 0;
			for (var index = 0; index < content.Length; index++)
			{
				if ((index & 0xFFF) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				var symbol = FindSymbol(content[index]);
				if (symbol < 0)
				{
					state = 0;
					continue;
				}
				state = _transitions[state * _alphabet.Length + symbol];
				var outputs = _outputs[state];
				if (!outputs.IsEmpty)
					outputs.Apply(candidates);
			}
		}

		public GitleaksKeywordPrefilterStatistics GetStatistics()
		{
			var estimatedBytes =
				(long)_alphabet.Length * sizeof(char) +
				(long)_asciiSymbols.Length * sizeof(ushort) +
				(long)_outputs.Length * RuleMask.ByteSize +
				(long)_transitions.Length * sizeof(ushort);
			return new GitleaksKeywordPrefilterStatistics(
				_outputs.Length,
				_transitionCount,
				_alphabet.Length,
				estimatedBytes,
				(long)_outputs.Length * Math.Max(128, _alphabet.Length) * sizeof(int),
				(long)_outputs.Length * (char.MaxValue + 1L) * sizeof(int));
		}

		private int FindSymbol(char character)
		{
			char normalized;
			if (character < 128)
			{
				normalized = character is >= 'A' and <= 'Z'
					? (char)(character + ('a' - 'A'))
					: character;
			}
			else
			{
				normalized = char.ToLowerInvariant(character);
			}
			if (normalized < 128)
				return _asciiSymbols[normalized] - 1;
			return BinarySearch(_alphabet, normalized);
		}

		private static int BinarySearch(char[] values, char value)
		{
			var low = 0;
			var high = values.Length - 1;
			while (low <= high)
			{
				var middle = (low + high) >>> 1;
				var comparison = values[middle].CompareTo(value);
				if (comparison == 0) return middle;
				if (comparison < 0) low = middle + 1;
				else high = middle - 1;
			}
			return -1;
		}

		private sealed class MutableNode
		{
			public Dictionary<char, int> Transitions { get; } = [];
			public RuleMask Outputs { get; set; }
			public int Failure { get; set; }
		}

		private readonly record struct RuleMask(ulong Word0, ulong Word1, ulong Word2, ulong Word3)
		{
			public const int Capacity = 256;
			public const int ByteSize = 4 * sizeof(ulong);

			public RuleMask Add(int ruleOrder) => (ruleOrder >> 6) switch
			{
				0 => this with { Word0 = Word0 | 1UL << ruleOrder },
				1 => this with { Word1 = Word1 | 1UL << (ruleOrder & 63) },
				2 => this with { Word2 = Word2 | 1UL << (ruleOrder & 63) },
				3 => this with { Word3 = Word3 | 1UL << (ruleOrder & 63) },
				_ => throw new ArgumentOutOfRangeException(nameof(ruleOrder))
			};

			public RuleMask Or(RuleMask other) => new(
				Word0 | other.Word0,
				Word1 | other.Word1,
				Word2 | other.Word2,
				Word3 | other.Word3);

			public bool IsEmpty => (Word0 | Word1 | Word2 | Word3) == 0;

			public void Apply(Span<ulong> candidates)
			{
				if (candidates.Length > 0) candidates[0] |= Word0;
				if (candidates.Length > 1) candidates[1] |= Word1;
				if (candidates.Length > 2) candidates[2] |= Word2;
				if (candidates.Length > 3) candidates[3] |= Word3;
			}
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
		public void EnsureCompiled()
		{
			foreach (var path in Paths)
			{
				if (!path.IsValueCreated)
					_ = path.Value;
			}
			foreach (var regex in Regexes)
			{
				if (!regex.IsValueCreated)
					_ = regex.Value;
			}
		}

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
			ReadOnlySpan<char> target = RegexTarget switch
			{
				AllowlistRegexTarget.Match => context.Match,
				AllowlistRegexTarget.Line => context.Line,
				_ => context.Secret
			};
			if (!RequireAll)
			{
				return Paths.Count > 0 && AllowsPath(context.Path) ||
				       Stopwords.Count > 0 && ContainsAny(context.Secret, Stopwords) ||
				       Regexes.Count > 0 && MatchesAny(target, Regexes);
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
				if (!ContainsAny(context.Secret, Stopwords))
				{
					return false;
				}
			}
			if (Regexes.Count > 0)
			{
				hasCriterion = true;
				if (!MatchesAny(target, Regexes))
					return false;
			}
			return hasCriterion;
		}

		private static bool ContainsAny(ReadOnlySpan<char> value, IReadOnlyList<string> candidates)
		{
			foreach (var candidate in candidates)
			{
				if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		private static bool MatchesAny(ReadOnlySpan<char> value, IReadOnlyList<Lazy<Regex>> regexes)
		{
			foreach (var regex in regexes)
			{
				if (regex.Value.IsMatch(value))
					return true;
			}
			return false;
		}
	}

	private readonly ref struct AllowlistContext(
		string path,
		ReadOnlySpan<char> secret,
		ReadOnlySpan<char> match,
		ReadOnlySpan<char> line)
	{
		public string Path { get; } = path;
		public ReadOnlySpan<char> Secret { get; } = secret;
		public ReadOnlySpan<char> Match { get; } = match;
		public ReadOnlySpan<char> Line { get; } = line;
	}

	private enum AllowlistRegexTarget
	{
		Secret,
		Match,
		Line
	}
}

internal readonly record struct GitleaksCandidateStatistics(int CandidateRuleCount);

internal readonly record struct GitleaksKeywordPrefilterStatistics(
	int NodeCount,
	int TransitionCount,
	int AlphabetSize,
	long EstimatedStorageBytes,
	long DenseAlphabetStorageBytes,
	long DenseUnicodeStorageBytes);

internal readonly record struct GitleaksRuleMatchProbe(
	bool IsMatch,
	int MatchStart,
	int MatchLength,
	int SecretStart,
	int SecretLength);
