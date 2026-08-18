namespace DevProjex.Application.Secrets;

public static class SecretRedactionLegend
{
	public const string PlaceholderPrefix = "DEVPROJEX_REDACTED[";

	public static string CreatePlaceholder(string ruleId, int index) =>
		$"{PlaceholderPrefix}{ruleId}#{index}]";
}

public sealed record SecretRedactionLegendText(
	string Notice,
	string NoFindingsNotice)
{
	public static SecretRedactionLegendText English { get; } = new(
		"Do not treat placeholder text as a real value.",
		"The configured rules matched nothing; this is not a safety guarantee.");

	public static SecretRedactionLegendText PrivacyEnglish { get; } = new(
		"Do not treat placeholder text as a real value.",
		"The private-data rules matched nothing; this is not a privacy guarantee.");
}
