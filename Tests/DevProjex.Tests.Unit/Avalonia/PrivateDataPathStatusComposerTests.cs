using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PrivateDataPathStatusComposerTests
{
	[Theory]
	[InlineData(0, 0, 1, 1)]
	[InlineData(156, 156, 157, 157)]
	public void ApplicablePath_AddsOneLogicalHiddenFinding(
		int contentDetected,
		int contentHidden,
		int expectedDetected,
		int expectedHidden)
	{
		const string projectRoot = @"C:\Users\alice\source\repo";
		using var session = CreateSession();
		var context = CreateContext(projectRoot, session);

		var status = PrivateDataPathStatusComposer.Compose(
			projectRoot,
			pathPresentation: null,
			context,
			contentDetected,
			contentHidden);

		Assert.Equal(expectedDetected, status.DetectedCount);
		Assert.Equal(expectedHidden, status.HiddenCount);
		Assert.True(status.PathUserNameHidden);
	}

	[Fact]
	public void KeptPath_RemainsDetectedButIsNotHidden()
	{
		const string projectRoot = @"C:\Users\alice\source\repo";
		using var session = CreateSession();
		var context = CreateContext(projectRoot, session);
		var decision = Assert.IsType<OutputPathRedactionDecision>(
			OutputRootPathPresentation.CaptureRedactionDecision(context));
		Assert.True(session.ToggleKeepAsIs(decision.OccurrenceId));

		var status = PrivateDataPathStatusComposer.Compose(
			projectRoot,
			pathPresentation: null,
			context,
			contentDetectedCount: 156,
			contentHiddenCount: 156);

		Assert.Equal(157, status.DetectedCount);
		Assert.Equal(156, status.HiddenCount);
		Assert.False(status.PathUserNameHidden);
	}

	[Theory]
	[InlineData(false, "Hide private data (1)")]
	[InlineData(true, "Hide private data (1/0)")]
	public void PathOnlyFinding_FormatsTheCompactAndKeptLabels(
		bool keep,
		string expectedLabel)
	{
		const string projectRoot = @"C:\Users\alice\source\repo";
		using var session = CreateSession();
		var context = CreateContext(projectRoot, session);
		if (keep)
		{
			var decision = Assert.IsType<OutputPathRedactionDecision>(
				OutputRootPathPresentation.CaptureRedactionDecision(context));
			Assert.True(session.ToggleKeepAsIs(decision.OccurrenceId));
		}
		var status = PrivateDataPathStatusComposer.Compose(
			projectRoot,
			pathPresentation: null,
			context,
			contentDetectedCount: 0,
			contentHiddenCount: 0);
		var catalog = new StubLocalizationCatalog(
			new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = new Dictionary<string, string>
				{
					["Settings.Ignore.HidePrivateData"] = "Hide private data"
				}
			});
		var service = new IgnoreOptionsService(new LocalizationService(catalog, AppLanguage.En));

		var label = service.FormatContentRedactionLabel(
			IgnoreOptionId.HidePrivateData,
			SecretScanState.Completed,
			status.DetectedCount,
			status.HiddenCount);

		Assert.Equal(expectedLabel, label);
	}

	[Theory]
	[InlineData(@"C:\projects\repo", null)]
	[InlineData(@"C:\Users\alice\source\repo", "https://github.com/example/repo")]
	public void PathWithoutDisplayUserSegment_DoesNotChangeContentCounts(
		string projectRoot,
		string? displayRootPath)
	{
		using var session = CreateSession();
		var context = CreateContext(projectRoot, session);
		var pathPresentation = displayRootPath is null
			? null
			: new ExportPathPresentation(displayRootPath, static path => path);

		var status = PrivateDataPathStatusComposer.Compose(
			projectRoot,
			pathPresentation,
			context,
			contentDetectedCount: 3,
			contentHiddenCount: 2);

		Assert.Equal(3, status.DetectedCount);
		Assert.Equal(2, status.HiddenCount);
		Assert.Null(status.PathUserNameHidden);
	}

	private static ContentTransformationContext CreateContext(
		string projectRoot,
		SecretRedactionSession session) =>
		new(
			null,
			new SecretRedactionContext(
				projectRoot,
				session,
				SecretRedactionFeatures.PrivateData));

	private static SecretRedactionSession CreateSession() =>
		SecretRedactionSession.CreateWithPrivateData(
			new EmptyDetector(),
			new EmptyDetector());

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
