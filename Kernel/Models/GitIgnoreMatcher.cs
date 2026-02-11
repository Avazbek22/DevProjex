using System.Text;
using System.Text.RegularExpressions;

namespace DevProjex.Kernel.Models;

public sealed class GitIgnoreMatcher
{
    private readonly string _normalizedRootPath;
    private readonly IReadOnlyList<Rule> _rules;

    public static GitIgnoreMatcher Empty { get; } = new(string.Empty, Array.Empty<Rule>());

    private GitIgnoreMatcher(string normalizedRootPath, IReadOnlyList<Rule> rules)
    {
        _normalizedRootPath = normalizedRootPath;
        _rules = rules;
    }

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

            if (line.StartsWith(@"\#") || line.StartsWith(@"\!"))
                line = line[1..];

            var isNegation = line.StartsWith('!');
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
            var matchByNameOnly = !anchored && !hasSlash;

            var globRegex = GlobToRegex(line);
            var regexPattern = matchByNameOnly
                ? $"^{globRegex}$"
                : BuildPathRegex(globRegex, anchored, directoryOnly);

            rules.Add(new Rule(
                new Regex(regexPattern, regexOptions),
                isNegation,
                directoryOnly,
                matchByNameOnly));
        }

        return new GitIgnoreMatcher(normalizedRoot, rules);
    }

    public bool IsIgnored(string fullPath, bool isDirectory, string name)
    {
        if (_rules.Count == 0 || string.IsNullOrWhiteSpace(fullPath))
            return false;

        var normalizedFullPath = NormalizePath(fullPath);
        if (!normalizedFullPath.StartsWith(_normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            return false;

        var relativePath = normalizedFullPath[_normalizedRootPath.Length..].TrimStart('/');
        if (relativePath.Length == 0)
            return false;

        var normalizedName = string.IsNullOrEmpty(name) ? Path.GetFileName(relativePath) : name;
        var ignored = false;

        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
                continue;

            var target = rule.MatchByNameOnly ? normalizedName : relativePath;
            if (!rule.Pattern.IsMatch(target))
                continue;

            ignored = !rule.IsNegation;
        }

        return ignored;
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
        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            switch (current)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
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
                case '[':
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

    private static string NormalizePath(string path)
        => path.Replace('\\', '/');

    private sealed record Rule(Regex Pattern, bool IsNegation, bool DirectoryOnly, bool MatchByNameOnly);
}
