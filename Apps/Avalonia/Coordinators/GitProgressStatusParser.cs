namespace DevProjex.Avalonia.Coordinators;

public static class GitProgressStatusParser
{
    public static bool TryParseTrailingPercent(string status, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var trimmed = status.Trim();
        if (!trimmed.EndsWith('%'))
            return false;

        var lastSpace = trimmed.LastIndexOf(' ');
        var token = lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed;
        token = token.TrimEnd('%');

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) ||
               double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out percent);
    }
}
