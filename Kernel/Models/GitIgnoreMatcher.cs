using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace DevProjex.Kernel.Models;

public sealed class GitIgnoreMatcher
{
    private readonly string _normalizedRootPath;
    private readonly IReadOnlyList<Rule> _rules;
    private readonly StringComparison _pathComparison;

    public static GitIgnoreMatcher Empty { get; } = new(string.Empty, Array.Empty<Rule>());

    private GitIgnoreMatcher(string normalizedRootPath, IReadOnlyList<Rule> rules)
    {
        _normalizedRootPath = normalizedRootPath;
        _rules = rules;
        _pathComparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    public bool HasNegationRules => _rules.Any(static rule => rule.IsNegation);

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

            var globRegex = GlobToRegex(line);
            var regexPattern = matchByNameOnly
                ? $"^{globRegex}$"
                : BuildPathRegex(globRegex, anchored, directoryOnly);

            rules.Add(new Rule(
                new Regex(regexPattern, regexOptions),
                isNegation,
                directoryOnly,
                matchByNameOnly,
                ComputeStaticPrefix(line)));
        }

        return new GitIgnoreMatcher(normalizedRoot, rules);
    }

    public bool IsIgnored(string fullPath, bool isDirectory, string name)
    {
        var relativePath = GetRelativePath(fullPath);
        if (relativePath is null)
            return false;

        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath) : name;
        var ignored = false;

        foreach (var rule in _rules)
        {
            var target = rule.MatchByNameOnly ? normalizedName : relativePath;
            if (!rule.Pattern.IsMatch(target))
                continue;

            ignored = !rule.IsNegation;
        }

        return ignored;
    }

    public bool ShouldTraverseIgnoredDirectory(string fullPath, string name)
    {
        if (!HasNegationRules)
            return false;

        var relativePath = GetRelativePath(fullPath);
        if (relativePath is null)
            return false;

        foreach (var rule in _rules)
        {
            if (!rule.IsNegation)
                continue;

            if (rule.MatchByNameOnly)
                return true;

            if (rule.StaticPrefix.Length == 0)
                return true;

            var rulePrefixWithSlash = $"{rule.StaticPrefix}/";
            var relativeWithSlash = $"{relativePath}/";

            if (rulePrefixWithSlash.StartsWith(relativeWithSlash, _pathComparison))
                return true;

            if (relativeWithSlash.StartsWith(rulePrefixWithSlash, _pathComparison))
                return true;

            if (string.Equals(rule.StaticPrefix, relativePath, _pathComparison))
                return true;
        }

        return false;
    }

    private string? GetRelativePath(string fullPath)
    {
        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
            return null;

        var normalizedFullPath = NormalizePath(fullPath);
        if (!normalizedFullPath.StartsWith(_normalizedRootPath, _pathComparison))
            return null;

        var relativePath = normalizedFullPath[_normalizedRootPath.Length..].TrimStart('/');
        return relativePath.Length == 0 ? null : relativePath;
    }

    private static string BuildPathRegex(string globRegex, bool anchored, bool directoryOnly)
    {
        var prefix = anchored ? "^" : "^(?:.*/)?";
        var suffix = directoryOnly ? "(?:/.*)?$" : "$";
        return $"{prefix}{globRegex}{suffix}";
    }

    private static string GlobToRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length * 2);
        var inCharClass = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];

            if (inCharClass)
            {
                // Inside character class - pass through most characters,
                // but handle closing bracket and escape special regex chars
                if (current == ']')
                {
                    sb.Append(']');
                    inCharClass = false;
                }
                else if (current == '\\' && i + 1 < pattern.Length)
                {
                    // Escape sequence in character class
                    sb.Append('\\').Append(pattern[++i]);
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
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // Check if ** is followed by /
                        if (i + 2 < pattern.Length && pattern[i + 2] == '/')
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

    private static string ComputeStaticPrefix(string pattern)
    {
        var idx = pattern.IndexOfAny(['*', '?', '[']);
        var prefix = idx < 0 ? pattern : pattern[..idx];
        return prefix.Trim('/');
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private sealed record Rule(
        Regex Pattern,
        bool IsNegation,
        bool DirectoryOnly,
        bool MatchByNameOnly,
        string StaticPrefix);
}
