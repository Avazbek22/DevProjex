using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

public sealed class DesktopShortcutTextFormatterTests
{
	[Theory]
	[InlineData(
		DesktopPlatform.Windows,
		"Ctrl+B | Shift+Enter | Alt+↑ | Ctrl+W")]
	[InlineData(
		DesktopPlatform.Linux,
		"Ctrl+B | Shift+Enter | Alt+↑ | Ctrl+W")]
	[InlineData(
		DesktopPlatform.MacOS,
		"⌘B | ⇧Enter | ⌥↑ | ⌘⇧E")]
	public void Format_ReplacesEveryDesktopShortcutToken(
		DesktopPlatform platform,
		string expected)
	{
		var result = DesktopShortcutTextFormatter.Format(
			"{mod}B | {shift}Enter | {alt}↑ | {collapseAll}",
			platform);

		Assert.Equal(expected, result);
		Assert.DoesNotContain('{', result);
		Assert.DoesNotContain('}', result);
	}

	[Theory]
	[InlineData(DesktopPlatform.Windows)]
	[InlineData(DesktopPlatform.MacOS)]
	[InlineData(DesktopPlatform.Linux)]
	public void Format_LeavesTextWithoutTokensUnchanged(DesktopPlatform platform)
	{
		const string source = "Enter or F3";

		Assert.Equal(source, DesktopShortcutTextFormatter.Format(source, platform));
	}
}
