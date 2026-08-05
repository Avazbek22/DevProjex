using System.Globalization;

namespace DevProjex.Application.Secrets;

public static class SecretRedactionLegend
{
	public const string CopyFileName = "DEVPROJEX_REDACTIONS.txt";
	public const string PlaceholderPrefix = "DEVPROJEX_REDACTED[";

	public static string CreatePlaceholder(string ruleId, int index) =>
		$"{PlaceholderPrefix}{ruleId}#{index}]";

	public static string CreatePlainText(
		int redactedCount,
		string placeholderExample,
		SecretRedactionLegendText text) =>
		string.Join(Environment.NewLine, BuildPlainLegend(redactedCount, placeholderExample, text));

	public static IReadOnlyList<string> BuildPlainLegend(
		int redactedCount,
		string placeholderExample,
		SecretRedactionLegendText text) =>
	[
		string.Format(CultureInfo.InvariantCulture, text.SummaryFormat, redactedCount),
		string.Format(CultureInfo.InvariantCulture, text.PlaceholderFormat, placeholderExample),
		text.Notice
	];

	public static string CreateMarkdown(
		int redactedCount,
		string placeholderExample,
		SecretRedactionLegendText text) =>
		$"<!--{Environment.NewLine}" +
		CreatePlainText(redactedCount, placeholderExample, text) +
		$"{Environment.NewLine}-->";
}

public sealed record SecretRedactionLegendText(
	string SummaryFormat,
	string PlaceholderFormat,
	string Notice,
	string NoFindingsNotice)
{
	public static SecretRedactionLegendText English { get; } = new(
		"Values redacted by DevProjex before export: {0}.",
		"Placeholders like {0} mark removed secrets.",
		"Do not treat placeholder text as a real value.",
		"The configured rules matched nothing; this is not a safety guarantee.");
}
