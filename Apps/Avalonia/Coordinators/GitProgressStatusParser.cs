namespace DevProjex.Avalonia.Coordinators;

public static class GitProgressStatusParser
{
    public static bool TryParseTrailingPercent(string status, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var trimmed = status.AsSpan().Trim();
        if (trimmed.IsEmpty || trimmed[^1] != '%')
            return false;

        return TryParsePercentAt(trimmed, trimmed.Length - 1, out percent);
    }

    public static bool TryParsePercent(string status, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var remaining = status.AsSpan();
        while (true)
        {
            var relativePercentIndex = remaining.IndexOf('%');
            if (relativePercentIndex < 0)
                return false;

            if (TryParsePercentAt(remaining, relativePercentIndex, out percent))
                return true;

            remaining = remaining[(relativePercentIndex + 1)..];
        }
    }

    private static bool TryParsePercentAt(
        ReadOnlySpan<char> value,
        int percentIndex,
        out double percent)
    {
        percent = 0;
        var tokenEnd = percentIndex;
        while (tokenEnd > 0 && char.IsWhiteSpace(value[tokenEnd - 1]))
            tokenEnd--;

        var tokenStart = tokenEnd;
        while (tokenStart > 0 && IsNumericTokenCharacter(value[tokenStart - 1]))
            tokenStart--;

        var token = value[tokenStart..tokenEnd];
        return !token.IsEmpty &&
               (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) ||
                double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out percent)) &&
               percent is >= 0 and <= 100;
    }

    private static bool IsNumericTokenCharacter(char value) =>
        char.IsDigit(value) || value is '.' or ',' or '+' or '-';
}
