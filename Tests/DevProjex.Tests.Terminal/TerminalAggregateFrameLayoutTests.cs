using DevProjex.Infrastructure.ResourceStore;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalAggregateFrameLayoutTests
{
	private static readonly string[] FrameTitleKeys =
	[
		"Settings.Secrets.Title",
		"Terminal.Tui.Exclusions",
		"Terminal.Tui.FileTypes"
	];

	public static TheoryData<string> Languages()
	{
		var data = new TheoryData<string>();
		foreach (var token in CliChoiceSets.Language.Tokens)
			data.Add(token);
		return data;
	}

	[Theory]
	[MemberData(nameof(Languages))]
	public void WideMiniPanelTitlesStayWholeNextToTheirAggregate(string language)
	{
		Assert.True(CliChoiceSets.Language.TryParse(language, out var appLanguage));
		var localization = new LocalizationService(new JsonLocalizationCatalog(), appLanguage);
		var all = localization["Settings.All"];
		foreach (var key in FrameTitleKeys)
		{
			var title = localization[key];
			foreach (var isOnBorder in new[] { true, false })
			{
				var label = TerminalAggregateFrameLayout.FitAggregateLabel(
					title,
					[$"{all} (12,345)", all, string.Empty],
					TerminalWorkspaceSession.WideControlsWidth,
					isOnBorder);
				var titleColumns = TerminalAggregateFrameLayout.ResolveAvailableColumns(
					TerminalWorkspaceSession.WideControlsWidth,
					isOnBorder) - TerminalAggregateControl.MeasureText(isOnBorder, label);
				var fitted = TerminalFrameTitle.Fit(title, titleColumns, useUnicode: true);

				Assert.Equal(TerminalFrameTitle.Normalize(title), fitted);
				Assert.DoesNotContain('…', fitted);
				Assert.True(
					fitted.GetColumns() <= titleColumns,
					$"{language} {key} on border={isOnBorder}: {fitted}");
			}
		}
	}

	[Fact]
	public void AggregateKeepsItsCountWhenTheTitleLeavesRoom()
	{
		var label = TerminalAggregateFrameLayout.FitAggregateLabel(
			"Content processing:",
			["All (5)", "All", string.Empty],
			TerminalWorkspaceSession.WideControlsWidth,
			isOnBorder: true);

		Assert.Equal("All (5)", label);
	}

	[Fact]
	public void AggregateDropsItsCountBeforeItsWordAndKeepsTheMarkerLast()
	{
		var labels = new[] { "Todos (5)", "Todos", string.Empty };

		Assert.Equal(
			"Todos",
			TerminalAggregateFrameLayout.FitAggregateLabel(
				"Tipos de archivo",
				labels,
				panelWidth: 36,
				isOnBorder: true));
		Assert.Equal(
			string.Empty,
			TerminalAggregateFrameLayout.FitAggregateLabel(
				"Procesamiento de contenido:",
				labels,
				TerminalWorkspaceSession.WideControlsWidth,
				isOnBorder: true));
		Assert.Equal(5, TerminalAggregateControl.MeasureText(isOnBorder: true, string.Empty));
	}
}
