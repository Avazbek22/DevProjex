using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceStatusLineTests
{
	private const string Separator = " · ";
	private const string CompactMetrics = "1 200 F  13 D  ~5 740 tok  0 W  0 E";
	private const string LiveContext = "Live context (Claude Code)";
	private const string Activity = "Активность агента: get_file (3 вызова)";
	private const string CompactActivity = "A get_file (3)";

	private static readonly string[] RussianMetrics =
	[
		"Файлы 1 200",
		"Папки 13",
		"996,09 KB",
		"~5 740 токенов",
		"Предупреждения 0",
		"Ошибки 0"
	];

	[Fact]
	public void FullStatusIsKeptWhenItFits()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[LiveContext],
			[LiveContext],
			Separator,
			availableColumns: 138);

		Assert.Equal(string.Join(Separator, [.. RussianMetrics, LiveContext]), status);
	}

	[Fact]
	public void NarrowStatusKeepsErrorsAndTheLiveContextClient()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[LiveContext],
			[LiveContext],
			Separator,
			availableColumns: 78);

		Assert.Equal($"{CompactMetrics}{Separator}{LiveContext}", status);
		Assert.True(status.GetColumns() <= 78);
	}

	[Fact]
	public void ActivityKeepsItsFullTextWhenCompactMetricsMakeRoom()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[LiveContext, Activity],
			[LiveContext, CompactActivity],
			Separator,
			availableColumns: 118);

		Assert.Equal($"{CompactMetrics}{Separator}{LiveContext}{Separator}{Activity}", status);
		Assert.True(status.GetColumns() <= 118);
	}

	[Fact]
	public void ActivityShortensBeforeTheLiveContextLosesItsClient()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[LiveContext, Activity],
			[LiveContext, CompactActivity],
			Separator,
			availableColumns: 98);

		Assert.Equal($"{CompactMetrics}{Separator}{LiveContext}{Separator}{CompactActivity}", status);
	}

	[Fact]
	public void TextThatStillDoesNotFitIsCutAtTheEndWithAnEllipsis()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[LiveContext, Activity],
			[LiveContext, CompactActivity],
			Separator,
			availableColumns: 78);

		Assert.StartsWith($"{CompactMetrics}{Separator}{LiveContext}{Separator}A get", status, StringComparison.Ordinal);
		Assert.EndsWith("...", status, StringComparison.Ordinal);
		Assert.Equal(78, status.GetColumns());
	}
}
