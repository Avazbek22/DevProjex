using System.Collections.ObjectModel;
using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.ViewModels;

internal sealed class AgentJournalSessionViewModel(
    AgentJournalSession session,
    string project,
    string mode,
    string duration,
    string masked,
    string maskedCompact) : ViewModelBase
{
    public AgentJournalSession Session { get; } = session;
    public string Time => Session.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Client => AgentJournalPresentation.FormatClient(Session.ClientName, Session.ClientVersion);
    public string Mode { get; } = mode;
    public string Project { get; } = project;
    public string Calls => AgentJournalPresentation.FormatNumber(Session.Totals.Calls);
    public string Characters => AgentJournalPresentation.FormatNumber(Session.Totals.ResultCharacters);
    public string Tokens => AgentJournalPresentation.FormatNumber(Session.Totals.EstimatedTokens);
    public string Files => AgentJournalPresentation.FormatNumber(Session.Totals.FilesDelivered);
    public string Masked { get; } = masked;
    public string MaskedCompact { get; } = maskedCompact;
    public string Duration { get; } = duration;
    public bool IsLive => Session.IsLive && Session.Mode == AgentJournalMode.Live;
}

internal sealed class AgentJournalCallViewModel(
    AgentJournalCall call,
    string masked,
    string maskedCompact,
    string notices) : ViewModelBase
{
    public AgentJournalCall Call { get; } = call;
    public string Sequence => Call.Sequence.ToString(CultureInfo.CurrentCulture);
    public string Time => Call.Utc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
    public string Tool => Call.Tool;
    public string Arguments => AgentJournalPresentation.FormatArguments(Call.Arguments);
    public string Revision => Call.Revision?.ToString(CultureInfo.CurrentCulture) ?? "—";
    public string Duration => AgentJournalPresentation.FormatDuration(Call.DurationMs);
    public string Characters => AgentJournalPresentation.FormatNumber(Call.ResultCharacters);
    public string Tokens => AgentJournalPresentation.FormatNumber(Call.EstimatedTokens);
    public string Files => Call.FilesDelivered.ToString(CultureInfo.CurrentCulture);
    public string Masked { get; } = masked;
    public string MaskedCompact { get; } = maskedCompact;
    public string Notices { get; } = notices;
    public string Error => Call.ErrorCode ?? string.Empty;
}

internal sealed class AgentJournalWindowViewModel : ViewModelBase
{
    private readonly LocalizationService _localization;
    private AgentJournalSessionViewModel? _selectedSession;
    private bool _currentProjectOnly;
    private bool _isLoading;
    private string _footerText = string.Empty;

    public AgentJournalWindowViewModel(LocalizationService localization, bool hasCurrentProject)
    {
        _localization = localization;
        HasCurrentProject = hasCurrentProject;
        _currentProjectOnly = hasCurrentProject;
        UpdateLocalization();
    }

    public ObservableCollection<AgentJournalSessionViewModel> Sessions { get; } = [];
    public ObservableCollection<AgentJournalCallViewModel> Calls { get; } = [];

