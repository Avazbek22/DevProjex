using System.Globalization;

namespace DevProjex.Application.Updates;

public readonly struct ApplicationReleaseVersion :
    IComparable<ApplicationReleaseVersion>,
    IEquatable<ApplicationReleaseVersion>
{
    private readonly int _major;
    private readonly int _minor;
    private readonly int _build;
    private readonly int _revision;
    private readonly int _componentCount;

    private ApplicationReleaseVersion(
        int major,
        int minor,
        int build,
        int revision,
        int componentCount)
    {
        _major = major;
        _minor = minor;
        _build = build;
        _revision = revision;
        _componentCount = componentCount;
    }

    public static bool TryParse(string? value, out ApplicationReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
            candidate = candidate[1..];

        var metadataSeparator = candidate.IndexOf('+');
        if (metadataSeparator >= 0)
            candidate = candidate[..metadataSeparator];

        // The update channel intentionally consumes stable GitHub releases only.
        // Reject prerelease syntax instead of accidentally ordering it as a stable build.
        if (candidate.Contains('-', StringComparison.Ordinal))
            return false;

        var parts = candidate.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 4)
            return false;

        Span<int> components = stackalloc int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out components[index]))
            {
                return false;
            }
        }

        version = new ApplicationReleaseVersion(
            components[0],
            components[1],
            components[2],
            components[3],
            parts.Length);
        return true;
    }

    public int CompareTo(ApplicationReleaseVersion other)
    {
        var comparison = _major.CompareTo(other._major);
        if (comparison != 0)
            return comparison;

        comparison = _minor.CompareTo(other._minor);
        if (comparison != 0)
            return comparison;

        comparison = _build.CompareTo(other._build);
        return comparison != 0
            ? comparison
            : _revision.CompareTo(other._revision);
    }

    public bool Equals(ApplicationReleaseVersion other)
        => CompareTo(other) == 0;

    public override bool Equals(object? obj)
        => obj is ApplicationReleaseVersion other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(_major, _minor, _build, _revision);

    public override string ToString()
    {
        Span<int> components = [_major, _minor, _build, _revision];
        return string.Join(
            '.',
            components[..Math.Max(_componentCount, 1)].ToArray());
    }
}
