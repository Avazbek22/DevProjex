using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Avalonia.Services;

internal sealed class DesktopShortcutModifiers
{
	public static DesktopShortcutModifiers Current { get; } =
		new(DesktopPlatformResolver.Resolve());

	public DesktopShortcutModifiers(DesktopPlatform platform)
	{
		if (platform is not (DesktopPlatform.Windows or DesktopPlatform.MacOS or DesktopPlatform.Linux))
			throw new ArgumentOutOfRangeException(nameof(platform), platform, null);

		Platform = platform;
	}

	public DesktopPlatform Platform { get; }

	public KeyModifiers PrimaryModifier =>
		Platform == DesktopPlatform.MacOS
			? KeyModifiers.Meta
			: KeyModifiers.Control;

	public bool IsPrimary(KeyModifiers modifiers) =>
		MatchesPrimaryCombination(modifiers, KeyModifiers.None);

	public bool IsPrimaryWithShift(KeyModifiers modifiers) =>
		MatchesPrimaryCombination(modifiers, KeyModifiers.Shift);

	public bool IsPrimaryWithAlt(KeyModifiers modifiers) =>
		MatchesPrimaryCombination(modifiers, KeyModifiers.Alt);

	public bool IsPrimaryWithOptionalShift(KeyModifiers modifiers) =>
		IsPrimary(modifiers) ||
		Platform == DesktopPlatform.MacOS && IsPrimaryWithShift(modifiers);

	public bool IsCollapseAll(Key key, KeyModifiers modifiers) =>
		Platform == DesktopPlatform.MacOS
			? key == Key.E && modifiers == (KeyModifiers.Meta | KeyModifiers.Shift) ||
			  key == Key.W && modifiers == KeyModifiers.Control
			: key == Key.W && modifiers == KeyModifiers.Control;

	public bool IsMacOSSecondaryClickModifier(KeyModifiers modifiers) =>
		Platform == DesktopPlatform.MacOS &&
		modifiers.HasFlag(KeyModifiers.Control);

	public bool IsUnboundMacOSCommandW(Key key, KeyModifiers modifiers) =>
		Platform == DesktopPlatform.MacOS &&
		key == Key.W &&
		modifiers == KeyModifiers.Meta;

	private bool MatchesPrimaryCombination(
		KeyModifiers modifiers,
		KeyModifiers additionalModifiers)
	{
		if (modifiers == (PrimaryModifier | additionalModifiers))
			return true;

		return Platform == DesktopPlatform.MacOS &&
		       modifiers == (KeyModifiers.Control | additionalModifiers);
	}
}
