using Avalonia.Controls.Primitives;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Avalonia.Services;

internal static class ThemedToolTipService
{
	private static readonly IDisposable LoadedSubscription = Control.LoadedEvent.AddClassHandler<ToolTip>(
		static (toolTip, _) => ApplyBackdrop(toolTip),
		RoutingStrategies.Direct,
		handledEventsToo: true);

	public static void Initialize()
		=> GC.KeepAlive(LoadedSubscription);

	internal static bool ApplyBackdrop(ToolTip toolTip)
	{
		var popupLevel = TopLevel.GetTopLevel(toolTip);
		if (popupLevel is null)
			return false;

		var host = ResolveHostTopLevel(popupLevel);
		if (host is null)
			return false;

		var effect = ResolveThemeEffect(toolTip, host);
		return PopupBackdropConfigurator.TryApplyToTopLevel(
			popupLevel,
			host,
			effect,
			PopupBackdropTransparencyFallback.Transparent);
	}

	internal static TopLevel? ResolveHostTopLevel(TopLevel popupLevel)
	{
		var current = popupLevel;
		while (current is PopupRoot { ParentTopLevel: { } parent } &&
		       !ReferenceEquals(current, parent))
		{
			current = parent;
		}

		return ReferenceEquals(current, popupLevel) ? null : current;
	}

	private static ThemeEffectMode ResolveThemeEffect(ToolTip toolTip, TopLevel? host)
	{
		if (host?.DataContext is MainWindowViewModel hostViewModel)
			return hostViewModel.ActiveThemeEffect;

		if (toolTip.DataContext is MainWindowViewModel toolTipViewModel)
			return toolTipViewModel.ActiveThemeEffect;

		return ThemeEffectMode.Solid;
	}
}
