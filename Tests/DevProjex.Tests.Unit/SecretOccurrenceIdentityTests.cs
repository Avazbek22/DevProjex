using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretOccurrenceIdentityTests
{
	private const string Secret = "canonical-secret-value";

	[Fact]
	public void KeepAsIs_SurvivesEquivalentProjectRootAlias()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", Secret);
		using var session = new SecretRedactionSession(new ExactSecretDetector());
		var initial = session.BeginOutput(workspace.Path, [path]).CreatePlan(
			path,
			Secret,
			ContentTransformMap.Identity,
			TestContext.Current.CancellationToken);
		var initialSpan = Assert.Single(initial.Spans);
		Assert.True(session.ToggleKeepAsIs(initialSpan.OccurrenceId));

		var aliased = session.BeginOutput(
			workspace.Path + Path.DirectorySeparatorChar,
			[path]).CreatePlan(
			path,
			Secret,
			ContentTransformMap.Identity,
			TestContext.Current.CancellationToken);
		var aliasedSpan = Assert.Single(aliased.Spans);

		Assert.Equal(initialSpan.OccurrenceId, aliasedSpan.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, aliasedSpan.State);
	}

	[Fact]
	public void KeepAsIs_UsesCanonicalSourceCoordinateAcrossAllTransformationCombinations()
	{
		const string source = "body-prefix\ncomment-prefix\n\nTOKEN=" + Secret + "\n";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", source);
		using var session = new SecretRedactionSession(new ExactSecretDetector());
		var initialScope = session.BeginOutput(workspace.Path, [path]);
		var initial = initialScope.CreatePlan(
			path,
			source,
			ContentTransformMap.Identity,
			TestContext.Current.CancellationToken);
		var initialSpan = Assert.Single(initial.Spans);
		Assert.True(session.ToggleKeepAsIs(initialSpan.OccurrenceId));

		foreach (var kinds in Enum.GetValues<CodeTransformKinds>())
		{
			if ((kinds & ~(CodeTransformKinds.Bodies | CodeTransformKinds.Comments |
			               CodeTransformKinds.BlankLines)) != 0)
				continue;
			var result = Transform(source, kinds);
			var identity = kinds == CodeTransformKinds.None ? "" : kinds.ToString();
			var scope = session.BeginOutput(workspace.Path, [path], identity);

			var plan = scope.CreatePlan(
				path,
				result.Text,
				result.Map,
				TestContext.Current.CancellationToken);

			var span = Assert.Single(plan.Spans);
			Assert.Equal(initialSpan.OccurrenceId, span.OccurrenceId);
			Assert.Equal(SecretPreviewSpanState.KeptAsIs, span.State);
			Assert.Equal(Secret, plan.BuildResult(result.Text).Text.AsSpan(span.Start, span.Length));

			var countScope = session.BeginOutput(workspace.Path, [path], identity);
			countScope.AnalyzeTransformed(
				path,
				result.Text,
				result.Map,
				SecretFileMetadata.Capture(path),
				knownFingerprint: null,
				TestContext.Current.CancellationToken);
			var snapshot = countScope.Complete();
			Assert.Equal(1, snapshot.DetectedCount);
			Assert.Equal(0, snapshot.RedactedCount);
		}
	}

	[Fact]
	public void ReplacementOnlyFinding_HasStableTransformBoundIdentityAndNeverInheritsSourceKeep()
	{
		var source = Secret + "\n" + new string('x', 48) + "\n";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", source);
		using var session = new SecretRedactionSession(new ExactSecretDetector());
		var sourceScope = session.BeginOutput(workspace.Path, [path]);
		var sourcePlan = sourceScope.CreatePlan(
			path,
			source,
			ContentTransformMap.Identity,
			TestContext.Current.CancellationToken);
		Assert.True(session.ToggleKeepAsIs(Assert.Single(sourcePlan.Spans).OccurrenceId));
		var edit = new CodeCompressionEdit(
			Secret.Length + 1,
			48,
			Secret);
		var compression = CodeCompressionPlan.Create(
			"config.txt",
			"test",
			[edit],
			source.Length,
			"replacement").ApplyForAnalysis(source, TestContext.Current.CancellationToken);

		var firstScope = session.BeginOutput(workspace.Path, [path], "replacement");
		var first = firstScope.CreatePlan(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var replacementSpan = first.Spans.Single(span => span.Start > 0);
		Assert.Equal(SecretPreviewSpanState.Redacted, replacementSpan.State);

		var secondScope = session.BeginOutput(workspace.Path, [path], "replacement");
		var second = secondScope.CreatePlan(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var repeated = second.Spans.Single(span => span.Start > 0);
		Assert.Equal(replacementSpan.OccurrenceId, repeated.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.Redacted, repeated.State);

		var otherTransformScope = session.BeginOutput(workspace.Path, [path], "other-replacement");
		var otherTransform = otherTransformScope.CreatePlan(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var otherReplacementSpan = otherTransform.Spans.Single(span => span.Start > 0);
		Assert.NotEqual(replacementSpan.OccurrenceId, otherReplacementSpan.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.Redacted, otherReplacementSpan.State);
	}

	[Fact]
	public void FindingSynthesizedAcrossDeletion_UsesTransformBoundIdentity()
	{
		const string removedGap = "gap";
		var source = "canonical-" + removedGap + "secret-value\n";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", source);
		var compression = CodeCompressionPlan.Create(
			"config.txt",
			"test",
			[new CodeCompressionEdit("canonical-".Length, removedGap.Length, "")],
			source.Length,
			"joined").ApplyForAnalysis(source, TestContext.Current.CancellationToken);
		Assert.StartsWith(Secret, compression.Text, StringComparison.Ordinal);
		using var session = new SecretRedactionSession(new ExactSecretDetector());

		var first = session.BeginOutput(workspace.Path, [path], "joined-a").CreatePlan(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var firstSpan = Assert.Single(first.Spans);
		Assert.True(session.ToggleKeepAsIs(firstSpan.OccurrenceId));
		var second = session.BeginOutput(workspace.Path, [path], "joined-b").CreatePlan(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var secondSpan = Assert.Single(second.Spans);

		Assert.NotEqual(firstSpan.OccurrenceId, secondSpan.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.Redacted, secondSpan.State);
	}

	[Fact]
	public void KeepAsIs_UsesTheSameSourceRangeWhenDeletionStartsAtTheFindingEnd()
	{
		const string removableSuffix = "// removable comment\n";
		var source = Secret + removableSuffix;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("config.txt", source);
		using var session = new SecretRedactionSession(new ExactSecretDetector());
		var initial = session.BeginOutput(workspace.Path, [path]).CreatePlan(
			path,
			source,
			ContentTransformMap.Identity,
			TestContext.Current.CancellationToken);
		var initialSpan = Assert.Single(initial.Spans);
		Assert.True(session.ToggleKeepAsIs(initialSpan.OccurrenceId));
		var transformed = CodeCompressionPlan.Create(
			"config.txt",
			"test",
			[new CodeCompressionEdit(Secret.Length, removableSuffix.Length, "")],
			source.Length,
			"comments").ApplyForAnalysis(source, TestContext.Current.CancellationToken);

		var afterDeletion = session.BeginOutput(workspace.Path, [path], "comments").CreatePlan(
			path,
			transformed.Text,
			transformed.Map,
			TestContext.Current.CancellationToken);
		var transformedSpan = Assert.Single(afterDeletion.Spans);

		Assert.Equal(initialSpan.OccurrenceId, transformedSpan.OccurrenceId);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, transformedSpan.State);
	}

	private static CodeCompressionResult Transform(string source, CodeTransformKinds kinds)
	{
		var edits = new List<CodeCompressionEdit>();
		if ((kinds & CodeTransformKinds.Bodies) != 0)
			edits.Add(new CodeCompressionEdit(0, "body-prefix\n".Length, ""));
		if ((kinds & CodeTransformKinds.Comments) != 0)
			edits.Add(new CodeCompressionEdit("body-prefix\n".Length, "comment-prefix\n".Length, ""));
		if ((kinds & CodeTransformKinds.BlankLines) != 0)
		{
			edits.Add(new CodeCompressionEdit(
				"body-prefix\ncomment-prefix\n".Length,
				1,
				""));
		}
		if (edits.Count == 0)
			return new CodeCompressionResult(source, ContentTransformMap.Identity);
		return CodeCompressionPlan.Create(
			"config.txt",
			"test",
			edits,
			source.Length,
			kinds.ToString()).ApplyForAnalysis(source, TestContext.Current.CancellationToken);
	}

	private sealed class ExactSecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var findings = new List<DetectedSecret>();
			var start = 0;
			while ((start = content.IndexOf(Secret, start, StringComparison.Ordinal)) >= 0)
			{
				findings.Add(new DetectedSecret("test-secret", start, Secret.Length, Secret, 0));
				start += Secret.Length;
			}
			return findings;
		}
	}
}
