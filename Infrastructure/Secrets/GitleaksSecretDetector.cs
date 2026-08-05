using System.Globalization;
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
	internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
	private const string ResourceSuffix = ".Secrets.Rules.gitleaks-v8.30.1.toml";
	private const string GitleaksAllowSignature = "gitleaks:allow";
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
	public string RulesIdentity => $"gitleaks:{RulesVersion}:{ConfigurationSha256}";

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(repositoryRelativePath);
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
			return [];

		try
		{
			return DetectCore(repositoryRelativePath, content, cancellationToken);
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
		CancellationToken cancellationToken)
	{

		var configuration = _configuration.Value;
		var normalizedPath = repositoryRelativePath.Replace('\\', '/');
		if (configuration.GlobalAllowlists.Any(allowlist => allowlist.AllowsWholeFileByPath(normalizedPath)))
			return [];
		var candidateRules = configuration.KeywordPrefilter.FindCandidates(
			content,
			configuration.Rules.Count,
			cancellationToken);

		var findings = new List<DetectedSecret>();
		foreach (var rule in configuration.Rules)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (rule.Regex is null ||
			    !rule.AppliesToPath(normalizedPath) ||
			    !candidateRules[rule.Order])
				continue;

			try
			{
				foreach (var valueMatch in rule.Regex.EnumerateMatches(content))
				{
					cancellationToken.ThrowIfCancellationRequested();
					// ValueMatch deliberately omits capture groups. Re-running the expression over
					// the already bounded full-match slice keeps the full file allocation-free while
					// preserving the reviewed Gitleaks secretGroup semantics.
					var matchText = content.Slice(valueMatch.Index, valueMatch.Length).ToString();
					var captureMatch = rule.Regex.Match(matchText);
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

		return findings;
	}

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
			.Where(static rule => rule.Regex is null)
			.Select(static rule => rule.Id)
			.ToArray();
		if (pathOnlyRules is not [PathOnlyRuleId] ||
		    rules.Count(static rule => rule.Regex is not null) != ExpectedContentRuleCount)
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
		var regex = GetOptionalString(table, "regex") is { Length: > 0 } pattern
			? CompileRegex(pattern, $"rule '{id}'")
			: null;
		var pathRegex = GetOptionalString(table, "path") is { Length: > 0 } path
			? CompileRegex(path, $"path for rule '{id}'")
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

	private static CompiledAllowlist CompileAllowlist(TomlTable table) =>
		new(
			GetStringArray(table, "paths").Select(pattern => CompileRegex(pattern, "allowlist path")).ToArray(),
			GetStringArray(table, "regexes").Select(pattern => CompileRegex(pattern, "allowlist expression")).ToArray(),
			GetStringArray(table, "stopwords"),
			GetOptionalString(table, "regexTarget") switch
			{
				"match" => AllowlistRegexTarget.Match,
				"line" => AllowlistRegexTarget.Line,
				_ => AllowlistRegexTarget.Secret
			},
			string.Equals(GetOptionalString(table, "condition"), "AND", StringComparison.OrdinalIgnoreCase));

	private static Regex CompileRegex(string pattern, string context)
	{
		var translated = pattern
			.Replace("[[:alnum:]]", "[A-Za-z0-9]", StringComparison.Ordinal)
			.Replace("(?P<", "(?<", StringComparison.Ordinal);
		try
		{
			return new Regex(
				translated,
				RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
				RegexTimeout);
		}
		catch (ArgumentException exception)
		{
			throw new SecretDetectionException($"Invalid {context} expression.", exception);
		}
	}

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

	private sealed record CompiledRule(
		string Id,
		Regex? Regex,
		Regex? PathRegex,
		double Entropy,
		int SecretGroup,
		IReadOnlyList<string> Keywords,
		IReadOnlyList<CompiledAllowlist> Allowlists,
		int Order)
	{
		public bool AppliesToPath(string path) => PathRegex?.IsMatch(path) ?? true;
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

		public bool[] FindCandidates(
			ReadOnlySpan<char> content,
			int ruleCount,
			CancellationToken cancellationToken)
		{
			var candidates = new bool[ruleCount];
			foreach (var ruleOrder in _rulesWithoutKeywords)
				candidates[ruleOrder] = true;

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
					candidates[ruleOrder] = true;
			}

			return candidates;
		}

		private sealed class Node
		{
			public Dictionary<char, int> Transitions { get; } = [];
			public List<int> RuleOrders { get; } = [];
			public int Failure { get; set; }
		}
	}

	private sealed record CompiledAllowlist(
		IReadOnlyList<Regex> Paths,
		IReadOnlyList<Regex> Regexes,
		IReadOnlyList<string> Stopwords,
		AllowlistRegexTarget RegexTarget,
		bool RequireAll)
	{
		public bool AllowsPath(string path) => Paths.Any(regex => regex.IsMatch(path));

		public bool AllowsWholeFileByPath(string path) =>
			IsPathSufficientForAllowlist(
				RequireAll,
				AllowsPath(path),
				Regexes.Count > 0 || Stopwords.Count > 0);

		public bool Allows(AllowlistContext context)
		{
			var pathMatches = AllowsPath(context.Path);
			var target = RegexTarget switch
			{
				AllowlistRegexTarget.Match => context.Match,
				AllowlistRegexTarget.Line => context.Line,
				_ => context.Secret
			};
			var regexMatches = Regexes.Any(regex => regex.IsMatch(target));
			var stopwordMatches = Stopwords.Any(stopword =>
				context.Secret.Contains(stopword, StringComparison.OrdinalIgnoreCase));
			if (!RequireAll)
				return pathMatches || regexMatches || stopwordMatches;

			var checks = new List<bool>(3);
			if (Paths.Count > 0)
				checks.Add(pathMatches);
			if (Regexes.Count > 0)
				checks.Add(regexMatches);
			if (Stopwords.Count > 0)
				checks.Add(stopwordMatches);
			return checks.Count > 0 && checks.All(static value => value);
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
