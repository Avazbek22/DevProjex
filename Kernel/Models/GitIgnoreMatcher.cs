using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Kernel.Models;

public sealed class GitIgnoreMatcher
{
    internal const int MaximumEffectiveRuleCount = 8_192;
    private const int RelativePathStackLimit = 512;

    private readonly string _normalizedRootPath;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly StringComparison _pathComparison;
    private readonly bool _ignoreAsciiCase;
    private readonly bool _normalizeUnicode;
    private readonly bool _hasNegationRules;
    private GitIgnoreMatcher? _lowerPrioritySource;
    private GitIgnoreMatcher? _higherPrioritySource;

    // Pre-compiled search values for SIMD-optimized character lookup
    private static readonly SearchValues<char> GlobSpecialChars = SearchValues.Create("*?[");

    public static GitIgnoreMatcher Empty { get; } = new(
        string.Empty,
        [],
        false,
        GitPathComparisonSemantics.PlatformDefault);

    private GitIgnoreMatcher(
        string normalizedRootPath,
        IReadOnlyList<Rule> rules,
        bool hasNegationRules,
        GitPathComparisonSemantics comparisonSemantics)
    {
        _normalizedRootPath = normalizedRootPath;
        _rules = rules;
        _hasNegationRules = hasNegationRules;
        // Git wildmatch applies WM_CASEFOLD only to ASCII bytes. Pre-folding patterns
        // and paths keeps literal and regex rules consistent without culture-sensitive
        // Unicode matches that native Git would not make.
        _pathComparison = StringComparison.Ordinal;
        _ignoreAsciiCase = comparisonSemantics.IgnoreCase;
        _normalizeUnicode = comparisonSemantics.NormalizeUnicode;
    }

    public bool HasNegationRules => _hasNegationRules;

    public static GitIgnoreMatcher Combine(GitIgnoreMatcher lowerPriority, GitIgnoreMatcher higherPriority)
    {
        if (lowerPriority._rules.Count == 0)
            return higherPriority;
        if (ReferenceEquals(higherPriority, Empty))
            return lowerPriority;
        if (lowerPriority._normalizedRootPath != higherPriority._normalizedRootPath ||
            lowerPriority._ignoreAsciiCase != higherPriority._ignoreAsciiCase ||
            lowerPriority._normalizeUnicode != higherPriority._normalizeUnicode)
            throw new ArgumentException("Rule sources must share a scope and comparison semantics.");
        if (lowerPriority._rules.Count + higherPriority._rules.Count > MaximumEffectiveRuleCount)
            throw new InvalidDataException("The combined ignore scope exceeds the rule limit.");

        return new GitIgnoreMatcher(
            higherPriority._normalizedRootPath,
            [.. lowerPriority._rules, .. higherPriority._rules],
            lowerPriority.HasNegationRules || higherPriority.HasNegationRules,
            new GitPathComparisonSemantics(higherPriority._ignoreAsciiCase, higherPriority._normalizeUnicode))
        {
            _lowerPrioritySource = lowerPriority,
            _higherPrioritySource = higherPriority
        };
    }

