using System.ComponentModel;
using System.Security;

namespace DevProjex.Avalonia.Services;

internal static class ExternalLinkLauncher
{
	public static bool TryOpen(string url) =>
		TryOpen(url, Process.Start);

	internal static bool TryOpen(
		string url,
		Func<ProcessStartInfo, Process?> startProcess)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);
		ArgumentNullException.ThrowIfNull(startProcess);

		try
		{
			using var process = startProcess(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
			return process is not null;
		}
		catch (Exception exception) when (exception is
		       Win32Exception or
		       InvalidOperationException or
		       IOException or
		       UnauthorizedAccessException or
		       SecurityException or
		       NotSupportedException)
		{
			return false;
		}
	}
}
