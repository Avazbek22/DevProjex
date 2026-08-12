using DevProjex.Application.Compression;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowCompressionPreviewPerformanceUiTests
{
	[AvaloniaFact]
	public async Task StripBlankLinesCheckbox_IsDraftUntilApplyAndRestoresFullSourceWhenDisabled()
	{
		const string marker = "strip-blank-lines-ui-marker";
		using var project = UiTestProject.CreateDefault();
		var sourcePath = Path.Combine(project.RootPath, "src", "AppHost", "Program.cs");
		var originalSource = await File.ReadAllTextAsync(
			sourcePath,
			TestContext.Current.CancellationToken);
		var adjacentBlankLine = marker + Environment.NewLine + Environment.NewLine;
		await File.WriteAllTextAsync(
			sourcePath,
			$"// {marker}{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}{originalSource}",
			TestContext.Current.CancellationToken);
		var sourceBytes = await File.ReadAllBytesAsync(
			sourcePath,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var option = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.StripBlankLines);
			Assert.False(option.IsChecked);
			await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);

			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var sourceMetrics));
			Assert.Contains(
				adjacentBlankLine,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			var diagnosticsBeforeDraft = UiTestDriver.GetCodeCompressionDiagnostics(window);

			var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
				window,
				IgnoreOptionId.StripBlankLines);
			await UiTestDriver.ClickAsync(window, checkBox);
			Assert.Contains(
				adjacentBlankLine,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			Assert.Equal(
				diagnosticsBeforeDraft.AnalysisExecutions,
				UiTestDriver.GetCodeCompressionDiagnostics(window).AnalysisExecutions);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.SettingsBlankLineStripNotice.StartsWith(
					"Removed blank lines from ",
					StringComparison.Ordinal),
				"blank-line prewarm to publish its exact snapshot after Apply");
			var snapshot = Assert.IsType<CodeCompressionSnapshot>(GetCompressionSnapshot(window));
			Assert.Equal(0, snapshot.BodyTransformedFiles);
			Assert.Equal(0, snapshot.CommentTransformedFiles);
			Assert.True(snapshot.BlankLineTransformedFiles >= 1);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var preview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					return preview.Contains(marker, StringComparison.Ordinal) &&
					       !preview.Contains(adjacentBlankLine, StringComparison.Ordinal) &&
					       preview.Contains("return \"app-value-1\";", StringComparison.Ordinal);
				},
				"blank-line removal to update Preview without changing source code");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
					UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var strippedMetrics) &&
					strippedMetrics.Chars < sourceMetrics.Chars,
				"blank-line removal to publish metrics for the transformed text");

			checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
				window,
				IgnoreOptionId.StripBlankLines);
			await UiTestDriver.ClickAsync(window, checkBox);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
					!option.IsChecked &&
					UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
						adjacentBlankLine,
						StringComparison.Ordinal) &&
					UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var restoredMetrics) &&
					restoredMetrics == sourceMetrics,
				"disabling blank-line removal to restore the original Preview and metrics");

			Assert.Equal(
				sourceBytes,
				await File.ReadAllBytesAsync(sourcePath, TestContext.Current.CancellationToken));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task StripCommentsCheckbox_IsDraftUntilApplyAndRestoresFullSourceWhenDisabled()
	{
		const string commentMarker = "strip-comments-ui-marker";
		const string cssCommentMarker = "strip-comments-css-ui-marker";
		const string xmlCommentMarker = "strip-comments-xml-ui-marker";
		const string yamlCommentMarker = "strip-comments-yaml-ui-marker";
		using var project = UiTestProject.CreateDefault();
		var sourcePath = Path.Combine(project.RootPath, "src", "AppHost", "Program.cs");
		var originalSource = await File.ReadAllTextAsync(
			sourcePath,
			TestContext.Current.CancellationToken);
		var commentLine = $"// {commentMarker} {new string('x', 4096)}";
		await File.WriteAllTextAsync(
			sourcePath,
			$"{commentLine}{Environment.NewLine}{originalSource}",
			TestContext.Current.CancellationToken);
		var cssPath = Path.Combine(project.RootPath, "src", "AppHost", "site.css");
		await File.WriteAllTextAsync(
			cssPath,
			$"/* {cssCommentMarker} */{Environment.NewLine}.app {{ color: red; }}{Environment.NewLine}",
			TestContext.Current.CancellationToken);
		var xmlPath = Path.Combine(project.RootPath, "src", "AppHost", "View.axaml");
		await File.WriteAllTextAsync(
			xmlPath,
			$"<!-- {xmlCommentMarker} -->{Environment.NewLine}<Panel xmlns=\"https://github.com/avaloniaui\" />{Environment.NewLine}",
			TestContext.Current.CancellationToken);
		var yamlPath = Path.Combine(project.RootPath, "src", "AppHost", "deployment.yaml");
		await File.WriteAllTextAsync(
			yamlPath,
			$"# {yamlCommentMarker}{Environment.NewLine}service: app{Environment.NewLine}",
			TestContext.Current.CancellationToken);
		var sourceBytes = await File.ReadAllBytesAsync(
			sourcePath,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var option = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.StripComments);
			Assert.False(option.IsChecked);
			await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);

			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var sourceMetrics));
			Assert.Contains(
				commentMarker,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			var diagnosticsBeforeDraft = UiTestDriver.GetCodeCompressionDiagnostics(window);

			var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
				window,
				IgnoreOptionId.StripComments);
			await UiTestDriver.ClickAsync(window, checkBox);
			Assert.Contains(
				commentMarker,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			Assert.Equal(
				diagnosticsBeforeDraft.AnalysisExecutions,
				UiTestDriver.GetCodeCompressionDiagnostics(window).AnalysisExecutions);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.SettingsCommentStripNotice.StartsWith(
					"Removed comments from ",
					StringComparison.Ordinal),
				"comment-removal prewarm to publish its exact snapshot after Apply");
			var snapshot = Assert.IsType<CodeCompressionSnapshot>(GetCompressionSnapshot(window));
			Assert.Equal(0, snapshot.BodyTransformedFiles);
			Assert.True(snapshot.CommentTransformedFiles >= 4);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var preview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					return !preview.Contains(commentMarker, StringComparison.Ordinal) &&
					       !preview.Contains(cssCommentMarker, StringComparison.Ordinal) &&
					       !preview.Contains(xmlCommentMarker, StringComparison.Ordinal) &&
					       !preview.Contains(yamlCommentMarker, StringComparison.Ordinal) &&
					       preview.Contains("return \"app-value-1\";", StringComparison.Ordinal);
				},
				"comment removal to update Preview while preserving implementation code");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
					UiTestDriver.TryGetCurrentStatusMetrics(
						window,
						out _,
						out var strippedMetrics) &&
					strippedMetrics.Chars < sourceMetrics.Chars,
				"comment removal to publish metrics for the transformed text");

			checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
				window,
				IgnoreOptionId.StripComments);
			await UiTestDriver.ClickAsync(window, checkBox);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
					!option.IsChecked &&
					UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
						commentMarker,
						StringComparison.Ordinal) &&
					UiTestDriver.TryGetCurrentStatusMetrics(
						window,
						out _,
						out var restoredMetrics) &&
					restoredMetrics == sourceMetrics,
				"disabling comment removal to restore the original Preview and metrics");

			Assert.Equal(
				sourceBytes,
				await File.ReadAllBytesAsync(
					sourcePath,
					TestContext.Current.CancellationToken));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task CompressionCheckbox_UpdatesVisiblePreviewAndRestoresFullSourceWhenDisabled()
    {
        using var project = UiTestProject.CreateDefault();
        var sourcePath = Path.Combine(project.RootPath, "src", "AppHost", "Program.cs");
        var sourceBytes = await File.ReadAllBytesAsync(
            sourcePath,
            TestContext.Current.CancellationToken);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var option = Assert.Single(
                viewModel.ContentProcessingOptions,
                static candidate => candidate.Id == IgnoreOptionId.CompressCode);
            Assert.False(option.IsChecked);
			await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
			Assert.True(
				UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var uncompressedMetrics));

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            Assert.Contains(
                "return \"app-value-1\";",
                UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                StringComparison.Ordinal);

            var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
                window,
                IgnoreOptionId.CompressCode);
            await UiTestDriver.ClickAsync(window, checkBox);
            // The checkbox is a draft; the preview and the measured counters change only after
            // «Apply settings» commits it.
            Assert.Contains(
                "return \"app-value-1\";",
                UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                StringComparison.Ordinal);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => viewModel.ContentProcessingOptions.Any(
                          static candidate =>
                              candidate.Id == IgnoreOptionId.CompressCode && candidate.IsChecked) &&
                      !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
                          "return \"app-value-1\";",
                          StringComparison.Ordinal) &&
				      UiTestDriver.TryGetCurrentStatusMetrics(
					      window,
					      out _,
					      out var compressedMetrics) &&
				      compressedMetrics.Chars < uncompressedMetrics.Chars,
                "compression to remove implementation bodies from the visible Preview");
            var compressedPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Contains("BuildAppValue1()", compressedPreview, StringComparison.Ordinal);
            Assert.True(UiTestDriver.GetCodeCompressionDiagnostics(window).AnalysisExecutions > 0);

            checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
                window,
                IgnoreOptionId.CompressCode);
            await UiTestDriver.ClickAsync(window, checkBox);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => viewModel.ContentProcessingOptions.Any(
                          static candidate =>
                              candidate.Id == IgnoreOptionId.CompressCode && !candidate.IsChecked) &&
                      UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
                          "return \"app-value-1\";",
                          StringComparison.Ordinal) &&
				      UiTestDriver.TryGetCurrentStatusMetrics(
					      window,
					      out _,
					      out var restoredMetrics) &&
				      restoredMetrics == uncompressedMetrics,
                "disabling compression to restore full source in the visible Preview");

            Assert.Equal(
                sourceBytes,
                await File.ReadAllBytesAsync(
                    sourcePath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	[AvaloniaFact]
	public async Task TreeSelectionChange_ReplacesCompressionSnapshotWithoutOpeningPreview()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			var compressionOption = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.CompressCode);
			compressionOption.IsChecked = true;
			// The checkbox is a draft; compression starts only after «Apply settings» commits it.
			await UiTestDriver.ClickApplySettingsAsync(window);

			CodeCompressionSnapshot? fullSelectionSnapshot = null;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					fullSelectionSnapshot = GetCompressionSnapshot(window);
					return fullSelectionSnapshot is { SelectionKey.Length: > 0 } &&
					       viewModel.SettingsCompressionNotice.StartsWith(
						       "Compressed ",
						       StringComparison.Ordinal);
				},
				"compression counts for the complete project selection");

			var rootNode = Assert.Single(viewModel.TreeNodes);
			rootNode.IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			var srcCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
			var previousNotice = viewModel.SettingsCompressionNotice;
			await UiTestDriver.ClickAsync(window, srcCheckBox);

			CodeCompressionSnapshot? srcSelectionSnapshot = null;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					srcSelectionSnapshot = GetCompressionSnapshot(window);
					return srcSelectionSnapshot is { SelectionKey.Length: > 0 } &&
					       !string.Equals(
						       srcSelectionSnapshot.SelectionKey,
						       fullSelectionSnapshot!.SelectionKey,
						       StringComparison.Ordinal) &&
					       !string.Equals(
						       viewModel.SettingsCompressionNotice,
						       previousNotice,
						       StringComparison.Ordinal);
				},
				"compression counts for the newly selected src subtree");

			Assert.True(srcCheckBox.IsChecked);
			Assert.NotEqual(fullSelectionSnapshot!.TotalFiles, srcSelectionSnapshot!.TotalFiles);
			Assert.False(viewModel.IsPreviewMode);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task PersistedCompression_PrewarmsBeforeTreeToBothPreviewTransition()
    {
        using var project = UiTestProject.CreateDefault();
        var appDataPath = Path.Combine(project.AppDataPath, "compression-profile");
        Directory.CreateDirectory(appDataPath);
        new ProjectProfileStore(() => appDataPath).SaveProfile(
            project.RootPath,
            new ProjectSelectionProfile(
                SelectedRootFolders: [],
                SelectedExtensions: [],
                SelectedIgnoreOptions: [IgnoreOptionId.CompressCode],
                IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.CompressCode] = true
                }));

        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            appDataPathOverride: appDataPath);
        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(
                viewModel.ContentProcessingOptions,
                static option => option.Id == IgnoreOptionId.CompressCode && option.IsChecked);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetCodeCompressionDiagnostics(window).PrewarmRequests >= 10,
                "persisted compression selection to start background prewarm");

            await UiTestDriver.OpenPreviewAsync(window);
            Assert.Equal(PreviewContentMode.Tree, viewModel.SelectedPreviewContentMode);
            var beforeBoth = UiTestDriver.GetCodeCompressionDiagnostics(window);

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

            var afterBoth = UiTestDriver.GetCodeCompressionDiagnostics(window);
            Assert.Equal(beforeBoth.AnalysisExecutions, afterBoth.AnalysisExecutions);
            Assert.True(
                afterBoth.CacheHits + afterBoth.PrewarmReuses >
                beforeBoth.CacheHits + beforeBoth.PrewarmReuses);
            Assert.Contains(
                "BuildAppValue",
                UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                StringComparison.Ordinal);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	private static CodeCompressionSnapshot? GetCompressionSnapshot(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_codeCompressionSnapshot",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		return field?.GetValue(window) as CodeCompressionSnapshot;
	}
}
