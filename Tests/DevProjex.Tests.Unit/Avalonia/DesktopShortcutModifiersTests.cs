using Avalonia.Input;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class DesktopShortcutModifiersTests
{
	[Theory]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Control, true)]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Meta, false)]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Control, true)]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Meta, false)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Control, true)]
	public void IsPrimary_MatchesPlatformPrimaryAndMacOSControlAlias(
		DesktopPlatform platform,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsPrimary(modifiers));
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Control)]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Control)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta)]
	public void PrimaryModifier_UsesNativePlatformModifier(
		DesktopPlatform platform,
		KeyModifiers expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.PrimaryModifier);
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Control | KeyModifiers.Shift, true)]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Meta | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Shift, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Control | KeyModifiers.Shift, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Alt, false)]
	public void IsPrimaryWithShift_RequiresExactCombination(
		DesktopPlatform platform,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsPrimaryWithShift(modifiers));
	}

	[Theory]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Control | KeyModifiers.Alt, true)]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Alt, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Control | KeyModifiers.Alt, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Shift, false)]
	public void IsPrimaryWithAlt_RequiresExactCombination(
		DesktopPlatform platform,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsPrimaryWithAlt(modifiers));
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows, Key.W, KeyModifiers.Control, true)]
	[InlineData(DesktopPlatform.Windows, Key.E, KeyModifiers.Control | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.MacOS, Key.E, KeyModifiers.Meta | KeyModifiers.Shift, true)]
	[InlineData(DesktopPlatform.MacOS, Key.W, KeyModifiers.Control, true)]
	[InlineData(DesktopPlatform.MacOS, Key.W, KeyModifiers.Meta, false)]
	[InlineData(DesktopPlatform.MacOS, Key.E, KeyModifiers.Control | KeyModifiers.Shift, false)]
	public void IsCollapseAll_UsesMacOSReverseActionWithoutBindingCommandW(
		DesktopPlatform platform,
		Key key,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsCollapseAll(key, modifiers));
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Control, true)]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Control | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.Linux, KeyModifiers.Control | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.Windows, KeyModifiers.Meta | KeyModifiers.Shift, false)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Meta | KeyModifiers.Shift, true)]
	[InlineData(DesktopPlatform.MacOS, KeyModifiers.Control | KeyModifiers.Shift, true)]
	public void IsPrimaryWithOptionalShift_CoversPhysicalPlusKeyModifiers(
		DesktopPlatform platform,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsPrimaryWithOptionalShift(modifiers));
	}

	[Theory]
	[InlineData(DesktopPlatform.MacOS, Key.W, KeyModifiers.Meta, true)]
	[InlineData(DesktopPlatform.MacOS, Key.W, KeyModifiers.Control, false)]
	[InlineData(DesktopPlatform.MacOS, Key.E, KeyModifiers.Meta, false)]
	[InlineData(DesktopPlatform.Windows, Key.W, KeyModifiers.Meta, false)]
	public void IsUnboundMacOSCommandW_MatchesOnlyCommandW(
		DesktopPlatform platform,
		Key key,
		KeyModifiers modifiers,
		bool expected)
	{
		var shortcuts = new DesktopShortcutModifiers(platform);

		Assert.Equal(expected, shortcuts.IsUnboundMacOSCommandW(key, modifiers));
	}
}
