using System.Collections.Concurrent;
using System.Collections.Frozen;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Secrets;

/// <summary>
/// Combines provider-shaped Gitleaks rules with local, scope-aware configuration rules.
/// The structured tier deliberately favours recall: inside a recognised configuration shape,
/// a sensitive key is sufficient evidence even when the value is short or low-entropy.
/// </summary>
public sealed class SmartSecretsDetector(
	ISecretDetector providerDetector,
	SmartIgnoreService smartIgnore) : ISecretDetector
{
	internal const string StructuredRulesVersion = "smart-secrets-v4";

	public string RulesIdentity =>
		$"{providerDetector.RulesIdentity}:{StructuredRulesVersion}";

	public void WarmUp(CancellationToken cancellationToken = default) =>
		providerDetector.WarmUp(cancellationToken);

	public bool ShouldInspectPath(string repositoryRelativePath) =>
		ResolveEligibility(
			providerDetector.ShouldInspectPath(repositoryRelativePath),
			repositoryRelativePath) != SecretInspectionEligibility.None;

	public ISecretDetectionScope CreateScope(string projectRoot) =>
		new Scope(
			providerDetector.CreateScope(projectRoot),
			smartIgnore.CreateScopeResolver(projectRoot),
			RulesIdentity);

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
		CancellationToken cancellationToken = default) =>
		DetectEligible(
			providerDetector,
			repositoryRelativePath,
			content,
			ResolveEligibility(
				providerDetector.ShouldInspectPath(repositoryRelativePath),
				repositoryRelativePath),
			budget,
			cancellationToken);

	private static IReadOnlyList<DetectedSecret> DetectEligible(
		ISecretDetector provider,
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SecretInspectionEligibility eligibility,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (eligibility == SecretInspectionEligibility.None)
			return [];
		var providerFindings = (eligibility & SecretInspectionEligibility.Provider) != 0
			? provider.Detect(repositoryRelativePath, content, budget, cancellationToken)
			: [];
		var structuredFindings = (eligibility & SecretInspectionEligibility.Structured) != 0
			? StructuredSecretDetector.Detect(
				repositoryRelativePath,
				content,
				SmartSecretStack.None,
				budget,
				cancellationToken)
			: [];
		budget.Checkpoint(cancellationToken);
		return Combine(providerFindings, structuredFindings);
	}

	private static SecretInspectionEligibility ResolveEligibility(
		bool providerEligible,
		string repositoryRelativePath)
	{
		var eligibility = providerEligible
			? SecretInspectionEligibility.Provider
			: SecretInspectionEligibility.None;
		if (StructuredSecretDetector.ShouldInspectPath(repositoryRelativePath))
			eligibility |= SecretInspectionEligibility.Structured;
		return eligibility;
	}

	private static IReadOnlyList<DetectedSecret> Combine(
		IReadOnlyList<DetectedSecret> providerFindings,
		IReadOnlyList<DetectedSecret> structuredFindings)
	{
		if (providerFindings.Count == 0)
			return structuredFindings;
		if (structuredFindings.Count == 0)
			return providerFindings;

		var combined = new DetectedSecret[providerFindings.Count + structuredFindings.Count];
		for (var index = 0; index < structuredFindings.Count; index++)
			combined[index] = structuredFindings[index];
		for (var index = 0; index < providerFindings.Count; index++)
			combined[structuredFindings.Count + index] = providerFindings[index];
		return combined;
	}

	private sealed class Scope(
		ISecretDetectionScope providerScope,
		ISmartIgnoreScopeResolver scopeResolver,
		string rulesIdentity) : ISecretDetectionScope
	{
		private static readonly IReadOnlySet<string> AdditionalMarkerFiles =
			new[]
			{
				"compose.yml",
				"compose.yaml",
				"docker-compose.yml",
				"docker-compose.yaml"
			}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		private static readonly IReadOnlySet<string> AdditionalMarkerExtensions =
			new[] { ".tf" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentDictionary<string, ScopeContext> _contextByDirectory =
			new(PathComparer.Default);
		private readonly ScopeContext _unscopedContext =
			new(SmartSecretStack.None, $"{rulesIdentity}:scope-0");

		public string GetRulesIdentity(string fullPath, string repositoryRelativePath)
		{
			var fileKind = StructuredSecretDetector.ClassifyFile(repositoryRelativePath);
			return GetContext(fullPath, fileKind).RulesIdentity;
		}

		public bool ShouldInspectPath(string fullPath, string repositoryRelativePath) =>
			ResolveEligibility(
				providerScope.ShouldInspectPath(fullPath, repositoryRelativePath),
				repositoryRelativePath) != SecretInspectionEligibility.None;

		public IReadOnlyList<DetectedSecret> Detect(
			string fullPath,
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default) =>
			Detect(
				fullPath,
				repositoryRelativePath,
				content,
				new SecretFileInspectionBudget(),
				cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string fullPath,
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			SecretFileInspectionBudget budget,
			CancellationToken cancellationToken = default)
		{
			var eligibility = ResolveEligibility(
				providerScope.ShouldInspectPath(fullPath, repositoryRelativePath),
				repositoryRelativePath);
			if (eligibility == SecretInspectionEligibility.None)
				return [];

			var providerFindings = (eligibility & SecretInspectionEligibility.Provider) != 0
				? providerScope.Detect(
					fullPath,
					repositoryRelativePath,
					content,
					budget,
					cancellationToken)
				: [];
			var fileKind = StructuredSecretDetector.ClassifyFile(repositoryRelativePath);
			var stack = GetContext(fullPath, fileKind).Stack;
			var structuredFindings = (eligibility & SecretInspectionEligibility.Structured) != 0
				? StructuredSecretDetector.Detect(
					repositoryRelativePath,
					content,
					stack,
					fileKind,
					budget,
					cancellationToken)
				: [];
			budget.Checkpoint(cancellationToken);
			return Combine(providerFindings, structuredFindings);
		}

		private ScopeContext GetContext(
			string fullPath,
			StructuredSecretDetector.StructuredSecretFileKind fileKind)
		{
			// URI and connection-string probes are globally applicable and need no project
			// facts. Avoiding scope resolution for ordinary source files removes a filesystem
			// fixed cost from the overwhelmingly common path.
			if (!StructuredSecretDetector.UsesScopedVocabulary(fileKind))
				return _unscopedContext;
			var directory = Path.GetDirectoryName(Path.GetFullPath(fullPath)) ?? string.Empty;
			return _contextByDirectory.GetOrAdd(directory, _ => ResolveContext(fullPath));
		}

		private ScopeContext ResolveContext(string fullPath)
		{
			var resolution = scopeResolver.ResolveFileOwningScope(
				fullPath,
				AdditionalMarkerFiles,
				AdditionalMarkerExtensions);
			var stack = SmartSecretStackResolver.Resolve(resolution.Facts);
			return new ScopeContext(stack, $"{rulesIdentity}:scope-{(int)stack}");
		}

		private readonly record struct ScopeContext(
			SmartSecretStack Stack,
			string RulesIdentity);
	}

	[Flags]
	private enum SecretInspectionEligibility : byte
	{
		None = 0,
		Provider = 1 << 0,
		Structured = 1 << 1
	}
}

