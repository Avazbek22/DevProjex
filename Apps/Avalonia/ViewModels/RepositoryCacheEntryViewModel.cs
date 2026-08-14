using System.Globalization;

namespace DevProjex.Avalonia.ViewModels;

public sealed record RepositoryCacheEntryViewModel(
	RepositoryCacheCatalogEntry Entry,
	string DisplayName,
	string DetailsText,
	string RemoveText,
	bool CanDelete,
	string? DeleteToolTip)
{
	public string RepositoryUrl => Entry.RepositoryUrl;

	public string LocalPath => Entry.LocalPath;

	internal static RepositoryCacheEntryViewModel Create(
		RepositoryCacheCatalogEntry entry,
		CultureInfo culture,
		string zipLabel,
		string removeText,
		bool canDelete,
		string activeDeleteToolTip)
	{
		var displayName = entry.ContentKind == RepositoryCacheContentKind.Zip
			? $"{entry.RepositoryName} ({zipLabel})"
			: entry.RepositoryName;
		var branch = string.IsNullOrWhiteSpace(entry.Branch) ? "-" : entry.Branch;
		var details = string.Concat(
			branch,
			" | ",
			FormatByteSize(entry.ApproximateSizeBytes, culture),
			" | ",
			entry.LastOpenedUtc.ToLocalTime().ToString("g", culture));
		return new RepositoryCacheEntryViewModel(
			entry,
			displayName,
			details,
			removeText,
			canDelete,
			canDelete ? null : activeDeleteToolTip);
	}

	internal static string FormatByteSize(long bytes, CultureInfo culture)
	{
		var value = Math.Max(0, bytes);
		var unitIndex = 0;
		var scaled = (double)value;
		while (scaled >= 1024 && unitIndex < 4)
		{
			scaled /= 1024;
			unitIndex++;
		}

		var format = unitIndex == 0 || scaled >= 100 ? "N0" : scaled >= 10 ? "N1" : "N2";
		var unit = unitIndex switch
		{
			0 => "B",
			1 => "KB",
			2 => "MB",
			3 => "GB",
			_ => "TB"
		};
		return string.Concat(scaled.ToString(format, culture), " ", unit);
	}
}
