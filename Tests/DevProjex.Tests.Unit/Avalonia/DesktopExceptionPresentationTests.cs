using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class DesktopExceptionPresentationTests
{
	[Fact]
	public void Resolve_MapsExpectedFailureCategoriesToStableCodes()
	{
		Assert.Equal(
			DesktopExceptionPresentation.AccessDeniedCode,
			DesktopExceptionPresentation.Resolve(new UnauthorizedAccessException()).Code);
		Assert.Equal(
			DesktopExceptionPresentation.ResourceUnavailableCode,
			DesktopExceptionPresentation.Resolve(new IOException()).Code);
		Assert.Equal(
			DesktopExceptionPresentation.InvalidDataCode,
			DesktopExceptionPresentation.Resolve(new InvalidDataException()).Code);
		Assert.Equal(
			DesktopExceptionPresentation.OperationFailedCode,
			DesktopExceptionPresentation.Resolve(new InvalidOperationException()).Code);
	}

	[Fact]
	public void Format_DoesNotExposeExceptionMessageOrLocalPath()
	{
		const string sensitivePath = @"C:\Users\private-user\repository\secret.txt";
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);

		var message = DesktopExceptionPresentation.Format(
			localization,
			new IOException($"Could not read {sensitivePath}."));

		Assert.Contains(DesktopExceptionPresentation.ResourceUnavailableCode, message, StringComparison.Ordinal);
		Assert.DoesNotContain(sensitivePath, message, StringComparison.Ordinal);
		Assert.DoesNotContain("private-user", message, StringComparison.Ordinal);
	}
}