[Flags]
internal enum SmartSecretStack
{
	None = 0,
	DotNet = 1 << 0,
	Node = 1 << 1,
	Python = 1 << 2,
	Jvm = 1 << 3,
	Terraform = 1 << 4,
	Container = 1 << 5
}

internal static class SmartSecretStackResolver
{
	private static readonly IReadOnlySet<string> TerraformExtensions =
		new[] { ".tf" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> DotNetExtensions =
		new[] { ".sln", ".csproj", ".fsproj", ".vbproj" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> NodeMarkers =
		new[] { "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> PythonMarkers =
		new[] { "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "poetry.lock" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> JvmMarkers =
		new[] { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> ContainerMarkers =
		new[] { "compose.yml", "compose.yaml", "docker-compose.yml", "docker-compose.yaml" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartSecretStack Resolve(ProjectRootFacts facts)
	{
		var result = SmartSecretStack.None;
		if (facts.HasAnyFileExtension(DotNetExtensions))
			result |= SmartSecretStack.DotNet;
		if (facts.HasAnyMarkerFile(NodeMarkers))
			result |= SmartSecretStack.Node;
		if (facts.HasAnyMarkerFile(PythonMarkers))
			result |= SmartSecretStack.Python;
		if (facts.HasAnyMarkerFile(JvmMarkers))
			result |= SmartSecretStack.Jvm;
		if (facts.HasAnyFileExtension(TerraformExtensions))
			result |= SmartSecretStack.Terraform;
		if (facts.HasAnyMarkerFile(ContainerMarkers))
			result |= SmartSecretStack.Container;
		return result;
	}
}

internal static class StructuredSecretDetector
{
	private const int BudgetCheckpointMask = 0x3FF;
	private const int CredentialUriOrder = -400;
	private const int AuthorizationHeaderOrder = -350;
	private const int CookieHeaderOrder = -325;
	private const int ConnectionPasswordOrder = -300;
	private const int PgPassPasswordOrder = -275;
	private const int NetrcPasswordOrder = -250;
	private const int ConfigurationValueOrder = -200;
	private const int ContainerValueOrder = -150;
	private const int EnvironmentValueOrder = -100;

	private static readonly string[] CredentialSchemes =
	[
		"postgres://",
		"postgresql://",
		"mysql://",
		"mongodb://",
		"mongodb+srv://",
		"redis://",
		"rediss://",
		"amqp://",
		"amqps://",
		"http://",
		"https://"
	];

	internal static bool ShouldInspectPath(string path) =>
		Path.GetFileName(path).Length > 0;

	private sealed class BudgetedSecretFindingCollection(
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken) : ICollection<DetectedSecret>, IReadOnlyList<DetectedSecret>
	{
		private readonly List<DetectedSecret> _items = [];

		public int Count => _items.Count;
		public bool IsReadOnly => false;
		public DetectedSecret this[int index] => _items[index];

		public void Add(DetectedSecret item)
		{
			ArgumentNullException.ThrowIfNull(item);
			budget.RegisterFinding(cancellationToken);
			_items.Add(item);
		}

		public void Clear() => _items.Clear();
		public bool Contains(DetectedSecret item) => _items.Contains(item);
		public void CopyTo(DetectedSecret[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
		public bool Remove(DetectedSecret item) => _items.Remove(item);
		public List<DetectedSecret>.Enumerator GetEnumerator() => _items.GetEnumerator();
		IEnumerator<DetectedSecret> IEnumerable<DetectedSecret>.GetEnumerator() => GetEnumerator();
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public static IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		CancellationToken cancellationToken) =>
		Detect(repositoryRelativePath, content, stack, new SecretFileInspectionBudget(), cancellationToken);

	internal static IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken) =>
		Detect(
			repositoryRelativePath,
			content,
			stack,
			ClassifyFile(repositoryRelativePath),
			budget,
			cancellationToken);

	internal static IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		StructuredSecretFileKind fileKind,
		CancellationToken cancellationToken) =>
		Detect(
			repositoryRelativePath,
			content,
			stack,
			fileKind,
			new SecretFileInspectionBudget(),
			cancellationToken);

	internal static IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		StructuredSecretFileKind fileKind,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(budget);
		budget.Checkpoint(cancellationToken);
		if (content.IsEmpty)
			return [];
		var features = ComputeFeatureMask(content, budget, cancellationToken);
		return DetectEnabledFeatures(
			repositoryRelativePath,
			content,
			stack,
			fileKind,
			features,
			budget,
			cancellationToken);
	}

	private static IReadOnlyList<DetectedSecret> DetectEnabledFeatures(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		StructuredSecretFileKind fileKind,
		StructuredSecretFeatureMask features,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var findings = new BudgetedSecretFindingCollection(budget, cancellationToken);
		if ((features & StructuredSecretFeatureMask.CredentialUri) != 0)
			DetectCredentialUris(content, findings, budget, cancellationToken);
		budget.Checkpoint(cancellationToken);
		if ((features & StructuredSecretFeatureMask.ConnectionString) != 0)
			DetectConnectionStrings(content, findings, budget, cancellationToken);
		budget.Checkpoint(cancellationToken);

		switch (fileKind)
		{
			case StructuredSecretFileKind.Environment:
				DetectEnvironmentAssignments(content, stack, findings, budget, cancellationToken);
				break;
			case StructuredSecretFileKind.Container:
				DetectContainerAssignments(content, stack, findings, budget, cancellationToken);
				break;
			case StructuredSecretFileKind.HttpRequest:
				DetectHttpRequestHeaders(content, findings, budget, cancellationToken);
				break;
			case StructuredSecretFileKind.PgPass:
				DetectPgPassPasswords(content, findings, budget, cancellationToken);
				break;
			case StructuredSecretFileKind.Netrc:
				DetectNetrcPasswords(content, findings, budget, cancellationToken);
				break;
			case not StructuredSecretFileKind.None:
				DetectConfigurationValues(content, fileKind, stack, findings, budget, cancellationToken);
				break;
		}

		budget.Checkpoint(cancellationToken);
		return findings;
	}

	internal static IReadOnlyList<DetectedSecret> DetectWithoutPrescanForAnalysis(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack = SmartSecretStack.None)
	{
		if (content.IsEmpty)
			return [];
		return DetectEnabledFeatures(
			repositoryRelativePath,
			content,
			stack,
			ClassifyFile(repositoryRelativePath),
			StructuredSecretFeatureMask.All,
			new SecretFileInspectionBudget(),
			CancellationToken.None);
	}

	internal static StructuredSecretFeatureMask ComputeFeatureMaskForAnalysis(
		ReadOnlySpan<char> content) =>
		ComputeFeatureMask(
			content,
			new SecretFileInspectionBudget(),
			CancellationToken.None);

	private static StructuredSecretFeatureMask ComputeFeatureMask(
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var result = StructuredSecretFeatureMask.None;
		var searchStart = 0;
		while (searchStart < content.Length && result != StructuredSecretFeatureMask.All)
		{
			budget.Checkpoint(cancellationToken);
			var relativeIndex = content[searchStart..].IndexOfAny(':', '=');
			if (relativeIndex < 0)
				break;

			var index = searchStart + relativeIndex;
			if (content[index] == '=')
			{
				result |= StructuredSecretFeatureMask.ConnectionString;
			}
			else if (index + 2 < content.Length &&
			         content[index + 1] == '/' &&
			         content[index + 2] == '/')
			{
				result |= StructuredSecretFeatureMask.CredentialUri;
			}

			searchStart = index + 1;
		}

		return result;
	}

	internal static bool UsesScopedVocabulary(StructuredSecretFileKind fileKind) =>
		fileKind is
			StructuredSecretFileKind.Environment or
			StructuredSecretFileKind.Configuration or
			StructuredSecretFileKind.Xml or
			StructuredSecretFileKind.Python or
			StructuredSecretFileKind.Container;

	private static void DetectCredentialUris(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpointCounter = 0;
		for (var schemeIndex = 0; schemeIndex < CredentialSchemes.Length; schemeIndex++)
		{
			var scheme = CredentialSchemes[schemeIndex];
			var searchStart = 0;
			while (searchStart <= content.Length - scheme.Length)
			{
				CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
				var relativeSchemeStart = content[searchStart..].IndexOf(
					scheme.AsSpan(),
					StringComparison.OrdinalIgnoreCase);
				if (relativeSchemeStart < 0)
					break;

				var authorityStart = searchStart + relativeSchemeStart + scheme.Length;
				var authorityEnd = FindUriAuthorityEnd(
					content,
					authorityStart,
					budget,
					cancellationToken);
				var authority = content[authorityStart..authorityEnd];
				var at = authority.LastIndexOf('@');
				if (at > 0)
				{
					var colon = authority[..at].IndexOf(':');
					if (colon >= 0 && !IsRfc2606DocumentationHost(authority[(at + 1)..]))
					{
						var valueStart = authorityStart + colon + 1;
						AddFinding(
							content,
							valueStart,
							authorityStart + at - valueStart,
							"credential-uri-password",
							CredentialUriOrder,
							findings);
					}
				}

				searchStart = Math.Max(authorityStart, authorityEnd);
			}
		}
	}

	internal static bool IsRfc2606DocumentationHost(ReadOnlySpan<char> host)
		=> SecretDetectionTextPolicy.IsRfc2606DocumentationHost(host);

	private static int FindUriAuthorityEnd(
		ReadOnlySpan<char> content,
		int start,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var end = start;
		var inTemplate = false;
		while (end < content.Length)
		{
			if (((end - start) & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			var character = content[end];
			if (!inTemplate && end + 1 < content.Length &&
			    character == '{' && content[end + 1] == '{')
			{
				inTemplate = true;
				end += 2;
				continue;
			}
			if (inTemplate && end + 1 < content.Length &&
			    character == '}' && content[end + 1] == '}')
			{
				inTemplate = false;
				end += 2;
				continue;
			}
			if (!inTemplate &&
			    (character is '/' or '?' or '#' or '\'' or '"' or '<' or '>' || char.IsWhiteSpace(character)))
				break;
			end++;
		}
		return end;
	}

	private static void DetectConnectionStrings(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			if (!LooksLikeConnectionString(line))
				continue;

			var position = 0;
			while (position < line.Length)
			{
				var equals = line[position..].IndexOf('=');
				if (equals < 0)
					break;
				equals += position;
				var keyStart = equals - 1;
				while (keyStart >= 0 && IsConnectionKeyCharacter(line[keyStart]))
					keyStart--;
				keyStart++;
				var key = line[keyStart..equals].Trim();
				if (key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
				    key.Equals("pwd", StringComparison.OrdinalIgnoreCase))
				{
					var value = FindDelimitedValue(line, equals + 1, ';', '&');
					AddFinding(
						content,
						lineStart + value.Start,
						value.Length,
						"connection-password",
						ConnectionPasswordOrder,
						findings);
					position = Math.Max(equals + 1, value.End + 1);
					continue;
				}

				position = equals + 1;
			}
		}
	}

	private static bool LooksLikeConnectionString(ReadOnlySpan<char> line)
	{
		var assignmentCount = 0;
		var hasConnectionSignal = Contains(line, "jdbc:");
		var position = 0;
		while (position < line.Length)
		{
			var relativeEquals = line[position..].IndexOf('=');
			if (relativeEquals < 0)
				break;
			var equals = position + relativeEquals;
			assignmentCount++;
			var keyStart = equals - 1;
			while (keyStart >= 0 && IsConnectionKeyCharacter(line[keyStart]))
				keyStart--;
			var key = line[(keyStart + 1)..equals].Trim();
			hasConnectionSignal |=
				key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("server", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("data source", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("database", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("initial catalog", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("user id", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("username", StringComparison.OrdinalIgnoreCase);
			position = equals + 1;
		}
		return assignmentCount >= 2 && hasConnectionSignal;
	}

	private static void DetectEnvironmentAssignments(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var trimmedOffset = IndexOfFirstNonWhitespace(line);
			if (trimmedOffset < 0 || line[trimmedOffset] == '#')
				continue;
			if (line[trimmedOffset..].StartsWith("export ", StringComparison.OrdinalIgnoreCase))
				trimmedOffset += "export ".Length;

			var equals = line[trimmedOffset..].IndexOf('=');
			if (equals <= 0)
				continue;
			equals += trimmedOffset;
			var key = line[trimmedOffset..equals].Trim();
			if (!IsSensitiveKey(key, stack))
				continue;

			var value = FindEnvironmentValue(line, equals + 1);
			AddFinding(
				content,
				lineStart + value.Start,
				value.Length,
				"environment-secret",
				EnvironmentValueOrder,
				findings);
		}
	}

	private static void DetectContainerAssignments(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var cursor = IndexOfFirstNonWhitespace(line);
			if (cursor < 0 || line[cursor] == '#')
				continue;

			var directiveStart = cursor;
			while (cursor < line.Length && char.IsLetter(line[cursor]))
				cursor++;
			var directive = line[directiveStart..cursor];
			var isEnvironment = directive.Equals("ENV", StringComparison.OrdinalIgnoreCase);
			var isArgument = directive.Equals("ARG", StringComparison.OrdinalIgnoreCase);
			if ((!isEnvironment && !isArgument) || cursor >= line.Length || !char.IsWhiteSpace(line[cursor]))
				continue;

			while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
				cursor++;
			var firstKeyStart = cursor;
			while (cursor < line.Length && IsEnvironmentKeyCharacter(line[cursor]))
				cursor++;
			if (cursor == firstKeyStart)
				continue;

			if (cursor < line.Length && line[cursor] == '=')
			{
				DetectContainerEqualsAssignments(
					content,
					line,
					lineStart,
					firstKeyStart,
					stack,
					findings,
					budget,
					cancellationToken);
				continue;
			}

			// Docker's legacy whitespace form belongs to ENV only. ARG accepts
			// NAME or NAME=value, so treating a second token as its value would
			// redact an invalid instruction and hide a likely authoring error.
			if (!isEnvironment || cursor >= line.Length || !char.IsWhiteSpace(line[cursor]))
				continue;
			var key = line[firstKeyStart..cursor];
			if (!IsSensitiveKey(key, stack))
				continue;
			while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
				cursor++;
			var value = FindContainerLegacyValue(line, cursor);
			AddFinding(
				content,
				lineStart + value.Start,
				value.Length,
				"container-secret",
				ContainerValueOrder,
				findings);
		}
	}

	private static void DetectContainerEqualsAssignments(
		ReadOnlySpan<char> content,
		ReadOnlySpan<char> line,
		int lineStart,
		int cursor,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var checkpointCounter = 0;
		while (cursor < line.Length)
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
				cursor++;
			var keyStart = cursor;
			while (cursor < line.Length && IsEnvironmentKeyCharacter(line[cursor]))
				cursor++;
			if (cursor == keyStart || cursor >= line.Length || line[cursor] != '=')
				break;

			var key = line[keyStart..cursor];
			var value = FindContainerEqualsValue(line, cursor + 1, out var nextAssignment);
			if (IsSensitiveKey(key, stack))
			{
				AddFinding(
					content,
					lineStart + value.Start,
					value.Length,
					"container-secret",
					ContainerValueOrder,
					findings);
			}
			cursor = nextAssignment;
		}
	}

	private static TextSpan FindContainerEqualsValue(
		ReadOnlySpan<char> line,
		int start,
		out int nextAssignment)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		if (start >= line.Length)
		{
			nextAssignment = line.Length;
			return new TextSpan(start, 0);
		}

		if (line[start] is '\'' or '"')
		{
			var quote = line[start++];
			var end = start;
			while (end < line.Length && (line[end] != quote || end > start && line[end - 1] == '\\'))
				end++;
			nextAssignment = Math.Min(line.Length, end + 1);
			return new TextSpan(start, end - start);
		}

		var valueEnd = start;
		while (valueEnd < line.Length && !char.IsWhiteSpace(line[valueEnd]))
			valueEnd++;
		nextAssignment = valueEnd;
		return new TextSpan(start, valueEnd - start);
	}

	private static TextSpan FindContainerLegacyValue(ReadOnlySpan<char> line, int start)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		if (start < line.Length && line[start] is '\'' or '"')
		{
			var quote = line[start++];
			var end = start;
			while (end < line.Length && (line[end] != quote || end > start && line[end - 1] == '\\'))
				end++;
			return new TextSpan(start, end - start);
		}
		return TrimEnd(line, start, line.Length);
	}

	private static void DetectHttpRequestHeaders(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var colon = line.IndexOf(':');
			if (colon <= 0)
				continue;
			var headerName = line[..colon].Trim();
			if (headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
			{
				DetectCookieHeaderValues(
					content,
					line,
					lineStart,
					colon + 1,
					firstPairOnly: false,
					findings,
					ref checkpointCounter,
					budget,
					cancellationToken);
				continue;
			}
			if (headerName.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
			{
				DetectCookieHeaderValues(
					content,
					line,
					lineStart,
					colon + 1,
					firstPairOnly: true,
					findings,
					ref checkpointCounter,
					budget,
					cancellationToken);
				continue;
			}
			if (!headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
			    !headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var schemeStart = colon + 1;
			while (schemeStart < line.Length && char.IsWhiteSpace(line[schemeStart]))
				schemeStart++;
			var schemeEnd = schemeStart;
			while (schemeEnd < line.Length && !char.IsWhiteSpace(line[schemeEnd]))
				schemeEnd++;
			var scheme = line[schemeStart..schemeEnd];
			var ruleId = scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
				? "authorization-bearer"
				: scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
					? "authorization-basic"
					: scheme.Equals("Token", StringComparison.OrdinalIgnoreCase)
						? "authorization-token"
						: null;
			if (ruleId is null)
				continue;

			var credentialStart = schemeEnd;
			while (credentialStart < line.Length && char.IsWhiteSpace(line[credentialStart]))
				credentialStart++;
			if (credentialStart < line.Length &&
			    TryFindReferenceEnd(line, credentialStart, out var referenceEnd) &&
			    IsReferenceOrPlaceholder(line[credentialStart..referenceEnd]))
			{
				continue;
			}
			var credentialEnd = credentialStart;
			while (credentialEnd < line.Length && !char.IsWhiteSpace(line[credentialEnd]))
				credentialEnd++;
			if (IsReferenceOrPlaceholder(line[credentialStart..credentialEnd]))
				continue;
			AddFinding(
				content,
				lineStart + credentialStart,
				credentialEnd - credentialStart,
				ruleId,
				AuthorizationHeaderOrder,
				findings);
		}
	}

	private static void DetectCookieHeaderValues(
		ReadOnlySpan<char> content,
		ReadOnlySpan<char> line,
		int lineStart,
		int firstSegmentStart,
		bool firstPairOnly,
		ICollection<DetectedSecret> findings,
		ref int checkpointCounter,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var segmentStart = firstSegmentStart;
		while (segmentStart <= line.Length)
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var relativeSeparator = line[segmentStart..].IndexOf(';');
			var segmentEnd = relativeSeparator < 0
				? line.Length
				: segmentStart + relativeSeparator;
			var relativeEquals = line[segmentStart..segmentEnd].IndexOf('=');
			if (relativeEquals >= 0)
			{
				var valueStart = segmentStart + relativeEquals + 1;
				while (valueStart < segmentEnd && char.IsWhiteSpace(line[valueStart]))
					valueStart++;
				var valueEnd = segmentEnd;
				while (valueEnd > valueStart && char.IsWhiteSpace(line[valueEnd - 1]))
					valueEnd--;
				AddFinding(
					content,
					lineStart + valueStart,
					valueEnd - valueStart,
					"http-cookie",
					CookieHeaderOrder,
					findings);
			}

			if (firstPairOnly || relativeSeparator < 0)
				break;
			segmentStart = segmentEnd + 1;
		}
	}

	private static void DetectPgPassPasswords(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var first = IndexOfFirstNonWhitespace(line);
			if (first < 0 || line[first] == '#')
				continue;

			var field = 0;
			var escaped = false;
			for (var index = first; index < line.Length; index++)
			{
				if (escaped)
				{
					escaped = false;
					continue;
				}
				if (line[index] == '\\')
				{
					escaped = true;
					continue;
				}
				if (line[index] != ':')
					continue;
				field++;
				if (field != 4)
					continue;

				var value = TrimEnd(line, index + 1, line.Length);
				AddFinding(
					content,
					lineStart + value.Start,
					value.Length,
					"pgpass-password",
					PgPassPasswordOrder,
					findings);
				break;
			}
		}
	}

	private static void DetectNetrcPasswords(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var cursor = 0;
			while (TryReadNetrcToken(line, ref cursor, out var token))
			{
				if (!line.Slice(token.Start, token.Length).Equals("password", StringComparison.OrdinalIgnoreCase))
					continue;
				if (!TryReadNetrcToken(line, ref cursor, out var value))
					break;
				AddFinding(
					content,
					lineStart + value.Start,
					value.Length,
					"netrc-password",
					NetrcPasswordOrder,
					findings);
			}
		}
	}

	private static bool TryReadNetrcToken(
		ReadOnlySpan<char> line,
		ref int cursor,
		out TextSpan token)
	{
		while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
			cursor++;
		if (cursor >= line.Length || line[cursor] == '#')
		{
			token = default;
			return false;
		}

		if (line[cursor] is '\'' or '"')
		{
			var quote = line[cursor++];
			var start = cursor;
			var escaped = false;
			while (cursor < line.Length)
			{
				var character = line[cursor];
				if (!escaped && character == quote)
					break;
				escaped = !escaped && character == '\\';
				if (character != '\\')
					escaped = false;
				cursor++;
			}
			token = new TextSpan(start, cursor - start);
			if (cursor < line.Length)
				cursor++;
			return true;
		}

		var tokenStart = cursor;
		while (cursor < line.Length && !char.IsWhiteSpace(line[cursor]) && line[cursor] != '#')
			cursor++;
		token = new TextSpan(tokenStart, cursor - tokenStart);
		return token.Length > 0;
	}

	private static void DetectConfigurationValues(
		ReadOnlySpan<char> content,
		StructuredSecretFileKind fileKind,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if (fileKind == StructuredSecretFileKind.Xml)
		{
			DetectXmlConfigurationValues(content, stack, findings, budget, cancellationToken);
			return;
		}

		var nextLineStart = 0;
		var checkpointCounter = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var position = 0;
			while (position < line.Length)
			{
				if (!TryFindKeyValue(line, position, fileKind, out var key, out var value))
					break;
				if (!IsSensitiveKey(key, stack))
				{
					// An object-valued key can contain another key/value pair on the same line.
					// Continue at the value start instead of skipping the entire object.
					position = Math.Max(value.Start, position + 1);
					continue;
				}

				AddFinding(
					content,
					lineStart + value.Start,
					value.Length,
					"config-secret",
					ConfigurationValueOrder,
					findings);
				position = Math.Max(value.End + 1, position + 1);
			}
		}
	}

	private static void DetectXmlConfigurationValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var searchStart = 0;
		var checkpointCounter = 0;
		while (searchStart < content.Length)
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			var relativeTagStart = content[searchStart..].IndexOf('<');
			if (relativeTagStart < 0)
				break;
			var tagStart = searchStart + relativeTagStart;
			var tagEnd = FindXmlTagEnd(
				content,
				tagStart + 1,
				budget,
				cancellationToken);
			if (tagEnd < 0)
				break;

			var tag = content[(tagStart + 1)..tagEnd];
			var cursor = IndexOfFirstNonWhitespace(tag);
			if (cursor < 0 || tag[cursor] is '/' or '!' or '?')
			{
				searchStart = tagEnd + 1;
				continue;
			}

			var elementNameStart = cursor;
			while (cursor < tag.Length && IsXmlNameCharacter(tag[cursor]))
				cursor++;
			var elementName = tag[elementNameStart..cursor];
			var attributes = ParseXmlAttributes(
				tag,
				tagStart + 1,
				cursor,
				budget,
				cancellationToken);
			var elementIsSensitive = IsSensitiveKey(elementName, stack);
			var keyAttributeIsSensitive = false;
			foreach (var attribute in attributes)
			{
				if ((attribute.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
				     attribute.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) &&
				    IsSensitiveKey(content.Slice(attribute.ValueStart, attribute.ValueLength), stack))
				{
					keyAttributeIsSensitive = true;
					break;
				}
			}

			foreach (var attribute in attributes)
			{
				var attributeIsSensitive = IsSensitiveKey(attribute.Name, stack) ||
				                           (attribute.Name.Equals("value", StringComparison.OrdinalIgnoreCase) &&
				                            (elementIsSensitive || keyAttributeIsSensitive));
				if (!attributeIsSensitive)
					continue;
				AddFinding(
					content,
					attribute.ValueStart,
					attribute.ValueLength,
					"config-secret",
					ConfigurationValueOrder,
					findings);
			}

			if (elementIsSensitive)
			{
				var textStart = tagEnd + 1;
				var relativeTextEnd = content[textStart..].IndexOf('<');
				if (relativeTextEnd >= 0)
				{
					var textEnd = textStart + relativeTextEnd;
					while (textStart < textEnd && char.IsWhiteSpace(content[textStart]))
						textStart++;
					while (textEnd > textStart && char.IsWhiteSpace(content[textEnd - 1]))
						textEnd--;
					AddFinding(
						content,
						textStart,
						textEnd - textStart,
						"config-secret",
						ConfigurationValueOrder,
						findings);
				}
			}

			searchStart = tagEnd + 1;
		}
	}

	private static int FindXmlTagEnd(
		ReadOnlySpan<char> content,
		int start,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var quote = '\0';
		for (var index = start; index < content.Length; index++)
		{
			if (((index - start) & BudgetCheckpointMask) == 0)
				budget.Checkpoint(cancellationToken);
			var character = content[index];
			if (quote != '\0')
			{
				if (character == quote)
					quote = '\0';
				continue;
			}
			if (character is '\'' or '"')
				quote = character;
			else if (character == '>')
				return index;
		}
		return -1;
	}

	private static IReadOnlyList<XmlAttributeSpan> ParseXmlAttributes(
		ReadOnlySpan<char> tag,
		int contentOffset,
		int start,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var attributes = new List<XmlAttributeSpan>();
		var cursor = start;
		var checkpointCounter = 0;
		while (cursor < tag.Length)
		{
			CheckpointPeriodically(ref checkpointCounter, budget, cancellationToken);
			while (cursor < tag.Length && (char.IsWhiteSpace(tag[cursor]) || tag[cursor] == '/'))
				cursor++;
			var nameStart = cursor;
			while (cursor < tag.Length && IsXmlNameCharacter(tag[cursor]))
				cursor++;
			if (cursor == nameStart)
				break;
			var name = tag[nameStart..cursor].ToString();
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length || tag[cursor] != '=')
				continue;
			cursor++;
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length)
				break;

			var quote = tag[cursor] is '\'' or '"' ? tag[cursor++] : '\0';
			var valueStart = cursor;
			if (quote == '\0')
			{
				while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] != '/')
					cursor++;
			}
			else
			{
				while (cursor < tag.Length && tag[cursor] != quote)
					cursor++;
			}
			attributes.Add(new XmlAttributeSpan(
				name,
				contentOffset + valueStart,
				cursor - valueStart));
			if (cursor < tag.Length && quote != '\0')
				cursor++;
		}
		return attributes;
	}

	private static bool TryFindKeyValue(
		ReadOnlySpan<char> line,
		int searchStart,
		StructuredSecretFileKind fileKind,
		out ReadOnlySpan<char> key,
		out TextSpan value)
	{
		for (var delimiter = searchStart; delimiter < line.Length; delimiter++)
		{
			if (line[delimiter] != '=' && line[delimiter] != ':')
				continue;
			if (line[delimiter] == ':' && delimiter + 2 < line.Length && line[delimiter + 1] == '/' && line[delimiter + 2] == '/')
				continue;

			var keyEnd = delimiter;
			while (keyEnd > searchStart && char.IsWhiteSpace(line[keyEnd - 1]))
				keyEnd--;
			var keyStart = keyEnd - 1;
			if (keyStart >= searchStart && line[keyStart] is '\'' or '"')
			{
				var quote = line[keyStart--];
				while (keyStart >= searchStart && line[keyStart] != quote)
					keyStart--;
				keyStart++;
			}
			else
			{
				while (keyStart >= searchStart && IsConfigKeyCharacter(line[keyStart]))
					keyStart--;
				keyStart++;
			}

			if (keyStart >= keyEnd)
				continue;
			key = TrimKey(line[keyStart..keyEnd]);
			value = fileKind switch
			{
				StructuredSecretFileKind.Xml => FindDelimitedValue(line, delimiter + 1, ' ', '>'),
				StructuredSecretFileKind.Python => FindPythonLiteralValue(line, delimiter + 1),
				_ => FindConfigurationValue(line, delimiter + 1)
			};
			return true;
		}

		key = default;
		value = default;
		return false;
	}

	private static TextSpan FindPythonLiteralValue(ReadOnlySpan<char> line, int start)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		// settings.py is executable source, even though it conventionally carries
		// configuration. Only string literals are values; environment lookups and other
		// expressions must remain visible as references rather than being redacted as data.
		return start < line.Length && line[start] is '\'' or '"'
			? FindConfigurationValue(line, start)
			: new TextSpan(start, 0);
	}

	private static TextSpan FindEnvironmentValue(ReadOnlySpan<char> line, int start)
	{
		var value = FindConfigurationValue(line, start);
		if (value.Length == 0)
			return value;
		if (line[value.Start] is '\'' or '"')
			return value;

		var valueSpan = line.Slice(value.Start, value.Length);
		for (var index = 1; index < valueSpan.Length; index++)
		{
			if (valueSpan[index] == '#' && char.IsWhiteSpace(valueSpan[index - 1]))
				return TrimEnd(line, value.Start, value.Start + index);
		}
		return value;
	}

	private static TextSpan FindConfigurationValue(ReadOnlySpan<char> line, int start)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		if (start >= line.Length)
			return new TextSpan(start, 0);
		if (TryFindReferenceEnd(line, start, out var referenceEnd))
			return new TextSpan(start, referenceEnd - start);

		if (line[start] is '\'' or '"')
		{
			var quote = line[start++];
			var end = start;
			while (end < line.Length)
			{
				if (line[end] == quote && (end == start || line[end - 1] != '\\'))
					break;
				end++;
			}
			return new TextSpan(start, end - start);
		}

		var unquotedEnd = start;
		while (unquotedEnd < line.Length && line[unquotedEnd] is not ',' and not '}' and not ']' and not '#')
			unquotedEnd++;
		return TrimEnd(line, start, unquotedEnd);
	}

	private static bool TryFindReferenceEnd(ReadOnlySpan<char> line, int start, out int end)
	{
		if (line[start..].StartsWith("${", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, "}", out end);
		if (line[start..].StartsWith("$(", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, ")", out end);
		if (line[start..].StartsWith("{{", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, "}}", out end);
		if (line[start] == '<')
			return TryFindSuffix(line, start + 1, ">", out end);
		if (line[start] == '%')
			return TryFindSuffix(line, start + 1, "%", out end);
		end = 0;
		return false;
	}

	private static bool TryFindSuffix(
		ReadOnlySpan<char> line,
		int searchStart,
		string suffix,
		out int end)
	{
		var relativeEnd = line[searchStart..].IndexOf(suffix, StringComparison.Ordinal);
		if (relativeEnd < 0)
		{
			end = 0;
			return false;
		}
		end = searchStart + relativeEnd + suffix.Length;
		return true;
	}

	private static TextSpan FindDelimitedValue(
		ReadOnlySpan<char> line,
		int start,
		char firstDelimiter,
		char secondDelimiter)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		var quote = start < line.Length && line[start] is '\'' or '"' ? line[start++] : '\0';
		var end = start;
		while (end < line.Length)
		{
			if (quote != '\0' && line[end] == quote)
				break;
			if (quote == '\0' && (line[end] == firstDelimiter || line[end] == secondDelimiter || line[end] is '\'' or '"'))
				break;
			end++;
		}
		return TrimEnd(line, start, end);
	}

	private static TextSpan TrimEnd(ReadOnlySpan<char> line, int start, int end)
	{
		while (end > start && char.IsWhiteSpace(line[end - 1]))
			end--;
		return new TextSpan(start, end - start);
	}

	private static void AddFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string ruleId,
		int ruleOrder,
		ICollection<DetectedSecret> findings)
	{
		if (length <= 0 || start < 0 || start > content.Length - length)
			return;
		var value = content.Slice(start, length);
		if (IsReferenceOrPlaceholder(value))
			return;
		findings.Add(new DetectedSecret(ruleId, start, length, value.ToString(), ruleOrder));
	}

	internal static bool IsReferenceOrPlaceholder(ReadOnlySpan<char> value)
		=> SecretDetectionTextPolicy.IsReferenceOrPlaceholder(value);

	private static bool IsSensitiveKey(ReadOnlySpan<char> key, SmartSecretStack stack)
	{
		Span<char> normalizedBuffer = stackalloc char[Math.Min(key.Length, 128)];
		var length = 0;
		for (var index = 0; index < key.Length && length < normalizedBuffer.Length; index++)
		{
			if (char.IsLetterOrDigit(key[index]))
				normalizedBuffer[length++] = char.ToLowerInvariant(key[index]);
		}
		var normalized = normalizedBuffer[..length];
		if (normalized.IsEmpty)
			return false;

		if (Contains(normalized, "password") ||
		    normalized.Equals("passwd", StringComparison.Ordinal) ||
		    normalized.Equals("pwd", StringComparison.Ordinal) ||
		    Contains(normalized, "secret") ||
		    normalized.EndsWith("token", StringComparison.Ordinal) ||
		    Contains(normalized, "apikey") ||
		    Contains(normalized, "accesskey") ||
		    Contains(normalized, "privatekey") ||
		    Contains(normalized, "signingkey") ||
		    HasSensitiveTrailingKey(key, normalized) ||
		    Contains(normalized, "credential"))
		{
			return true;
		}

		return stack.HasFlag(SmartSecretStack.DotNet) && normalized.Equals("jwtkey", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Node) && normalized.Equals("npmauth", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Python) && normalized.Equals("djangokey", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Terraform) && normalized.Equals("terraformauth", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Container) && normalized.Equals("registryauth", StringComparison.Ordinal);
	}

	private static bool HasSensitiveTrailingKey(ReadOnlySpan<char> key, ReadOnlySpan<char> normalized)
	{
		// PUBLIC_KEY describes material intended for distribution. Check the normalized suffix
		// so qualified names such as JWT_PUBLIC_KEY remain outside the redaction vocabulary.
		if (normalized.EndsWith("publickey", StringComparison.Ordinal))
			return false;

		key = key.Trim();
		if (key.Length <= 3 || !key.EndsWith("key", StringComparison.OrdinalIgnoreCase))
			return false;

		var separator = key[^4];
		if (!char.IsLetterOrDigit(separator))
			return true;

		// Preserve conventional camelCase/PascalCase config keys without treating ordinary
		// words ending in lowercase "key" (for example, "monkey") as credential names.
		return key[^3] == 'K' && char.IsLower(separator);
	}

	internal static StructuredSecretFileKind ClassifyFile(string path)
	{
		var fileName = Path.GetFileName(path);
		if (fileName.Length == 0)
			return StructuredSecretFileKind.None;

		// Special extensionless and dotfile names are rare. Guarding them by the first
		// character keeps ordinary source files on a single extension dispatch path.
		if ((fileName[0] is 'D' or 'd' or 'C' or 'c') &&
		    (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
		     fileName.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
		     fileName.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)))
		{
			return StructuredSecretFileKind.Container;
		}
		if (fileName[0] == '.' || fileName[0] == '_')
		{
			if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
			    fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase))
			{
				return StructuredSecretFileKind.Environment;
			}
			if (fileName.Equals(".pgpass", StringComparison.OrdinalIgnoreCase))
				return StructuredSecretFileKind.PgPass;
			if (fileName.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
			    fileName.Equals("_netrc", StringComparison.OrdinalIgnoreCase))
			{
				return StructuredSecretFileKind.Netrc;
			}
		}
		if ((fileName[0] is 'P' or 'p') &&
		    fileName.Equals("pgpass.conf", StringComparison.OrdinalIgnoreCase))
		{
			return StructuredSecretFileKind.PgPass;
		}

		var extension = Path.GetExtension(fileName.AsSpan());
		if (extension.Equals(".dockerfile", StringComparison.OrdinalIgnoreCase))
		{
			return StructuredSecretFileKind.Container;
		}
		if (extension.Equals(".http", StringComparison.OrdinalIgnoreCase) ||
		    extension.Equals(".rest", StringComparison.OrdinalIgnoreCase))
		{
			return StructuredSecretFileKind.HttpRequest;
		}
		if (extension.Equals(".config", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Xml;
		if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
		    (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
		     fileName.EndsWith(".tfvars.json", StringComparison.OrdinalIgnoreCase)))
		{
			return StructuredSecretFileKind.Configuration;
		}
		if ((extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) ||
		     extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)) &&
		    (fileName.StartsWith("application", StringComparison.OrdinalIgnoreCase) ||
		     fileName.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) ||
		     fileName.StartsWith("compose.", StringComparison.OrdinalIgnoreCase)))
			return StructuredSecretFileKind.Configuration;
		if (extension.Equals(".tfvars", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Configuration;
		if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) &&
		    fileName.Equals("settings.py", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Python;
		return StructuredSecretFileKind.None;
	}

	private static bool TryReadLine(
		ReadOnlySpan<char> content,
		ref int nextLineStart,
		out ReadOnlySpan<char> line,
		out int lineStart)
	{
		if (nextLineStart > content.Length)
		{
			line = default;
			lineStart = 0;
			return false;
		}

		lineStart = nextLineStart;
		var relativeEnd = content[lineStart..].IndexOfAny('\r', '\n');
		var lineEnd = relativeEnd < 0 ? content.Length : lineStart + relativeEnd;
		line = content[lineStart..lineEnd];
		if (relativeEnd < 0)
		{
			nextLineStart = content.Length + 1;
			return true;
		}

		var separatorLength = content[lineEnd] == '\r' &&
		                      lineEnd + 1 < content.Length &&
		                      content[lineEnd + 1] == '\n'
			? 2
			: 1;
		nextLineStart = lineEnd + separatorLength;
		return true;
	}

	private static void CheckpointPeriodically(
		ref int counter,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		if ((counter++ & BudgetCheckpointMask) == 0)
			budget.Checkpoint(cancellationToken);
	}

	private static ReadOnlySpan<char> TrimKey(ReadOnlySpan<char> key)
	{
		while (!key.IsEmpty && key[0] is ' ' or '\t' or '\'' or '"')
			key = key[1..];
		while (!key.IsEmpty && key[^1] is ' ' or '\t' or '\'' or '"')
			key = key[..^1];
		return key;
	}

	private static int IndexOfFirstNonWhitespace(ReadOnlySpan<char> value)
	{
		for (var index = 0; index < value.Length; index++)
			if (!char.IsWhiteSpace(value[index]))
				return index;
		return -1;
	}

	private static bool IsConnectionKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is ' ' or '_' or '-';

	private static bool IsEnvironmentKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	private static bool IsConfigKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or '.';

	private static bool IsXmlNameCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':';

	private static bool Contains(ReadOnlySpan<char> value, string candidate) =>
		value.Contains(candidate, StringComparison.OrdinalIgnoreCase);

	private readonly record struct TextSpan(int Start, int Length)
	{
		public int End => Start + Length;
	}

	private readonly record struct XmlAttributeSpan(
		string Name,
		int ValueStart,
		int ValueLength);

	internal enum StructuredSecretFileKind
	{
		None,
		Environment,
		Configuration,
		Xml,
		Python,
		Container,
		HttpRequest,
		PgPass,
		Netrc
	}
}

[Flags]
internal enum StructuredSecretFeatureMask : byte
{
	None = 0,
	CredentialUri = 1 << 0,
	ConnectionString = 1 << 1,
	All = CredentialUri | ConnectionString
}
