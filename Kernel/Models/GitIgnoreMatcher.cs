using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Kernel.Models;

public sealed class GitIgnoreMatcher
{
    private readonly string _normalizedRootPath;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly StringComparison _pathComparison;
    private readonly bool _hasNegationRules;

    // Pre-compiled search values for SIMD-optimized character lookup
    private static readonly SearchValues<char> GlobSpecialChars = SearchValues.Create("*?[");

    public static GitIgnoreMatcher Empty { get; } = new(string.Empty, [], false);

    private GitIgnoreMatcher(string normalizedRootPath, IReadOnlyList<Rule> rules, bool hasNegationRules)
    {
        _normalizedRootPath = normalizedRootPath;
        _rules = rules;
        _hasNegationRules = hasNegationRules;
        _pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    public bool HasNegationRules => _hasNegationRules;

    public readonly record struct IgnoreEvaluation(bool HasMatch, bool IsIgnored);

    public static GitIgnoreMatcher Build(string rootPath, IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Empty;

        var normalizedRoot = NormalizePath(rootPath).TrimEnd('/');
        if (normalizedRoot.Length == 0)
            return Empty;

        var rules = new List<Rule>();
        var regexOptions = RegexOptions.Compiled;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            regexOptions |= RegexOptions.IgnoreCase;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var escapedSpecial = line.StartsWith(@"\#") || line.StartsWith(@"\!");
            if (escapedSpecial)
                line = line[1..];

            // Only treat as negation if not escaped
            var isNegation = !escapedSpecial && line.StartsWith('!');
            if (isNegation)
            {
                line = line[1..];
                if (line.Length == 0)
                    continue;
            }

            line = line.Replace('\\', '/').Trim();
            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
                line = line.TrimEnd('/');

            if (line.Length == 0)
                continue;

            var anchored = line.StartsWith('/');
            if (anchored)
                line = line[1..];

            if (line.Length == 0)
                continue;

            var hasSlash = line.Contains('/');
            var matchByNameOnly = !anchored && !hasSlash && !directoryOnly;

            var matchKind = GetRuleMatchKind(line, anchored, directoryOnly, matchByNameOnly);
            Regex? pattern = null;
            if (matchKind == RuleMatchKind.Regex)
            {
                var globRegex = GlobToRegex(line);
                var regexPattern = matchByNameOnly
                    ? $"^{globRegex}$"
                    : BuildPathRegex(globRegex, anchored, directoryOnly);
                pattern = new Regex(regexPattern, regexOptions);
            }

            rules.Add(new Rule(
                pattern,
                line,
                matchKind,
                isNegation,
                directoryOnly,
                matchByNameOnly,
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

        return new GitIgnoreMatcher(normalizedRoot, rules, hasNegation);
    }

    public IgnoreEvaluation Evaluate(string fullPath, bool isDirectory, string name)
    {
        if (!TryGetRelativePath(fullPath, out var relativePath))
            return default;

        return EvaluateRelativeCore(relativePath, isDirectory, name);
    }

    public IgnoreEvaluation EvaluateRelative(string relativePath, bool isDirectory, string name)
    {
        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(relativePath))
            return default;

        return EvaluateRelativeCore(NormalizeRelativePath(relativePath), isDirectory, name);
    }

    private IgnoreEvaluation EvaluateRelativeCore(string relativePath, bool isDirectory, string name)
    {
        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath) : name;
        var ignored = false;
        var hasMatch = false;

        foreach (var rule in _rules)
        {
            if (!rule.IsMatch(relativePath, normalizedName, isDirectory, _pathComparison))
                continue;

            ignored = !rule.IsNegation;
            hasMatch = true;
        }

        // For directories: if not directly ignored, check if all contents would be ignored
        // Pattern like **/bin/* ignores contents but not the directory itself
        // For UI purposes, if all contents are ignored, the directory should be hidden too
        // Skip this optimization if there are negation rules - they might un-ignore specific files
        if (!ignored && isDirectory && !HasNegationRules)
        {
            var testChildPath = relativePath + "/_";
            foreach (var rule in _rules)
            {
                if (rule.DirectoryOnly || rule.MatchByNameOnly)
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
        if (!HasNegationRules || string.IsNullOrWhiteSpace(relativePath))
            return false;

        return ShouldTraverseIgnoredDirectoryRelativeCore(NormalizeRelativePath(relativePath), name);
    }

    private bool ShouldTraverseIgnoredDirectoryRelativeCore(string relativePath, string name)
    {
        // If the directory itself is ignored by an explicit directory rule (e.g. "bin/"),
        // name-only negation (e.g. "!Directory.Build.rsp") cannot re-include descendants
        // unless a path-based negation re-includes the directory chain.
        var ignoredByDirectoryRule = IsIgnoredByDirectoryRule(relativePath, name);

        foreach (var rule in _rules)
        {
            if (!rule.IsNegation)
                continue;

            // Name-only negation rules (like !keep.txt) can match files anywhere
            // unless the parent directory is excluded by an explicit directory rule.
            if (rule.MatchByNameOnly)
            {
                if (!ignoredByDirectoryRule)
                    return true;
                continue;
            }

            // Path-based negation rules with no static prefix (like !**/*.txt)
            // could match anywhere, so we must traverse
            if (rule.StaticPrefix.Length == 0)
                return true;

            var rulePrefixWithSlash = $"{rule.StaticPrefix}/";
            var relativeWithSlash = $"{relativePath}/";

            // Negation target is inside this directory
            if (rulePrefixWithSlash.StartsWith(relativeWithSlash, _pathComparison))
                return true;

            // This directory is inside the negation target path
            if (relativeWithSlash.StartsWith(rulePrefixWithSlash, _pathComparison))
                return true;

            // Exact match
            if (string.Equals(rule.StaticPrefix, relativePath, _pathComparison))
                return true;
        }

        return false;
    }

    private bool IsIgnoredByDirectoryRule(string relativePath, string name)
    {
        foreach (var rule in _rules)
        {
            if (rule.IsNegation || !rule.DirectoryOnly)
                continue;

            if (rule.IsMatch(relativePath, name, isDirectory: true, _pathComparison))
                return true;
        }

        return false;
    }

    public bool TryGetRelativePath(string fullPath, out string relativePath, bool allowRoot = false)
    {
        relativePath = string.Empty;

        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
            return false;

        var normalizedFullPath = NormalizePath(fullPath);
        if (!normalizedFullPath.StartsWith(_normalizedRootPath, _pathComparison))
            return false;

        if (normalizedFullPath.Length == _normalizedRootPath.Length)
            return allowRoot;

        if (normalizedFullPath[_normalizedRootPath.Length] != '/')
            return false;

        relativePath = normalizedFullPath[(_normalizedRootPath.Length + 1)..];
        return relativePath.Length > 0 || allowRoot;
    }

    private static string BuildPathRegex(string globRegex, bool anchored, bool directoryOnly)
    {
        var prefix = anchored ? "^" : "^(?:.*/)?";
        // Directory-only rules must match directories (or their descendants), not plain files.
        var suffix = directoryOnly ? "/.*$" : "$";
        return $"{prefix}{globRegex}{suffix}";
    }

    private static RuleMatchKind GetRuleMatchKind(
        string pattern,
        bool anchored,
        bool directoryOnly,
        bool matchByNameOnly)
    {
        // Literal rules dominate real .gitignore files. Keeping them out of Regex
        // reduces allocations at build time and avoids a Regex call for every path.
        if (pattern.AsSpan().IndexOfAny(GlobSpecialChars) >= 0)
            return RuleMatchKind.Regex;

        if (matchByNameOnly)
            return RuleMatchKind.NameLiteral;

        if (directoryOnly)
            return anchored
                ? RuleMatchKind.AnchoredDirectoryLiteral
                : RuleMatchKind.UnanchoredDirectoryLiteral;

        return anchored
            ? RuleMatchKind.AnchoredPathLiteral
            : RuleMatchKind.UnanchoredPathLiteral;
    }

    private static string GlobToRegex(string pattern)
    {
        // Pre-size StringBuilder based on typical expansion factor
        var sb = new StringBuilder(pattern.Length * 2);
        var span = pattern.AsSpan();
        var inCharClass = false;

        for (var i = 0; i < span.Length; i++)
        {
            var current = span[i];

            if (inCharClass)
            {
                // Inside character class - pass through most characters,
                // but handle closing bracket and escape special regex chars
                if (current == ']')
                {
                    sb.Append(']');
                    inCharClass = false;
                }
                else if (current == '\\' && i + 1 < span.Length)
                {
                    // Escape sequence in character class
                    sb.Append('\\').Append(span[++i]);
                }
                else
                {
                    sb.Append(current);
                }
                continue;
            }

            switch (current)
            {
                case '*':
                    if (i + 1 < span.Length && span[i + 1] == '*')
                    {
                        // Check if ** is followed by /
                        if (i + 2 < span.Length && span[i + 2] == '/')
                        {
                            // **/ means "zero or more directories"
                            sb.Append("(?:.*/)?");
                            i += 2; // Skip both * and /
                        }
                        else
                        {
                            // ** at end or not followed by / - match anything
                            sb.Append(".*");
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    // Start of character class - preserve it for regex
                    sb.Append('[');
                    inCharClass = true;
                    break;
                case '.':
                case '(':
                case ')':
                case '+':
                case '|':
                case '^':
                case '$':
                case '{':
                case '}':
                case ']':
                case '\\':
                    sb.Append('\\').Append(current);
                    break;
                default:
                    sb.Append(current);
                    break;
            }
        }

        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ComputeStaticPrefix(string pattern)
    {
        // Use SIMD-optimized search for glob special characters
        var span = pattern.AsSpan();
        var idx = span.IndexOfAny(GlobSpecialChars);
        var prefix = idx < 0 ? pattern : pattern[..idx];
        return prefix.Trim('/');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizePath(string path)
    {
        // Fast path: check if normalization is needed using Span
        var span = path.AsSpan();
        if (!span.Contains('\\'))
            return path;

        return path.Replace('\\', '/');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizeRelativePath(string path)
    {
        var span = path.AsSpan().TrimStart('/');
        if (span.IndexOf('\\') < 0)
            return span.Length == path.Length ? path : span.ToString();

        return span.ToString().Replace('\\', '/');
    }

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
        string StaticPrefix)
    {
        public bool IsMatch(
            string relativePath,
            string normalizedName,
            bool isDirectory,
            StringComparison comparison) =>
            MatchKind switch
            {
                RuleMatchKind.NameLiteral => string.Equals(normalizedName, LiteralPattern, comparison),
                RuleMatchKind.AnchoredPathLiteral => string.Equals(relativePath, LiteralPattern, comparison),
                RuleMatchKind.UnanchoredPathLiteral => MatchesUnanchoredPathLiteral(relativePath, LiteralPattern, comparison),
                RuleMatchKind.AnchoredDirectoryLiteral => MatchesAnchoredDirectoryLiteral(relativePath, LiteralPattern, isDirectory, comparison),
                RuleMatchKind.UnanchoredDirectoryLiteral => MatchesUnanchoredDirectoryLiteral(relativePath, LiteralPattern, isDirectory, comparison),
                _ => Pattern!.IsMatch(GetRegexTarget(relativePath, normalizedName, isDirectory))
            };

        private string GetRegexTarget(string relativePath, string normalizedName, bool isDirectory) =>
            MatchByNameOnly
                ? normalizedName
                : DirectoryOnly && isDirectory
                    ? relativePath + "/"
                    : relativePath;

        private static bool MatchesUnanchoredPathLiteral(
            string relativePath,
            string literalPattern,
            StringComparison comparison) =>
            string.Equals(relativePath, literalPattern, comparison) ||
            HasPathSegmentSuffix(relativePath, literalPattern, comparison);

        private static bool MatchesAnchoredDirectoryLiteral(
            string relativePath,
            string literalPattern,
            bool isDirectory,
            StringComparison comparison) =>
            isDirectory && string.Equals(relativePath, literalPattern, comparison) ||
            StartsWithDirectorySegment(relativePath, literalPattern, comparison);

        private static bool MatchesUnanchoredDirectoryLiteral(
            string relativePath,
            string literalPattern,
            bool isDirectory,
            StringComparison comparison)
        {
            if (string.Equals(relativePath, literalPattern, comparison) ||
                HasPathSegmentSuffix(relativePath, literalPattern, comparison))
            {
                return isDirectory;
            }

            if (StartsWithDirectorySegment(relativePath, literalPattern, comparison))
                return true;

            return ContainsDirectorySegment(relativePath, literalPattern, comparison);
        }

        private static bool HasPathSegmentSuffix(
            string relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            if (relativePath.Length <= literalPattern.Length)
                return false;

            var start = relativePath.Length - literalPattern.Length;
            return relativePath[start - 1] == '/' &&
                   relativePath.AsSpan(start).Equals(literalPattern.AsSpan(), comparison);
        }

        private static bool StartsWithDirectorySegment(
            string relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            return relativePath.Length > literalPattern.Length &&
                   relativePath[literalPattern.Length] == '/' &&
                   relativePath.AsSpan(0, literalPattern.Length).Equals(literalPattern.AsSpan(), comparison);
        }

        private static bool ContainsDirectorySegment(
            string relativePath,
            string literalPattern,
            StringComparison comparison)
        {
            var searchStart = 0;
            while (searchStart < relativePath.Length)
            {
                var index = relativePath.IndexOf(literalPattern, searchStart, comparison);
                if (index < 0)
                    return false;

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
