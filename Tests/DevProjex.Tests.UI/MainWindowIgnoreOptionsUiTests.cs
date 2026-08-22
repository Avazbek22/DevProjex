using System.Collections.Concurrent;
using System.IO.Compression;
using DevProjex.Avalonia.Controls;
using DevProjex.Application.Context;
using DevProjex.Application.Presentation;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Kernel.Abstractions;
using Avalonia.Layout;
using Avalonia.VisualTree;
using System.ComponentModel;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowIgnoreOptionsUiTests
{
	[AvaloniaFact]
	public async Task ContentHeaders_UseFullPerFilePathsAndOneInteractivePrivateDataDecision()
	{
		using var project = UiTestProject.CreateDefaultUnderUserProfile();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var unprotected = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
				window,
				PreviewContentMode.Content,
				TestContext.Current.CancellationToken);
			Assert.Equal(unprotected, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			var programPath = Path.Combine(project.RootPath, "src", "AppHost", "Program.cs");
			Assert.Contains($"{programPath}:", unprotected, StringComparison.Ordinal);
			Assert.DoesNotContain(
				$"{project.RootPath}:{Environment.NewLine}",
				unprotected,
				StringComparison.Ordinal);
			await UiTestDriver.CopyContentToClipboardAsync(window, unprotected);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HidePrivateData);
			await UiTestDriver.ClickApplySettingsAsync(window);
			var protectedRoot = OutputRootPathPresentation.MaskLocalUserSegment(project.RootPath);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var protectedContent = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
				window,
				PreviewContentMode.Content,
				TestContext.Current.CancellationToken);
			Assert.Contains(
				$"{OutputRootPathPresentation.MaskLocalUserSegment(programPath)}:",
				protectedContent,
				StringComparison.Ordinal);
			Assert.DoesNotContain($"{programPath}:", protectedContent, StringComparison.Ordinal);
			Assert.Equal(protectedContent, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			await UiTestDriver.CopyContentToClipboardAsync(window, protectedContent);

			var control = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			var viewModel = UiTestDriver.GetViewModel(window);
			var privateDataOption = Assert.Single(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HidePrivateData);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(privateDataOption.Label, "Hide private data (1)", StringComparison.Ordinal) &&
				      string.Equals(
					      viewModel.SettingsPrivateDataNotice,
					      $"Found: 1. Hidden: 1.{Environment.NewLine}" +
					      "User name in file paths: hidden.",
					      StringComparison.Ordinal),
				"the generated path finding to be represented in the private-data status");
			var generatedPathSpans = control.Document!.Redactions
				.Where(static span =>
					span.RuleId == OutputRootPathPresentation.LocalUserRuleId &&
					span.Source == SecretFindingSource.GeneratedPath)
				.ToArray();
			Assert.True(generatedPathSpans.Length > 1);
			Assert.Equal(
				[
					new PreviewMarkerSource(
						generatedPathSpans.Min(static span => span.LineNumber),
						PreviewMarkerCategory.Redaction)
				],
				control.MarkerSnapshot.Markers);
			var generatedOccurrenceId = Assert.Single(
				generatedPathSpans
					.Select(static span => span.OccurrenceId)
					.Distinct(StringComparer.Ordinal));
			var navigationTarget = generatedPathSpans
				.OrderBy(static span => span.LineNumber)
				.ThenBy(static span => span.StartColumn)
				.Skip(1)
				.First();
			control.Focus();
			for (var attempt = 0; attempt <= control.Document.Redactions.Count; attempt++)
			{
				await UiTestDriver.PressKeyAsync(window, Key.Down, RawInputModifiers.Alt);
				var activeTarget = UiTestDriver.GetActiveRedactionTarget(control);
				if (activeTarget is { } active &&
				    active.OccurrenceId == navigationTarget.OccurrenceId &&
				    active.LineNumber == navigationTarget.LineNumber &&
				    active.StartColumn == navigationTarget.StartColumn)
				{
					break;
				}
			}
			Assert.Equal(
				(generatedOccurrenceId, navigationTarget.LineNumber, navigationTarget.StartColumn),
				UiTestDriver.GetActiveRedactionTarget(control));

			await UiTestDriver.PressKeyAsync(window, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(programPath, StringComparison.Ordinal) &&
				      control.Document!.Redactions
					      .Where(static span => span.Source == SecretFindingSource.GeneratedPath)
					      .All(static span => span.State == SecretPreviewSpanState.KeptAsIs) &&
				      string.Equals(privateDataOption.Label, "Hide private data (1/0)", StringComparison.Ordinal) &&
				      string.Equals(
					      viewModel.SettingsPrivateDataNotice,
					      $"Found: 1. Hidden: 0.{Environment.NewLine}" +
					      "User name in file paths: shown.",
					      StringComparison.Ordinal),
				"the generated path occurrence to be kept as-is");
			Assert.Empty(control.MarkerSnapshot.Markers);
			var keptContent = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
				window,
				PreviewContentMode.Content,
				TestContext.Current.CancellationToken);
			Assert.Equal(unprotected, keptContent);
			Assert.Equal(keptContent, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			await UiTestDriver.CopyContentToClipboardAsync(window, keptContent);

			await UiTestDriver.PressKeyAsync(window, Key.Enter);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
					protectedContent,
					StringComparison.Ordinal) &&
				      control.Document!.Redactions
					      .Where(static span => span.Source == SecretFindingSource.GeneratedPath)
					      .All(static span => span.State == SecretPreviewSpanState.Redacted),
				"the generated path occurrence to be hidden again");

			foreach (var mode in new[]
			{
				PreviewContentMode.Tree,
				PreviewContentMode.TreeAndContent
			})
			{
				await UiTestDriver.SwitchPreviewModeAsync(window, mode);
				var modeGeneratedPathSpans = control.Document!.Redactions
					.Where(static span =>
						span.Source == SecretFindingSource.GeneratedPath &&
						span.State == SecretPreviewSpanState.Redacted)
					.ToArray();
				Assert.NotEmpty(modeGeneratedPathSpans);
				Assert.Equal(
					[
						new PreviewMarkerSource(
							modeGeneratedPathSpans.Min(static span => span.LineNumber),
							PreviewMarkerCategory.Redaction)
					],
					control.MarkerSnapshot.Markers);
				Assert.True(
					UiTestDriver.GetRequiredControl<PreviewMarkerBar>(window, "PreviewMarkerBar").IsVisible);
				var output = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
					window,
					mode,
					TestContext.Current.CancellationToken);
				Assert.Contains(protectedRoot, output, StringComparison.Ordinal);
				if (!string.Equals(protectedRoot, project.RootPath, StringComparison.Ordinal))
					Assert.DoesNotContain(project.RootPath, output, StringComparison.Ordinal);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(0, false)]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(3, false)]
	[InlineData(4, false)]
	[InlineData(5, false)]
	[InlineData(6, false)]
	[InlineData(7, false)]
	[InlineData(0, true)]
	[InlineData(1, true)]
	[InlineData(2, true)]
	[InlineData(3, true)]
	[InlineData(4, true)]
	[InlineData(5, true)]
	[InlineData(6, true)]
	[InlineData(7, true)]
	public async Task HideHere_ThroughPreviewContextMenu_RedactsSelectedContent(
		int transformMode,
		bool hideSecretsInitiallyEnabled)
	{
		const string manualValue = "ordinary-context-menu-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var source = $$"""
		             internal sealed class Secrets
		             {
		                 // Coordinate-shifting comment.

		                 const string X = "prefix{{manualValue}}suffix";
		             }
		             """;
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			source.ReplaceLineEndings("\r\n"),
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var observedLabels = new List<string>();
		IgnoreOptionViewModel? hideSecretsOption = null;
		PropertyChangedEventHandler? labelChanged = null;
		try
		{
			await SetTransformationAsync(IgnoreOptionId.CompressCode, (transformMode & 1) != 0);
			await SetTransformationAsync(IgnoreOptionId.StripComments, (transformMode & 2) != 0);
			await SetTransformationAsync(IgnoreOptionId.StripBlankLines, (transformMode & 4) != 0);
			if (transformMode != 0)
				await UiTestDriver.ClickApplySettingsAsync(window);
			if (hideSecretsInitiallyEnabled)
			{
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
				await UiTestDriver.ClickApplySettingsAsync(window);
			}
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
			hideSecretsOption = UiTestDriver.GetViewModel(window).HideSecretsOption;
			Assert.NotNull(hideSecretsOption);
			observedLabels.Add(hideSecretsOption!.Label);
			labelChanged = (_, args) =>
			{
				if (args.PropertyName == nameof(IgnoreOptionViewModel.Label))
					observedLabels.Add(hideSecretsOption.Label);
			};
			hideSecretsOption.PropertyChanged += labelChanged;
			Assert.Contains(
				manualValue,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).HideSecretsOption is { IsChecked: true } &&
				      !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					      manualValue,
					      StringComparison.Ordinal) &&
				      hideSecretsOption.Label.EndsWith("(1)", StringComparison.Ordinal),
				"the context-menu session mark to redact Preview");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			var measuredIndex = observedLabels.FindIndex(static label =>
				label.EndsWith("(1)", StringComparison.Ordinal));
			Assert.True(measuredIndex >= 0);
			Assert.All(
				observedLabels.Skip(measuredIndex),
				static label => Assert.EndsWith("(1)", label, StringComparison.Ordinal));
		}
		finally
		{
			if (hideSecretsOption is not null && labelChanged is not null)
				hideSecretsOption.PropertyChanged -= labelChanged;
			await UiTestDriver.CloseWindowAsync(window);
		}

		async Task SetTransformationAsync(IgnoreOptionId optionId, bool enabled)
		{
			var option = UiTestDriver.GetViewModel(window).ContentProcessingOptions.Single(
				candidate => candidate.Id == optionId);
			if (option.IsChecked != enabled)
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
		}
	}

	[AvaloniaFact]
	public async Task HideHere_RepeatedRequestReportsFeedbackAndUnmarkRestoresContent()
	{
		const string manualValue = "repeatable-manual-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string X = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var observedToastMessages = new ConcurrentQueue<string>();
		var toastItems = UiTestDriver.GetToastService(window).Items;
		System.Collections.Specialized.NotifyCollectionChangedEventHandler toastChanged = (_, args) =>
		{
			if (args.NewItems is null)
				return;
			foreach (var toast in args.NewItems.OfType<ToastMessageViewModel>())
				observedToastMessages.Enqueue(toast.Message);
		};
		toastItems.CollectionChanged += toastChanged;
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(
				window,
				manualValue,
				clickCount: 2);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the repeated session mark to redact Preview");
			Assert.Contains(
				"Value is already hidden",
				observedToastMessages);

			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"removing the session mark to restore Preview content");
		}
		finally
		{
			toastItems.CollectionChanged -= toastChanged;
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task HideHere_UnmarkDuringIdentityInitializationDoesNotPersistTheRemovedAnchor()
	{
		const string manualValue = "pending-identity-unmark-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "pending-identity-unmark");
		var keyDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(keyDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);

		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		var lockPath = Path.Combine(keyDirectory, "secret-mark-hmac.key.lock");
		FileStream? heldLock = new(
			lockPath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		try
		{
			await UiTestDriver.OpenPreviewAsync(firstWindow);
			await UiTestDriver.SwitchPreviewModeAsync(firstWindow, PreviewContentMode.Content);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(firstWindow, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(firstWindow).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the source anchor to hide content while identity initialization is blocked");

			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(firstWindow);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => UiTestDriver.ComputeCurrentPreviewCopyPayload(firstWindow).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the pending source anchor to be removed before promotion");
		}
		finally
		{
			heldLock?.Dispose();
			heldLock = null;
			await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
		}

		var stored = await new ProjectProfileStore(() => appDataPath).LoadMarksAsync(
			project.RootPath,
			TestContext.Current.CancellationToken);
		Assert.True(stored.Succeeded);
		Assert.Empty(stored.Snapshot!.Marks);

		var reopenedWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(reopenedWindow);
			await UiTestDriver.SwitchPreviewModeAsync(reopenedWindow, PreviewContentMode.Content);
			Assert.Contains(
				manualValue,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(reopenedWindow),
				StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(reopenedWindow);
		}
	}

	[AvaloniaFact]
	public async Task HideHere_UnmarkDuringPendingAddPreservesCausalStoreOrder()
	{
		const string manualValue = "pending-add-unmark-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "pending-add-unmark");
		var storeDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(storeDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		}

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		FileStream? heldLock = new(
			Path.Combine(storeDirectory, "project-secret-marks.json.lock"),
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			var session = UiTestDriver.GetSecretRedactionSession(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetPendingPersistentMarkCount(session) == 1 &&
				      !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					      manualValue,
					      StringComparison.Ordinal),
				"the source anchor to be promoted while its durable Add is blocked");

			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(window);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.Equal(1, UiTestDriver.GetPendingPersistentMarkCount(session));
			Assert.DoesNotContain(
				manualValue,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);

			heldLock.Dispose();
			heldLock = null;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetPendingPersistentMarkCount(session) == 0 &&
				      UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					      manualValue,
					      StringComparison.Ordinal),
				"the durable Add to finish before Remove is issued with its resulting revision");
		}
		finally
		{
			heldLock?.Dispose();
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		var stored = await new ProjectProfileStore(() => appDataPath).LoadMarksAsync(
			project.RootPath,
			TestContext.Current.CancellationToken);
		Assert.True(stored.Succeeded);
		Assert.Empty(stored.Snapshot!.Marks);
	}

	[AvaloniaFact]
	public async Task PersistentUnmark_DurableRefreshRemovedMarkStillSendsStagedDelta()
	{
		const string manualValue = "ordinary-ui-unmark-race-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "persistent-unmark-race");
		Directory.CreateDirectory(appDataPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var store = new ProjectProfileStore(() => appDataPath);
		Assert.True(store.TrySaveProfile(
			project.RootPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HideSecrets] = true
				})));
		string identity;
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(identityProvider, manualValue, out identity));
		}
		Assert.True((await store.AddMarkAsync(
			project.RootPath,
			new MarkedSecretProfileEntry(identity, "caption", manualValue.Length),
			TestContext.Current.CancellationToken)).Succeeded);

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the durable manual mark to redact Preview before the competing refresh");

			var session = UiTestDriver.GetSecretRedactionSession(window);
			var capturedRevision = session.PersistentMarksStoreRevision;
			Assert.True(capturedRevision >= 1);
			session.ReplacePersistentMarks(
				project.RootPath,
				new PersistentSecretMarksSnapshot(capturedRevision + 1, []));
			Assert.Empty(session.GetMarkedSecrets());

			var sent = new TaskCompletionSource<PersistentSecretMarkDelta>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			UiTestDriver.OverridePersistentSecretMarkDeltaHandler(
				window,
				delta =>
				{
					var acknowledged = new PersistentSecretMarksSnapshot(
						capturedRevision + 2,
						[],
						new Dictionary<PersistentSecretMarkId, long>
						{
							[delta.MarkId] = capturedRevision + 2
						});
					session.AcknowledgePersistentMarkDelta(
						project.RootPath,
						delta.OperationId,
						acknowledged);
					sent.TrySetResult(delta);
					return Task.FromResult(new PersistentSecretMarkWriteResult(
						PersistentSecretMarkStoreStatus.Success,
						acknowledged));
				});

			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(window);
			var remove = await sent.Task.WaitAsync(
				TimeSpan.FromSeconds(5),
				TestContext.Current.CancellationToken);
			Assert.Equal(PersistentSecretMarkDeltaKind.Remove, remove.Kind);
			Assert.Equal(new PersistentSecretMarkId(identity, manualValue.Length), remove.MarkId);
			Assert.Equal(0, UiTestDriver.GetPendingPersistentMarkCount(session));

			session.ReplacePersistentMarks(
				project.RootPath,
				new PersistentSecretMarksSnapshot(capturedRevision + 1, [
					new MarkedSecretProfileEntry(identity, "caption", manualValue.Length)
				]));
			Assert.Empty(session.GetMarkedSecrets());
			Assert.Contains(
				manualValue,
				UiTestDriver.RedactFileWithCurrentSession(
					window,
					Path.Combine(project.RootPath, "src", "Secrets.cs")),
				StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(0)]
	[InlineData(7)]
	public async Task HideHere_ContextMenuRedactsEveryOutputSurface(int transformMode)
	{
		const string manualValue = "all-output-context-menu-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      // Coordinate-shifting comment.

			      const string X = "prefix{{manualValue}}suffix";
			  }
			  """,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			foreach (var (optionId, mask) in new[]
			         {
				         (IgnoreOptionId.CompressCode, 1),
				         (IgnoreOptionId.StripComments, 2),
				         (IgnoreOptionId.StripBlankLines, 4)
			         })
			{
				if ((transformMode & mask) != 0)
					await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			}
			if (transformMode != 0)
				await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the UI mark to redact Preview before validating exports");

			var preview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			await UiTestDriver.SetClipboardTextAsync(window, "pending-context-menu-copy");
			await UiTestDriver.ClickPreviewCopyButtonAsync(window);
			await UiTestDriver.WaitForClipboardTextAsync(window, preview);
			var clipboard = await UiTestDriver.GetClipboardTextAsync(window);
			var session = UiTestDriver.GetSecretRedactionSession(window);
			var compression = UiTestDriver.GetCodeCompressionSession(window);
			var analyzer = new FileContentAnalyzer();
			var plan = await BuildSecretOutputPlanAsync(project.RootPath, transformMode);
			var contextService = new ProjectContextDocumentService(
				new TreeExportService(),
				analyzer,
				secretRedactionSession: session,
				codeCompressionSession: compression);
			using var contextDestination = new MemoryStream();
			await contextService.WriteCompleteAsync(
				plan,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Text,
				contextDestination,
				TestContext.Current.CancellationToken,
				plain: true);
			var contextOutput = Encoding.UTF8.GetString(contextDestination.ToArray());
			var copyService = new ProjectCopyExportService(
				new ProjectCopyExportPlanBuilder(),
				analyzer,
				session,
				compression);
			var folderPath = Path.Combine(project.AppDataPath, $"folder-{Guid.NewGuid():N}");
			var zipPath = Path.Combine(project.AppDataPath, $"archive-{Guid.NewGuid():N}.zip");
			await ExportAsync(ProjectCopyExportFormat.Folder, folderPath);
			await ExportAsync(ProjectCopyExportFormat.Zip, zipPath);
			var folderOutput = await File.ReadAllTextAsync(
				Path.Combine(folderPath, "src", "Secrets.cs"),
				TestContext.Current.CancellationToken);
			using var archive = ZipFile.OpenRead(zipPath);
			var entry = Assert.Single(
				archive.Entries,
				static candidate => candidate.FullName.EndsWith("src/Secrets.cs", StringComparison.Ordinal));
			using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var zipOutput = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

			Assert.All(
				new[] { preview, clipboard, contextOutput, folderOutput, zipOutput },
				output =>
				{
					Assert.DoesNotContain(manualValue, output, StringComparison.Ordinal);
					Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", output, StringComparison.Ordinal);
				});
			Assert.Contains(
				manualValue,
				await File.ReadAllTextAsync(
					Path.Combine(project.RootPath, "src", "Secrets.cs"),
					TestContext.Current.CancellationToken),
				StringComparison.Ordinal);

			async Task ExportAsync(ProjectCopyExportFormat format, string destination)
			{
				await copyService.ExportAsync(
					new ProjectCopyExportRequest(
						plan.SourceRoot,
						"project",
						plan.ProjectedTree,
						new HashSet<string>(PathComparer.Default),
						destination,
						format,
						ProjectCopyDestinationMode.Exact,
						ProjectCopyConflictPolicy.Fail,
						RedactSecrets: true,
						CompressCode: (transformMode & 1) != 0,
						StripComments: (transformMode & 2) != 0,
						StripBlankLines: (transformMode & 4) != 0),
					cancellationToken: TestContext.Current.CancellationToken);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(0)]
	[InlineData(7)]
	public async Task AlwaysHide_ThroughPreviewContextMenuPersistsAcrossTransformExtremes(int transformMode)
	{
		const string manualValue = "persistent-context-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      // Coordinate-shifting comment.

			      const string X = "{{manualValue}}";
			  }
			  """,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			foreach (var (optionId, mask) in new[]
			         {
				         (IgnoreOptionId.CompressCode, 1),
				         (IgnoreOptionId.StripComments, 2),
				         (IgnoreOptionId.StripBlankLines, 4)
			         })
			{
				if ((transformMode & mask) != 0)
					await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			}
			if (transformMode != 0)
				await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(
				window,
				manualValue,
				persistent: true);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the persistent context-menu mark to redact Preview");

			var store = new ProjectProfileStore(() => UiTestDriver.GetWindowAppDataPath(window));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var loaded = store.LoadMarksAsync(project.RootPath).AsTask().GetAwaiter().GetResult();
					return loaded.Succeeded && loaded.Snapshot!.Marks.Count == 1;
				},
				"the context-menu mark to become durable");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	public async Task HideHere_CloseImmediatelyAndReopen_PreservesOnlySelectedOccurrence(int transformMode)
	{
		const string manualValue = "abcdefghij";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, $"hide-here-restart-{transformMode}");
		Directory.CreateDirectory(appDataPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal static class Secrets
			  {
			      private const string First = "{{manualValue}}";
			      private const string Second = "{{manualValue}}";
			  }
			  """,
			TestContext.Current.CancellationToken);

		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			foreach (var (optionId, mask) in new[]
			         {
				         (IgnoreOptionId.CompressCode, 1),
				         (IgnoreOptionId.StripComments, 2),
				         (IgnoreOptionId.StripBlankLines, 4)
			         })
			{
				if ((transformMode & mask) != 0)
					await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(firstWindow, optionId);
			}
			if (transformMode != 0)
				await UiTestDriver.ClickApplySettingsAsync(firstWindow);
			await UiTestDriver.OpenPreviewAsync(firstWindow);
			await UiTestDriver.SwitchPreviewModeAsync(firstWindow, PreviewContentMode.Content);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(
				firstWindow,
				manualValue,
				persistent: false);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => CountOccurrences(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(firstWindow),
					manualValue) == 1,
				"the selected occurrence to be hidden before immediate shutdown");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
		}
		var persisted = await new ProjectProfileStore(() => appDataPath).LoadMarksAsync(
			project.RootPath,
			TestContext.Current.CancellationToken);
		Assert.True(persisted.Succeeded);
		var persistedSourceMark = Assert.Single(persisted.Snapshot!.Marks);
		Assert.True(PersistentSecretIdentity.IsV2(persistedSourceMark.H));
		Assert.Equal("src/Secrets.cs", persistedSourceMark.RelativePath);
		Assert.True(persistedSourceMark.SourceOffset > 0);
		Assert.DoesNotContain(
			manualValue,
			await File.ReadAllTextAsync(
				Path.Combine(appDataPath, "DevProjex", "project-secret-marks.json"),
				TestContext.Current.CancellationToken),
			StringComparison.Ordinal);

		var reopenedWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				reopenedWindow,
				IgnoreOptionId.HideSecrets,
				visible: true,
				isChecked: true);
			await UiTestDriver.OpenPreviewAsync(reopenedWindow);
			await UiTestDriver.SwitchPreviewModeAsync(reopenedWindow, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				reopenedWindow,
				() => CountOccurrences(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(reopenedWindow),
					manualValue) == 1,
				"only the selected source occurrence to remain hidden after restart");
			var expectedClipboard = UiTestDriver.ComputeCurrentPreviewCopyPayload(reopenedWindow);
			await UiTestDriver.SetClipboardTextAsync(reopenedWindow, "hide-here-restart-copy-pending");
			await UiTestDriver.ClickPreviewCopyButtonAsync(reopenedWindow);
			await UiTestDriver.WaitForClipboardTextAsync(reopenedWindow, expectedClipboard);
			Assert.Equal(
				1,
				CountOccurrences(
					Assert.IsType<string>(await UiTestDriver.GetClipboardTextAsync(reopenedWindow)),
					manualValue));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(reopenedWindow);
		}

		static int CountOccurrences(string content, string value)
		{
			var count = 0;
			var offset = 0;
			while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
			{
				count++;
				offset += value.Length;
			}

			return count;
		}
	}

	[AvaloniaFact]
	public async Task HideHere_UnmarkAfterRestartAndCloseImmediately_RemainsRemoved()
	{
		const string manualValue = "restart-unmark-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "hide-here-restart-unmark");
		Directory.CreateDirectory(appDataPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string first = \"{manualValue}\";\nconst string second = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);

		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(firstWindow);
			await UiTestDriver.SwitchPreviewModeAsync(firstWindow, PreviewContentMode.Content);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(firstWindow, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
					firstWindow,
					"PreviewTextControl").Document?.Redactions.Count == 1,
				"the source-bound mark to become visible before restart");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
		}

		var unmarkWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(unmarkWindow);
			await UiTestDriver.SwitchPreviewModeAsync(unmarkWindow, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				unmarkWindow,
				() => UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
					unmarkWindow,
					"PreviewTextControl").Document?.Redactions.Count == 1,
				"the source-bound mark to load before removal");
			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(unmarkWindow);
			await UiTestDriver.WaitForConditionAsync(
				unmarkWindow,
				() => CountOccurrences(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(unmarkWindow),
					manualValue) == 2,
				"the selected occurrence to be restored before immediate shutdown");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(unmarkWindow, cleanupAppData: false);
		}

		var finalWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(finalWindow);
			await UiTestDriver.SwitchPreviewModeAsync(finalWindow, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				finalWindow,
				() => CountOccurrences(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(finalWindow),
					manualValue) == 2,
				"the durable source-bound removal to survive a second restart");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(finalWindow);
		}

		static int CountOccurrences(string content, string value)
		{
			var count = 0;
			var offset = 0;
			while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
			{
				count++;
				offset += value.Length;
			}
			return count;
		}
	}

	[AvaloniaFact]
	public async Task HideHere_CloseWhileMarkStoreIsContended_WaitsForDurableWrite()
	{
		const string manualValue = "contended-close-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "hide-here-contended-close");
		var storeDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(storeDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string first = \"{manualValue}\";\nconst string second = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		}

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		FileStream? heldLock = new(
			Path.Combine(storeDirectory, "project-secret-marks.json.lock"),
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.None);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => CountOccurrences(
					UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
					manualValue) == 1,
				"the source-bound mark to redact before its store write completes");

			window.Close();
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.False(window.ShutdownCompletion.IsCompleted);

			heldLock.Dispose();
			heldLock = null;
			await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			heldLock?.Dispose();
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		var loaded = await new ProjectProfileStore(() => appDataPath).LoadMarksAsync(
			project.RootPath,
			TestContext.Current.CancellationToken);
		Assert.True(loaded.Succeeded);
		var mark = Assert.Single(loaded.Snapshot!.Marks);
		Assert.Equal("src/Secrets.cs", mark.RelativePath);
		Assert.True(mark.SourceOffset >= 0);

		static int CountOccurrences(string content, string value)
		{
			var count = 0;
			var offset = 0;
			while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
			{
				count++;
				offset += value.Length;
			}
			return count;
		}
	}

	private static async Task<ProjectContextPlan> BuildSecretOutputPlanAsync(
		string projectRoot,
		int transformMode)
	{
		var analysisService = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		return await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				projectRoot,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: [],
					HideSecrets: true,
					CompressCode: (transformMode & 1) != 0,
					StripComments: (transformMode & 2) != 0,
					StripBlankLines: (transformMode & 4) != 0)),
			TestContext.Current.CancellationToken);
	}

	[AvaloniaFact]
	public async Task KeepAsIs_PersistsAcrossEveryContentTransformationCombination()
	{
		const string secret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      // The transformations before the value deliberately move its output coordinate.

			      private static string Build()
			      {
			          return "noise";
			      }

			      public const string AwsAccessKey = "{{secret}}";
			  }
			  """,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver
					.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
						window,
						"PreviewTextControl")
					.Document?.Redactions is [{ State: SecretPreviewSpanState.Redacted }],
				"the initial secret occurrence to be redacted");

			var previewControl = UiTestDriver
				.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
					window,
					"PreviewTextControl");
			previewControl.Focus();
			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => previewControl.Document?.Redactions is
					[{ State: SecretPreviewSpanState.KeptAsIs }],
				"the source occurrence to be kept");

			var modes = new[]
			{
				0b000, 0b001, 0b011, 0b010, 0b110, 0b111, 0b101, 0b100, 0b000
			};
			foreach (var mode in modes.Skip(1))
			{
				await SetContentOptionAsync(IgnoreOptionId.CompressCode, (mode & 0b001) != 0);
				await SetContentOptionAsync(IgnoreOptionId.StripComments, (mode & 0b010) != 0);
				await SetContentOptionAsync(IgnoreOptionId.StripBlankLines, (mode & 0b100) != 0);
				await UiTestDriver.ClickApplySettingsAsync(window);
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => previewControl.Document?.Redactions is
						[{ State: SecretPreviewSpanState.KeptAsIs }] &&
					      UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
						      secret,
						      StringComparison.Ordinal),
					$"Keep as is to survive transformation mode {mode}");
			}

			async Task SetContentOptionAsync(IgnoreOptionId optionId, bool expected)
			{
				var option = UiTestDriver.GetViewModel(window).ContentProcessingOptions.Single(
					candidate => candidate.Id == optionId);
				if (option.IsChecked != expected)
					await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PersistentManualSecret_RestartRestoresProfileAndPreviewClipboardRedaction()
	{
		const string manualValue = "ordinary-manual-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "persistent-restart");
		Directory.CreateDirectory(appDataPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var firstWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.OpenPreviewAsync(firstWindow);
			await UiTestDriver.SwitchPreviewModeAsync(firstWindow, PreviewContentMode.Content);
			Assert.Contains(
				manualValue,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(firstWindow),
				StringComparison.Ordinal);

			await UiTestDriver.RequestPersistentSecretMarkAsync(firstWindow, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				firstWindow,
				() => UiTestDriver.GetViewModel(firstWindow).HideSecretsOption is { IsChecked: true } &&
				      !UiTestDriver.ComputeCurrentPreviewCopyPayload(firstWindow).Contains(
					      manualValue,
					      StringComparison.Ordinal),
				"the durable manual mark to redact Preview");

			var store = new ProjectProfileStore(() => appDataPath);
			PersistentSecretMarksLoadResult? persisted = null;
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
			while (DateTime.UtcNow < deadline)
			{
				persisted = await store.LoadMarksAsync(
					project.RootPath,
					TestContext.Current.CancellationToken);
				if (persisted.Succeeded && persisted.Snapshot!.Marks.Count == 1)
					break;
				await Task.Delay(25, TestContext.Current.CancellationToken);
			}
			Assert.True(persisted?.Succeeded);
			var mark = Assert.Single(persisted!.Snapshot!.Marks);
			Assert.True(PersistentSecretIdentity.IsV2(mark.H));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(firstWindow, cleanupAppData: false);
		}

		var reopenedWindow = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				reopenedWindow,
				IgnoreOptionId.HideSecrets,
				visible: true,
				isChecked: true);
			await UiTestDriver.OpenPreviewAsync(reopenedWindow);
			await UiTestDriver.SwitchPreviewModeAsync(reopenedWindow, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				reopenedWindow,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(reopenedWindow).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the reopened Preview to apply the persistent mark");

			var expectedClipboard = UiTestDriver.ComputeCurrentPreviewCopyPayload(reopenedWindow);
			await UiTestDriver.SetClipboardTextAsync(reopenedWindow, "pending-persistent-mark-copy");
			await UiTestDriver.ClickPreviewCopyButtonAsync(reopenedWindow);
			await UiTestDriver.WaitForClipboardTextAsync(reopenedWindow, expectedClipboard);
			Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", expectedClipboard, StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(reopenedWindow);
		}
	}

	[AvaloniaFact]
	public async Task FirstPersistentManualSecret_WithContendedKeyLockDoesNotBlockDispatcher()
	{
		const string manualValue = "dispatcher-responsive-manual-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "persistent-key-contention");
		var keyDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(keyDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		var lockPath = Path.Combine(keyDirectory, "secret-mark-hmac.key.lock");
		var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		try
		{
			var sourcePath = Path.Combine(project.RootPath, "src", "Secrets.cs");
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);

			await UiTestDriver.RequestPersistentSecretMarkAsync(window, manualValue);
			await window.Dispatcher
				.InvokeAsync(static () => { }, DispatcherPriority.Background)
				.GetTask()
				.WaitAsync(TimeSpan.FromSeconds(1));
			Assert.DoesNotContain(
				manualValue,
				UiTestDriver.RedactFileWithCurrentSession(window, sourcePath),
				StringComparison.Ordinal);

			heldLock.Dispose();
			var store = new ProjectProfileStore(() => appDataPath);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var loaded = store.LoadMarksAsync(project.RootPath).AsTask().GetAwaiter().GetResult();
					return loaded.Succeeded && loaded.Snapshot!.Marks.Count == 1;
				},
				"the async identity initialization to persist the pending mark");
		}
		finally
		{
			heldLock.Dispose();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task LockedProfileStore_IsRetriedWithoutTreatingPersistedSelectionsOrMarksAsMissing()
	{
		const string manualValue = "locked-profile-manual-secret-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "locked-profile-retry");
		Directory.CreateDirectory(appDataPath);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var store = new ProjectProfileStore(() => appDataPath);
		Assert.True(store.TrySaveProfile(
			project.RootPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HideSecrets] = true
				})));
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(
				identityProvider,
				manualValue,
				out var identity));
			Assert.True((await store.AddMarkAsync(
				project.RootPath,
				new MarkedSecretProfileEntry(identity, "caption", manualValue.Length),
				TestContext.Current.CancellationToken)).Succeeded);
		}

		var storeDirectory = Path.Combine(appDataPath, "DevProjex");
		var lockPath = Path.Combine(storeDirectory, "project-profiles.json.lock");
		Task<MainWindow> opening;
		using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
		{
			opening = UiTestDriver.CreateLoadedMainWindowAsync(
				project,
				appDataPathOverride: appDataPath);
			await Task.Delay(500, TestContext.Current.CancellationToken);
			Assert.False(opening.IsCompleted);
		}

		var window = await opening;
		try
		{
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.HideSecrets,
				visible: true,
				isChecked: true);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the profile loaded after lock release to retain its persistent mark");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PersistentManualSecret_WithUnavailableIdentityKeyFallsBackToSessionOnlyRedaction()
	{
		const string manualValue = "ordinary-session-fallback-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var appDataPath = Path.Combine(project.AppDataPath, "persistent-key-unavailable");
		var keyDirectory = Path.Combine(appDataPath, "DevProjex");
		Directory.CreateDirectory(keyDirectory);
		await File.WriteAllBytesAsync(
			Path.Combine(keyDirectory, "secret-mark-hmac.key"),
			[1, 2, 3],
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		var errors = new ConcurrentQueue<string>();
		UiTestDriver.OverridePreviewErrorHandler(
			window,
			message =>
			{
				errors.Enqueue(message);
				return Task.CompletedTask;
			});
		try
		{
			var sourcePath = Path.Combine(project.RootPath, "src", "Secrets.cs");
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);

			var location = await UiTestDriver.RequestPersistentSecretMarkAsync(window, manualValue);
			Assert.Equal("src/Secrets.cs", location.RelativePath);
			Assert.Equal(
				(await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken))
					.IndexOf(manualValue, StringComparison.Ordinal),
				location.SourceOffset);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetViewModel(window).HideSecretsOption is { IsChecked: true },
				"Hide Secrets to be enabled for the session-only fallback");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => errors.Count == 1,
				"the existing profile-write error channel to be invoked");
			Assert.DoesNotContain(
				manualValue,
				UiTestDriver.RedactFileWithCurrentSession(window, sourcePath),
				StringComparison.Ordinal);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the failed persistent mark to remain redacted for the current session");

			var stored = await new ProjectProfileStore(() => appDataPath)
				.LoadMarksAsync(project.RootPath, TestContext.Current.CancellationToken);
			Assert.True(stored.Succeeded);
			Assert.Empty(stored.Snapshot!.Marks);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PersistentManualSecret_CompletingAfterProjectSwitchDoesNotReportFailureInNewProject()
	{
		const string manualValue = "ordinary-stale-project-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$"const string caption = \"{manualValue}\";\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var errors = new ConcurrentQueue<string>();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		UiTestDriver.OverridePreviewErrorHandler(
			window,
			message =>
			{
				errors.Enqueue(message);
				return Task.CompletedTask;
			});
		UiTestDriver.OverridePersistentSecretMarkDeltaHandler(
			window,
			async _ =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task;
				return new PersistentSecretMarkWriteResult(
					PersistentSecretMarkStoreStatus.WriteFailed,
					null);
			});

		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.RequestPersistentSecretMarkAsync(window, manualValue);
			await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

			var nextProject = Path.Combine(project.AppDataPath, "next-project");
			Directory.CreateDirectory(nextProject);
			await window.Dispatcher.InvokeAsync(
				() => UiTestDriver.SetCurrentProjectPath(window, nextProject),
				DispatcherPriority.Normal);
			releaseWrite.TrySetResult();
			await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
			await Task.Delay(100, TestContext.Current.CancellationToken);

			Assert.Empty(errors);
			await window.Dispatcher.InvokeAsync(
				() => UiTestDriver.SetCurrentProjectPath(window, project.RootPath),
				DispatcherPriority.Normal);
		}
		finally
		{
			releaseWrite.TrySetResult();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task HideSecrets_IsOptInAndUpdatesPreviewCountWithoutChangingSource()
    {
        const string secret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
        using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
        var sourcePath = Path.Combine(project.RootPath, "src", "Secrets.cs");
		var sourceText = await File.ReadAllTextAsync(sourcePath);
		await File.WriteAllTextAsync(
			sourcePath,
			string.Concat(Enumerable.Repeat("// preview padding\n", 240)) + sourceText);
        var sourceBefore = File.ReadAllBytes(sourcePath);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            var originalPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Contains(secret, originalPreview, StringComparison.Ordinal);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: true);
			Assert.Equal(originalPreview, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var document = UiTestDriver
                        .GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
                            window,
                            "PreviewTextControl")
                        .Document;
					return document?.Redactions.Count == 1 &&
					       document.Redactions[0].State == SecretPreviewSpanState.Redacted;
                },
                "Hide Secrets preview and count to converge");

            var redactedPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.DoesNotContain(secret, redactedPreview, StringComparison.Ordinal);
            Assert.Contains(
                "DEVPROJEX_REDACTED[aws-access-token#1]",
                redactedPreview,
                StringComparison.Ordinal);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.HideSecrets,
                "Hide secrets (1)");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					UiTestDriver.GetViewModel(window).SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal),
				"the content-processing tooltip to publish detected and hidden counts");

			foreach (var mode in new[]
			         {
				         PreviewContentMode.TreeAndContent,
				         PreviewContentMode.Tree,
				         PreviewContentMode.Content
			         })
			{
				await UiTestDriver.SwitchPreviewModeAsync(window, mode);
				await UiTestDriver.WaitForIgnoreOptionStateAsync(
					window,
					IgnoreOptionId.HideSecrets,
					visible: true,
					isChecked: true);
			}

			var previewControl = UiTestDriver
				.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
					window,
					"PreviewTextControl");
			var previewScrollViewer = UiTestDriver.GetRequiredControl<ScrollViewer>(
				window,
				"PreviewTextScrollViewer");
			previewControl.Focus();
			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var viewportBeforeOverride = previewScrollViewer.Offset;
			Assert.True(viewportBeforeOverride.Y > 0);
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => previewControl.Document?.Redactions.Count == 1 &&
				      previewControl.Document.Redactions[0].State == SecretPreviewSpanState.KeptAsIs,
				"the keyboard override to restore the detected value");
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HideSecrets,
				"Hide secrets (1/0)");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					UiTestDriver.GetViewModel(window).SettingsSecretsNotice,
					"Found: 1. Hidden: 0.",
					StringComparison.Ordinal),
				"the content-processing tooltip to update after a redaction override");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.InRange(
				Math.Abs(previewScrollViewer.Offset.Y - viewportBeforeOverride.Y),
				0,
				1);
			var previewBeforeDisableDraft = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			var documentBeforeDisableDraft = previewControl.Document;

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
			Assert.Same(documentBeforeDisableDraft, previewControl.Document);
			Assert.Equal(previewBeforeDisableDraft, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => string.Equals(
                    UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                    originalPreview,
                    StringComparison.Ordinal),
                "disabled Hide Secrets preview to return to the original payload");

            Assert.Equal(sourceBefore, File.ReadAllBytes(sourcePath));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	[AvaloniaFact]
	public async Task HideSecrets_WithNoFindings_UpdatesOnlyContentState()
	{
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
		await AssertHideSecretsIsolatedFromPathSelectionAsync(project, expectedCount: 0);
	}

	[AvaloniaFact]
	public async Task PersistedCompression_DoesNotExposeSecretsRemovedFromEveryOutputMode()
	{
		const string removedSecret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      public string Read()
			      {
			          return "{{removedSecret}}";
			      }
			  }
			  """,
			TestContext.Current.CancellationToken);
		var appDataPath = Path.Combine(project.AppDataPath, "compression-and-secrets-profile");
		Directory.CreateDirectory(appDataPath);
		new ProjectProfileStore(() => appDataPath).SaveProfile(
			project.RootPath,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions:
				[
					IgnoreOptionId.CompressCode,
					IgnoreOptionId.HideSecrets
				],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.CompressCode] = true,
					[IgnoreOptionId.HideSecrets] = true
				}));

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"DevProjex found no secrets",
					StringComparison.Ordinal) &&
				      viewModel.ContentProcessingOptions.Any(
					      static option =>
						      option.Id == IgnoreOptionId.HideSecrets &&
						      option.StatusText == "DevProjex found no secrets" &&
						      option.IsInformationStatus) &&
				      viewModel.ContentProcessingOptions.Any(
					      static option =>
						      option.Id == IgnoreOptionId.CompressCode &&
						      option.Label == "Compress code") &&
				      viewModel.SettingsCompressionNotice.StartsWith(
					      "Compressed ",
					      StringComparison.Ordinal),
				"compressed secret discovery to ignore a removed method body");
			Assert.Contains(
				$"{Environment.NewLine}≈Tokens: ",
				viewModel.SettingsCompressionNotice,
				StringComparison.Ordinal);
			Assert.True(viewModel.HideSecretsOption?.IsChecked);
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.CompressCode && option.IsChecked);

			await UiTestDriver.OpenPreviewAsync(window);
			foreach (var mode in new[]
			         {
				         PreviewContentMode.Tree,
				         PreviewContentMode.TreeAndContent,
				         PreviewContentMode.Content,
				         PreviewContentMode.Tree
			         })
			{
				await UiTestDriver.SwitchPreviewModeAsync(window, mode);
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.ContentProcessingOptions.Any(
						      static option =>
							      option.Id == IgnoreOptionId.HideSecrets &&
							      option.StatusText == "DevProjex found no secrets") &&
					      viewModel.ContentProcessingOptions.Any(
						      static option =>
							      option.Id == IgnoreOptionId.CompressCode &&
							      option.Label == "Compress code"),
					$"content-processing state to remain stable in {mode}");
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task HideSecrets_OptInEmptyScanCompletesWhilePreviewIsOpen()
	{
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
		for (var index = 0; index < 120; index++)
		{
			await File.WriteAllTextAsync(
				Path.Combine(project.RootPath, $"source-{index:D3}.cs"),
				$"internal static class Source{index:D3} {{ }}\n");
		}
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.IsAnyPreviewVisible &&
				      viewModel.ContentProcessingOptions.Any(
					      static option =>
						      option.Id == IgnoreOptionId.HideSecrets &&
						      option.StatusText == "DevProjex found no secrets" &&
						      option.IsInformationStatus) &&
				      string.Equals(
					      viewModel.SettingsSecretsNotice,
					      "DevProjex found no secrets",
					      StringComparison.Ordinal),
				"the opt-in empty scan to complete without closing Preview");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task HideSecrets_WithFindings_UpdatesOnlyContentState()
	{
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
		Directory.CreateDirectory(Path.Combine(project.RootPath, "secrets"));
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "secrets", "credentials.env"),
			"AWS_ACCESS_KEY_ID=AKIA" + "Z7M3Q5X2P6N4R7T5\n");

		await AssertHideSecretsIsolatedFromPathSelectionAsync(project, expectedCount: 1);
	}

	private static async Task AssertHideSecretsIsolatedFromPathSelectionAsync(
		UiTestProject project,
		int expectedCount)
	{
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);
			Assert.False(UiTestDriver.GetViewModel(window).IsAnyPreviewVisible);

			var tree = UiTestDriver.GetCurrentTreeIdentity(window);
			var inventory = UiTestDriver.GetCurrentTreeInventoryIdentity(window);
			var previewDocument = UiTestDriver.GetViewModel(window).PreviewDocument;
			var selectionRevision = UiTestDriver.GetSelectionRevision(window);
			using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();

			var viewModel = UiTestDriver.GetViewModel(window);
			if (expectedCount == 0)
			{
				// No scan may run before the user opts in: the row is offered idle, without status.
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.HasContentProcessingOptions &&
					      UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible &&
					      viewModel.ContentProcessingOptions.Any(
						      static option =>
							      option.Id == IgnoreOptionId.HideSecrets &&
							      !option.HasStatus),
					"the idle Hide secrets row to appear without any background scan");
				Assert.False(viewModel.HideSecretsOption?.IsChecked);
				Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
				await UiTestDriver.ClickApplySettingsAsync(window);
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.ContentProcessingOptions.Any(
						      static option =>
							      option.Id == IgnoreOptionId.HideSecrets &&
							      option.StatusText == "DevProjex found no secrets" &&
							      option.IsInformationStatus) &&
					      string.Equals(
						      viewModel.SettingsSecretsNotice,
						      "DevProjex found no secrets",
						      StringComparison.Ordinal),
					"the opt-in scan to confirm no secrets on the visible row");
			}
			else
			{
				await UiTestDriver.WaitForIgnoreOptionLabelAsync(
					window,
					IgnoreOptionId.HideSecrets,
					"Hide secrets");
				Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
				await UiTestDriver.ClickApplySettingsAsync(window);
				await UiTestDriver.WaitForIgnoreOptionLabelAsync(
					window,
					IgnoreOptionId.HideSecrets,
					$"Hide secrets ({expectedCount})");
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => string.Equals(
						viewModel.SettingsSecretsNotice,
						$"Found: {expectedCount}. Hidden: {expectedCount}.",
						StringComparison.Ordinal),
					"the opt-in scan to report and hide the findings");
				Assert.True(UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible);
			}

			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);
			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventory, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			Assert.Same(previewDocument, UiTestDriver.GetViewModel(window).PreviewDocument);
			Assert.Equal(selectionRevision, UiTestDriver.GetSelectionRevision(window));

			if (expectedCount > 0)
			{
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
				await UiTestDriver.WaitForIgnoreOptionStateAsync(
					window,
					IgnoreOptionId.HideSecrets,
					visible: true,
					isChecked: false);
				await UiTestDriver.ClickApplySettingsAsync(window);
				await UiTestDriver.WaitForIgnoreOptionLabelAsync(
					window,
					IgnoreOptionId.HideSecrets,
					"Hide secrets");
				// Switching the option off withdraws the request entirely: no counters remain.
				await UiTestDriver.WaitForConditionAsync(
					window,
					() => viewModel.SettingsSecretsNotice.Length == 0,
					"disabling Hide secrets to clear its status");
			}
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.SmartIgnore,
				visible: true,
				isChecked: true);

			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventory, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			Assert.Same(previewDocument, UiTestDriver.GetViewModel(window).PreviewDocument);
			Assert.Equal(selectionRevision, UiTestDriver.GetSelectionRevision(window));
			var diagnostics = measurement.Capture();
			Assert.Equal(0, diagnostics.WorkspaceScans);
			Assert.Equal(0, diagnostics.DirectoryEnumerations);
			Assert.Equal(0, diagnostics.FileEnumerations);
			Assert.Equal(0, diagnostics.IgnoreRulesBuilds);
			Assert.Equal(0, diagnostics.FullSelectionRefreshes);
			Assert.Equal(0, diagnostics.LiveSelectionRefreshes);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task IgnoredNumericExtensions_AreNotOfferedUntilTheirOwningIgnoreRuleIsDisabled()
    {
        using var project = UiTestProject.CreateWithIgnoredNumericExtensions();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(viewModel.Extensions, option => option.Name == ".1770912967592");
            Assert.Contains(viewModel.Extensions, option => option.Name == ".1770912967593");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967589");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967590");
            Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".1770912967591");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).Extensions.Any(option => option.Name == ".1770912967589") &&
                      UiTestDriver.GetViewModel(window).Extensions.Any(option => option.Name == ".1770912967590"),
                "empty-file numeric extensions to become available");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).Extensions.All(option =>
                    option.Name is not ".1770912967589" and not ".1770912967590"),
                "empty-file numeric extensions to be removed again");

            Assert.Contains(UiTestDriver.GetViewModel(window).Extensions, option => option.Name == ".1770912967593");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NewWorkspace_WithDynamicIgnoreEntries_KeepsDynamicOptionsCheckedByDefault()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFiles,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HideSecrets,
                visible: true,
                isChecked: false);
			Assert.True(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionSelectionRefresh_ShowsAndHidesEmptyFoldersCounterBasedOnEffectiveTreeDelta()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (2)");

            markdownOption = UiTestDriver.GetViewModel(window).Extensions.Single(option => option.Name == ".md");
            markdownOption.IsChecked = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionsAllToggleRefresh_RecomputesEmptyFoldersCounterForBulkSelectionChanges()
    {
        using var project = UiTestProject.CreateWithExtensionSensitiveEmptyFolders();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);

            var allExtensionsCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "ExtensionsAllCheckBox");
            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                "Empty folders (4)");

            await UiTestDriver.ClickAsync(window, allExtensionsCheckBox);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.EmptyFolders,
                visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitIgnoredExtensionlessNoise_DoesNotInflateExtensionlessCounter()
    {
        using var project = UiTestProject.CreateWithGitIgnoredExtensionlessNoise();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.ExtensionlessFiles,
                "Files without extension (1)");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanGitAndSmartControllers_RemainHiddenUntilTheyAffectVisibleContent()
    {
        using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "bin", "Debug", "net10.0", "App.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RepositoryGitModes_AllowTrackedGitIgnoreSmartOnlyAndNoFiltering()
    {
        EnsureGitAvailable();
        using var project = UiTestProject.CreateWithCleanGitAndSmartWorkspace();
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "LocalOnly.cs"),
            "class LocalOnly {}\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(project.RootPath, "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "logs", "ignored.log"),
            "ignored by gitignore\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(project.RootPath, "bin", "Debug"));
        await File.WriteAllTextAsync(
            Path.Combine(project.RootPath, "bin", "Debug", "app.dll"),
            "generated artifact\n",
            TestContext.Current.CancellationToken);
        RunGit(project.RootPath, "init", "--quiet");
        RunGit(
            project.RootPath,
            "add",
            "--",
            ".gitignore",
            "App.csproj",
            "Program.cs");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            var initialIgnoreOptions = UiTestDriver.GetViewModel(window).IgnoreOptions;
            Assert.DoesNotContain(initialIgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
            Assert.Equal(
                IgnoreOptionId.TrackedGitFilesOnly,
                Assert.Single(initialIgnoreOptions, option => option.IsControllerGroupEnd).Id);
            await WaitForProjectTreePathStateAsync(window, exists: true, "LocalOnly.cs");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "ignored.log");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "LocalOnly.cs");
            await WaitForProjectTreePathStateAsync(window, exists: true, "Program.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "LocalOnly.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                isChecked: false);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            var smartOnlyIgnoreOptions = UiTestDriver.GetViewModel(window).IgnoreOptions;
            Assert.Equal(
                [
                    IgnoreOptionId.SmartIgnore,
                    .. ProjectPresentationCatalog.ContentTransformations
                        .OrderBy(static descriptor => descriptor.Order)
                        .Select(static descriptor => descriptor.LegacyOptionId),
                    IgnoreOptionId.UseGitIgnore,
                    IgnoreOptionId.TrackedGitFilesOnly
                ],
                smartOnlyIgnoreOptions
                    .Take(3 + ProjectPresentationCatalog.ContentTransformationOptionIds.Count)
                    .Select(static option => option.Id));
            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "ignored.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "bin", "Debug", "app.dll");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "bin", "Debug", "app.dll");

            await UiTestDriver.ClickAsync(
                window,
                UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SwitchingToTrackedOnlyRescansGitIgnoredContainerForNestedRepository()
    {
        EnsureGitAvailable();
        using var project = UiTestProject.CreateWithIgnoredNestedGitRepositoryWorkspace();
        RunGit(project.RootPath, "init", "--quiet");
        RunGit(
            project.RootPath,
            "add",
            "--",
            ".gitignore",
            "App.csproj",
            "Program.cs");
        var nestedRepositoryPath = Path.Combine(project.RootPath, "ignored-container", "nested");
        RunGit(nestedRepositoryPath, "init", "--quiet");
        RunGit(nestedRepositoryPath, "add", "--", "Nested.csproj", "Tracked.cs");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(
                window,
                exists: false,
                "ignored-container",
                "nested",
                "Tracked.cs");

            await SetIgnoreOptionCheckedAsync(
                window,
                IgnoreOptionId.TrackedGitFilesOnly,
                isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await WaitForProjectTreePathStateAsync(
                window,
                exists: true,
                "ignored-container",
                "nested",
                "Tracked.cs");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ExplicitUncheckedGitIgnoreController_RemainsVisibleWhenDotFilesTakesOver()
    {
        using var project = UiTestProject.CreateWithGitIgnoreDotFileOnlyWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFiles,
                visible: true,
                isChecked: true);

            await SetIgnoreAllCheckedAsync(window, isChecked: true);
            await UiTestDriver.ClickAsync(window, UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CleanPythonSmartController_RemainsHiddenUntilSmartArtifactAppears()
    {
        using var project = UiTestProject.CreateWithCleanPythonSmartWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);

            var artifactPath = Path.Combine(project.RootPath, "src", "__pycache__", "app.cpython-310.pyc");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllTextAsync(artifactPath, "binary");

            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SmartIgnoreNegativeMatrix_RefreshAndToggleCyclePrunesOnlyNewProvenArtifact()
    {
        using var project = UiTestProject.CreateWithSmartIgnoreNegativeMatrixWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: false);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".cs", ".json", ".md", ".nupkg", ".php", ".txt"],
                hidden: [".user"]);

            WriteTextFile(project.RootPath, Path.Combine("obj", "project.assets.json"), "{}\n");
            WriteTextFile(project.RootPath, "App.csproj.user", "local state\n");
            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: true, isChecked: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await AssertSmartIgnoreNegativeSourcePathsAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
            await WaitForExtensionStateAsync(window, ".user", visible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task HiddenDotFolderOverlap_ShowsHiddenFoldersOnlyWhenDotFoldersNoLongerHidesSameFolder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = UiTestProject.CreateWithHiddenDotFolderOverlapWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.DotFolders,
                "dot folders (2)");
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionLabelAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                "Hidden folders (1)");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.HiddenFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            // Disabling user-facing dot/hidden filters may expose ordinary project data, but
            // it must never override the independently selected Git administrative boundary.
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: true, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: false, ".hidden-dot", "payload.txt");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.HiddenFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "config.txt");
            await WaitForProjectTreePathStateAsync(window, exists: true, ".hidden-dot", "payload.txt");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RefreshProject_NewGitIgnoreInExistingScope_BypassesHotDiscoveryCacheImmediately()
    {
		using var project = UiTestProject.CreateWithPythonSmartIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            WriteTextFile(project.RootPath, ".gitignore", "*.log\n");
            WriteTextFile(project.RootPath, Path.Combine("logs", "runtime.log"), "ignored immediately\n");
            await UiTestDriver.RefreshProjectAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "runtime.log");
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulCloneCommit_LoadsManagedIgnoreBoundaryThroughProductionPath()
    {
        using var initialProject = UiTestProject.CreateWithDynamicIgnoreEntries();
        using var clonedProject = UiTestProject.CreateWithManagedGitCloneContentWorkspace();
        WriteTextFile(clonedProject.RootPath, "App.csproj", "<Project />\n");
        WriteTextFile(clonedProject.RootPath, ".gitignore", "logs/\n");
        WriteTextFile(clonedProject.RootPath, Path.Combine("logs", "runtime.log"), "git ignored\n");
        WriteTextFile(clonedProject.RootPath, Path.Combine("obj", "project.assets.json"), "{}\n");
        RunGit(clonedProject.RootPath, "init", "--quiet");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(initialProject);

        try
        {
            var result = new GitCloneResult(
                Success: true,
                LocalPath: clonedProject.RootPath,
                SourceType: ProjectSourceType.GitClone,
                DefaultBranch: "main",
                RepositoryName: "managed-ignore-probe",
                RepositoryUrl: "https://example.test/managed-ignore-probe.git",
                ErrorMessage: null);
            await window.Dispatcher.InvokeAsync(() =>
                window.ApplySuccessfulGitCloneAsync(
                    result,
                    clonedProject.RootPath,
                    result.RepositoryUrl!,
                    TestContext.Current.CancellationToken));

            Assert.Equal(ProjectSourceType.GitClone, UiTestDriver.GetViewModel(window).ProjectSourceType);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "HEAD");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: false, ".git", "HEAD");
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "runtime.log");
            await WaitForProjectTreePathStateAsync(window, exists: false, "obj", "project.assets.json");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithGitIgnore_KeepsGitAndSmartControllersIndependent()
    {
        using var project = UiTestProject.CreateWithPythonGitIgnoreWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);

            await WaitForProjectTreePathStateAsync(window, exists: true, "src", "app.py");
            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: false, "logs", "app.log");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            await WaitForProjectTreePathStateAsync(window, exists: true, "logs", "app.log");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_KeepsDotFoldersToggleAvailableAfterSmartIgnoreChanges()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await WaitForProjectTreePathStateAsync(window, exists: false, ".idea");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);

            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPythonProjectWithIdeaFolder_SmartOnlyKeepsDotFolderVisible()
    {
        using var project = UiTestProject.CreateWithNestedPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await WaitForProjectTreePathStateAsync(window, exists: true, "lab2", ".idea", "workspace.xml");
            await WaitForProjectTreePathStateAsync(window, exists: false, "lab2", "__pycache__");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task NestedPolyglotWorkspace_AllOffAndSingleIgnoreTogglesStayScoped()
    {
        using var project = UiTestProject.CreateWithNestedPolyglotIgnoreMatrixWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);

            await SetVisibleIgnoreOptionsCheckedAsync(window, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            Assert.False(UiTestDriver.GetViewModel(window).AllIgnoreChecked);
            Assert.DoesNotContain(UiTestDriver.GetViewModel(window).IgnoreOptions, option => option.IsChecked);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc", ".xml", ".env"],
                hidden: []);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: []);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.SmartIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".log", ".xml", ".env"],
                hidden: [".dll", ".js", ".pyc"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "logs", "runtime.log"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.UseGitIgnore, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".js", ".pyc", ".xml", ".env"],
                hidden: [".log"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    [".idea", "workspace.xml"],
                    [".env"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths: [["api", "logs", "runtime.log"]]);
            await AssertIgnoreOptionsStayStableAsync(window);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFiles, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFolders, visible: true, isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(window, IgnoreOptionId.DotFiles, visible: true, isChecked: true);
            await AssertExtensionStatesAsync(
                window,
                visibleChecked: [".dll", ".log", ".js", ".pyc"],
                hidden: [".xml", ".env"]);
            await AssertNestedPolyglotTreeStateAsync(
                window,
                visiblePaths:
                [
                    ["api", "bin", "Debug", "app.dll"],
                    ["api", "logs", "runtime.log"],
                    ["web", "node_modules", "pkg", "index.js"],
                    ["python", "__pycache__", "app.pyc"],
                    ["README"],
                    ["empty.txt"],
                    ["empty-root"]
                ],
                hiddenPaths:
                [
                    [".idea", "workspace.xml"],
                    [".env"]
                ]);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task HierarchicalGitIgnore_ToggleAndRefreshKeepFourScopesAtomic()
    {
        using var project = UiTestProject.CreateWithHierarchicalGitIgnoreCombatWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: true,
                isChecked: true);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: false);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.UseGitIgnore, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);

            await UiTestDriver.RefreshProjectAsync(window);
            await AssertHierarchicalGitIgnoreTreeStateAsync(window, gitIgnoreEnabled: true);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaGitIgnore_DoesNotExposeGitIgnoreOptionAcrossSmartAndDotToggles()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: true);

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.UseGitIgnore,
                visible: false);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_IgnoreOptionsStayStableAcrossRepeatedRefreshes()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);

            if (UiTestDriver.GetViewModel(window).IgnoreOptions.Single(option => option.Id == IgnoreOptionId.DotFolders).IsChecked is false)
            {
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.ClickApplySettingsAsync(window);
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.SmartIgnore);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            await AssertIgnoreOptionsStayStableAsync(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.DotFolders);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: false);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RepeatedSmartAndDotFolderCyclesKeepTreeAndTogglesAligned()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            for (var cycle = 0; cycle < 2; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: false);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: true, "src", "__pycache__", "app.pyc");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.SmartIgnore,
                    visible: true,
                    isChecked: true);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: false);
                await WaitForProjectTreePathStateAsync(window, exists: true, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await UiTestDriver.WaitForIgnoreOptionStateAsync(
                    window,
                    IgnoreOptionId.DotFolders,
                    visible: true,
                    isChecked: true);
                await WaitForProjectTreePathStateAsync(window, exists: false, ".idea", "workspace.xml");
                await WaitForProjectTreePathStateAsync(window, exists: false, "src", "__pycache__");
            }

            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_RapidSmartAndDotFolderChangesConvergeToLastAppliedState()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: true,
                    dotChecked: false,
                    ideaVisible: true,
                    pycacheVisible: false);
                await AssertIgnoreOptionsStayStableAsync(window);

                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
                await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
                await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
                await AssertPythonIdeaWorkspaceStateAsync(
                    window,
                    smartChecked: false,
                    dotChecked: true,
                    ideaVisible: false,
                    pycacheVisible: true);
                await AssertIgnoreOptionsStayStableAsync(window);
            }

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: true);
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: true);
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);
            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: true,
                dotChecked: true,
                ideaVisible: false,
                pycacheVisible: false);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PythonProjectWithIdeaFolder_BlockedStaleSmartRefreshCannotRestoreOldIgnoreChecks()
    {
        using var project = UiTestProject.CreateWithPythonSmartIgnoreAndIdeaWorkspace();
        using var blockingScanner = new SwitchableBlockingFileSystemScanner(
            project.RootPath,
            ignoreCancellation: true);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                ScanOptionsUseCase = new ScanOptionsUseCase(
                    LegacyWorkspaceScannerTestAdapter.Adapt(blockingScanner))
            });

        try
        {
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.SmartIgnore,
                visible: true,
                isChecked: true);
            await UiTestDriver.WaitForIgnoreOptionStateAsync(
                window,
                IgnoreOptionId.DotFolders,
                visible: true,
                isChecked: true);

            blockingScanner.EnableBlocking();
            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.SmartIgnore, isChecked: false);
            Assert.True(
                blockingScanner.WaitForBlockedCall(TimeSpan.FromSeconds(10)),
                "The stale Python smart-ignore refresh did not reach the controlled scanner block.");

            await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.DotFolders, isChecked: false);
            blockingScanner.Release();
            await ApplySettingsAndWaitForIgnoreRefreshAsync(window);

            await AssertPythonIdeaWorkspaceStateAsync(
                window,
                smartChecked: false,
                dotChecked: false,
                ideaVisible: true,
                pycacheVisible: true);
            await AssertIgnoreOptionsStayStableAsync(window);
        }
        finally
        {
            blockingScanner.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task WaitForExtensionStateAsync(
        MainWindow window,
        string extensionName,
        bool visible,
        bool? isChecked = null)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var option = UiTestDriver.GetViewModel(window).Extensions
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, extensionName, StringComparison.OrdinalIgnoreCase));
                if (!visible)
                    return option is null;

                return option is not null && (isChecked is null || option.IsChecked == isChecked);
            },
            $"extension option '{extensionName}' to be visible={visible}, checked={isChecked?.ToString() ?? "<any>"}");
    }

    private static void MutateExternalRefreshWorkspace(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("api", ".gitignore"), "logs/\n");
        WriteTextFile(rootPath, Path.Combine("api", "App.csproj"), "<Project />\n");
        WriteTextFile(rootPath, Path.Combine("api", "src", "Program.cs"), "class Program {}\n");
        WriteTextFile(rootPath, Path.Combine("api", "logs", "runtime.log"), "ignored by nested gitignore\n");
        WriteTextFile(rootPath, Path.Combine("web", "package.json"), "{}\n");
        WriteTextFile(rootPath, Path.Combine("web", "src", "app.ts"), "export const app = true;\n");
        WriteTextFile(rootPath, Path.Combine("web", "node_modules", "pkg", "index.js"), "module.exports = {};\n");
        WriteTextFile(rootPath, Path.Combine("generated", "report.log"), "new visible log\n");
        WriteTextFile(rootPath, "new-data.csv", "2,updated\n");
        WriteTextFile(rootPath, Path.Combine(".idea", "workspace.xml"), "<project />\n");
        WriteTextFile(rootPath, ".env", "APP_ENV=test\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "empty-root"));
    }

    private static void MutateExternalRefreshWorkspaceSecondWave(string rootPath)
    {
        WriteTextFile(rootPath, Path.Combine("cli", "go.mod"), "module refreshstage\n");
        WriteTextFile(rootPath, Path.Combine("cli", "main.go"), "package main\nfunc main() {}\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "run.py"), "print('refresh stage')\n");
        WriteTextFile(rootPath, Path.Combine("scripts", "debug.log"), "manual extension state must survive refresh\n");
        WriteTextFile(rootPath, Path.Combine(".vscode", "settings.json"), "{}\n");
        WriteTextFile(rootPath, "Dockerfile", "FROM scratch\n");
        Directory.CreateDirectory(Path.Combine(rootPath, "second-empty-root"));
    }

    private static void WriteTextFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static async Task AssertPythonIdeaWorkspaceStateAsync(
        MainWindow window,
        bool smartChecked,
        bool dotChecked,
        bool ideaVisible,
        bool pycacheVisible)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.SmartIgnore,
            visible: true,
            isChecked: smartChecked);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            IgnoreOptionId.DotFolders,
            visible: true,
            isChecked: dotChecked);
        await WaitForProjectTreePathStateAsync(window, exists: ideaVisible, ".idea", "workspace.xml");
        await WaitForProjectTreePathStateAsync(window, exists: pycacheVisible, "src", "__pycache__", "app.pyc");
    }

    private static async Task AssertHierarchicalGitIgnoreTreeStateAsync(
        MainWindow window,
        bool gitIgnoreEnabled)
    {
        string[][] alwaysVisiblePaths =
        [
            ["repo", "keep.rootdrop"],
            ["repo", "module", "module-keep.rootdrop"],
            ["repo", "module", "child", "rescue.moddrop"],
            ["repo", "module", "child", "grand", "visible.deepdrop"],
            ["repo", "module", "child", "grand", "invalid", "visible.txt"],
            ["repo", "outside", "visible.siblingdrop"]
        ];
        string[][] gitIgnoredPaths =
        [
            ["repo", "drop.rootdrop"],
            ["repo", "module", "drop.moddrop"],
            ["repo", "module", "child", "drop.deepdrop"],
            ["repo", "module", "child", "grand", "drop.lastdrop"],
            ["repo", "sibling", "drop.siblingdrop"]
        ];

        foreach (var path in alwaysVisiblePaths)
            await WaitForProjectTreePathStateAsync(window, exists: true, path);
        foreach (var path in gitIgnoredPaths)
            await WaitForProjectTreePathStateAsync(window, exists: !gitIgnoreEnabled, path);
    }

    private static async Task AssertIgnoreOptionsStayStableAsync(MainWindow window)
    {
        var expected = CaptureIgnoreOptionState(window);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            Assert.Equal(expected, CaptureIgnoreOptionState(window));
        }
    }

    private static IReadOnlyList<(IgnoreOptionId Id, bool IsChecked)> CaptureIgnoreOptionState(MainWindow window)
    {
        return UiTestDriver.GetViewModel(window).IgnoreOptions
            .Select(option => (option.Id, option.IsChecked))
            .ToArray();
    }

    private static async Task SetIgnoreOptionCheckedAsync(
        MainWindow window,
        IgnoreOptionId optionId,
        bool isChecked)
    {
        await UiTestDriver.WaitForIgnoreOptionStateAsync(window, optionId, visible: true);

        var option = UiTestDriver.GetViewModel(window).IgnoreOptions.Single(candidate => candidate.Id == optionId);
        if (option.IsChecked == isChecked)
            return;

        await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
        await UiTestDriver.WaitForIgnoreOptionStateAsync(
            window,
            optionId,
            visible: true,
            isChecked: isChecked);
    }

	private static Border GetContentProcessingStatusIndicator(
		MainWindow window,
		IgnoreOptionId optionId) =>
		window.GetVisualDescendants()
			.OfType<Border>()
			.Single(control =>
				string.Equals(control.Name, "ContentProcessingStatusIndicator", StringComparison.Ordinal) &&
				control.DataContext is IgnoreOptionViewModel { Id: var id } &&
				id == optionId);

	[AvaloniaFact]
	public async Task PreviewBulkKeep_ByRuleKeepsAndRehidesAllOccurrencesWithOneRefresh()
	{
		const string firstSecret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		const string secondSecret = firstSecret;
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "SecondSecret.cs"),
			$"const string awsAccessKey = \"{secondSecret}\";\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await SetIgnoreOptionCheckedAsync(window, IgnoreOptionId.HideSecrets, isChecked: true);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var document = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
						window,
						"PreviewTextControl").Document;
					return document?.Redactions
						.Select(static span => span.OccurrenceId)
						.Distinct(StringComparer.Ordinal)
						.Count() == 2;
				},
				"both secret occurrences to be published in Preview");

			var control = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			var initialSpans = control.Document!.Redactions
				.GroupBy(static span => span.OccurrenceId, StringComparer.Ordinal)
				.Select(static group => group.First())
				.ToArray();
			Assert.Equal(2, initialSpans.Length);
			Assert.Single(initialSpans.Select(static span => span.RuleId).Distinct(StringComparer.Ordinal));
			Assert.All(initialSpans, span => Assert.False(string.IsNullOrWhiteSpace(span.RelativePath)));
			var refreshBeforeKeep = UiTestDriver.GetPreviewRefreshVersions(window);

			await UiTestDriver.RequestBulkRedactionToggleThroughContextMenuAsync(
				window,
				initialSpans[0].OccurrenceId,
				ruleScope: true);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					var firstIndex = payload.IndexOf(firstSecret, StringComparison.Ordinal);
					return firstIndex >= 0 &&
					       payload.IndexOf(secondSecret, firstIndex + firstSecret.Length, StringComparison.Ordinal) >= 0;
				},
				"the rule-scoped bulk keep to publish both source values");
			var refreshAfterKeep = UiTestDriver.GetPreviewRefreshVersions(window);
			Assert.Equal(refreshBeforeKeep.Requested + 1, refreshAfterKeep.Requested);
			Assert.Contains(
				UiTestDriver.GetToastService(window).Items,
				toast => string.Equals(toast.Message, "Kept values: 2", StringComparison.Ordinal));

			await UiTestDriver.RequestBulkRedactionToggleThroughContextMenuAsync(
				window,
				initialSpans[0].OccurrenceId,
				ruleScope: true);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
					return !payload.Contains(firstSecret, StringComparison.Ordinal) &&
					       !payload.Contains(secondSecret, StringComparison.Ordinal);
				},
				"the rule-scoped bulk hide to redact both values again");
			Assert.Contains(
				UiTestDriver.GetToastService(window).Items,
				toast => string.Equals(toast.Message, "Hidden again: 2", StringComparison.Ordinal));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PrivateDataAlwaysHide_CreatesValueMarkAndAutoEnablesOnlyItsClass()
	{
		const string manualValue = "privdata42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Contact.txt"),
			$"contact={manualValue}; visible-tail\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			Assert.Equal((false, false), UiTestDriver.GetAppliedContentRedactionState(window));
			var pathFinding = string.Equals(
				OutputRootPathPresentation.MaskLocalUserSegment(project.RootPath),
				project.RootPath,
				StringComparison.Ordinal)
				? 0
				: 1;
			var expectedPrivateDataCount = 1 + pathFinding;

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(
				window,
				manualValue,
				persistent: true,
				classification: ManualRedactionClass.PrivateData);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetAppliedContentRedactionState(window) == (false, true) &&
				      UiTestDriver.GetViewModel(window).HidePrivateDataOption is
				      {
				          IsChecked: true,
				          Label: var label
				      } &&
				      label.EndsWith($"({expectedPrivateDataCount})", StringComparison.Ordinal) &&
				      UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					      "DEVPROJEX_REDACTED[manual-private-data#1]",
					      StringComparison.Ordinal),
				"the private-data mark to enable and redact only its own class");
			Assert.False(UiTestDriver.GetViewModel(window).HideSecretsOption!.IsChecked);
			var store = new ProjectProfileStore(() => UiTestDriver.GetWindowAppDataPath(window));
			MarkedSecretProfileEntry? persistedMark = null;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var loaded = store.LoadMarksAsync(project.RootPath).AsTask().GetAwaiter().GetResult();
					if (!loaded.Succeeded || loaded.Snapshot!.Marks.Count != 1)
						return false;
					persistedMark = loaded.Snapshot.Marks.Single();
					return true;
				},
				"the private-data value mark to become durable");
			Assert.NotNull(persistedMark);
			Assert.Equal(ManualRedactionClass.PrivateData, persistedMark!.Class);
			Assert.Null(persistedMark.RelativePath);
			Assert.Null(persistedMark.SourceOffset);

			await UiTestDriver.RequestManualSecretUnmarkThroughContextMenuAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var loaded = store.LoadMarksAsync(project.RootPath).AsTask().GetAwaiter().GetResult();
					return loaded.Succeeded &&
					       loaded.Snapshot!.Marks.Count == 0 &&
					       UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
						       manualValue,
						       StringComparison.Ordinal);
				},
				"removing the private-data mark to restore Preview content");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData((int)ManualRedactionClass.Secret)]
	[InlineData((int)ManualRedactionClass.PrivateData)]
	public async Task ManualMark_AutoEnablesItsClassWithoutPublishingPendingApplyState(int classValue)
	{
		var classification = (ManualRedactionClass)classValue;
		const string manualValue = "atomic-manual-value-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Atomic.txt"),
			$"value=prefix{manualValue}suffix\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		var observedPendingStates = new List<bool>();
		PropertyChangedEventHandler? propertyChanged = null;
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.False(viewModel.HasPendingFilterSettingsChanges);
			propertyChanged = (_, args) =>
			{
				if (args.PropertyName == nameof(MainWindowViewModel.HasPendingFilterSettingsChanges))
					observedPendingStates.Add(viewModel.HasPendingFilterSettingsChanges);
			};
			viewModel.PropertyChanged += propertyChanged;

			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(
				window,
				manualValue,
				persistent: true,
				classification: classification);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => classification == ManualRedactionClass.Secret
					? UiTestDriver.GetAppliedContentRedactionState(window).HideSecrets
					: UiTestDriver.GetAppliedContentRedactionState(window).HidePrivateData,
				"the manual mark class to become applied");

			Assert.DoesNotContain(true, observedPendingStates);
			Assert.False(viewModel.HasPendingFilterSettingsChanges);
			Assert.False(viewModel.IsApplySettingsAttentionActive);
			var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
			Assert.DoesNotContain("apply-attention", applyButton.Classes);
		}
		finally
		{
			if (propertyChanged is not null)
				UiTestDriver.GetViewModel(window).PropertyChanged -= propertyChanged;
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task AlwaysHide_ExistingSessionAnchorPromotesItAndEnablesRedaction()
	{
		const string manualValue = "promote-existing-session-value-42";
		const string relativePath = "src/Secrets.cs";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var sourcePath = Path.Combine(project.RootPath, relativePath);
		var source = $"const string value = \"{manualValue}\";\n";
		await File.WriteAllTextAsync(sourcePath, source, TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var markedValue, out _));
			var session = UiTestDriver.GetSecretRedactionSession(window);
			Assert.True(session.AddSessionMarkedSecret(
				relativePath,
				source.IndexOf(manualValue, StringComparison.Ordinal),
				markedValue));

			await UiTestDriver.RequestPersistentSecretMarkAsync(window, manualValue);
			var store = new ProjectProfileStore(() => UiTestDriver.GetWindowAppDataPath(window));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => store.LoadMarksAsync(project.RootPath).AsTask().GetAwaiter().GetResult() is
					{ Succeeded: true, Snapshot.Marks.Count: 1 },
				"the existing session anchor to become durable");
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"the promoted mark to redact Preview");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task HideHere_ExistingSessionAnchorShowsAlreadyHiddenToastWithoutPersisting()
	{
		const string manualValue = "duplicate-session-value-42";
		const string relativePath = "src/Secrets.cs";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var sourcePath = Path.Combine(project.RootPath, relativePath);
		var source = $"const string value = \"{manualValue}\";\n";
		await File.WriteAllTextAsync(sourcePath, source, TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var markedValue, out _));
			Assert.True(UiTestDriver.GetSecretRedactionSession(window).AddSessionMarkedSecret(
				relativePath,
				source.IndexOf(manualValue, StringComparison.Ordinal),
				markedValue));

			await UiTestDriver.RequestPersistentSecretMarkAsync(window, manualValue, persistent: false);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetToastService(window).Items.Any(
					static item => item.Message == "Value is already hidden"),
				"the duplicate manual mark toast");
			var loaded = await new ProjectProfileStore(() => UiTestDriver.GetWindowAppDataPath(window))
				.LoadMarksAsync(project.RootPath);
			Assert.True(loaded.Succeeded);
			Assert.Empty(loaded.Snapshot!.Marks);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(IgnoreOptionId.HideSecrets)]
	[InlineData(IgnoreOptionId.HidePrivateData)]
	public async Task ContentRedactionCheckbox_IsDraftUntilApplyWithoutTouchingPreviewOrDetector(
		IgnoreOptionId optionId)
	{
		const string secret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		const string privateEmail = "ivan.petrov@gmail.com";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Contact.txt"),
			$"contact={privateEmail}\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var previewControl = UiTestDriver.GetRequiredControl<VirtualizedPreviewTextControl>(
				window,
				"PreviewTextControl");
			Assert.NotNull(previewControl.Document);
			var documentBefore = previewControl.Document;
			var payloadBefore = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			var refreshVersionsBefore = UiTestDriver.GetPreviewRefreshVersions(window);
			var cacheBefore = UiTestDriver.GetSecretRedactionSession(window).GetCacheDiagnostics();
			var appliedBefore = UiTestDriver.GetAppliedContentRedactionState(window);
			var treeBefore = UiTestDriver.GetCurrentTreeIdentity(window);
			var inventoryBefore = UiTestDriver.GetCurrentTreeInventoryIdentity(window);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				optionId,
				visible: true,
				isChecked: true);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				optionId,
				visible: true,
				isChecked: false);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				optionId,
				visible: true,
				isChecked: true);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			Assert.Equal(refreshVersionsBefore, UiTestDriver.GetPreviewRefreshVersions(window));
			Assert.Same(documentBefore, previewControl.Document);
			Assert.Equal(payloadBefore, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			Assert.Equal(
				cacheBefore.DetectionRuns,
				UiTestDriver.GetSecretRedactionSession(window).GetCacheDiagnostics().DetectionRuns);
			Assert.Equal(appliedBefore, UiTestDriver.GetAppliedContentRedactionState(window));
			Assert.True(UiTestDriver.GetViewModel(window).HasPendingFilterSettingsChanges);

			await UiTestDriver.ClickApplySettingsAsync(window);
			var valueToHide = optionId == IgnoreOptionId.HideSecrets ? secret : privateEmail;
			var valueToKeep = optionId == IgnoreOptionId.HideSecrets ? privateEmail : secret;
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					valueToHide,
					StringComparison.Ordinal),
				"the applied redaction option to publish exactly at the Apply boundary");
			Assert.Contains(
				valueToKeep,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			var appliedAfter = UiTestDriver.GetAppliedContentRedactionState(window);
			Assert.Equal(optionId == IgnoreOptionId.HideSecrets, appliedAfter.HideSecrets);
			Assert.Equal(optionId == IgnoreOptionId.HidePrivateData, appliedAfter.HidePrivateData);
			Assert.False(UiTestDriver.GetViewModel(window).HasPendingFilterSettingsChanges);
			Assert.Same(treeBefore, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventoryBefore, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			var firstApplyRefreshVersions = UiTestDriver.GetPreviewRefreshVersions(window);
			Assert.Equal(refreshVersionsBefore.Requested + 1, firstApplyRefreshVersions.Requested);
			Assert.Equal(firstApplyRefreshVersions.Requested, firstApplyRefreshVersions.Completed);

			var appliedDocument = previewControl.Document;
			var appliedPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			var appliedRefreshVersions = UiTestDriver.GetPreviewRefreshVersions(window);
			var appliedCache = UiTestDriver.GetSecretRedactionSession(window).GetCacheDiagnostics();
			var appliedNotice = optionId == IgnoreOptionId.HideSecrets
				? UiTestDriver.GetViewModel(window).SettingsSecretsNotice
				: UiTestDriver.GetViewModel(window).SettingsPrivateDataNotice;

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				optionId,
				visible: true,
				isChecked: false);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			Assert.Equal(appliedRefreshVersions, UiTestDriver.GetPreviewRefreshVersions(window));
			Assert.Same(appliedDocument, previewControl.Document);
			Assert.Equal(appliedPayload, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
			Assert.Equal(
				appliedCache.DetectionRuns,
				UiTestDriver.GetSecretRedactionSession(window).GetCacheDiagnostics().DetectionRuns);
			Assert.Equal(appliedAfter, UiTestDriver.GetAppliedContentRedactionState(window));
			Assert.Equal(
				appliedNotice,
				optionId == IgnoreOptionId.HideSecrets
					? UiTestDriver.GetViewModel(window).SettingsSecretsNotice
					: UiTestDriver.GetViewModel(window).SettingsPrivateDataNotice);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					valueToHide,
					StringComparison.Ordinal),
				"the disabled redaction option to publish exactly at the Apply boundary");
			Assert.Equal((false, false), UiTestDriver.GetAppliedContentRedactionState(window));
			Assert.False(UiTestDriver.GetViewModel(window).HasPendingFilterSettingsChanges);
			Assert.Same(treeBefore, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventoryBefore, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			var secondApplyRefreshVersions = UiTestDriver.GetPreviewRefreshVersions(window);
			Assert.Equal(firstApplyRefreshVersions.Requested + 1, secondApplyRefreshVersions.Requested);
			Assert.Equal(secondApplyRefreshVersions.Requested, secondApplyRefreshVersions.Completed);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task HidePrivateData_UsesIndependentAppliedStatusAndSharedPreviewPipeline()
	{
		const string privateEmail = "ivan.petrov@gmail.com";
		const string secret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Contact.txt"),
			$"contact={privateEmail}\n",
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var pathFinding = string.Equals(
				OutputRootPathPresentation.MaskLocalUserSegment(project.RootPath),
				project.RootPath,
				StringComparison.Ordinal)
				? 0
				: 1;
			var expectedPrivateDataCount = 1 + pathFinding;
			var expectedPrivateDataStatus = $"Found: {expectedPrivateDataCount}. Hidden: {expectedPrivateDataCount}.";
			if (pathFinding == 1)
			{
				expectedPrivateDataStatus +=
					$"{Environment.NewLine}User name in file paths: hidden.";
			}
			var viewModel = UiTestDriver.GetViewModel(window);
			var privacy = Assert.IsType<IgnoreOptionViewModel>(viewModel.HidePrivateDataOption);
			var privacyIndex = viewModel.ContentProcessingOptions.IndexOf(privacy);
			Assert.True(privacyIndex > 0);
			Assert.Equal(IgnoreOptionId.HideSecrets, viewModel.ContentProcessingOptions[privacyIndex - 1].Id);
			Assert.False(privacy.IsChecked);
			Assert.Equal(string.Empty, viewModel.SettingsPrivateDataNotice);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HidePrivateData);
			Assert.Equal(string.Empty, viewModel.SettingsPrivateDataNotice);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HidePrivateData,
				$"Hide private data ({expectedPrivateDataCount})");
			Assert.Equal(expectedPrivateDataStatus, viewModel.SettingsPrivateDataNotice);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
			var privacyOnly = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			Assert.DoesNotContain(privateEmail, privacyOnly, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[email#1]", privacyOnly, StringComparison.Ordinal);
			Assert.Contains(secret, privacyOnly, StringComparison.Ordinal);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			Assert.Contains(secret, UiTestDriver.ComputeCurrentPreviewCopyPayload(window), StringComparison.Ordinal);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HideSecrets,
				"Hide secrets (1)");
			var combined = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			Assert.DoesNotContain(privateEmail, combined, StringComparison.Ordinal);
			Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HidePrivateData);
			Assert.DoesNotContain(privateEmail, UiTestDriver.ComputeCurrentPreviewCopyPayload(window), StringComparison.Ordinal);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			var secretsOnly = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			Assert.Contains(privateEmail, secretsOnly, StringComparison.Ordinal);
			Assert.DoesNotContain(secret, secretsOnly, StringComparison.Ordinal);
			Assert.Equal(string.Empty, viewModel.SettingsPrivateDataNotice);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task HideSecrets_IsRenderedInItsOwnSectionAndIsIndependentFromIgnoreAll()
    {
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.Any(
					      static option => option.Id == IgnoreOptionId.HideSecrets) &&
				      viewModel.SettingsSecretsNotice.Length == 0,
				"the content processing section to offer Hide secrets without scanning");
			var hideSecrets = Assert.IsType<IgnoreOptionViewModel>(viewModel.HideSecretsOption);
			var compressCode = Assert.Single(
				viewModel.IgnoreOptions,
				static option => option.Id == IgnoreOptionId.CompressCode);
			Assert.False(compressCode.IsChecked);
			Assert.DoesNotContain(viewModel.PathIgnoreOptions, static option => option.Id == IgnoreOptionId.HideSecrets);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.HideSecrets);
			var processingList = UiTestDriver.GetRequiredControl<ListBox>(window, "ContentProcessingOptionsList");
			var processingBorder = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingOptionsBorder");
			var processingContent = Assert.IsType<Grid>(processingBorder.Child);
			var helpIndicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HideSecrets);
            var ignoreList = UiTestDriver.GetRequiredControl<ListBox>(window, "IgnoreOptionsList");
			Assert.Equal("Content processing:", UiTestDriver.GetRequiredControl<TextBlock>(window, "ContentProcessingHeaderText").Text);
			Assert.Contains(processingList, processingContent.Children);
			Assert.Null(helpIndicator.Cursor);
			// No scan has run yet, so the idle row offers its checkbox without any indicator.
			Assert.False(helpIndicator.IsVisible);

			viewModel.SetContentProcessingStatus(SecretScanState.Completed, detectedCount: 3, hiddenCount: 2);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			helpIndicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HideSecrets);
			Assert.True(helpIndicator.IsVisible);
			checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.OpenToolTipThroughClickAsync(window, helpIndicator);
			var helpToolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(helpIndicator));
			var helpText = Assert.IsType<TextBlock>(helpToolTip.Content);
			Assert.Equal("Found: 3. Hidden: 2.", helpText.Text);
			Assert.Equal(PlacementMode.BottomEdgeAlignedRight, ToolTip.GetPlacement(helpIndicator));
			Assert.Equal(5, ToolTip.GetVerticalOffset(helpIndicator));
			Assert.Equal(VerticalAlignment.Center, helpIndicator.VerticalAlignment);
			var checkBoxPosition = Assert.IsType<Point>(checkBox.TranslatePoint(default, processingBorder));
			var indicatorPosition = Assert.IsType<Point>(helpIndicator.TranslatePoint(default, processingBorder));
			var checkBoxCenter = checkBoxPosition.Y + (checkBox.Bounds.Height / 2);
			var indicatorCenter = indicatorPosition.Y + (helpIndicator.Bounds.Height / 2);
			Assert.InRange(indicatorCenter - checkBoxCenter, 0, 2);
			var indicatorGap = indicatorPosition.X - (checkBoxPosition.X + checkBox.Bounds.Width);
			Assert.InRange(indicatorGap, 4, 8);
			var processingCollectionChanges = 0;
			viewModel.ContentProcessingOptions.CollectionChanged += (_, _) =>
				processingCollectionChanges++;

			viewModel.SetContentProcessingStatus(
				SecretScanState.Limited,
				detectedCount: 3,
				hiddenCount: 2,
				skippedFileCount: 2);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			// Unscannable files are reported in place (marked document entries, copy notices),
			// not in this status - it stays a plain counters line.
			Assert.Equal("Found: 3. Hidden: 2.", hideSecrets.StatusText);
			Assert.False(hideSecrets.IsWarningStatus);
			Assert.True(hideSecrets.IsInformationStatus);
			Assert.Equal(0, processingCollectionChanges);

			viewModel.SetContentProcessingStatus(
				SecretScanState.Limited,
				detectedCount: 0,
				hiddenCount: 0,
				skippedFileCount: 2);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(string.Empty, hideSecrets.StatusText);
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			viewModel.SetContentProcessingStatus(
				SecretScanState.Limited,
				detectedCount: 3,
				hiddenCount: 2,
				skippedFileCount: 2);

			viewModel.SetContentProcessingStatus(SecretScanState.Pending);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			Assert.Equal(0, processingCollectionChanges);

			viewModel.SetContentProcessingStatus(SecretScanState.Scanning);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);

			viewModel.SetContentProcessingStatus(SecretScanState.Failed);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			Assert.Equal(
				$"The analysis could not be completed.{Environment.NewLine}Click to run the check again.",
				hideSecrets.StatusText);
			Assert.True(hideSecrets.IsWarningStatus);
			Assert.Equal(0, processingCollectionChanges);

			viewModel.SetContentProcessingStatus(SecretScanState.Completed, detectedCount: 0, hiddenCount: 0);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal("DevProjex found no secrets", viewModel.SettingsSecretsNotice);
			Assert.True(UiTestDriver.GetRequiredControl<Grid>(window, "ContentProcessingSection").IsVisible);
			// A clean completed scan keeps the row and confirms the result on its indicator, so
			// "no secrets found" and "not scanned yet" are visibly different states.
			Assert.Contains(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			Assert.Equal("DevProjex found no secrets", hideSecrets.StatusText);
			Assert.False(hideSecrets.IsWarningStatus);
			Assert.True(hideSecrets.IsInformationStatus);
			Assert.Equal(0, processingCollectionChanges);

			viewModel.SetContentProcessingStatus(SecretScanState.Completed, detectedCount: 3, hiddenCount: 2);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			helpIndicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HideSecrets);
			Assert.True(helpIndicator.IsVisible);
			Assert.Same(viewModel.ContentProcessingOptions, processingList.ItemsSource);
			Assert.Equal(
				ProjectPresentationCatalog.ContentTransformationOptionIds.Count,
				viewModel.ContentProcessingOptions.Count);
			checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.HideSecrets);
			Assert.Contains(checkBox.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, processingList));
			Assert.DoesNotContain(checkBox.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, ignoreList));
			var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
			var processingPosition = Assert.IsType<Point>(processingList.TranslatePoint(default, settingsPanel));
			var ignorePosition = Assert.IsType<Point>(ignoreList.TranslatePoint(default, settingsPanel));
			Assert.True(processingPosition.Y < ignorePosition.Y);

			var processingCount = viewModel.ContentProcessingOptions.Count;
			viewModel.ContentProcessingOptions.Add(
				new IgnoreOptionViewModel(IgnoreOptionId.HiddenFiles, "Future transformation", false));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal(processingCount + 1, processingList.ItemCount);

            var ignoreAll = UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox");
            await UiTestDriver.RaiseButtonClickAsync(ignoreAll);
            Assert.False(hideSecrets.IsChecked);
            await UiTestDriver.RaiseButtonClickAsync(ignoreAll);
            Assert.False(hideSecrets.IsChecked);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
	}

	[AvaloniaFact]
	public async Task ContentProcessingAll_TogglesItsRowsAndTracksIndividualDrafts()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.Count ==
				      ProjectPresentationCatalog.ContentTransformationOptionIds.Count,
				"all content-processing rows to load");
			var allCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(
				window,
				"ContentProcessingAllCheckBox");

			Assert.Equal(viewModel.SettingsAllContentProcessing, allCheckBox.Content);
			Assert.Equal($"All ({viewModel.ContentProcessingOptions.Count})", allCheckBox.Content);
			Assert.False(allCheckBox.IsChecked);

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.AllContentProcessingChecked &&
				      viewModel.ContentProcessingOptions.All(static option => option.IsChecked),
				"the section-wide content toggle to check every row");

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.StripBlankLines);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !viewModel.AllContentProcessingChecked,
				"an individual draft to clear the section-wide checkbox");

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.All(static option => option.IsChecked),
				"the section-wide toggle to restore every row");
			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.All(static option => !option.IsChecked),
				"the section-wide toggle to clear every row");
			Assert.False(viewModel.AllContentProcessingChecked);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ContentProcessingAll_KeepsThePublishedPreviewStableUntilApply()
	{
		const string removedSecret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      public string Read()
			      {
			          return "{{removedSecret}}";
			      }
			  }
			  """,
			TestContext.Current.CancellationToken);
		var analyzer = new CountingSecretScanContentAnalyzer();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
			});
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
			var viewModel = UiTestDriver.GetViewModel(window);
			var publishedDocument = viewModel.PreviewDocument;
			Assert.NotNull(publishedDocument);
			Assert.Contains(
				removedSecret,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			analyzer.Reset();

			var allCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(
				window,
				"ContentProcessingAllCheckBox");
			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);

			Assert.All(viewModel.ContentProcessingOptions, static option => Assert.True(option.IsChecked));
			Assert.Same(publishedDocument, viewModel.PreviewDocument);
			Assert.Equal(0, analyzer.TotalReadCount);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);
			Assert.Contains(
				removedSecret,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			Assert.All(viewModel.ContentProcessingOptions, static option => Assert.False(option.IsChecked));
			Assert.Same(publishedDocument, viewModel.PreviewDocument);
			Assert.Equal(0, analyzer.TotalReadCount);

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			Assert.All(viewModel.ContentProcessingOptions, static option => Assert.True(option.IsChecked));
			Assert.Same(publishedDocument, viewModel.PreviewDocument);
			Assert.Equal(0, analyzer.TotalReadCount);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"DevProjex found no secrets",
					StringComparison.Ordinal) &&
				      !viewModel.StatusBusy &&
				      !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					      removedSecret,
					      StringComparison.Ordinal),
				"the atomic content pipeline to settle after Apply");
			Assert.NotSame(publishedDocument, viewModel.PreviewDocument);
			Assert.DoesNotContain(
				removedSecret,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ContentProcessingAll_AppliesDeferredHideSecretsWhenSyntaxModesAreAlreadyApplied()
	{
		const string survivingSecret = "AKIA" + "Z7M3Q5X2P6N4R7T5";
		const string manualValue = "ordinary-draft-reconcile-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			"internal sealed class Secrets { }",
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "settings.json"),
			$$"""
			  {
			    "accessKey": "{{survivingSecret}}",
			    "label": "{{manualValue}}"
			  }
			  """,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			foreach (var optionId in new[]
			         {
				         IgnoreOptionId.CompressCode,
				         IgnoreOptionId.StripComments,
				         IgnoreOptionId.StripBlankLines
			         })
			{
				await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
			}
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

			var viewModel = UiTestDriver.GetViewModel(window);
			var publishedDocument = viewModel.PreviewDocument;
			Assert.NotNull(publishedDocument);
			Assert.Contains(
				survivingSecret,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);

			var allCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(
				window,
				"ContentProcessingAllCheckBox");
			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);

			Assert.True(viewModel.HideSecretsOption?.IsChecked);
			Assert.Same(publishedDocument, viewModel.PreviewDocument);
			Assert.Contains(
				survivingSecret,
				UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
				StringComparison.Ordinal);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal) &&
				      !viewModel.StatusBusy,
				"the deferred Hide Secrets selection to publish after Apply");
			var output = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			Assert.DoesNotContain(survivingSecret, output, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[aws-access-token#1]", output, StringComparison.Ordinal);

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal),
				"manual redaction to refresh while reconciling an unchecked section draft");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ContentProcessingAll_DraftThenHideHereActivatesRedactionWithoutApplyingSyntaxModes()
	{
		const string manualValue = "ordinary-bulk-draft-secret-42";
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		await File.WriteAllTextAsync(
			Path.Combine(project.RootPath, "src", "Secrets.cs"),
			$$"""
			  internal sealed class Secrets
			  {
			      // This comment proves that syntax transformations remain drafts.
			      public string Read() => "{{manualValue}}";
			  }
			  """,
			TestContext.Current.CancellationToken);
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			await UiTestDriver.OpenPreviewAsync(window);
			await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
			var viewModel = UiTestDriver.GetViewModel(window);
			var allCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(
				window,
				"ContentProcessingAllCheckBox");

			await UiTestDriver.ClickAsync(window, allCheckBox);
			await UiTestDriver.WaitForPreviewReadyAsync(window);
			await UiTestDriver.RequestSecretMarkThroughContextMenuAsync(window, manualValue);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
					manualValue,
					StringComparison.Ordinal) &&
				      string.Equals(
					      viewModel.SettingsSecretsNotice,
					      "Found: 1. Hidden: 1.",
					      StringComparison.Ordinal),
				"the explicit manual mark to activate redaction over the staged section draft");

			var output = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
			Assert.Contains(
				"This comment proves that syntax transformations remain drafts.",
				output,
				StringComparison.Ordinal);
			Assert.True(viewModel.HasPendingFilterSettingsChanges);
			Assert.Equal(string.Empty, viewModel.SettingsCompressionNotice);
			Assert.Equal(string.Empty, viewModel.SettingsCommentStripNotice);
			Assert.Equal(string.Empty, viewModel.SettingsBlankLineStripNotice);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContentProcessingStatusToolTip_StaysInsideWindowAtMinimumSizeInRussian(bool compactMode)
	{
		using var project = UiTestProject.CreateDefault();
		LocalizationService? localization = null;
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services =>
			{
				localization = services.Localization;
				return services;
			});
		try
		{
			window.Width = window.MinWidth;
			window.Height = window.MinHeight;
			Assert.IsType<LocalizationService>(localization).SetLanguage(AppLanguage.Ru);
			if (compactMode)
				window.Classes.Add("compact-mode");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.Count ==
				      ProjectPresentationCatalog.ContentTransformationOptionIds.Count,
				"the content processing section to load");

			viewModel.SetAppliedContentTransformationState(
				compressCode: false,
				stripComments: false,
				stripBlankLines: true);
			viewModel.SetBlankLineStripStatus(strippedFiles: 999, totalFiles: 1000);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);

			var indicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.StripBlankLines);
			Assert.True(indicator.IsVisible);
			ToolTip.SetIsOpen(indicator, true);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => ToolTip.GetIsOpen(indicator),
				"the content-processing tooltip to open");
			var toolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(indicator));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

			var windowLeft = window.PointToScreen(default).X;
			var windowRight = window.PointToScreen(new Point(window.ClientSize.Width, 0)).X;
			var toolTipLeft = toolTip.PointToScreen(default).X;
			var toolTipRight = toolTip.PointToScreen(new Point(toolTip.Bounds.Width, 0)).X;
			Assert.True(
				toolTipLeft >= windowLeft,
				$"The content-processing tooltip extends beyond the window: tooltip={toolTipLeft}, window={windowLeft}.");
			Assert.True(
				toolTipRight <= windowRight,
				$"The content-processing tooltip extends beyond the window: tooltip={toolTipRight}, window={windowRight}.");
			Assert.Equal(PlacementMode.BottomEdgeAlignedRight, ToolTip.GetPlacement(indicator));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LongRedactionRowLabel_TrimsNameButKeepsCounterAndIndicatorInsideThePanel(bool compactMode)
	{
		using var project = UiTestProject.CreateDefault();
		LocalizationService? localization = null;
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services =>
			{
				localization = services.Localization;
				return services;
			});
		try
		{
			window.Width = window.MinWidth;
			window.Height = window.MinHeight;
			Assert.IsType<LocalizationService>(localization).SetLanguage(AppLanguage.Ru);
			if (compactMode)
				window.Classes.Add("compact-mode");
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.Count ==
				      ProjectPresentationCatalog.ContentTransformationOptionIds.Count,
				"the content processing section to load");

			viewModel.SetPrivateDataProcessingStatus(
				SecretScanState.Completed,
				detectedCount: 156,
				hiddenCount: 156);
			var option = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.HidePrivateData);
			// Wider than any shipped locale, so the layout must trim regardless of translation drift.
			option.Label = "Скрывать обнаруженные личные данные проекта целиком (156/156)";
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

			var border = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingOptionsBorder");
			var indicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HidePrivateData);
			Assert.True(indicator.IsVisible);
			var indicatorTopLeft = Assert.IsType<Point>(indicator.TranslatePoint(default, border));
			Assert.InRange(
				indicatorTopLeft.X + indicator.Bounds.Width,
				1,
				border.Bounds.Width);

			var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.HidePrivateData);
			var counter = Assert.Single(
				checkBox.GetVisualDescendants().OfType<TextBlock>(),
				static text => string.Equals(text.Text, "(156/156)", StringComparison.Ordinal));
			Assert.True(counter.IsVisible);
			var counterTopLeft = Assert.IsType<Point>(counter.TranslatePoint(default, border));
			Assert.InRange(counterTopLeft.X + counter.Bounds.Width, 1, border.Bounds.Width);

			var name = Assert.Single(
				checkBox.GetVisualDescendants().OfType<TextBlock>(),
				static text => text.Text?.StartsWith("Скрывать", StringComparison.Ordinal) == true);
			Assert.Equal(global::Avalonia.Media.TextTrimming.CharacterEllipsis, name.TextTrimming);
			Assert.Equal(global::Avalonia.Media.TextWrapping.NoWrap, name.TextWrapping);
			var nameTopLeft = Assert.IsType<Point>(name.TranslatePoint(default, border));
			Assert.True(
				nameTopLeft.X + name.Bounds.Width <= counterTopLeft.X,
				"The trimmed name must end before the counter starts.");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task CompressionWithoutSecretFindings_KeepsCompressionAndSecretsStatusesSeparate()
	{
		using var project = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.ContentProcessingOptions.Any(
					static candidate => candidate.Id == IgnoreOptionId.CompressCode),
				"the content processing section to offer its rows");
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			var option = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.CompressCode);
			option.IsChecked = true;
			// The checkbox is a draft; compression starts only after «Apply settings» commits it.
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.SettingsCompressionNotice.StartsWith(
					"Compressed ",
					StringComparison.Ordinal),
				"the real compression prewarm to finish before injecting status states");
			viewModel.SetCompressionPreparationStatus(isActive: true);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.Equal("Compressing code…", option.StatusText);
			Assert.True(GetContentProcessingStatusIndicator(window, IgnoreOptionId.CompressCode).IsVisible);

			viewModel.SetCompressionStatus(
				compressedFiles: 98,
				totalFiles: 123,
				sourceCharacters: 400,
				transformedCharacters: 100);
			Assert.Equal("Compressing code…", option.StatusText);
			viewModel.SetCompressionPreparationStatus(isActive: false);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);

			Assert.True(option.IsChecked);
			Assert.Equal("Compress code", option.Label);
			var compressionIndicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.CompressCode);
			Assert.True(compressionIndicator.IsVisible);
			Assert.Equal(
				$"Compressed 98 of 123 files.{Environment.NewLine}≈Tokens: 100 → 25.",
				option.StatusText);
			var processingBorder = UiTestDriver.GetRequiredControl<Border>(window, "ContentProcessingOptionsBorder");
			var compressionCheckBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(window, IgnoreOptionId.CompressCode);
			var checkBoxPosition = Assert.IsType<Point>(compressionCheckBox.TranslatePoint(default, processingBorder));
			var indicatorPosition = Assert.IsType<Point>(compressionIndicator.TranslatePoint(default, processingBorder));
			Assert.InRange(
				indicatorPosition.Y + (compressionIndicator.Bounds.Height / 2) -
				checkBoxPosition.Y - (compressionCheckBox.Bounds.Height / 2),
				0,
				2);
			await UiTestDriver.OpenToolTipThroughClickAsync(window, compressionIndicator);
			var compressionToolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(compressionIndicator));
			Assert.Equal(PlacementMode.BottomEdgeAlignedRight, ToolTip.GetPlacement(compressionIndicator));
			Assert.Equal(5, ToolTip.GetVerticalOffset(compressionIndicator));
			Assert.Equal(
				option.StatusText,
				Assert.IsType<TextBlock>(compressionToolTip.Content).Text);
			// Compression status stays on its own row: the never-enabled Hide Secrets row keeps
			// no status at all, because no scan was requested for it.
			var secretsOption = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.HideSecrets);
			Assert.False(secretsOption.HasStatus);
			Assert.False(secretsOption.IsWarningStatus);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			var commentsOption = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.StripComments);
			Assert.False(commentsOption.IsChecked);
			Assert.False(commentsOption.HasStatus);
			commentsOption.IsChecked = true;
			viewModel.SetAppliedContentTransformationState(
				compressCode: true,
				stripComments: true,
				stripBlankLines: false);
			viewModel.SetCommentStripPreparationStatus(isActive: true);
			Assert.Equal("Removing comments…", commentsOption.StatusText);
			viewModel.SetCommentStripStatus(strippedFiles: 7, totalFiles: 11);
			Assert.Equal("Removing comments…", commentsOption.StatusText);
			viewModel.SetCommentStripPreparationStatus(isActive: false);
			Assert.Equal("Removed comments from 7 of 11 files.", commentsOption.StatusText);

			var blankLinesOption = Assert.Single(
				viewModel.ContentProcessingOptions,
				static candidate => candidate.Id == IgnoreOptionId.StripBlankLines);
			Assert.False(blankLinesOption.IsChecked);
			Assert.False(blankLinesOption.HasStatus);
			blankLinesOption.IsChecked = true;
			viewModel.SetAppliedContentTransformationState(
				compressCode: true,
				stripComments: true,
				stripBlankLines: true);
			viewModel.SetBlankLineStripPreparationStatus(isActive: true);
			Assert.Equal("Removing blank lines…", blankLinesOption.StatusText);
			viewModel.SetBlankLineStripStatus(strippedFiles: 5, totalFiles: 11);
			Assert.Equal("Removing blank lines…", blankLinesOption.StatusText);
			viewModel.SetBlankLineStripPreparationStatus(isActive: false);
			Assert.Equal("Removed blank lines from 5 of 11 files.", blankLinesOption.StatusText);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task OptInSecretDiscoveryFailure_ShowsPersistentSecretWarningWithoutModal()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = new FailingSecretScanContentAnalyzer(services.FileContentAnalyzer)
			});
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);
			await UiTestDriver.ClickApplySettingsAsync(window);
			var expectedFailureStatus =
				$"Files that could not be checked: 2.{Environment.NewLine}Click to run the check again.";
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					expectedFailureStatus,
					StringComparison.Ordinal),
				"the failed opt-in secret discovery to settle");

			Assert.Equal(
				ProjectPresentationCatalog.ContentTransformationOptionIds.Count,
				viewModel.ContentProcessingOptions.Count);
			var compression = Assert.Single(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.CompressCode);
			var hideSecrets = Assert.Single(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			Assert.False(compression.HasStatus);
			Assert.Equal(expectedFailureStatus, hideSecrets.StatusText);
			Assert.True(hideSecrets.IsWarningStatus);
			var contentProcessingList = UiTestDriver.GetRequiredControl<ListBox>(
				window,
				"ContentProcessingOptionsList");
			contentProcessingList.ScrollIntoView(hideSecrets);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			var warningIndicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HideSecrets);
			Assert.True(warningIndicator.IsVisible);
			Assert.Contains(
				warningIndicator.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>(),
				static warning => warning.IsVisible);
			Assert.Contains(
				warningIndicator.GetVisualDescendants().OfType<TextBlock>(),
				static label => label.IsVisible && label.Text == "!");
			var informationLabel = Assert.Single(
				warningIndicator.GetVisualDescendants().OfType<TextBlock>(),
				static label => label.Text == "?");
			var informationIndicator = Assert.IsType<Border>(
				informationLabel.GetVisualParent());
			Assert.False(informationIndicator.IsVisible);
			Assert.Empty(window.OwnedWindows);

			compression.IsChecked = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
			Assert.True(compression.IsChecked);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task OversizedSecretDiscovery_ShowsLimitedCoverageWithoutEngineFailure()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = new LimitedSecretScanContentAnalyzer(
					services.FileContentAnalyzer,
					"README.md")
			});
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			// The oversized file is reported in place (marked document entry, copy notice);
			// the status carries only the counters.
			var expectedStatus = "Found: 1. Hidden: 1.";
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(viewModel.SettingsSecretsNotice, expectedStatus, StringComparison.Ordinal),
				"the size-limited discovery to publish its findings and coverage");

			var hideSecrets = Assert.Single(
				viewModel.ContentProcessingOptions,
				static option => option.Id == IgnoreOptionId.HideSecrets);
			Assert.Equal(expectedStatus, hideSecrets.StatusText);
			Assert.False(hideSecrets.IsWarningStatus);
			Assert.True(hideSecrets.IsInformationStatus);
			var indicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HideSecrets);
			Assert.Contains(
				indicator.GetVisualDescendants().OfType<TextBlock>(),
				static label => label.IsVisible && label.Text == "?");
			var warningLabel = Assert.Single(
				indicator.GetVisualDescendants().OfType<TextBlock>(),
				static label => label.Text == "!");
			Assert.False(Assert.IsType<Grid>(warningLabel.GetVisualParent()).IsVisible);
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task FailedPrivateDataDiscovery_WarningUsesTheSharedRetryContract()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = new FailingSecretScanContentAnalyzer(services.FileContentAnalyzer)
			});
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HidePrivateData);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.HidePrivateDataOption is { IsWarningStatus: true },
				"the private-data discovery warning to appear");
			var indicator = GetContentProcessingStatusIndicator(window, IgnoreOptionId.HidePrivateData);

			Assert.True(SettingsPanelView.IsRedactionRetryIndicator(indicator));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task PartialSecretDiscovery_PreservesFindingsAndBecomesCompleteForNarrowerSelection()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = new FailingSecretScanContentAnalyzer(
					services.FileContentAnalyzer,
					"README.md")
			});
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.HideSecretsOption is { IsWarningStatus: true },
				"the incomplete full-project discovery to expose a persistent warning");
			Assert.Equal(
				$"Found: 1. Hidden: 1.{Environment.NewLine}" +
				$"Files that could not be checked: 1.{Environment.NewLine}" +
				"Click to run the check again.",
				viewModel.SettingsSecretsNotice);

			Assert.Single(viewModel.TreeNodes).IsExpanded = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			var srcCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
			await UiTestDriver.ClickAsync(window, srcCheckBox);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal),
				"the narrower readable scope to publish its exact secret count");
			Assert.False(viewModel.HideSecretsOption!.IsWarningStatus);
			await UiTestDriver.WaitForIgnoreOptionLabelAsync(
				window,
				IgnoreOptionId.HideSecrets,
				"Hide secrets (1)");
			Assert.Empty(window.OwnedWindows);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task SecretDiscovery_SelectionChangesReuseValidatedFilesAndScanOnlyNewEntries()
	{
		using var project = UiTestProject.CreateWithSecretRedactionSelectionWorkspace();
		var analyzer = new CountingSecretScanContentAnalyzer();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
			});
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal),
				"the opt-in secret discovery to complete");
			analyzer.Reset();

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.Equal("Found: 1. Hidden: 1.", viewModel.SettingsSecretsNotice);
			Assert.Equal(0, analyzer.TotalReadCount);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal) &&
				      !viewModel.StatusBusy,
				"the expanded selection secret discovery to settle");

			Assert.Equal(0, analyzer.GetReadCount("Secrets.cs"));
			Assert.Equal(0, analyzer.GetReadCount("README.md"));
			Assert.Equal(1, analyzer.GetReadCount("empty.txt"));

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.Equal(1, analyzer.TotalReadCount);

			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal) &&
				      !viewModel.StatusBusy,
				"the restored selection secret discovery to settle");

			Assert.Equal(1, analyzer.TotalReadCount);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ApplyingHideSecrets_StartsScanWithImmediateActionAndIndeterminateProgress()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var analyzer = new BlockingSecretScanContentAnalyzer();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
			},
			waitForStatusIdle: false);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			// The load itself must not touch a single file for secrets: scanning is opt-in.
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			Assert.Equal(0, analyzer.ReadAttempts);
			Assert.False(analyzer.Started.Task.IsCompleted);
			Assert.Equal(string.Empty, viewModel.SettingsSecretsNotice);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
			Assert.False(analyzer.Started.Task.IsCompleted);
			Assert.False(viewModel.StatusOperationVisible);

			var previousApplyTask = window.LatestApplySettingsTask;
			await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredApplySettingsButton(window));
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !ReferenceEquals(window.LatestApplySettingsTask, previousApplyTask),
				"the Apply request to start");
			await analyzer.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => viewModel.StatusOperationVisible &&
				      string.Equals(
					      viewModel.StatusOperationText,
					      "Searching for secrets…",
					      StringComparison.Ordinal),
				"the opt-in secret search progress to be visible");

			Assert.True(viewModel.StatusProgressIsIndeterminate);
			Assert.Contains(
				window.GetVisualDescendants().OfType<TextBlock>(),
				static text => text.IsVisible && text.Text == "Searching for secrets…");
			Assert.Contains(
				window.GetVisualDescendants().OfType<ProgressBar>(),
				static progress => progress.IsVisible && progress.IsIndeterminate);

			analyzer.Release();
			await window.LatestApplySettingsTask.WaitAsync(TestContext.Current.CancellationToken);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !viewModel.StatusBusy,
				"the opt-in secret search progress to complete");
			Assert.DoesNotContain(
				window.GetVisualDescendants().OfType<TextBlock>(),
				static text => text.IsVisible && text.Text == "Searching for secrets…");
		}
		finally
		{
			analyzer.Release();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task ApplyingHideSecrets_UsesFastPathAndKeepsTheActiveScanAndTree()
	{
		using var project = UiTestProject.CreateWithSecretRedactionWorkspace();
		var analyzer = new BlockingSecretScanContentAnalyzer();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			configureServices: services => services with
			{
				FileContentAnalyzer = analyzer.Attach(services.FileContentAnalyzer)
			},
			waitForStatusIdle: false);
		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
			var tree = UiTestDriver.GetCurrentTreeIdentity(window);
			var inventory = UiTestDriver.GetCurrentTreeInventoryIdentity(window);

			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			Assert.True(viewModel.HasPendingFilterSettingsChanges);
			Assert.False(analyzer.Started.Task.IsCompleted);

			var applyButton = UiTestDriver.GetRequiredApplySettingsButton(window);
			var previousApplyTask = window.LatestApplySettingsTask;
			Assert.True(applyButton.IsEnabled);
			await UiTestDriver.RaiseButtonClickAsync(applyButton);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => !ReferenceEquals(window.LatestApplySettingsTask, previousApplyTask),
				"the Apply request to start");
			await analyzer.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
			await window.LatestApplySettingsTask.WaitAsync(TestContext.Current.CancellationToken);

			Assert.False(viewModel.HasPendingFilterSettingsChanges);
			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
			Assert.Same(inventory, UiTestDriver.GetCurrentTreeInventoryIdentity(window));
			Assert.Equal(0, analyzer.CancellationCount);

			analyzer.Release();
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => string.Equals(
					viewModel.SettingsSecretsNotice,
					"Found: 1. Hidden: 1.",
					StringComparison.Ordinal) &&
				      !viewModel.StatusBusy,
				"the original secret scan to complete");

			Assert.Equal(2, analyzer.ReadAttempts);
			Assert.Equal(0, analyzer.CancellationCount);
			Assert.Same(tree, UiTestDriver.GetCurrentTreeIdentity(window));
		}
		finally
		{
			analyzer.Release();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	private sealed class FailingSecretScanContentAnalyzer(
		IFileContentAnalyzer inner,
		string? failingFileName = null) : IFileContentAnalyzer
	{
		private bool ShouldFail(string path) =>
			failingFileName is null ||
			string.Equals(Path.GetFileName(path), failingFileName, StringComparison.OrdinalIgnoreCase);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			maxSizeForFullRead == SecretRedactionOutputPreparer.MaximumScannableFileBytes && ShouldFail(path)
				? ValueTask.FromException<FileContentReadResult>(new IOException("Injected background scan failure."))
				: inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default) =>
			maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes && ShouldFail(path)
				? ValueTask.FromException<ICompleteTextFileBuffer>(new IOException("Injected background scan failure."))
				: inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
	}

	private sealed class LimitedSecretScanContentAnalyzer(
		IFileContentAnalyzer inner,
		string limitedFileName) : IFileContentAnalyzer
	{
		private bool ShouldLimit(string path, long maximumBytes) =>
			maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes &&
			string.Equals(Path.GetFileName(path), limitedFileName, StringComparison.OrdinalIgnoreCase);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ShouldLimit(path, maxSizeForFullRead)
				? ValueTask.FromResult(new FileContentReadResult(FileContentClassification.TooLarge))
				: inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default) =>
			ShouldLimit(path, maximumBytes)
				? ValueTask.FromResult<ICompleteTextFileBuffer>(new LimitedTextFileBuffer())
				: inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		private sealed class LimitedTextFileBuffer : ICompleteTextFileBuffer
		{
			public FileContentClassification Classification => FileContentClassification.TooLarge;
			public long SizeBytes => 0;
			public ReadOnlyMemory<char> Content => ReadOnlyMemory<char>.Empty;
			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}

	private sealed class BlockingSecretScanContentAnalyzer : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private IFileContentAnalyzer? _inner;
		private int _readAttempts;
		private int _cancellationCount;

		public TaskCompletionSource Started => _started;
		public int ReadAttempts => Volatile.Read(ref _readAttempts);
		public int CancellationCount => Volatile.Read(ref _cancellationCount);

		public IFileContentAnalyzer Attach(IFileContentAnalyzer inner)
		{
			_inner = inner;
			return this;
		}

		public void Release() => _release.TrySetResult();

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			Inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			Inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			if (maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes)
			{
				Interlocked.Increment(ref _readAttempts);
				_started.TrySetResult();
				try
				{
					await _release.Task.WaitAsync(cancellationToken);
				}
				catch (OperationCanceledException)
				{
					Interlocked.Increment(ref _cancellationCount);
					throw;
				}
			}

			return await Inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			Inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		private IFileContentAnalyzer Inner =>
			_inner ?? throw new InvalidOperationException("The analyzer is not attached.");
	}

	private sealed class CountingSecretScanContentAnalyzer : IFileContentAnalyzer
	{
		private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);
		private IFileContentAnalyzer? _inner;

		public int TotalReadCount => _reads.Values.Sum();

		public IFileContentAnalyzer Attach(IFileContentAnalyzer inner)
		{
			_inner = inner;
			return this;
		}

		public void Reset() => _reads.Clear();

		public int GetReadCount(string fileName) => _reads.GetValueOrDefault(fileName);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			Inner.ClassifyWithoutReading(path);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			Inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.OpenCompleteSnapshotAsync(path, cancellationToken);

		public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			var buffer = await Inner
				.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
			if (maximumBytes == SecretRedactionOutputPreparer.MaximumScannableFileBytes)
				_reads.AddOrUpdate(Path.GetFileName(path), 1, static (_, count) => count + 1);
			return buffer;
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			Inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			Inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		private IFileContentAnalyzer Inner =>
			_inner ?? throw new InvalidOperationException("The analyzer is not attached.");
	}

    private static async Task SetIgnoreAllCheckedAsync(MainWindow window, bool isChecked)
    {
        var viewModel = UiTestDriver.GetViewModel(window);
        if (viewModel.AllIgnoreChecked == isChecked)
            return;

        await UiTestDriver.ClickAsync(
            window,
            UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => viewModel.AllIgnoreChecked == isChecked,
            $"the all-ignore checkbox to become {isChecked}");
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
    }

    private static void EnsureGitAvailable()
    {
        var startInfo = CreateGitStartInfo(workingDirectory: null);
        startInfo.ArgumentList.Add("--version");
        Process? startedProcess;
        try
        {
            startedProcess = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Git is not available in this test environment.");
            return;
        }

        using var process = startedProcess;
        if (process is null)
            Assert.Skip("Git is not available in this test environment.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000) || process.ExitCode != 0)
            Assert.Skip("Git is not available in this test environment.");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = CreateGitStartInfo(workingDirectory);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(20_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Git command did not complete within 20 seconds.");
        }

        Assert.True(process.ExitCode == 0, $"git failed ({process.ExitCode}): {error}{output}");
    }

    private static ProcessStartInfo CreateGitStartInfo(string? workingDirectory) =>
        new("git")
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

    private static async Task SetVisibleIgnoreOptionsCheckedAsync(
        MainWindow window,
        bool isChecked)
    {
        foreach (var optionId in UiTestDriver.GetViewModel(window).IgnoreOptions.Select(option => option.Id).ToArray())
            await SetIgnoreOptionCheckedAsync(window, optionId, isChecked);
    }

    private static async Task ApplySettingsAndWaitForIgnoreRefreshAsync(MainWindow window)
    {
        await UiTestDriver.ClickApplySettingsAsync(window);
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
    }

    private static async Task AssertNestedPolyglotTreeStateAsync(
        MainWindow window,
        IReadOnlyCollection<string[]> visiblePaths,
        IReadOnlyCollection<string[]> hiddenPaths)
    {
        foreach (var visiblePath in visiblePaths)
            await WaitForProjectTreePathStateAsync(window, exists: true, visiblePath);
        foreach (var hiddenPath in hiddenPaths)
            await WaitForProjectTreePathStateAsync(window, exists: false, hiddenPath);
    }

    private static async Task AssertExtensionStatesAsync(
        MainWindow window,
        IReadOnlyCollection<string> visibleChecked,
        IReadOnlyCollection<string> hidden)
    {
        // These assertions bind the user-visible extension list to the currently applied
        // ignore controllers. A path can be correct while the extension checklist is stale.
        foreach (var extension in visibleChecked)
            await WaitForExtensionStateAsync(window, extension, visible: true, isChecked: true);
        foreach (var extension in hidden)
            await WaitForExtensionStateAsync(window, extension, visible: false);
    }

    private static async Task AssertSmartIgnoreNegativeSourcePathsAsync(MainWindow window)
    {
        string[][] visiblePaths =
        [
            ["obj-backup", "project.assets.json"],
            ["build", "README.md"],
            ["build", "docs", "CMakeCache.txt"],
            ["vendor", "src", "autoload.php"],
            ["packages", "Alpha", "Alpha.nupkg"],
            ["m2-backup", "repository", "service", "package.json"],
            ["cmake-build", "CMakeCache.txt"]
        ];

        await UiTestDriver.WaitForConditionAsync(
            window,
            () => visiblePaths.All(path => ProjectTreeContainsPath(window, path)),
            "all source lookalikes in the Smart Ignore negative matrix to remain visible");
        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
    }

    private static async Task WaitForProjectTreePathStateAsync(
        MainWindow window,
        bool exists,
        params string[] relativeDisplayPath)
    {
        await UiTestDriver.WaitForConditionAsync(
            window,
            () => ProjectTreeContainsPath(window, relativeDisplayPath) == exists,
            $"project tree path '{string.Join("/", relativeDisplayPath)}' to exist={exists}");

        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
    }

    private static bool ProjectTreeContainsPath(MainWindow window, IReadOnlyList<string> relativeDisplayPath)
    {
        var roots = UiTestDriver.GetViewModel(window).TreeNodes;
        if (roots.Count != 1)
            return false;

        return ContainsTreePath(roots[0].Children, relativeDisplayPath);
    }

    private static bool ContainsTreePath(IEnumerable<TreeNodeViewModel> candidates, IReadOnlyList<string> displayPath)
    {
        var current = candidates;
        foreach (var segment in displayPath)
        {
            var match = current.FirstOrDefault(node => string.Equals(node.DisplayName, segment, StringComparison.Ordinal));
            if (match is null)
                return false;

            current = match.Children;
        }

        return true;
    }

    private sealed class SwitchableBlockingFileSystemScanner(
        string blockedRootPath,
        bool ignoreCancellation = false)
        : IFileSystemScanner,
            IFileSystemScannerAdvanced,
            IFileSystemScannerEffectiveEmptyFolderCounter,
            IFileSystemScannerEffectiveIgnoreCountsProvider,
            IFileSystemScannerIgnoreSectionSnapshotProvider,
            IFileSystemScannerExtensionPolicySnapshotProvider,
            IFileSystemScannerProjectWorkspaceScanner,
            IDisposable
    {
        private readonly FileSystemScanner _inner = new();
        private readonly ManualResetEventSlim _blocked = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly bool _ignoreCancellation = ignoreCancellation;
        private int _enabled;

        public void EnableBlocking() => Volatile.Write(ref _enabled, 1);

        public bool WaitForBlockedCall(TimeSpan timeout) => _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public bool CanReadRoot(string rootPath) => _inner.CanReadRoot(rootPath);

        public ScanResult<HashSet<string>> GetExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<HashSet<string>> GetRootFileExtensions(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensions(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<List<string>> GetRootFolderNames(string rootPath, IgnoreRules rules, CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFolderNames(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ExtensionsScanData> GetRootFileExtensionsWithIgnoreOptionCounts(
            string rootPath,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileExtensionsWithIgnoreOptionCounts(rootPath, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<int> GetEffectiveEmptyFolderCount(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveEmptyFolderCount(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreOptionCounts> GetEffectiveRootFileIgnoreOptionCounts(
            string rootPath,
            IReadOnlySet<string> allowedExtensions,
            IgnoreRules rules,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetEffectiveRootFileIgnoreOptionCounts(rootPath, allowedExtensions, rules, EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IReadOnlySet<string>? effectiveAllowedExtensions,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveAllowedExtensions,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<IgnoreSectionScanData> GetRootFileIgnoreSectionSnapshot(
            string rootPath,
            IgnoreRules extensionDiscoveryRules,
            IgnoreRules effectiveRules,
            IExtensionInclusionPolicy? effectiveExtensionPolicy,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(rootPath, cancellationToken);
            return _inner.GetRootFileIgnoreSectionSnapshot(
                rootPath,
                extensionDiscoveryRules,
                effectiveRules,
                effectiveExtensionPolicy,
                EffectiveCancellationToken(cancellationToken));
        }

        public ScanResult<ProjectWorkspaceScanSnapshot> ScanProjectWorkspace(
            ProjectWorkspaceScanRequest request,
            CancellationToken cancellationToken = default)
        {
            MaybeBlock(request.RootPath, cancellationToken);
            return _inner.ScanProjectWorkspace(
                request,
                EffectiveCancellationToken(cancellationToken));
        }

        public void Dispose()
        {
            _blocked.Dispose();
            _release.Dispose();
        }

        private void MaybeBlock(string rootPath, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _enabled) == 0 || !IsInsideBlockedRoot(rootPath))
                return;

            _blocked.Set();
            if (_ignoreCancellation)
            {
                if (!_release.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");
                return;
            }

            var signaled = WaitHandle.WaitAny(
                [_release.WaitHandle, cancellationToken.WaitHandle],
                TimeSpan.FromSeconds(30));
            if (signaled == WaitHandle.WaitTimeout)
                throw new TimeoutException("Timed out waiting to release the controlled stale refresh.");

            cancellationToken.ThrowIfCancellationRequested();
        }

        private CancellationToken EffectiveCancellationToken(CancellationToken cancellationToken) =>
            _ignoreCancellation ? CancellationToken.None : cancellationToken;

        private bool IsInsideBlockedRoot(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var rootPath = Path.GetFullPath(blockedRootPath).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            if (string.Equals(fullPath, rootPath, PathComparison))
                return true;
            if (!fullPath.StartsWith(rootPath, PathComparison))
                return false;

            var next = fullPath[rootPath.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        private static StringComparison PathComparison => OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }
}
