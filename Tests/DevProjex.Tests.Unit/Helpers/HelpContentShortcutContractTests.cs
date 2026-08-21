using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit.Helpers;

public sealed class HelpContentShortcutContractTests
{
	[Theory]
	[InlineData(DesktopPlatform.Windows)]
	[InlineData(DesktopPlatform.MacOS)]
	[InlineData(DesktopPlatform.Linux)]
	public void GetHelpBody_RendersDesktopShortcutsForEveryLanguage(
		DesktopPlatform platform)
	{
		var provider = new HelpContentProvider(platform);
		foreach (var language in Enum.GetValues<AppLanguage>())
		{
			var help = provider.GetHelpBody(language);

			Assert.DoesNotContain("{mod}", help, StringComparison.Ordinal);
			Assert.DoesNotContain("{shift}", help, StringComparison.Ordinal);
			Assert.DoesNotContain("{alt}", help, StringComparison.Ordinal);
			Assert.DoesNotContain("{collapseAll}", help, StringComparison.Ordinal);
			if (platform == DesktopPlatform.MacOS)
			{
				Assert.Contains("⌘⇧E", help, StringComparison.Ordinal);
				Assert.DoesNotContain("Ctrl+", help, StringComparison.Ordinal);
			}
			else
			{
				Assert.Contains("Ctrl+W", help, StringComparison.Ordinal);
				Assert.DoesNotContain('⌘', help);
				Assert.DoesNotContain('⇧', help);
				Assert.DoesNotContain('⌥', help);
			}
		}
	}
}
