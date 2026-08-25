using DevProjex.Application.Secrets;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Execution;

internal static class UnscannableFileOutput
{
	public static void Write(
		TextWriter writer,
		string projectRoot,
		IReadOnlyList<UnscannableFile> files,
		LocalizationService localization)
	{
		if (files.Count == 0)
			return;
		writer.WriteLine(localization.Format("Content.Redaction.UnscannableFiles", files.Count));
		foreach (var value in FormatEntries(projectRoot, files, localization))
			writer.WriteLine("  " + value);
	}

	public static string FormatSummary(
		string projectRoot,
		IReadOnlyList<UnscannableFile> files,
		LocalizationService localization) =>
		string.Join(Environment.NewLine, FormatEntries(projectRoot, files, localization));

	public static string ToReasonToken(FileContentClassification classification) => classification switch
	{
		FileContentClassification.TooLarge => "too-large",
		FileContentClassification.UnsupportedEncoding => "unsupported-encoding",
		_ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
	};

	private static IEnumerable<string> FormatEntries(
		string projectRoot,
		IReadOnlyList<UnscannableFile> files,
		LocalizationService localization) =>
		files
			.Select(file => string.Concat(
				NormalizePath(projectRoot, file.Path),
				" - ",
				localization[ResolveReasonKey(file.Classification)]))
			.OrderBy(static value => value, StringComparer.Ordinal);

	private static string NormalizePath(string projectRoot, string path)
	{
		try
		{
			return TerminalTextEscaping.EscapeSingleLine(
				PathUtility.GetPortableRelativePath(projectRoot, path));
		}
		catch (ArgumentException)
		{
			return TerminalTextEscaping.EscapeSingleLine(Path.GetFileName(path));
		}
	}

	private static string ResolveReasonKey(FileContentClassification classification) => classification switch
	{
		FileContentClassification.TooLarge => "Content.Redaction.Reason.TooLarge",
		FileContentClassification.UnsupportedEncoding => "Content.Redaction.Reason.UnsupportedEncoding",
		_ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
	};
}
