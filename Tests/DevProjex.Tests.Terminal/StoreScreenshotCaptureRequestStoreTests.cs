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
    public void TryConsume_LiveContextSelectionInsideProject_ReturnsTheSelection()
    {
        using var session = CaptureSession.Create();
        session.WriteRequest(new
        {
            projectPath = session.ProjectDirectory,
            sessionDirectory = session.SessionDirectory,
            appDataDirectory = session.AppDataDirectory,
            languageCode = "en",
            liveContextSelection = new[] { "Infrastructure/AgentJournal", "Docs\\McpServer.md" }
        });

        var actual = StoreScreenshotCaptureRequestStore.TryConsume();

        Assert.NotNull(actual);
        Assert.Equal(
            ["Infrastructure/AgentJournal", "Docs\\McpServer.md"],
            actual.LiveContextSelection);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Infrastructure/../../outside")]
    [InlineData("./Infrastructure")]
    [InlineData("Infrastructure//AgentJournal")]
    [InlineData("Infrastructure/AgentJournal/")]
    [InlineData("   ")]
    public void TryConsume_LiveContextSelectionThatCanLeaveTheProject_RejectsTheRequest(string entry)
    {
        using var session = CaptureSession.Create();
        session.WriteRequest(new
        {
            projectPath = session.ProjectDirectory,
            sessionDirectory = session.SessionDirectory,
            appDataDirectory = session.AppDataDirectory,
            languageCode = "en",
            liveContextSelection = new[] { "Infrastructure/AgentJournal", entry }
        });

        Assert.Null(StoreScreenshotCaptureRequestStore.TryConsume());
        Assert.False(File.Exists(session.RequestPath));
    }

    [Fact]
    public void TryConsume_RootedOrEmptyLiveContextSelection_RejectsTheRequest()
    {
        using (var rooted = CaptureSession.Create())
        {
            rooted.WriteRequest(new
            {
                projectPath = rooted.ProjectDirectory,
                sessionDirectory = rooted.SessionDirectory,
                appDataDirectory = rooted.AppDataDirectory,
                languageCode = "en",
                liveContextSelection = new[] { Path.Combine(rooted.ProjectDirectory, "Infrastructure") }
            });

            Assert.Null(StoreScreenshotCaptureRequestStore.TryConsume());
        }

        using var empty = CaptureSession.Create();
        empty.WriteRequest(new
        {
            projectPath = empty.ProjectDirectory,
            sessionDirectory = empty.SessionDirectory,
            appDataDirectory = empty.AppDataDirectory,
            languageCode = "en",
            liveContextSelection = Array.Empty<string>()
        });

        Assert.Null(StoreScreenshotCaptureRequestStore.TryConsume());
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

    private sealed class CaptureSession : IDisposable
    {
        private CaptureSession(string sessionDirectory)
        {
            SessionDirectory = sessionDirectory;
            ProjectDirectory = Directory.CreateDirectory(Path.Combine(sessionDirectory, "project")).FullName;
            AppDataDirectory = Directory.CreateDirectory(Path.Combine(sessionDirectory, "app-data")).FullName;
            RequestPath = Path.Combine(sessionDirectory, "request.json");
        }

        public string SessionDirectory { get; }
        public string ProjectDirectory { get; }
        public string AppDataDirectory { get; }
        public string RequestPath { get; }

        public static CaptureSession Create() => new(Path.Combine(
            StoreScreenshotCaptureRequestStore.GetSessionRoot(),
            Guid.NewGuid().ToString("N")));

        public void WriteRequest(object request)
        {
            File.WriteAllText(RequestPath, JsonSerializer.Serialize(request));
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                RequestPath);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                StoreScreenshotCaptureRequestStore.EnvironmentVariable,
                null);
            Directory.Delete(SessionDirectory, recursive: true);
        }
    }
}
