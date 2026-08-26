using System.ComponentModel;
using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ExternalLinkLauncherTests
{
	[Fact]
	public void TryOpenReportsShellLaunchFailureWithoutThrowing()
	{
		var result = ExternalLinkLauncher.TryOpen(
			"https://example.test/project",
			_ => throw new Win32Exception("No registered browser."));

		Assert.False(result);
	}

	[Fact]
	public void TryOpenUsesShellExecutionForTheExactUrl()
	{
		ProcessStartInfo? captured = null;

		var result = ExternalLinkLauncher.TryOpen(
			"https://example.test/project",
			startInfo =>
			{
				captured = startInfo;
				return null;
			});

		Assert.False(result);
		Assert.NotNull(captured);
		Assert.True(captured.UseShellExecute);
		Assert.Equal("https://example.test/project", captured.FileName);
	}
}