    public bool IsRootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _normalizedRootPath.Length == 0)
            return false;

        return string.Equals(
            GitPathTextNormalizer.NormalizeObservedPath(
                NormalizePath(path).TrimEnd('/'),
                _normalizeUnicode,
                _ignoreAsciiCase),
            _normalizedRootPath,
            _pathComparison);
    }

    public readonly record struct IgnoreEvaluation(bool HasMatch, bool IsIgnored);

    public static GitIgnoreMatcher Build(string rootPath, IEnumerable<string> lines)
    {
        return Build(rootPath, lines, GitPathComparisonSemantics.PlatformDefault);
    }

    public static GitIgnoreMatcher Build(
        string rootPath,
        IEnumerable<string> lines,
        GitPathComparisonSemantics comparisonSemantics)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Empty;

        var normalizedRoot = GitPathTextNormalizer.NormalizeObservedPath(
            NormalizePath(rootPath).TrimEnd('/'),
            comparisonSemantics.NormalizeUnicode,
            comparisonSemantics.IgnoreCase);
        if (normalizedRoot.Length == 0)
            return Empty;

        var rules = new List<Rule>();
        var regexOptions = RegexOptions.Compiled |
                           RegexOptions.CultureInvariant |
                           RegexOptions.NonBacktracking;

        foreach (var raw in lines)
        {
            if (raw is null)
                continue;

            var sourceLine = TrimUnescapedTrailingSpaces(raw);
            var line = GitPathTextNormalizer.NormalizePattern(
                sourceLine,
                comparisonSemantics.IgnoreCase);
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var escapedSpecial = line.StartsWith(@"\#") || line.StartsWith(@"\!");
            if (escapedSpecial)
            {
                line = line[1..];
                sourceLine = sourceLine[1..];
            }

            // Only treat as negation if not escaped
            var isNegation = !escapedSpecial && line.StartsWith('!');
            if (isNegation)
            {
                line = line[1..];
                sourceLine = sourceLine[1..];
                if (line.Length == 0)
                    continue;
            }

            // Git defines a terminal backslash as an invalid pattern. Ignoring only that
            // line keeps a damaged entry from disabling every valid rule in the file.
            if (HasUnescapedTrailingBackslash(line))
                continue;

            var hasEscapes = line.Contains('\\');
            if (line.Contains('[') && HasUnterminatedCharacterClass(line))
                continue;

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line.TrimEnd('/');
                sourceLine = sourceLine.TrimEnd('/');
            }

            if (line.Length == 0)
                continue;

            var anchored = line.StartsWith('/');
            if (anchored)
            {
                line = line[1..];
                sourceLine = sourceLine[1..];
            }

            if (line.Length == 0)
                continue;

            var hasSlash = line.Contains('/');
            var matchByNameOnly = !anchored && !hasSlash && !directoryOnly;
            var relativeToMatcherRoot = anchored || hasSlash;
            if (rules.Count == MaximumEffectiveRuleCount)
            {
                throw new IOException(
                    $"The .gitignore source exceeds the safe limit of {MaximumEffectiveRuleCount} effective rules.");
            }

            var matchKind = GetRuleMatchKind(line, relativeToMatcherRoot, directoryOnly, matchByNameOnly, hasEscapes);
            Regex? pattern = null;
            if (matchKind == RuleMatchKind.Regex)
            {
                var projectedPattern = ProjectUtf8BytesForRegex(line);
                var projectedSourcePattern = ProjectUtf8BytesForRegex(sourceLine);
                if (!TryGlobToRegex(
                        projectedPattern,
                        projectedSourcePattern,
                        anchored,
                        comparisonSemantics.IgnoreCase,
                        out var globRegex))
                {
                    continue;
                }

                var regexPattern = matchByNameOnly
                    ? $"^{globRegex}$"
                    : BuildPathRegex(globRegex, relativeToMatcherRoot, directoryOnly);
                try
                {
                    pattern = new Regex(regexPattern, regexOptions);
                }
                catch (ArgumentException)
                {
                    // A malformed pattern must never invalidate the remaining scope.
                    continue;
                }
            }

            rules.Add(new Rule(
                pattern,
                line,
                matchKind,
                isNegation,
                directoryOnly,
                matchByNameOnly,
                !directoryOnly && !matchByNameOnly && MatchesEveryDirectChildName(line),
                ComputeStaticPrefix(line)));
        }

        // Precompute hasNegationRules to avoid repeated enumeration
        var hasNegation = false;
        foreach (var rule in rules)
        {
            if (rule.IsNegation)
            {
                hasNegation = true;
                break;
            }
        }

        return new GitIgnoreMatcher(normalizedRoot, rules, hasNegation, comparisonSemantics);
    }

    public IgnoreEvaluation Evaluate(string fullPath, bool isDirectory, string name)
    {
        if (!TryGetRelativePath(fullPath, out var relativePath))
            return default;

        return EvaluateRelativeCore(relativePath, isDirectory, name);
    }

    public IgnoreEvaluation EvaluateRelative(string relativePath, bool isDirectory, string name)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(relativePath))
            return default;

        return EvaluateRelativeCore(NormalizeRelativePathForComparison(relativePath), isDirectory, name);
    }

    internal IgnoreEvaluation EvaluateRelativeNormalized(
        ReadOnlySpan<char> relativePath,
        bool isDirectory,
        string name)
    {
        if (_rules.Count == 0 || relativePath.IsEmpty)
            return default;

        return EvaluateRelativeNormalizedCore(relativePath, isDirectory, name);
    }

    internal IgnoreEvaluation EvaluateRelativeNormalized(
        ReadOnlySpan<char> baseRelativePath,
        ReadOnlySpan<char> scanRelativePath,
        bool isDirectory,
        string name)
    {
        if (baseRelativePath.IsEmpty)
            return EvaluateRelativeNormalized(scanRelativePath, isDirectory, name);
        if (scanRelativePath.IsEmpty)
            return EvaluateRelativeNormalized(baseRelativePath, isDirectory, name);

        var length = checked(baseRelativePath.Length + scanRelativePath.Length + 1);
        char[]? rented = null;
        Span<char> relativePath = length <= RelativePathStackLimit
            ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            WriteCombinedRelativePath(baseRelativePath, scanRelativePath, relativePath);
            return EvaluateRelativeNormalized(relativePath, isDirectory, name);
        }
		finally
		{
			if (rented is not null)
				ArrayPool<char>.Shared.Return(rented, clearArray: true);
		}
    }

    internal IgnoreEvaluation EvaluateRulesOnly(string fullPath, bool isDirectory, string name)
    {
        if (!TryGetRelativePath(fullPath, out var relativePath))
            return default;

        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath) : name;
        return EvaluateRules(relativePath.AsSpan(), isDirectory, normalizedName);
    }

    internal IgnoreEvaluation EvaluateRelativeRulesOnlyNormalized(
        ReadOnlySpan<char> relativePath,
        bool isDirectory,
        string name)
    {
        if (_rules.Count == 0 || relativePath.IsEmpty)
            return default;

        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath).ToString() : name;
        if (RequiresComparisonNormalization(relativePath))
        {
            var normalizedPath = GitPathTextNormalizer.NormalizeObservedPath(
                relativePath.ToString(),
                _normalizeUnicode,
                _ignoreAsciiCase);
            return EvaluateRules(normalizedPath, isDirectory, normalizedName);
        }

        return EvaluateRules(relativePath, isDirectory, normalizedName);
    }

    internal IgnoreEvaluation EvaluateRelativeRulesOnlyNormalized(
        ReadOnlySpan<char> baseRelativePath,
        ReadOnlySpan<char> scanRelativePath,
        bool isDirectory,
        string name)
    {
        if (baseRelativePath.IsEmpty)
            return EvaluateRelativeRulesOnlyNormalized(scanRelativePath, isDirectory, name);
        if (scanRelativePath.IsEmpty)
            return EvaluateRelativeRulesOnlyNormalized(baseRelativePath, isDirectory, name);

        var length = checked(baseRelativePath.Length + scanRelativePath.Length + 1);
        char[]? rented = null;
        Span<char> relativePath = length <= RelativePathStackLimit
            ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            WriteCombinedRelativePath(baseRelativePath, scanRelativePath, relativePath);
            return EvaluateRelativeRulesOnlyNormalized(relativePath, isDirectory, name);
        }
		finally
		{
			if (rented is not null)
				ArrayPool<char>.Shared.Return(rented, clearArray: true);
		}
    }

    private IgnoreEvaluation EvaluateRelativeNormalizedCore(
        ReadOnlySpan<char> relativePath,
        bool isDirectory,
        string name)
    {
        if (!RequiresComparisonNormalization(relativePath))
            return EvaluateRelativeCore(relativePath, isDirectory, name);

        var normalizedPath = GitPathTextNormalizer.NormalizeObservedPath(
            relativePath.ToString(),
            _normalizeUnicode,
            _ignoreAsciiCase);
        return EvaluateRelativeCore(normalizedPath, isDirectory, name);
    }

    private IgnoreEvaluation EvaluateRelativeCore(ReadOnlySpan<char> relativePath, bool isDirectory, string name)
    {
        if (_higherPrioritySource is { } higher && _lowerPrioritySource is { } lower)
        {
            var higherEvaluation = higher.EvaluateRelativeCore(relativePath, isDirectory, name);
            return higherEvaluation.HasMatch ? higherEvaluation : lower.EvaluateRelativeCore(relativePath, isDirectory, name);
        }
        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath).ToString() : name;
        var evaluation = EvaluateRules(relativePath, isDirectory, normalizedName);
        var ignored = evaluation.IsIgnored;
        var hasMatch = evaluation.HasMatch;

        // Git cannot re-include a path while one of its parent directories remains excluded.
        // Keep this off the common path: ancestor evaluation is needed only after a negation
        // changed a matching path back to visible.
        if (!ignored && hasMatch && HasNegationRules && HasIgnoredAncestor(relativePath))
            ignored = true;

        // For directories: if not directly ignored, check if all contents would be ignored
        // Pattern like **/bin/* ignores contents but not the directory itself
        // For UI purposes, if all contents are ignored, the directory should be hidden too
        // Skip this optimization if there are negation rules - they might un-ignore specific files
        if (!ignored && isDirectory && !HasNegationRules)
        {
            Span<char> testChildPath = relativePath.Length <= 510
                ? stackalloc char[relativePath.Length + 2]
                : new char[relativePath.Length + 2];
            relativePath.CopyTo(testChildPath);
            testChildPath[^2] = '/';
            testChildPath[^1] = '_';
            foreach (var rule in _rules)
            {
                if (!rule.MatchesEveryDirectChildName)
                    continue;

                if (rule.IsMatch(testChildPath, "_", isDirectory: false, _pathComparison))
                {
                    ignored = true;
                    hasMatch = true;
                    break;
                }
            }
        }

        return new IgnoreEvaluation(hasMatch, ignored);
    }

    private IgnoreEvaluation EvaluateRules(
        ReadOnlySpan<char> relativePath,
        bool isDirectory,
        string normalizedName)
    {
        if (_higherPrioritySource is { } higher && _lowerPrioritySource is { } lower)
        {
            var higherEvaluation = higher.EvaluateRules(relativePath, isDirectory, normalizedName);
            if (higherEvaluation.HasMatch)
                return higherEvaluation;
            // A root rule source can reopen descendants hidden only by info/exclude.
            if (isDirectory && higher.ShouldTraverseIgnoredDirectoryRelativeCore(relativePath, normalizedName))
                return default;
            return lower.EvaluateRules(relativePath, isDirectory, normalizedName);
        }
        normalizedName = GitPathTextNormalizer.NormalizeObservedPath(
            normalizedName,
            _normalizeUnicode,
            _ignoreAsciiCase);
        var regexProjectionInitialized = false;
        string? projectedRelativePath = null;
        string? projectedName = null;
        for (var ruleIndex = _rules.Count - 1; ruleIndex >= 0; ruleIndex--)
        {
            var rule = _rules[ruleIndex];
            if (rule.MatchKind == RuleMatchKind.Regex && !regexProjectionInitialized)
            {
                projectedRelativePath = ProjectUtf8BytesForRegexOrNull(relativePath);
                projectedName = ProjectUtf8BytesForRegexOrNull(normalizedName);
                regexProjectionInitialized = true;
            }

            if (!rule.IsMatch(
                    relativePath,
                    normalizedName,
                    isDirectory,
                    _pathComparison,
                    projectedRelativePath,
                    projectedName))
            {
                continue;
            }

            return new IgnoreEvaluation(HasMatch: true, IsIgnored: !rule.IsNegation);
        }

        return default;
    }

    private bool HasIgnoredAncestor(ReadOnlySpan<char> relativePath)
    {
        var segmentStart = 0;
        for (var index = 0; index < relativePath.Length; index++)
        {
            if (relativePath[index] != '/')
                continue;

            var ancestor = relativePath[..index];
            var ancestorName = relativePath[segmentStart..index].ToString();
            if (EvaluateRules(ancestor, isDirectory: true, ancestorName).IsIgnored)
                return true;

            segmentStart = index + 1;
        }

        return false;
    }

    public bool IsIgnored(string fullPath, bool isDirectory, string name)
    {
        return Evaluate(fullPath, isDirectory, name).IsIgnored;
    }

    public bool ShouldTraverseIgnoredDirectory(string fullPath, string name)
    {
        if (!HasNegationRules)
            return false;

        if (!TryGetRelativePath(fullPath, out var relativePath))
            return false;

        return ShouldTraverseIgnoredDirectoryRelativeCore(relativePath, name);
    }

    public bool ShouldTraverseIgnoredDirectoryRelative(string relativePath, string name)
    {
        if (!HasNegationRules || string.IsNullOrEmpty(relativePath))
            return false;

        return ShouldTraverseIgnoredDirectoryRelativeCore(NormalizeRelativePathForComparison(relativePath), name);
    }

    internal bool ShouldTraverseIgnoredDirectoryRelativeNormalized(ReadOnlySpan<char> relativePath, string name)
    {
        if (!HasNegationRules || relativePath.IsEmpty)
            return false;

        if (!RequiresComparisonNormalization(relativePath))
            return ShouldTraverseIgnoredDirectoryRelativeCore(relativePath, name);

        var normalizedPath = GitPathTextNormalizer.NormalizeObservedPath(
            relativePath.ToString(),
            _normalizeUnicode,
            _ignoreAsciiCase);
        return ShouldTraverseIgnoredDirectoryRelativeCore(normalizedPath, name);
    }

    internal bool ShouldTraverseIgnoredDirectoryRelativeNormalized(
        ReadOnlySpan<char> baseRelativePath,
        ReadOnlySpan<char> scanRelativePath,
        string name)
    {
        if (baseRelativePath.IsEmpty)
            return ShouldTraverseIgnoredDirectoryRelativeNormalized(scanRelativePath, name);
        if (scanRelativePath.IsEmpty)
            return ShouldTraverseIgnoredDirectoryRelativeNormalized(baseRelativePath, name);

        var length = checked(baseRelativePath.Length + scanRelativePath.Length + 1);
        char[]? rented = null;
        Span<char> relativePath = length <= RelativePathStackLimit
            ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            WriteCombinedRelativePath(baseRelativePath, scanRelativePath, relativePath);
            return ShouldTraverseIgnoredDirectoryRelativeNormalized(relativePath, name);
        }
		finally
		{
			if (rented is not null)
				ArrayPool<char>.Shared.Return(rented, clearArray: true);
		}
    }

    private static void WriteCombinedRelativePath(
        ReadOnlySpan<char> baseRelativePath,
        ReadOnlySpan<char> scanRelativePath,
        Span<char> destination)
    {
        baseRelativePath.CopyTo(destination);
        destination[baseRelativePath.Length] = '/';
        scanRelativePath.CopyTo(destination[(baseRelativePath.Length + 1)..]);
    }

    private bool ShouldTraverseIgnoredDirectoryRelativeCore(ReadOnlySpan<char> relativePath, string name)
    {
        // A real directory match cannot be bypassed by a negation aimed only at a child.
        // Synthetic directory hiding for patterns such as "bin/*" is different: the
        // directory itself is still traversable, so a later child negation can apply.
        if (EvaluateRules(relativePath, isDirectory: true, name).IsIgnored)
            return false;

        foreach (var rule in _rules)
        {
            if (!rule.IsNegation)
                continue;

            // Name-only negation rules (like !keep.txt) can match files anywhere
            // because an explicitly excluded directory already returned above.
            if (rule.MatchByNameOnly)
                return true;

            // Path-based negation rules with no static prefix (like !**/*.txt)
            // could match anywhere, so we must traverse
            if (rule.StaticPrefix.Length == 0)
                return true;

            // Negation target is inside this directory
            if (rule.StaticPrefix.Length > relativePath.Length &&
                rule.StaticPrefix[relativePath.Length] == '/' &&
                rule.StaticPrefix.AsSpan(0, relativePath.Length).Equals(relativePath, _pathComparison))
                return true;

            // This directory is inside the negation target path
            if (relativePath.Length > rule.StaticPrefix.Length &&
                relativePath[rule.StaticPrefix.Length] == '/' &&
                relativePath[..rule.StaticPrefix.Length].Equals(rule.StaticPrefix.AsSpan(), _pathComparison))
                return true;

            // Exact match
            if (relativePath.Equals(rule.StaticPrefix.AsSpan(), _pathComparison))
                return true;
        }

        return false;
    }

    public bool TryGetRelativePath(string fullPath, out string relativePath, bool allowRoot = false)
    {
        relativePath = string.Empty;

        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
            return false;

        var normalizedFullPath = GitPathTextNormalizer.NormalizeObservedPath(
            NormalizePath(fullPath),
            _normalizeUnicode,
            _ignoreAsciiCase);
        if (!normalizedFullPath.StartsWith(_normalizedRootPath, _pathComparison))
            return false;

        if (normalizedFullPath.Length == _normalizedRootPath.Length)
            return allowRoot;

        if (normalizedFullPath[_normalizedRootPath.Length] != '/')
            return false;

        relativePath = normalizedFullPath[(_normalizedRootPath.Length + 1)..];
        return relativePath.Length > 0 || allowRoot;
    }

    private static string BuildPathRegex(string globRegex, bool relativeToMatcherRoot, bool directoryOnly)
    {
        var prefix = relativeToMatcherRoot ? "^" : "^(?:.*/)?";
        // Directory-only rules must match directories (or their descendants), not plain files.
        var suffix = directoryOnly ? "/.*$" : "$";
        return $"{prefix}{globRegex}{suffix}";
    }

    private static RuleMatchKind GetRuleMatchKind(
        string pattern,
        bool relativeToMatcherRoot,
        bool directoryOnly,
        bool matchByNameOnly,
        bool hasEscapes)
    {
        // Literal rules dominate real .gitignore files. Keeping them out of Regex
        // reduces allocations at build time and avoids a Regex call for every path.
        if (hasEscapes || pattern.AsSpan().IndexOfAny(GlobSpecialChars) >= 0)
            return RuleMatchKind.Regex;

        if (matchByNameOnly)
            return RuleMatchKind.NameLiteral;

        if (directoryOnly)
            return relativeToMatcherRoot
                ? RuleMatchKind.AnchoredDirectoryLiteral
                : RuleMatchKind.UnanchoredDirectoryLiteral;

        return relativeToMatcherRoot
            ? RuleMatchKind.AnchoredPathLiteral
            : RuleMatchKind.UnanchoredPathLiteral;
    }

    private static bool TryGlobToRegex(
        string pattern,
        string sourcePattern,
        bool anchored,
        bool ignoreAsciiCase,
        out string regex)
    {
        // Pre-size StringBuilder based on typical expansion factor
        var sb = new StringBuilder(pattern.Length * 2);
        var span = pattern.AsSpan();
        var sourceSpan = sourcePattern.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var current = span[i];

            switch (current)
            {
                case '*':
                    var runEnd = i + 1;
                    while (runEnd < span.Length && span[runEnd] == '*')
                        runEnd++;

                    var runLength = runEnd - i;
                    var atSegmentStart = i == 0 || span[i - 1] == '/';
                    var followedBySlash = runEnd < span.Length && span[runEnd] == '/';
                    var isDirectoryGlobStar = runLength == 2 && atSegmentStart && followedBySlash;
                    var isTrailingGlobStar = runLength == 2 && runEnd == span.Length &&
                                             (i > 0 && span[i - 1] == '/' || i == 0 && anchored);
                    if (!isDirectoryGlobStar && !isTrailingGlobStar)
                    {
                        // Consecutive asterisks outside the three documented globstar
                        // positions are ordinary '*' wildcards and cannot cross '/'.
                        sb.Append("[^/]*");
                        i = runEnd - 1;
                        break;
                    }

                    if (isDirectoryGlobStar)
                    {
                        // Leading **/ and /**/ match zero or more directories.
                        sb.Append("(?:.*/)?");
                        i = runEnd; // Consume the slash after the globstar too.
                        break;
                    }

                    // A trailing /** matches every descendant. The anchored flag
                    // preserves that meaning for the root pattern '/**' after its
                    // leading slash has been removed by the parser.
                    if (i > 0 || anchored)
                        sb.Append(".*");
                    else
                        sb.Append("[^/]*");
                    i = runEnd - 1;
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    var closingBracket = GitPathTextNormalizer.FindCharacterClassEnd(span, i + 1);
                    if (closingBracket < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }

                    if (!TryAppendCharacterClass(
                            sb,
                            span[(i + 1)..closingBracket],
                            sourceSpan[(i + 1)..closingBracket],
                            ignoreAsciiCase))
                    {
                        regex = string.Empty;
                        return false;
                    }

                    i = closingBracket;
                    break;
                case '\\':
                    if (i + 1 < span.Length)
                        AppendEscapedRegexCharacter(sb, span[++i], insideCharacterClass: false);
                    break;
                case '.' or '(' or ')' or '+' or '|' or '^' or '$' or '{' or '}' or ']':
                    sb.Append('\\').Append(current);
                    break;
                default:
                    sb.Append(current);
                    break;
            }
        }

        regex = sb.ToString();
        return true;
    }

    private static bool MatchesEveryDirectChildName(string pattern)
    {
        var lastSlash = pattern.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash == pattern.Length - 1)
            return false;

        var segment = pattern.AsSpan(lastSlash + 1);
        var hasAsterisk = false;
        var questionMarkCount = 0;
        foreach (var current in segment)
        {
            switch (current)
            {
                case '*':
                    hasAsterisk = true;
                    break;
                case '?':
                    questionMarkCount++;
                    break;
                default:
                    return false;
            }
        }

        // A direct child always has a non-empty name. '*' covers every name;
        // one '?' combined with '*' still covers every non-empty name. More
        // question marks impose a minimum length and are therefore partial.
        return hasAsterisk && questionMarkCount <= 1;
    }

    private static bool TryAppendCharacterClass(
        StringBuilder builder,
        ReadOnlySpan<char> content,
        ReadOnlySpan<char> sourceContent,
        bool ignoreAsciiCase)
    {
        var index = 0;
        var negated = false;
        if (index < content.Length && content[index] is '!' or '^')
        {
            negated = true;
            index++;
        }

        var classBody = new StringBuilder(content.Length * 2);
        var hasMatchableCharacter = false;
        var hasPreviousCharacter = false;
        var previousCharacter = '\0';
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '\\')
            {
                if (++index >= content.Length)
                    return false;

                current = content[index++];
                AppendCharacterClassLiteral(classBody, current, ref hasMatchableCharacter);
                previousCharacter = current;
                hasPreviousCharacter = true;
                continue;
            }

            if (current == '-' && hasPreviousCharacter && index + 1 < content.Length)
            {
                var rangeEnd = content[++index];
                if (rangeEnd == '\\')
                {
                    if (++index >= content.Length)
                        return false;
                    rangeEnd = content[index];
                }

                if (!TryAppendCharacterClassRange(
                        classBody,
                        previousCharacter,
                        rangeEnd,
                        ignoreAsciiCase,
                        ref hasMatchableCharacter))
                {
                    return false;
                }

                hasPreviousCharacter = false;
                index++;
                continue;
            }

            if (current == '[' && index + 1 < content.Length && content[index + 1] == ':')
            {
                var relativeEnd = content[(index + 2)..].IndexOf(']');
                if (relativeEnd < 0)
                {
                    AppendCharacterClassLiteral(classBody, current, ref hasMatchableCharacter);
                    previousCharacter = current;
                    hasPreviousCharacter = true;
                    index++;
                    continue;
                }

                var posixClassEnd = index + 2 + relativeEnd;
                if (posixClassEnd > index + 1 && content[posixClassEnd - 1] == ':')
                {
                    var classNameStart = index + 2;
                    var classNameLength = posixClassEnd - classNameStart - 1;
                    if (!TryAppendPosixCharacterClass(
                            classBody,
                            sourceContent.Slice(classNameStart, classNameLength),
                            ignoreAsciiCase))
                    {
                        return false;
                    }

                    hasMatchableCharacter = true;
                    hasPreviousCharacter = false;
                    index = posixClassEnd + 1;
                    continue;
                }
            }

            AppendCharacterClassLiteral(classBody, current, ref hasMatchableCharacter);
            previousCharacter = current;
            hasPreviousCharacter = true;
            index++;
        }

        if (negated)
        {
            builder.Append("[^/").Append(classBody).Append(']');
            return true;
        }

        if (!hasMatchableCharacter)
        {
            builder.Append("(?!)");
            return true;
        }

        builder.Append('[').Append(classBody).Append(']');
        return true;
    }

    private static bool TryAppendPosixCharacterClass(
        StringBuilder builder,
        ReadOnlySpan<char> className,
        bool ignoreAsciiCase)
    {
        var fragment = className.ToString() switch
        {
            "alnum" => @"\u0030-\u0039\u0041-\u005A\u0061-\u007A",
            "alpha" => @"\u0041-\u005A\u0061-\u007A",
            "blank" => @"\u0009\u0020",
            "cntrl" => @"\u0000-\u001F\u007F",
            "digit" => @"\u0030-\u0039",
            "graph" => @"\u0021-\u002E\u0030-\u007E",
            "lower" => @"\u0061-\u007A",
            "print" => @"\u0020-\u002E\u0030-\u007E",
            "punct" => @"\u0021-\u002E\u003A-\u0040\u005B-\u0060\u007B-\u007E",
            "space" => @"\u0009-\u000D\u0020",
            "upper" when ignoreAsciiCase => @"\u0061-\u007A",
            "upper" => @"\u0041-\u005A",
            "xdigit" => @"\u0030-\u0039\u0041-\u0046\u0061-\u0066",
            _ => null
        };
        if (fragment is null)
            return false;

        builder.Append(fragment);
        return true;
    }

    private static void AppendCharacterClassLiteral(
        StringBuilder builder,
        char value,
        ref bool hasMatchableCharacter)
    {
        if (value == '/')
            return;

        AppendCharacterClassCodeUnit(builder, value);
        hasMatchableCharacter = true;
    }

    private static bool TryAppendCharacterClassRange(
        StringBuilder builder,
        char rangeStart,
        char rangeEnd,
        bool ignoreAsciiCase,
        ref bool hasMatchableCharacter)
    {
        if (rangeStart > rangeEnd)
            return false;

        const char slash = '/';
        if (rangeStart < slash)
        {
            AppendCharacterClassRangeSegment(
                builder,
                rangeStart,
                rangeEnd < slash ? rangeEnd : (char)(slash - 1));
            hasMatchableCharacter = true;
        }

        if (rangeEnd > slash)
        {
            AppendCharacterClassRangeSegment(
                builder,
                rangeStart > slash ? rangeStart : (char)(slash + 1),
                rangeEnd);
            hasMatchableCharacter = true;
        }

        if (ignoreAsciiCase)
        {
            var upperStart = rangeStart > 'A' ? rangeStart : 'A';
            var upperEnd = rangeEnd < 'Z' ? rangeEnd : 'Z';
            if (upperStart <= upperEnd)
            {
                AppendCharacterClassRangeSegment(
                    builder,
                    (char)(upperStart + ('a' - 'A')),
                    (char)(upperEnd + ('a' - 'A')));
                hasMatchableCharacter = true;
            }
        }

        return true;
    }

    private static void AppendCharacterClassRangeSegment(
        StringBuilder builder,
        char rangeStart,
        char rangeEnd)
    {
        AppendCharacterClassCodeUnit(builder, rangeStart);
        if (rangeStart == rangeEnd)
            return;

        builder.Append('-');
        AppendCharacterClassCodeUnit(builder, rangeEnd);
    }

    private static void AppendCharacterClassCodeUnit(StringBuilder builder, char value)
    {
        builder
            .Append(@"\u")
            .Append(((int)value).ToString("X4", CultureInfo.InvariantCulture));
    }

    private static string ProjectUtf8BytesForRegex(string value)
    {
        if (IsAscii(value))
            return value;

        return Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value));
    }

    private static string? ProjectUtf8BytesForRegexOrNull(ReadOnlySpan<char> value)
    {
        if (IsAscii(value))
            return null;

        return Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(value.ToString()));
    }

    private static bool IsAscii(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character > 0x7f)
                return false;
        }

        return true;
    }

    private static void AppendEscapedRegexCharacter(
        StringBuilder builder,
        char value,
        bool insideCharacterClass)
    {
        if (value is '.' or '^' or '$' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '*' or '+' or '?' or '\\' ||
            insideCharacterClass && value == '-')
        {
            builder.Append('\\');
        }

        builder.Append(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ComputeStaticPrefix(string pattern)
    {
        var span = pattern.AsSpan();
        if (!span.Contains('\\'))
        {
            var specialIndex = span.IndexOfAny(GlobSpecialChars);
            var prefix = specialIndex < 0 ? pattern : pattern[..specialIndex];
            return prefix.Trim('/');
        }

        var builder = new StringBuilder(pattern.Length);
        for (var index = 0; index < span.Length; index++)
        {
            var current = span[index];
            if (current == '\\' && index + 1 < span.Length)
            {
                builder.Append(span[++index]);
                continue;
            }

            if (current is '*' or '?' or '[')
                break;

            builder.Append(current);
        }

        return builder.ToString().Trim('/');
    }

    private static string TrimUnescapedTrailingSpaces(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] == ' ')
        {
            var backslashCount = 0;
            for (var index = end - 2; index >= 0 && value[index] == '\\'; index--)
                backslashCount++;
            if ((backslashCount & 1) != 0)
                break;

            end--;
        }

        return end == value.Length ? value : value[..end];
    }

    private static bool HasUnescapedTrailingBackslash(string value)
    {
        var backslashCount = 0;
        for (var index = value.Length - 1; index >= 0 && value[index] == '\\'; index--)
            backslashCount++;
        return (backslashCount & 1) != 0;
    }

    private static bool HasUnterminatedCharacterClass(ReadOnlySpan<char> pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                index++;
                continue;
            }

            if (pattern[index] != '[')
                continue;

            var closingBracket = GitPathTextNormalizer.FindCharacterClassEnd(pattern, index + 1);
            if (closingBracket < 0)
                return true;
            index = closingBracket;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizePath(string path)
    {
        // Fast path: check if normalization is needed using Span
        var span = path.AsSpan();
        if (!span.Contains('\\'))
            return path;

        return PathUtility.NormalizeSeparators(path);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizeRelativePath(string path)
    {
        var span = path.AsSpan().TrimStart('/');
        if (span.IndexOf('\\') < 0)
            return span.Length == path.Length ? path : span.ToString();

        return span.ToString().Replace('\\', '/');
    }

    private string NormalizeRelativePathForComparison(string path) =>
        GitPathTextNormalizer.NormalizeObservedPath(
            NormalizeRelativePath(path),
            _normalizeUnicode,
            _ignoreAsciiCase);

    private bool RequiresComparisonNormalization(ReadOnlySpan<char> value) =>
        GitPathTextNormalizer.RequiresObservedPathNormalization(
            value,
            _normalizeUnicode,
            _ignoreAsciiCase);

    private enum RuleMatchKind
    {
        Regex,
        NameLiteral,
        AnchoredPathLiteral,
        UnanchoredPathLiteral,
        AnchoredDirectoryLiteral,
        UnanchoredDirectoryLiteral
    }

    private sealed record Rule(
        Regex? Pattern,
        string LiteralPattern,
        RuleMatchKind MatchKind,
        bool IsNegation,
        bool DirectoryOnly,
        bool MatchByNameOnly,
        bool MatchesEveryDirectChildName,
        string StaticPrefix)
    {
        public bool IsMatch(
            ReadOnlySpan<char> relativePath,
            string normalizedName,
            bool isDirectory,
            StringComparison comparison,
            string? projectedRelativePath = null,
            string? projectedName = null) =>
            MatchKind switch
            {
                RuleMatchKind.NameLiteral => string.Equals(normalizedName, LiteralPattern, comparison),
                RuleMatchKind.AnchoredPathLiteral => relativePath.Equals(LiteralPattern.AsSpan(), comparison),
                RuleMatchKind.UnanchoredPathLiteral => MatchesUnanchoredPathLiteral(relativePath, LiteralPattern, comparison),
                RuleMatchKind.AnchoredDirectoryLiteral => MatchesAnchoredDirectoryLiteral(relativePath, LiteralPattern, isDirectory, comparison),
                RuleMatchKind.UnanchoredDirectoryLiteral => MatchesUnanchoredDirectoryLiteral(relativePath, LiteralPattern, isDirectory, comparison),
                _ => MatchesRegex(
                    relativePath,
                    normalizedName,
                    isDirectory,
                    projectedRelativePath,
                    projectedName)
            };

        private bool MatchesRegex(
            ReadOnlySpan<char> relativePath,
            string normalizedName,
            bool isDirectory,
            string? projectedRelativePath,
            string? projectedName)
        {
            if (MatchByNameOnly)
                return Pattern!.IsMatch(projectedName ?? normalizedName);

            if (!DirectoryOnly || !isDirectory)
            {
                return projectedRelativePath is null
                    ? Pattern!.IsMatch(relativePath)
                    : Pattern!.IsMatch(projectedRelativePath);
            }

            if (projectedRelativePath is not null)
                return Pattern!.IsMatch(projectedRelativePath + "/");

            Span<char> directoryPath = relativePath.Length <= 511
                ? stackalloc char[relativePath.Length + 1]
                : new char[relativePath.Length + 1];
            relativePath.CopyTo(directoryPath);
            directoryPath[^1] = '/';
            return Pattern!.IsMatch(directoryPath);
        }

        private static bool MatchesUnanchoredPathLiteral(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            StringComparison comparison) =>
            relativePath.Equals(literalPattern.AsSpan(), comparison) ||
            HasPathSegmentSuffix(relativePath, literalPattern, comparison);

        private static bool MatchesAnchoredDirectoryLiteral(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            bool isDirectory,
            StringComparison comparison) =>
            isDirectory && relativePath.Equals(literalPattern.AsSpan(), comparison) ||
            StartsWithDirectorySegment(relativePath, literalPattern, comparison);

        private static bool MatchesUnanchoredDirectoryLiteral(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            bool isDirectory,
            StringComparison comparison)
        {
            if (relativePath.Equals(literalPattern.AsSpan(), comparison) ||
                HasPathSegmentSuffix(relativePath, literalPattern, comparison))
            {
                return isDirectory;
            }

            if (StartsWithDirectorySegment(relativePath, literalPattern, comparison))
                return true;

            return ContainsDirectorySegment(relativePath, literalPattern, comparison);
        }

        private static bool HasPathSegmentSuffix(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            if (relativePath.Length <= literalPattern.Length)
                return false;

            var start = relativePath.Length - literalPattern.Length;
            return relativePath[start - 1] == '/' &&
                   relativePath[start..].Equals(literalPattern.AsSpan(), comparison);
        }

        private static bool StartsWithDirectorySegment(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            return relativePath.Length > literalPattern.Length &&
                   relativePath[literalPattern.Length] == '/' &&
                   relativePath[..literalPattern.Length].Equals(literalPattern.AsSpan(), comparison);
        }

        private static bool ContainsDirectorySegment(
            ReadOnlySpan<char> relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            var searchStart = 0;
            while (searchStart < relativePath.Length)
            {
                var localIndex = relativePath[searchStart..].IndexOf(literalPattern.AsSpan(), comparison);
                if (localIndex < 0)
                    return false;
                var index = searchStart + localIndex;

                var hasLeadingSeparator = index > 0 && relativePath[index - 1] == '/';
                var nextIndex = index + literalPattern.Length;
                var hasTrailingSeparator = nextIndex < relativePath.Length && relativePath[nextIndex] == '/';
                if (hasLeadingSeparator && hasTrailingSeparator)
                    return true;

                searchStart = index + 1;
            }

            return false;
        }
    }
}
