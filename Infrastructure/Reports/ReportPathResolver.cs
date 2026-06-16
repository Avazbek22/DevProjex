using System.Globalization;

namespace DevProjex.Infrastructure.Reports;

public sealed class ReportPathResolver(
	Func<Environment.SpecialFolder, string>? specialFolderPathProvider = null,
	Func<string?>? userProfilePathProvider = null,
	Func<string>? tempPathProvider = null,
	Func<DateTimeOffset>? utcNowProvider = null)
{
	private const string AppFolderName = "DevProjex";
	private const string ReportsFolderName = "reports";

	private readonly Func<Environment.SpecialFolder, string> _specialFolderPathProvider =
		specialFolderPathProvider ?? Environment.GetFolderPath;
	private readonly Func<string?> _userProfilePathProvider =
		userProfilePathProvider ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	private readonly Func<string> _tempPathProvider = tempPathProvider ?? Path.GetTempPath;
	private readonly Func<DateTimeOffset> _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

	public string Resolve(StartupReportOptions reportOptions)
	{
		if (!string.IsNullOrWhiteSpace(reportOptions.Path))
			return Path.GetFullPath(reportOptions.Path);

		var baseDirectory = ResolveBaseDirectory();
		var timestamp = _utcNowProvider().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
		return Path.Combine(baseDirectory, AppFolderName, ReportsFolderName, $"devprojex-report-{timestamp}.json");
	}

	private string ResolveBaseDirectory()
	{
		var documents = _specialFolderPathProvider(Environment.SpecialFolder.MyDocuments);
		if (!string.IsNullOrWhiteSpace(documents))
			return documents;

		var userProfile = _userProfilePathProvider();
		if (!string.IsNullOrWhiteSpace(userProfile))
			return userProfile;

		return _tempPathProvider();
	}
}
