using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Terminal;

public sealed class ExportContextMeasuredAdmissionConsistencyTests
{
	[Fact]
	public async Task ChangedSourceAfterMeasuredAdmissionDoesNotPublishAContextFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/Content.txt", "measured content\n");
		var destination = Path.Combine(workspace.Path, "context.json");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var detector = new MutateBeforeMaterializationDetector(source);
		using var redactionSession = new SecretRedactionSession(detector);
		var controlledServices = services with { SecretRedactionSession = redactionSession };
		var request = new ExportContextCommandRequest(
			ProjectPath: project,
			Selection: new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				HideSecrets: true),
			View: ProjectContextView.Content,
			Format: ProjectContextDocumentFormat.Json,
			OutputPath: destination,
			Force: false,
			DryRun: false,
			MaximumEstimatedTokens: 1_000,
			Output: new TerminalOutputOptions(Progress: TerminalProgressMode.Never));

		var failure = await Record.ExceptionAsync(() =>
			new ExportContextCommandHandler(controlledServices, new TestTerminalEnvironment())
				.ExecuteAsync(request, TestContext.Current.CancellationToken));

		Assert.Equal(2, detector.ScopeCount);
		Assert.Equal(5_000, File.ReadAllText(source).Length);
		Assert.IsType<SecretDetectionException>(failure);
		Assert.False(File.Exists(destination));
	}

	private sealed class MutateBeforeMaterializationDetector(string sourcePath) : ISecretDetector
	{
		private int _scopeCount;

		public int ScopeCount => Volatile.Read(ref _scopeCount);

		public ISecretDetectionScope CreateScope(string projectRoot)
		{
			if (Interlocked.Increment(ref _scopeCount) == 2)
				File.WriteAllText(sourcePath, new string('x', 5_000));
			return new EmptyScope();
		}

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class EmptyScope : ISecretDetectionScope
	{
		public string GetRulesIdentity(string fullPath, string repositoryRelativePath) =>
			nameof(EmptyScope);

		public IReadOnlyList<DetectedSecret> Detect(
			string fullPath,
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default) => [];
	}
}
