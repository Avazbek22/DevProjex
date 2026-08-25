using System.Security;
using System.Text.Json;

namespace DevProjex.Avalonia.Services;

internal readonly record struct DesktopExceptionDescriptor(
	string LocalizationKey,
	string Code);

internal static class DesktopExceptionPresentation
{
	internal const string AccessDeniedCode = "DPX-DESKTOP-ACCESS-DENIED";
	internal const string ResourceUnavailableCode = "DPX-DESKTOP-RESOURCE-UNAVAILABLE";
	internal const string InvalidDataCode = "DPX-DESKTOP-INVALID-DATA";
	internal const string OperationFailedCode = "DPX-DESKTOP-OPERATION-FAILED";

	public static DesktopExceptionDescriptor Resolve(Exception? exception)
	{
		exception = Unwrap(exception);
		return exception switch
		{
			UnauthorizedAccessException or SecurityException => new DesktopExceptionDescriptor(
				"Desktop.Error.AccessDenied",
				AccessDeniedCode),
			InvalidDataException or JsonException or FormatException => new DesktopExceptionDescriptor(
				"Desktop.Error.InvalidData",
				InvalidDataCode),
			FileNotFoundException or DirectoryNotFoundException or IOException or TimeoutException =>
				new DesktopExceptionDescriptor(
					"Desktop.Error.ResourceUnavailable",
					ResourceUnavailableCode),
			_ => new DesktopExceptionDescriptor(
				"Desktop.Error.OperationFailed",
				OperationFailedCode)
		};
	}

	public static string Format(LocalizationService localization, Exception? exception)
	{
		ArgumentNullException.ThrowIfNull(localization);
		var descriptor = Resolve(exception);
		return AppendCode(localization[descriptor.LocalizationKey], descriptor.Code);
	}

	public static string AppendCode(string message, string code) =>
		$"{message}{Environment.NewLine}{Environment.NewLine}{code}";

	private static Exception? Unwrap(Exception? exception)
	{
		while (exception is AggregateException aggregate)
		{
			var flattened = aggregate.Flatten();
			if (flattened.InnerExceptions.Count != 1)
				return exception;
			exception = flattened.InnerExceptions[0];
		}

		return exception;
	}
}
