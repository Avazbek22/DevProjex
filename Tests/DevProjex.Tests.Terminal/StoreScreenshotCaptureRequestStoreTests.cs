using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class StoreScreenshotCaptureRequestStoreTests
{
    [Fact]
    public void TryConsume_ValidPrivateRequest_ReturnsRequestAndDeletesEnvelope()
    {
        var sessionDirectory = Path.Combine(
            StoreScreenshotCaptureRequestStore.GetSessionRoot(),
            Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(sessionDirectory, "project");
        var appDataDirectory = Path.Combine(sessionDirectory, "app-data");
        var requestPath = Path.Combine(sessionDirectory, "request.json");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(appDataDirectory);
        try
        {
            var expected = new StoreScreenshotCaptureRequest(
                projectDirectory,
                sessionDirectory,
                appDataDirectory,
                "pt-pt");
            File.WriteAllText(
                requestPath,
                JsonSerializer.Serialize(expected, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                requestPath);

            var actual = StoreScreenshotCaptureRequestStore.TryConsume();

            Assert.Equal(expected, actual);
            Assert.False(File.Exists(requestPath));
            Assert.Null(Environment.GetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                null);
            Directory.Delete(sessionDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryConsume_RequestOutsidePrivateRoot_IsRejectedWithoutDeletingCallerFile()
    {
        var externalDirectory = Path.Combine(
            Path.GetTempPath(),
            "DevProjex-store-request-boundary-tests",
            Guid.NewGuid().ToString("N"));
        var requestPath = Path.Combine(externalDirectory, "request.json");
        Directory.CreateDirectory(externalDirectory);
        File.WriteAllText(requestPath, "{}");
        try
        {
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                requestPath);

            Assert.Null(StoreScreenshotCaptureRequestStore.TryConsume());
            Assert.True(File.Exists(requestPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                null);
            Directory.Delete(externalDirectory, recursive: true);
        }
    }
}
