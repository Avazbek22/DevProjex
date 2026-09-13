using System.Collections;
using System.Reflection;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Terminal;

public sealed class ImportanceRankingSnapshotConsistencyTests
{
	[Fact]
	public async Task ExportFailsClosedWhenSourceChangesAfterRankingBeforePreparation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var source = workspace.WriteFile("project/Source.cs", "class VersionA;");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var environment = new TestTerminalEnvironment();
		var ranker = new MutatingRankingService(source, "class VersionB;");
		var request = new ExportContextCommandRequest(
			ProjectPath: project,
			Selection: new ProjectSelectionSpec(
				GitMode: GitFilteringMode.None,
				Exclusions: [],
				HideSecrets: true),
			View: ProjectContextView.Content,
			Format: ProjectContextDocumentFormat.Text,
			OutputPath: "-",
			Force: false,
			DryRun: false,
			MaximumEstimatedTokens: null,
			Output: new TerminalOutputOptions(Progress: TerminalProgressMode.Never),
			Rank: ProjectContextRank.Importance);

		var exception = await Assert.ThrowsAsync<IOException>(() =>
			new ExportContextCommandHandler(services, environment, ranker)
				.ExecuteAsync(request, TestContext.Current.CancellationToken));

		Assert.Contains("repeat the export", exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("class VersionA", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("class VersionB", environment.StandardOutput, StringComparison.Ordinal);
	}

	private sealed class MutatingRankingService(string sourcePath, string replacement) : IImportanceRankingService
	{
		public Task<ImportanceRankingReport> RankAsync(
			string sourceRoot,
			IReadOnlyList<string> candidateFiles,
			CancellationToken cancellationToken = default)
		{
			var capturedWriteTime = File.GetLastWriteTimeUtc(sourcePath);
			var report = CreateReport(sourcePath);
			var version = CaptureRankingVersion(sourcePath);
			SetSourceVersions(report, sourcePath, version);
			File.WriteAllText(sourcePath, replacement, new UTF8Encoding(false));
			File.SetLastWriteTimeUtc(sourcePath, capturedWriteTime);
			return Task.FromResult(report);
		}

		private static ImportanceRankingReport CreateReport(string path)
		{
			var entry = new ImportanceRankingEntry(
				path,
				Path.GetFileName(path),
				1,
				1,
				0,
				0,
				null,
				null,
				ImportanceFileRole.Source,
				true,
				false);
			return new ImportanceRankingReport(
				ImportanceRankingService.AlgorithmId,
				[entry],
				[entry],
				1,
				1,
				0,
				1,
				200,
				0,
				ProjectGitHistoryUnavailableReason.NotRepository,
				true,
				ImportanceRankingService.GraphVariant);
		}

		private static object CaptureRankingVersion(string path)
		{
			var type = typeof(ImportanceRankingReport).Assembly.GetType(
				"DevProjex.Application.Ranking.RankingSourceVersion",
				throwOnError: true)!;
			return type.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!
				.Invoke(null, [path])!;
		}

		private static void SetSourceVersions(
			ImportanceRankingReport report,
			string path,
			object version)
		{
			var versionType = version.GetType();
			var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), versionType);
			var versions = (IDictionary)Activator.CreateInstance(dictionaryType)!;
			versions.Add(Path.GetFullPath(path), version);
			typeof(ImportanceRankingReport)
				.GetProperty("SourceVersions", BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(report, versions);
		}
	}
}
