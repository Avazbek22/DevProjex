using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceStatusLineTests
{
	private const string Separator = " · ";
	private const string CompactMetrics = "1 200 F  13 D  ~5 740 tok  0 W  0 E";
	private const string LiveContext = "Live context (Claude Code)";
	private const string Compression = "Сжатие недоступно";
	private const string CompactCompression = "C!";
	private const string FocusedDelivery = "Агент получил 3 раза";

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
	public void IndicatorsKeepTheirFullTextWhenCompactMetricsMakeRoom()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[Compression, LiveContext, FocusedDelivery],
			[CompactCompression, LiveContext, FocusedDelivery],
			Separator,
			availableColumns: 118);

		Assert.Equal(
			$"{CompactMetrics}{Separator}{Compression}{Separator}{LiveContext}{Separator}{FocusedDelivery}",
			status);
		Assert.True(status.GetColumns() <= 118);
	}

	[Fact]
	public void CompressionNoticeShortensBeforeTheLiveContextLosesItsClient()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[Compression, LiveContext, FocusedDelivery],
			[CompactCompression, LiveContext, FocusedDelivery],
			Separator,
			availableColumns: 98);

		Assert.Equal(
			$"{CompactMetrics}{Separator}{CompactCompression}{Separator}{LiveContext}{Separator}{FocusedDelivery}",
			status);
	}

	[Fact]
	public void TextThatStillDoesNotFitIsCutAtTheEndWithAnEllipsis()
	{
		var status = TerminalWorkspaceSession.FitStatusLine(
			RussianMetrics,
			CompactMetrics,
			[Compression, LiveContext, FocusedDelivery],
			[CompactCompression, LiveContext, FocusedDelivery],
			Separator,
			availableColumns: 78);

		Assert.StartsWith(
			$"{CompactMetrics}{Separator}{CompactCompression}{Separator}{LiveContext}{Separator}Аг",
			status,
			StringComparison.Ordinal);
		Assert.EndsWith("...", status, StringComparison.Ordinal);
		Assert.Equal(78, status.GetColumns());
	}
}
