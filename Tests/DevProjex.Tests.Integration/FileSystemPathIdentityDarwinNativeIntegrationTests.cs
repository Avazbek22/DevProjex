namespace DevProjex.Tests.Integration;

public sealed class FileSystemPathIdentityDarwinNativeIntegrationTests
{
	[Fact]
	public void TryReadLocationOnUnicodeDirectoryReturnsCanonicalDarwinLocation()
	{
		if (!OperatingSystem.IsMacOS())
			Assert.Skip("The Darwin fcntl ABI regression requires a native macOS host.");

		using var workspace = new TemporaryDirectory();
		var unicodeDirectory = workspace.CreateDirectory("Проект-資料-e\u0301");
		var pathIdentityType = typeof(ProjectCopyExportService).Assembly.GetType(
			"DevProjex.Application.Services.FileSystemPathIdentity",
			throwOnError: true)!;
		var method = pathIdentityType.GetMethod(
			"TryReadLocation",
			BindingFlags.Public | BindingFlags.Static);
		Assert.NotNull(method);
		object?[] arguments = [unicodeDirectory, null];

		var success = Assert.IsType<bool>(method.Invoke(null, arguments));

		Assert.True(success);
		Assert.NotNull(arguments[1]);
		var location = arguments[1]!;
		var locationType = location.GetType();
		var namespaceId = Assert.IsType<string>(
			locationType.GetProperty("NamespaceId")!.GetValue(location));
		var canonicalPath = Assert.IsType<string>(
			locationType.GetProperty("CanonicalPath")!.GetValue(location));
		Assert.Equal("darwin", namespaceId);
		Assert.True(Path.IsPathFullyQualified(canonicalPath));
		Assert.True(Directory.Exists(canonicalPath));
		Assert.Equal(
			Path.GetFileName(unicodeDirectory).Normalize(NormalizationForm.FormC),
			Path.GetFileName(canonicalPath).Normalize(NormalizationForm.FormC));
	}
}