    public AgentJournalSessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (ReferenceEquals(_selectedSession, value))
                return;
            _selectedSession = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasSelectedSession));
        }
    }

    public bool HasSelectedSession => SelectedSession is not null;
    public bool HasCurrentProject { get; }

    public bool CurrentProjectOnly
    {
        get => _currentProjectOnly;
        set
        {
            if (_currentProjectOnly == value)
                return;
            _currentProjectOnly = value;
            RaisePropertyChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading == value)
                return;
            _isLoading = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !IsLoading && Sessions.Count == 0;

    public string FooterText
    {
        get => _footerText;
        set
        {
            if (string.Equals(_footerText, value, StringComparison.Ordinal))
                return;
            _footerText = value;
            RaisePropertyChanged();
        }
    }

    public string WindowTitle { get; private set; } = string.Empty;
    public string CurrentProjectText { get; private set; } = string.Empty;
    public string EmptyText { get; private set; } = string.Empty;
    public string ExportText { get; private set; } = string.Empty;
    public string ClearText { get; private set; } = string.Empty;
    public string LiveText { get; private set; } = string.Empty;
    public string TimeText { get; private set; } = string.Empty;
    public string ClientText { get; private set; } = string.Empty;
    public string ModeText { get; private set; } = string.Empty;
    public string ProjectText { get; private set; } = string.Empty;
    public string CallsText { get; private set; } = string.Empty;
    public string CharactersText { get; private set; } = string.Empty;
    public string TokensText { get; private set; } = string.Empty;
    public string FilesText { get; private set; } = string.Empty;
    public string MaskedText { get; private set; } = string.Empty;
    public string DurationText { get; private set; } = string.Empty;
    public string NumberText { get; private set; } = string.Empty;
    public string ToolText { get; private set; } = string.Empty;
    public string ArgumentsText { get; private set; } = string.Empty;
    public string RevisionText { get; private set; } = string.Empty;
    public string NoticesText { get; private set; } = string.Empty;
    public string ErrorText { get; private set; } = string.Empty;

    public void ReplaceSessions(IReadOnlyList<AgentJournalSessionViewModel> sessions)
    {
        var selectedId = SelectedSession?.Session.Id;
        Sessions.Clear();
        foreach (var session in sessions)
            Sessions.Add(session);

        SelectedSession = Sessions.FirstOrDefault(item =>
            string.Equals(item.Session.Id, selectedId, StringComparison.Ordinal)) ?? Sessions.FirstOrDefault();
        RaisePropertyChanged(nameof(IsEmpty));
    }

    public void ReplaceCalls(IReadOnlyList<AgentJournalCallViewModel> calls)
    {
        Calls.Clear();
        foreach (var call in calls)
            Calls.Add(call);
    }

    public void UpdateLocalization()
    {
        WindowTitle = _localization["AgentJournal.Title"];
        CurrentProjectText = _localization["AgentJournal.CurrentProject"];
        EmptyText = _localization["AgentJournal.Empty"];
        ExportText = _localization["AgentJournal.Export"];
        ClearText = _localization["AgentJournal.Clear"];
        LiveText = _localization["AgentJournal.Live"];
        TimeText = _localization["AgentJournal.Column.Time"];
        ClientText = _localization["AgentJournal.Column.Client"];
        ModeText = _localization["AgentJournal.Column.Mode"];
        ProjectText = _localization["AgentJournal.Column.Project"];
        CallsText = _localization["AgentJournal.Column.Calls"];
        CharactersText = _localization["AgentJournal.Column.Characters"];
        TokensText = _localization["AgentJournal.Column.Tokens"];
        FilesText = _localization["AgentJournal.Column.Files"];
        MaskedText = _localization["AgentJournal.Column.Masked"];
        DurationText = _localization["AgentJournal.Column.Duration"];
        NumberText = _localization["AgentJournal.Column.Number"];
        ToolText = _localization["AgentJournal.Column.Tool"];
        ArgumentsText = _localization["AgentJournal.Column.Arguments"];
        RevisionText = _localization["AgentJournal.Column.Revision"];
        NoticesText = _localization["AgentJournal.Column.Notices"];
        ErrorText = _localization["AgentJournal.Column.Error"];

        foreach (var property in GetType().GetProperties()
                     .Where(static property => property.PropertyType == typeof(string)))
        {
            RaisePropertyChanged(property.Name);
        }
    }
}

internal static class AgentJournalPresentation
{
    public static string FormatNumber(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#M", CultureInfo.CurrentCulture),
        >= 1_000 => (value / 1_000d).ToString("0.#K", CultureInfo.CurrentCulture),
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    public static string FormatNumber(long value, AppLanguage language)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(AppLanguageUtility.ToCode(language));
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.CurrentCulture;
        }

        return value switch
        {
            >= 1_000_000 => (value / 1_000_000d).ToString("0.#M", culture),
            >= 1_000 => (value / 1_000d).ToString("0.#K", culture),
            _ => value.ToString("N0", culture)
        };
    }

    public static string FormatDuration(long durationMs) => durationMs switch
    {
        < 1_000 => $"{durationMs.ToString(CultureInfo.CurrentCulture)} ms",
        _ => TimeSpan.FromMilliseconds(durationMs).ToString("g", CultureInfo.CurrentCulture)
    };

    public static string FormatSessionDuration(AgentJournalSession session, DateTimeOffset now)
    {
        var end = session.EndedUtc ?? now;
        var duration = end >= session.StartedUtc ? end - session.StartedUtc : TimeSpan.Zero;
        return duration.ToString("g", CultureInfo.CurrentCulture);
    }

    public static string FormatClient(string name, string version) =>
        string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";

    public static string FormatArguments(IReadOnlyDictionary<string, string> arguments) =>
        string.Join(", ", arguments
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}={CollapseLine(pair.Value)}"));

    public static string FormatProject(AgentJournalSession session)
    {
        if (session.Roots.Count == 0)
            return "—";
        if (session.Roots.Count == 1)
            return session.Roots[0].Name;
        return $"{session.Roots[0].Name} +{session.Roots.Count - 1}";
    }

    private static string CollapseLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
}

internal static class AgentActivityPresentation
{
    public static string FormatCount(
        LocalizationService localization,
        string key,
        long count)
    {
        var plural = ResolvePlural(localization.CurrentLanguage, count);
        return localization.Format(
            $"{key}.{plural}",
            AgentJournalPresentation.FormatNumber(count, localization.CurrentLanguage));
    }

    private static string ResolvePlural(AppLanguage language, long count)
    {
        var absolute = count == long.MinValue ? long.MaxValue : Math.Abs(count);
        var modulo10 = absolute % 10;
        var modulo100 = absolute % 100;
        return language switch
        {
            AppLanguage.Ru or AppLanguage.Uk => modulo10 == 1 && modulo100 != 11
                ? "One"
                : modulo10 is >= 2 and <= 4 && modulo100 is not (>= 12 and <= 14)
                    ? "Few"
                    : "Many",
            AppLanguage.Pl => absolute == 1
                ? "One"
                : modulo10 is >= 2 and <= 4 && modulo100 is not (>= 12 and <= 14)
                    ? "Few"
                    : "Many",
            AppLanguage.Fr => absolute is 0 or 1 ? "One" : "Other",
            _ => absolute == 1 ? "One" : "Other"
        };
    }
}
