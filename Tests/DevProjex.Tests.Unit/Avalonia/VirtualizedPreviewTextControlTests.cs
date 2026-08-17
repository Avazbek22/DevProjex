using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DevProjex.Application.Preview;
using DevProjex.Application.Secrets;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class VirtualizedPreviewTextControlTests
{
    [Fact]
    public void DetachedConstruction_DoesNotRequirePlatformCursorFactory()
    {
        var control = new VirtualizedPreviewTextControl();

        Assert.True(control.Focusable);
        Assert.Null(control.Cursor);
        Assert.Equal(TextHintingMode.Strong, TextOptions.GetTextHintingMode(control));
        Assert.Equal(
            BaselinePixelAlignment.Aligned,
            TextOptions.GetBaselinePixelAlignment(control));
    }

    [AvaloniaFact]
    public void PointerCursor_RemainsIBeamAcrossTrailingAreaAndSelectionPress()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = "short\nlonger preview line",
            Width = 480,
            Height = 160,
            TextFontSize = 15
        };
        var window = new Window
        {
            Width = 520,
            Height = 220,
            WindowDecorations = WindowDecorations.None,
            Content = control
        };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            var origin = Assert.IsType<Point>(
                control.TranslatePoint(default, window));
            var lineHeight = InvokeResolveLineHeight(control);
            var textPoint = new Point(
                origin.X + control.LeftPadding + 2,
                origin.Y + control.TopPadding + (lineHeight / 2));
            var trailingAreaPoint = new Point(
                origin.X + 360,
                textPoint.Y);

            window.MouseMove(textPoint, RawInputModifiers.None);
            var textCursor = Assert.IsType<Cursor>(control.Cursor);

            window.MouseMove(trailingAreaPoint, RawInputModifiers.None);
            Assert.Same(textCursor, control.Cursor);

            window.MouseDown(
                trailingAreaPoint,
                MouseButton.Left,
                RawInputModifiers.LeftMouseButton);
            Assert.Same(textCursor, control.Cursor);

            window.MouseMove(
                new Point(trailingAreaPoint.X, trailingAreaPoint.Y + lineHeight),
                RawInputModifiers.LeftMouseButton);
            Assert.Same(textCursor, control.Cursor);
            window.MouseUp(
                trailingAreaPoint,
                MouseButton.Left,
                RawInputModifiers.None);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingRedactedSpan_RequestsOnlyThatOccurrenceOverride()
    {
        const string placeholder = "DEVPROJEX_REDACTED[github-pat#1]";
        const string occurrenceId = "github-occurrence";
        const string prefix = "token = \"";
        var text = prefix + placeholder + "\";";
        using var document = new InMemoryPreviewTextDocument(
            text,
            redactions:
            [
                new PreviewRedactionSpan(
                    occurrenceId,
                    "github-pat",
                    1,
                    prefix.Length,
                    placeholder.Length,
                    SecretPreviewSpanState.Redacted)
            ]);
        var control = new VirtualizedPreviewTextControl
        {
            Document = document,
            Width = 720,
            Height = 120,
            TextFontSize = 16,
            TextBrush = Brushes.White
        };
        var window = new Window
        {
            Width = 760,
            Height = 180,
            WindowDecorations = WindowDecorations.None,
            Content = control
        };
        string? requestedOccurrence = null;
        control.RedactionToggleRequested += (_, eventArgs) =>
            requestedOccurrence = eventArgs.OccurrenceId;

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
            var typeface = ResolveTestTypeface(control);
            var x = origin.X + control.LeftPadding +
                    MeasureRenderedPrefixWidth(control, text, prefix.Length + 2, typeface);
            var y = origin.Y + control.TopPadding + (InvokeResolveLineHeight(control) / 2);
            var point = new Point(x, y);
			var ordinaryTextPoint = new Point(origin.X + control.LeftPadding + 1, y);

			window.MouseMove(ordinaryTextPoint, RawInputModifiers.None);
			var textCursor = Assert.IsType<Cursor>(control.Cursor);
			window.MouseMove(point, RawInputModifiers.None);
			Assert.NotSame(textCursor, control.Cursor);
			var toolTip = Assert.IsType<ToolTip>(ToolTip.GetTip(control));
			Assert.Equal(
				"Detected github-pat.\n" +
				"Click to keep the original value.\n" +
				"Alt+Up / Alt+Down navigates findings.",
				Assert.IsType<TextBlock>(toolTip.Content).Text);

            window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);

            Assert.Equal(occurrenceId, requestedOccurrence);
        }
        finally
        {
            window.Close();
        }
    }

	[AvaloniaFact]
	public void ContextGestureOutsideSelection_SelectsTokenAndOpensContextFlyout()
	{
		const string secret = "manual-secret-value-42";
		const string prefix = "TOKEN=";
		var text = $"config.env:\n\n{prefix}{secret};";
		using var document = new InMemoryPreviewTextDocument(
			text,
			[new PreviewDocumentSection("config.env", 1, 3, 1, 3)]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 180,
			TextFontSize = 16,
			TextBrush = Brushes.White,
			HideHereSecretToolTip = "only this occurrence",
			AlwaysHideValueToolTip = "all value occurrences",
			PrivateDataAlwaysHideToolTip = "private-data controlled occurrences"
		};
		var window = new Window
		{
			Width = 760,
			Height = 240,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var typeface = ResolveTestTypeface(control);
			var x = control.LeftPadding +
			        MeasureRenderedPrefixWidth(control, prefix + secret, prefix.Length + 2, typeface);
			var y = control.TopPadding +
			        (InvokeResolveLineHeight(control) * 2) +
			        (InvokeResolveLineHeight(control) / 2);

			var point = new Point(x, y);
			InvokePrivate(control, "PrepareContextSelection", point);
			InvokePrivate(control, "OpenContextMenu");
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			Assert.Equal(secret, control.GetSelectedText());
			var flyout = Assert.IsType<MenuFlyout>(control.ContextFlyout);
			Assert.True(flyout.IsOpen);
			Separator? manualSeparator = null;
			Separator? bulkSeparator = null;
			Assert.Collection(
				flyout.Items,
				item => Assert.Same(GetMenuItem(control, "_copyMenuItem"), item),
				item => Assert.Same(GetMenuItem(control, "_selectAllMenuItem"), item),
				item => manualSeparator = Assert.IsType<Separator>(item),
				item => Assert.Same(GetMenuItem(control, "_secretHideHereMenuItem"), item),
				item => Assert.Same(GetMenuItem(control, "_secretAlwaysHideMenuItem"), item),
				item => Assert.Same(GetMenuItem(control, "_privateDataAlwaysHideMenuItem"), item),
				item => Assert.Same(GetMenuItem(control, "_removeSecretMarkMenuItem"), item),
				item => bulkSeparator = Assert.IsType<Separator>(item),
				item => Assert.Same(GetMenuItem(control, "_bulkRuleRedactionMenuItem"), item),
				item => Assert.Same(GetMenuItem(control, "_bulkFileRedactionMenuItem"), item));
			var alwaysItem = GetMenuItem(control, "_secretAlwaysHideMenuItem");
			Assert.Same(alwaysItem.Cursor, manualSeparator!.Cursor);
			Assert.True(manualSeparator.IsVisible);
			Assert.False(bulkSeparator!.IsVisible);
			Assert.True(alwaysItem.IsEnabled);
			var hideHereItem = GetMenuItem(control, "_secretHideHereMenuItem");
			Assert.Contains('…', Assert.IsType<string>(hideHereItem.Header));
			Assert.DoesNotContain(secret, Assert.IsType<string>(hideHereItem.Header), StringComparison.Ordinal);
			Assert.Equal("only this occurrence", ToolTip.GetTip(hideHereItem));
			Assert.Equal("all value occurrences", ToolTip.GetTip(alwaysItem));
			Assert.Equal(
				"private-data controlled occurrences",
				ToolTip.GetTip(GetMenuItem(control, "_privateDataAlwaysHideMenuItem")));
			Assert.False(GetMenuItem(control, "_bulkRuleRedactionMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_bulkFileRedactionMenuItem").IsVisible);
			Assert.Contains('…', Assert.IsType<string>(alwaysItem.Header));
			Assert.DoesNotContain(secret, Assert.IsType<string>(alwaysItem.Header), StringComparison.Ordinal);
			flyout.Hide();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void ValidContentSelection_OffersThreeClassScopedManualMarkCommands()
	{
		const string value = "manual-mark-value-42";
		using var document = new InMemoryPreviewTextDocument(
			$"config.env:\n\n{value}",
			[new PreviewDocumentSection("config.env", 1, 3, 1, 3)]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 640,
			Height = 160
		};
		var window = new Window { Content = control };
		var requests = new List<(ManualRedactionClass Class, bool Persistent)>();
		control.ManualSecretMarkRequested += (_, args) => requests.Add((args.Class, args.Persistent));

		try
		{
			window.Show();
			SelectRange(window, control, new PreviewSelectionRange(3, 0, 3, value.Length));
			InvokePrivate(control, "EnsureContextMenu");
			InvokePrivate(control, "PrepareManualSecretMenuItems");
			foreach (var field in new[]
			{
				"_secretHideHereMenuItem",
				"_secretAlwaysHideMenuItem",
				"_privateDataAlwaysHideMenuItem"
			})
			{
				var item = GetMenuItem(control, field);
				Assert.True(item.IsVisible);
				Assert.True(item.IsEnabled);
				item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			}
		}
		finally
		{
			window.Close();
		}

		Assert.Equal(
			[
				(ManualRedactionClass.Secret, false),
				(ManualRedactionClass.Secret, true),
				(ManualRedactionClass.PrivateData, true)
			],
			requests);
	}

	[AvaloniaFact]
	public void ContextMenu_SeparatorsFollowVisibleActionGroups()
	{
		const string value = "manual-mark-value-42";
		using var document = new InMemoryPreviewTextDocument(
			$"config.env:\n\n{value}",
			[new PreviewDocumentSection("config.env", 1, 3, 1, 3)]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 640,
			Height = 160
		};
		var window = new Window { Content = control };

		try
		{
			window.Show();
			InvokePrivate(control, "EnsureContextMenu");
			var manualSeparator = GetSeparator(control, "_manualRedactionSeparator");
			var bulkSeparator = GetSeparator(control, "_bulkRedactionSeparator");

			control.ClearSelection();
			InvokePrivate(control, "OnContextMenuOpening", null!, EventArgs.Empty);
			Assert.False(manualSeparator.IsVisible);
			Assert.False(bulkSeparator.IsVisible);

			SelectRange(window, control, new PreviewSelectionRange(1, 0, 1, "config.env".Length));
			InvokePrivate(control, "OnContextMenuOpening", null!, EventArgs.Empty);
			Assert.False(manualSeparator.IsVisible);
			Assert.False(bulkSeparator.IsVisible);

			SelectRange(window, control, new PreviewSelectionRange(3, 0, 3, value.Length));
			InvokePrivate(control, "OnContextMenuOpening", null!, EventArgs.Empty);
			Assert.True(manualSeparator.IsVisible);
			Assert.False(bulkSeparator.IsVisible);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void DetectorContextMenu_BulkScopesCountDistinctOccurrencesAndRaiseOneRequest()
	{
		const string placeholder = "DEVPROJEX_REDACTED[email#1]";
		var lines = new[]
		{
			$"a={placeholder}",
			$"b={placeholder}",
			$"c={placeholder}",
			$"d={placeholder}",
			$"e={placeholder}"
		};
		using var document = new InMemoryPreviewTextDocument(
			string.Join('\n', lines),
			redactions:
			[
				new PreviewRedactionSpan("multi", "email", 1, 2, placeholder.Length, SecretPreviewSpanState.Redacted, RelativePath: "src/a.txt"),
				new PreviewRedactionSpan("multi", "email", 2, 2, placeholder.Length, SecretPreviewSpanState.Redacted, RelativePath: "src/a.txt"),
				new PreviewRedactionSpan("same-rule-file", "email", 3, 2, placeholder.Length, SecretPreviewSpanState.Redacted, RelativePath: "src/a.txt"),
				new PreviewRedactionSpan("same-rule-other-file", "email", 4, 2, placeholder.Length, SecretPreviewSpanState.Redacted, RelativePath: "src/b.txt"),
				new PreviewRedactionSpan("other-rule-same-file", "ipv4", 5, 2, placeholder.Length, SecretPreviewSpanState.Redacted, RelativePath: "src/a.txt")
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 220,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 280,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		var requests = new List<PreviewBulkRedactionToggleRequestedEventArgs>();
		control.BulkRedactionToggleRequested += (_, eventArgs) => requests.Add(eventArgs);

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
			var typeface = ResolveTestTypeface(control);
			var point = new Point(
				origin.X + control.LeftPadding + MeasureRenderedPrefixWidth(control, lines[0], 4, typeface),
				origin.Y + control.TopPadding + (InvokeResolveLineHeight(control) / 2));

			InvokePrivate(control, "PrepareContextSelection", point);
			InvokePrivate(control, "OpenContextMenu");
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			var ruleItem = GetMenuItem(control, "_bulkRuleRedactionMenuItem");
			var fileItem = GetMenuItem(control, "_bulkFileRedactionMenuItem");
			Assert.False(GetSeparator(control, "_manualRedactionSeparator").IsVisible);
			Assert.True(GetSeparator(control, "_bulkRedactionSeparator").IsVisible);
			Assert.True(ruleItem.IsVisible);
			Assert.True(fileItem.IsVisible);
			Assert.Equal("Keep all occurrences \"email\" (3)", ruleItem.Header);
			Assert.Equal("Keep all occurrences in \"a.txt\" (3)", fileItem.Header);

			ruleItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			fileItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		}
		finally
		{
			window.Close();
		}

		Assert.Collection(
			requests,
			request =>
			{
				Assert.True(request.Keep);
				Assert.Equal(3, request.OccurrenceIds.Count);
				Assert.Contains("multi", request.OccurrenceIds);
				Assert.Contains("same-rule-file", request.OccurrenceIds);
				Assert.Contains("same-rule-other-file", request.OccurrenceIds);
			},
			request =>
			{
				Assert.True(request.Keep);
				Assert.Equal(3, request.OccurrenceIds.Count);
				Assert.Contains("multi", request.OccurrenceIds);
				Assert.Contains("same-rule-file", request.OccurrenceIds);
				Assert.Contains("other-rule-same-file", request.OccurrenceIds);
			});
	}

	[AvaloniaFact]
	public void GeneratedPathRedaction_TogglesNormallyButDoesNotOfferBulkActions()
	{
		const string placeholder = "[local-user-1]";
		var text = $@"C:\Users\{placeholder}\repo:";
		using var document = new InMemoryPreviewTextDocument(
			text,
			redactions:
			[
				new PreviewRedactionSpan(
					"generated-path",
					"local-user",
					1,
					@"C:\Users\".Length,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					SourceLength: "alice".Length,
					Source: SecretFindingSource.GeneratedPath)
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 120,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 180,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		string? requestedOccurrence = null;
		control.RedactionToggleRequested += (_, eventArgs) => requestedOccurrence = eventArgs.OccurrenceId;

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
			var typeface = ResolveTestTypeface(control);
			var point = new Point(
				origin.X + control.LeftPadding +
				MeasureRenderedPrefixWidth(control, text, @"C:\Users\".Length + 2, typeface),
				origin.Y + control.TopPadding + (InvokeResolveLineHeight(control) / 2));

			InvokePrivate(control, "PrepareContextSelection", point);
			InvokePrivate(control, "OpenContextMenu");
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			Assert.False(GetMenuItem(control, "_bulkRuleRedactionMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_bulkFileRedactionMenuItem").IsVisible);
			Assert.False(GetSeparator(control, "_bulkRedactionSeparator").IsVisible);
			Assert.False(GetSeparator(control, "_manualRedactionSeparator").IsVisible);
			Assert.IsType<MenuFlyout>(control.ContextFlyout).Hide();

			window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
			window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
			Assert.Equal("generated-path", requestedOccurrence);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void KeptDetectorContextMenu_OffersBulkHide()
	{
		const string value = "ivan.petrov@corp.local";
		using var document = new InMemoryPreviewTextDocument(
			value,
			redactions:
			[
				new PreviewRedactionSpan(
					"occurrence",
					"email",
					1,
					0,
					value.Length,
					SecretPreviewSpanState.KeptAsIs,
					RelativePath: "config/appsettings.json")
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 120,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 180,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		PreviewBulkRedactionToggleRequestedEventArgs? requested = null;
		control.BulkRedactionToggleRequested += (_, eventArgs) => requested = eventArgs;

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			InvokePrivate(control, "EnsureContextMenu");
			var contextField = typeof(VirtualizedPreviewTextControl).GetField(
				"_contextDetectorRedaction",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(contextField);
			contextField!.SetValue(control, Assert.Single(document.Redactions));
			InvokePrivate(control, "PrepareBulkSecretMenuItems");

			var item = GetMenuItem(control, "_bulkRuleRedactionMenuItem");
			Assert.Equal("Hide all occurrences \"email\" (1)", item.Header);
			item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		}
		finally
		{
			window.Close();
		}

		Assert.NotNull(requested);
		Assert.False(requested!.Keep);
		Assert.Equal(["occurrence"], requested.OccurrenceIds);
	}

	[AvaloniaFact]
	public void ReplacingDocumentClearsPendingBulkContext()
	{
		const string placeholder = "DEVPROJEX_REDACTED[email#1]";
		using var firstDocument = new InMemoryPreviewTextDocument(
			placeholder,
			redactions:
			[
				new PreviewRedactionSpan(
					"old-occurrence",
					"email",
					1,
					0,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					RelativePath: "old.txt")
			]);
		using var secondDocument = new InMemoryPreviewTextDocument("replacement");
		var control = new VirtualizedPreviewTextControl { Document = firstDocument };
		InvokePrivate(control, "EnsureContextMenu");
		var contextField = typeof(VirtualizedPreviewTextControl).GetField(
			"_contextDetectorRedaction",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(contextField);
		contextField!.SetValue(control, Assert.Single(firstDocument.Redactions));
		InvokePrivate(control, "PrepareBulkSecretMenuItems");
		Assert.True(GetMenuItem(control, "_bulkRuleRedactionMenuItem").IsVisible);

		control.Document = secondDocument;
		InvokePrivate(control, "PrepareBulkSecretMenuItems");

		Assert.False(GetMenuItem(control, "_bulkRuleRedactionMenuItem").IsVisible);
		Assert.False(GetMenuItem(control, "_bulkFileRedactionMenuItem").IsVisible);
	}

	[AvaloniaFact]
	public void ManualSecretContextMenu_DisabledSelectionsExposeAndReportTheirReason()
	{
		const string text = "config.env:\n\nshort\nfirst-valid-value\nsecond-valid-value";
		using var document = new InMemoryPreviewTextDocument(
			text,
			[new PreviewDocumentSection("config.env", 1, 5, 1, 3)]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 220,
			TextFontSize = 16,
			TextBrush = Brushes.White,
			SecretSelectionTooShort = "too short",
			SecretSelectionMultiline = "multiline",
			SecretSelectionContentOnly = "content only"
		};
		var window = new Window
		{
			Width = 760,
			Height = 280,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		string? rejected = null;
		control.ManualSecretMarkRejected += (_, args) => rejected = args.Message;

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			InvokePrivate(control, "EnsureContextMenu");
			AssertHidden(new PreviewSelectionRange(1, 0, 1, "config.env".Length));
			AssertRejected(new PreviewSelectionRange(3, 0, 3, "short".Length), "too short");
			AssertRejected(
				new PreviewSelectionRange(4, 0, 5, "second-valid-value".Length),
				"multiline");
			AssertStaleActiveRequestIsRejected();
		}
		finally
		{
			window.Close();
		}

		void AssertHidden(PreviewSelectionRange range)
		{
			SelectRange(window, control, range);
			InvokePrivate(control, "PrepareManualSecretMenuItems");
			Assert.False(GetMenuItem(control, "_secretHideHereMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_secretAlwaysHideMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_privateDataAlwaysHideMenuItem").IsVisible);
		}

		void AssertRejected(PreviewSelectionRange range, string expectedReason)
		{
			SelectRange(window, control, range);
			InvokePrivate(control, "PrepareManualSecretMenuItems");
			var item = GetMenuItem(control, "_secretHideHereMenuItem");
			Assert.True(item.IsVisible);
			Assert.False(item.IsEnabled);
			Assert.Equal(expectedReason, ToolTip.GetTip(item));
		}

		void AssertStaleActiveRequestIsRejected()
		{
			var range = new PreviewSelectionRange(4, 0, 4, "first-valid-value".Length);
			SelectRange(window, control, range);
			InvokePrivate(control, "PrepareManualSecretMenuItems");
			var item = GetMenuItem(control, "_secretHideHereMenuItem");
			Assert.True(item.IsEnabled);
			var candidateField = typeof(VirtualizedPreviewTextControl).GetField(
				"_contextMarkedSecret",
				BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(candidateField);
			candidateField!.SetValue(control, null);
			rejected = null;
			item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
			Assert.Equal("content only", rejected);
		}
	}

	[AvaloniaFact]
	public void PersistentManualPlaceholder_RightClickOffersUndoAndReportsDetectorOverlap()
	{
		const string hash = "9f2a4c1e8b3d";
		const string placeholder = "DEVPROJEX_REDACTED[manual-secret#1]";
		using var document = new InMemoryPreviewTextDocument(
			placeholder,
			[new PreviewDocumentSection("config.env", 1, 1, 1, 1)],
			[
				new PreviewRedactionSpan(
					"occurrence",
					"manual-secret",
					1,
					0,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					20,
					SecretFindingSource.PersistentMark | SecretFindingSource.Detector,
					hash)
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 120,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 180,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		PreviewManualSecretUnmarkRequestedEventArgs? requested = null;
		control.ManualSecretUnmarkRequested += (_, eventArgs) => requested = eventArgs;

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
			var typeface = ResolveTestTypeface(control);
			var point = new Point(
				origin.X + control.LeftPadding + MeasureRenderedPrefixWidth(control, placeholder, 3, typeface),
				origin.Y + control.TopPadding + (InvokeResolveLineHeight(control) / 2));
			window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
			window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			var undoItem = GetMenuItem(control, "_removeSecretMarkMenuItem");
			Assert.True(undoItem.IsVisible);
			Assert.Equal("Remove mark", undoItem.Header);
			Assert.False(GetMenuItem(control, "_secretAlwaysHideMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_secretHideHereMenuItem").IsVisible);
			Assert.False(GetMenuItem(control, "_privateDataAlwaysHideMenuItem").IsVisible);
			undoItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		}
		finally
		{
			window.Close();
		}

		Assert.NotNull(requested);
		Assert.Equal(hash, requested!.PersistentMarkHash);
		Assert.Equal(20, requested.PersistentMarkLength);
		Assert.Null(requested.SessionMarkId);
		Assert.True(requested.AlsoDetected);
	}

	[AvaloniaFact]
	public void SessionManualPlaceholder_RightClickOffersUndoForThatOccurrence()
	{
		const string sessionMarkId = "session-mark-id";
		const string placeholder = "DEVPROJEX_REDACTED[manual-secret#1]";
		using var document = new InMemoryPreviewTextDocument(
			placeholder,
			[new PreviewDocumentSection("config.env", 1, 1, 1, 1)],
			[
				new PreviewRedactionSpan(
					"occurrence",
					"manual-secret",
					1,
					0,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					20,
					SecretFindingSource.SessionMark,
					null,
					sessionMarkId)
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 120,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 180,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		PreviewManualSecretUnmarkRequestedEventArgs? requested = null;
		control.ManualSecretUnmarkRequested += (_, eventArgs) => requested = eventArgs;

		try
		{
			window.Show();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
			var typeface = ResolveTestTypeface(control);
			var point = new Point(
				origin.X + control.LeftPadding + MeasureRenderedPrefixWidth(control, placeholder, 3, typeface),
				origin.Y + control.TopPadding + (InvokeResolveLineHeight(control) / 2));
			window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
			window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			var undoItem = GetMenuItem(control, "_removeSecretMarkMenuItem");
			Assert.True(undoItem.IsVisible);
			undoItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		}
		finally
		{
			window.Close();
		}

		Assert.NotNull(requested);
		Assert.Null(requested!.PersistentMarkHash);
		Assert.Equal(20, requested.PersistentMarkLength);
		Assert.Equal(sessionMarkId, requested.SessionMarkId);
		Assert.False(requested.AlsoDetected);
	}

	[AvaloniaFact]
	public void KeyboardNavigation_CyclesFindingsAndEnterTogglesTheActiveOccurrence()
	{
		const string firstOccurrence = "first-occurrence";
		const string secondOccurrence = "second-occurrence";
		const string firstPlaceholder = "DEVPROJEX_REDACTED[github-pat#1]";
		const string secondPlaceholder = "DEVPROJEX_REDACTED[aws-access-token#1]";
		var text = $"first={firstPlaceholder}\nsecond={secondPlaceholder}";
		using var document = new InMemoryPreviewTextDocument(
			text,
			redactions:
			[
				new PreviewRedactionSpan(
					firstOccurrence,
					"github-pat",
					1,
					"first=".Length,
					firstPlaceholder.Length,
					SecretPreviewSpanState.Redacted),
				new PreviewRedactionSpan(
					secondOccurrence,
					"aws-access-token",
					2,
					"second=".Length,
					secondPlaceholder.Length,
					SecretPreviewSpanState.Redacted)
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 160,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 220,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		string? requestedOccurrence = null;
		control.RedactionToggleRequested += (_, eventArgs) =>
			requestedOccurrence = eventArgs.OccurrenceId;

		try
		{
			window.Show();
			control.Focus();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(firstOccurrence, GetActiveRedactionOccurrenceId(control));
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);

			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(secondOccurrence, GetActiveRedactionOccurrenceId(control));
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);

			window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(firstOccurrence, GetActiveRedactionOccurrenceId(control));
			window.KeyRelease(Key.Up, RawInputModifiers.Alt, PhysicalKey.None, null);

			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			Assert.Equal(firstOccurrence, requestedOccurrence);
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void FullyKeptExactCascade_RequestsRestoringEveryCandidate()
	{
		var occurrenceIds = new[] { "secret-occurrence", "private-occurrence" };
		var span = new PreviewRedactionSpan(
			occurrenceIds[0],
			"secret-rule",
			1,
			0,
			12,
			SecretPreviewSpanState.KeptAsIs,
			CascadedOccurrenceIds: occurrenceIds);
		var control = new VirtualizedPreviewTextControl();
		PreviewRedactionToggleRequestedEventArgs? requested = null;
		control.RedactionToggleRequested += (_, eventArgs) => requested = eventArgs;

		InvokePrivate(control, "RaiseRedactionToggleRequested", span);

		Assert.NotNull(requested);
		Assert.Equal(occurrenceIds[0], requested!.OccurrenceId);
		Assert.Equal(occurrenceIds, requested.RestoreOccurrenceIds);
	}

	[AvaloniaFact]
	public void KeyboardNavigation_VisitsEveryGeneratedPathButCollapsesMultilineOccurrences()
	{
		const string generatedOccurrence = "generated-path";
		const string multilineOccurrence = "multiline-value";
		const string placeholder = "[local-user-1]";
		var lines = new[]
		{
			$"a={placeholder}",
			$"first={placeholder}",
			$"second={placeholder}",
			$"b={placeholder}",
			$"c={placeholder}"
		};
		var generatedSpans = new[]
		{
			new PreviewRedactionSpan(
				generatedOccurrence,
				"local-user",
				1,
				"a=".Length,
				placeholder.Length,
				SecretPreviewSpanState.Redacted,
				Source: SecretFindingSource.GeneratedPath),
			new PreviewRedactionSpan(
				generatedOccurrence,
				"local-user",
				4,
				"b=".Length,
				placeholder.Length,
				SecretPreviewSpanState.Redacted,
				Source: SecretFindingSource.GeneratedPath),
			new PreviewRedactionSpan(
				generatedOccurrence,
				"local-user",
				5,
				"c=".Length,
				placeholder.Length,
				SecretPreviewSpanState.Redacted,
				Source: SecretFindingSource.GeneratedPath)
		};
		using var document = new InMemoryPreviewTextDocument(
			string.Join('\n', lines),
			redactions:
			[
				generatedSpans[0],
				new PreviewRedactionSpan(
					multilineOccurrence,
					"multi-line",
					2,
					"first=".Length,
					placeholder.Length,
					SecretPreviewSpanState.Redacted),
				new PreviewRedactionSpan(
					multilineOccurrence,
					"multi-line",
					3,
					"second=".Length,
					placeholder.Length,
					SecretPreviewSpanState.Redacted),
				generatedSpans[1],
				generatedSpans[2]
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			Width = 720,
			Height = 180,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var window = new Window
		{
			Width = 760,
			Height = 240,
			WindowDecorations = WindowDecorations.None,
			Content = control
		};
		string? requestedOccurrence = null;
		control.RedactionToggleRequested += (_, eventArgs) => requestedOccurrence = eventArgs.OccurrenceId;

		try
		{
			window.Show();
			control.Focus();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

			Navigate(Key.Down, expectedLine: 1);
			Navigate(Key.Down, expectedLine: 2);
			Navigate(Key.Down, expectedLine: 4);
			Navigate(Key.Down, expectedLine: 5);
			Navigate(Key.Down, expectedLine: 1);
			Navigate(Key.Up, expectedLine: 5);
			Navigate(Key.Up, expectedLine: 4);

			Assert.False(IsActiveRedactionStop(control, generatedSpans[0]));
			Assert.True(IsActiveRedactionStop(control, generatedSpans[1]));
			Assert.False(IsActiveRedactionStop(control, generatedSpans[2]));
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
			Assert.Equal(generatedOccurrence, requestedOccurrence);
		}
		finally
		{
			window.Close();
		}

		void Navigate(Key key, int expectedLine)
		{
			window.KeyPress(key, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(key, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(expectedLine, GetActiveRedactionLineNumber(control));
		}
	}

	[AvaloniaFact]
	public void RebuildRedactionIndex_DropsAStaleGeneratedPathPositionWithTheSameOccurrenceId()
	{
		const string occurrenceId = "generated-path";
		const string placeholder = "[local-user-1]";
		using var firstDocument = new InMemoryPreviewTextDocument(
			$"a={placeholder}\nplain\nb={placeholder}",
			redactions:
			[
				new PreviewRedactionSpan(
					occurrenceId,
					"local-user",
					1,
					2,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					Source: SecretFindingSource.GeneratedPath),
				new PreviewRedactionSpan(
					occurrenceId,
					"local-user",
					3,
					2,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					Source: SecretFindingSource.GeneratedPath)
			]);
		using var replacement = new InMemoryPreviewTextDocument(
			$"a={placeholder}",
			redactions:
			[
				new PreviewRedactionSpan(
					occurrenceId,
					"local-user",
					1,
					2,
					placeholder.Length,
					SecretPreviewSpanState.Redacted,
					Source: SecretFindingSource.GeneratedPath)
			]);
		var control = new VirtualizedPreviewTextControl { Document = firstDocument };

		InvokePrivate(control, "MoveToRedaction", true);
		InvokePrivate(control, "MoveToRedaction", true);
		Assert.Equal(3, GetActiveRedactionLineNumber(control));

		control.Document = replacement;

		Assert.False(HasActiveRedactionTarget(control));
	}

	[AvaloniaFact]
	public void KeyboardNavigation_AfterManualScroll_ContinuesFromViewportInsteadOfHiddenActiveFinding()
	{
		const string firstOccurrence = "first-occurrence";
		const string secondOccurrence = "second-occurrence";
		const string placeholder = "DEVPROJEX_REDACTED[github-pat#1]";
		var lines = Enumerable.Range(1, 100)
			.Select(lineNumber => lineNumber is 40 or 80
				? $"secret-{lineNumber}={placeholder}"
				: $"preview line {lineNumber}")
			.ToArray();
		using var document = new InMemoryPreviewTextDocument(
			string.Join('\n', lines),
			redactions:
			[
				new PreviewRedactionSpan(
					firstOccurrence,
					"github-pat",
					40,
					"secret-40=".Length,
					placeholder.Length,
					SecretPreviewSpanState.Redacted),
				new PreviewRedactionSpan(
					secondOccurrence,
					"github-pat",
					80,
					"secret-80=".Length,
					placeholder.Length,
					SecretPreviewSpanState.Redacted)
			]);
		var control = new VirtualizedPreviewTextControl
		{
			Document = document,
			TextFontSize = 16,
			TextBrush = Brushes.White
		};
		var scrollViewer = new ScrollViewer
		{
			Width = 720,
			Height = 160,
			Content = control
		};
		var window = new Window
		{
			Width = 760,
			Height = 220,
			WindowDecorations = WindowDecorations.None,
			Content = scrollViewer
		};

		try
		{
			window.Show();
			control.Focus();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(firstOccurrence, GetActiveRedactionOccurrenceId(control));
			Assert.True(scrollViewer.Offset.Y > 0);

			scrollViewer.Offset = default;
			AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);

			Assert.Equal(firstOccurrence, GetActiveRedactionOccurrenceId(control));
			Assert.True(scrollViewer.Offset.Y > 0);

			window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
			Assert.Equal(secondOccurrence, GetActiveRedactionOccurrenceId(control));
		}
		finally
		{
			window.Close();
		}
	}

    [AvaloniaFact]
    public void SelectAll_WithDocument_SelectsFullNormalizedTextAndRange()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\r\nbeta\ngamma");
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        var changeCount = 0;
        control.PreviewSelectionChanged += (_, _) => changeCount++;

        control.SelectAll();

        Assert.True(control.HasSelection);
        Assert.Equal("alpha\nbeta\ngamma", control.GetSelectedText());
        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(1, 0, 3, 5), selectionRange);
        Assert.Equal(1, changeCount);
    }

    [AvaloniaFact]
    public void SelectAll_WithTextFallback_SelectsEntireText()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = "one\r\ntwo"
        };

        control.SelectAll();

        Assert.True(control.HasSelection);
        Assert.Equal("one\ntwo", control.GetSelectedText());
        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(1, 0, 2, 3), selectionRange);
    }

    [AvaloniaFact]
    public void ClearSelection_AfterSelectAll_RemovesSelectionAndRaisesEvent()
    {
        using var document = new InMemoryPreviewTextDocument("alpha\nbeta");
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        var changeCount = 0;
        control.PreviewSelectionChanged += (_, _) => changeCount++;

        control.SelectAll();
        control.ClearSelection();

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
        Assert.Equal(string.Empty, control.GetSelectedText());
        Assert.Equal(2, changeCount);
    }

    [AvaloniaFact]
    public void ChangingDocument_ClearsExistingSelection()
    {
        using var firstDocument = new InMemoryPreviewTextDocument("alpha\nbeta");
        using var secondDocument = new InMemoryPreviewTextDocument("gamma");
        var control = new VirtualizedPreviewTextControl
        {
            Document = firstDocument
        };

        control.SelectAll();
        Assert.True(control.HasSelection);

        control.Document = secondDocument;

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
        Assert.Equal(string.Empty, control.GetSelectedText());
    }

    [AvaloniaFact]
    public void SelectAll_WithEmptyDocument_LeavesSelectionEmpty()
    {
        using var document = new InMemoryPreviewTextDocument(string.Empty);
        var control = new VirtualizedPreviewTextControl
        {
            Document = document
        };

        control.SelectAll();

        Assert.False(control.HasSelection);
        Assert.False(control.TryGetSelectionRange(out _));
    }

    [AvaloniaFact]
    public void GetLineNumberAtVerticalOffset_RecalculatesMetricsWhenFontSizeChanges()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = "one\ntwo\nthree",
            TopPadding = 0,
            TextFontSize = 10
        };

        var smallLineHeight = InvokeResolveLineHeight(control);
        Assert.Equal(2, control.GetLineNumberAtVerticalOffset(smallLineHeight + 0.1));

        control.TextFontSize = 30;

        var largeLineHeight = InvokeResolveLineHeight(control);
        Assert.True(largeLineHeight > smallLineHeight);
        Assert.Equal(1, control.GetLineNumberAtVerticalOffset(smallLineHeight + 0.1));
        Assert.Equal(2, control.GetLineNumberAtVerticalOffset(largeLineHeight + 0.1));
    }

    [AvaloniaFact]
    public void HugeDocumentOffset_MapsToExpectedLineWithoutInt32CoordinateOverflow()
    {
        using var document = new SyntheticLargePreviewDocument(lineCount: 100_000_000);
        var control = new VirtualizedPreviewTextControl
        {
            Document = document,
            TopPadding = 10,
            TextFontSize = 16
        };
        var lineHeight = InvokeResolveLineHeight(control);
        var targetLine = 99_999_990;
        var verticalOffset = control.TopPadding + ((targetLine - 1) * lineHeight) + (lineHeight / 2);

        var actualLine = control.GetLineNumberAtVerticalOffset(verticalOffset);

        Assert.Equal(targetLine, actualLine);
    }

    [Fact]
    public void ViewportRelativeOrigin_RemainsSmallAtHundredMillionthLine()
    {
        const int firstVisibleLine = 99_999_990;
        const double contentTopPadding = 10;
        const double lineHeight = 18.5;
        var viewportTop = contentTopPadding + ((firstVisibleLine - 1) * lineHeight) + 4.25;

        var originY = VirtualizedPreviewTextControl.CalculateViewportRelativeLineOriginY(
            firstVisibleLine,
            contentTopPadding,
            lineHeight,
            viewportTop);

        Assert.Equal(-4.25, originY, precision: 5);
        Assert.InRange(originY, -lineHeight, 0);
    }

    [AvaloniaFact]
    public void SelectionHitTesting_UsesRenderedPreviewTextGeometry()
    {
        var lineText = "mmmmiiWW preview selection geometry check 12345";
        var startColumn = 7;
        var endColumn = 38;
        var control = new VirtualizedPreviewTextControl
        {
            Text = $"before\n{lineText}\nafter",
            Width = 720,
            Height = 180,
            TopPadding = 8,
            BottomPadding = 8,
            LeftPadding = 12,
            RightPadding = 12,
            TextFontFamily = FontFamily.Default,
            TextFontSize = 18,
            TextBrush = Brushes.White
        };
        var typeface = ResolveTestTypeface(control);
        var lineHeight = InvokeResolveLineHeight(control);
        var y = control.TopPadding + lineHeight + (lineHeight / 2.0);
        var startX = control.LeftPadding + MeasureRenderedPrefixWidth(control, lineText, startColumn, typeface);
        var endX = control.LeftPadding + MeasureRenderedPrefixWidth(control, lineText, endColumn, typeface);
        var startPosition = InvokeHitTestSelectionPosition(control, new Point(startX, y));

        SetSelectionAnchor(control, startPosition);
        InvokeUpdateSelectionActivePosition(
            control,
            InvokeHitTestSelectionPosition(control, new Point(endX, y)));

        Assert.True(control.TryGetSelectionRange(out var selectionRange));
        Assert.Equal(new PreviewSelectionRange(2, startColumn, 2, endColumn), selectionRange);
        Assert.Equal(lineText[startColumn..endColumn], control.GetSelectedText());
    }

    [AvaloniaFact]
    public void ResolveDistanceFromColumn_IncludesRenderedTrailingWhitespace()
    {
        var control = new VirtualizedPreviewTextControl
        {
            TextFontFamily = FontFamily.Default,
            TextFontSize = 18,
            TextBrush = Brushes.White
        };
        var typeface = ResolveTestTypeface(control);
        const string lineText = "abc   ";

        var beforeTrailingSpaces = InvokeResolveDistanceFromColumn(control, lineText, 3, typeface);
        var fullLineWidth = InvokeResolveDistanceFromColumn(control, lineText, lineText.Length, typeface);

        Assert.Equal(
            MeasureRenderedPrefixWidth(control, lineText, lineText.Length, typeface),
            fullLineWidth,
            precision: 6);
        Assert.True(fullLineWidth > beforeTrailingSpaces);
    }

    [AvaloniaFact]
    public void ClearingLargeStringPreview_ReleasesOversizedLineMetadataBuffer()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = string.Join('\n', Enumerable.Repeat("line", 10_000))
        };

        Assert.True(GetLineStartsCapacity(control) >= 10_000);

        control.Text = string.Empty;

        Assert.InRange(GetLineStartsCapacity(control), 1, 4096);
    }

    [AvaloniaFact]
    public void Render_ReusesFormattedVisibleLinesAndKeepsScrollCacheBounded()
    {
        var control = new VirtualizedPreviewTextControl
        {
            Text = string.Join(
                '\n',
                Enumerable.Range(1, 3_000).Select(
                    static lineNumber =>
                        $"line {lineNumber:D4}: preview rendering cache")),
            TextBrush = Brushes.White,
            TextFontSize = 15,
            ViewportWidth = 640,
            ViewportHeight = 180,
            Width = 640,
            Height = 180
        };
        control.Measure(new Size(640, 180));
        control.Arrange(new Rect(0, 0, 640, 180));

        using var bitmap = new RenderTargetBitmap(new PixelSize(640, 180));
        bitmap.Render(control);
        var firstRenderEntries = GetFormattedLineCacheEntries(control);

        bitmap.Render(control);
        var secondRenderEntries = GetFormattedLineCacheEntries(control);

        Assert.NotEmpty(firstRenderEntries);
        Assert.Equal(firstRenderEntries.Keys, secondRenderEntries.Keys);
        foreach (var (lineNumber, entry) in firstRenderEntries)
            Assert.Same(entry, secondRenderEntries[lineNumber]);

        var lineHeight = InvokeResolveLineHeight(control);
        for (var firstLine = 1; firstLine <= 3_000; firstLine += 20)
        {
            control.VerticalOffset = (firstLine - 1) * lineHeight;
            bitmap.Render(control);
        }

        Assert.InRange(
            GetFormattedLineCacheEntries(control).Count,
            1,
            (512 + (3 * 2)) * 2);
    }

    private static double InvokeResolveLineHeight(VirtualizedPreviewTextControl control)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "ResolveLineHeight",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(control, [])!;
    }

    private static int GetLineStartsCapacity(VirtualizedPreviewTextControl control)
    {
        var field = typeof(VirtualizedPreviewTextControl).GetField(
            "_lineStarts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var lineStarts = Assert.IsType<List<int>>(field!.GetValue(control));
        return lineStarts.Capacity;
    }

	private static MenuItem GetMenuItem(VirtualizedPreviewTextControl control, string fieldName)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return Assert.IsType<MenuItem>(field!.GetValue(control));
	}

	private static Separator GetSeparator(VirtualizedPreviewTextControl control, string fieldName)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return Assert.IsType<Separator>(field!.GetValue(control));
	}

	private static void InvokePrivate(
		VirtualizedPreviewTextControl control,
		string methodName,
		params object[] arguments)
	{
		var method = typeof(VirtualizedPreviewTextControl).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(control, arguments);
	}

	private static string? GetActiveRedactionOccurrenceId(
		VirtualizedPreviewTextControl control)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			"_activeRedactionTarget",
			BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.NotNull(field);
		var target = field!.GetValue(control);
		Assert.NotNull(target);
		var property = target!.GetType().GetProperty("OccurrenceId");
		Assert.NotNull(property);
		return Assert.IsType<string>(property!.GetValue(target));
	}

	private static int GetActiveRedactionLineNumber(VirtualizedPreviewTextControl control)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			"_activeRedactionTarget",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		var target = field!.GetValue(control);
		Assert.NotNull(target);
		var property = target!.GetType().GetProperty("LineNumber");
		Assert.NotNull(property);
		return Assert.IsType<int>(property!.GetValue(target));
	}

	private static bool HasActiveRedactionTarget(VirtualizedPreviewTextControl control)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			"_activeRedactionTarget",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return field!.GetValue(control) is not null;
	}

	private static bool IsActiveRedactionStop(
		VirtualizedPreviewTextControl control,
		PreviewRedactionSpan span)
	{
		var field = typeof(VirtualizedPreviewTextControl).GetField(
			"_activeRedactionTarget",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		var target = field!.GetValue(control);
		Assert.NotNull(target);
		var method = typeof(VirtualizedPreviewTextControl).GetMethod(
			"IsNavigationTarget",
			BindingFlags.Static | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return Assert.IsType<bool>(method!.Invoke(null, [span, target]));
	}

	private static void SelectRange(
		Window window,
		VirtualizedPreviewTextControl control,
		PreviewSelectionRange range)
	{
		_ = window;
		var positionType = typeof(VirtualizedPreviewTextControl).GetNestedType(
			"SelectionPosition",
			BindingFlags.NonPublic);
		Assert.NotNull(positionType);
		var anchor = Activator.CreateInstance(positionType!, range.StartLine, range.StartColumn);
		var active = Activator.CreateInstance(positionType!, range.EndLine, range.EndColumn);
		Assert.NotNull(anchor);
		Assert.NotNull(active);
		SetSelectionAnchor(control, anchor!);
		var activeField = typeof(VirtualizedPreviewTextControl).GetField(
			"_selectionActive",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(activeField);
		activeField!.SetValue(control, active);
		Assert.Equal(range.Normalize(), AssertSelectionRange(control));
	}

	private static PreviewSelectionRange AssertSelectionRange(VirtualizedPreviewTextControl control)
	{
		Assert.True(control.TryGetSelectionRange(out var range));
		return range;
	}

	private static Point ResolvePoint(
		Window window,
		VirtualizedPreviewTextControl control,
		int lineNumber,
		int column)
	{
		var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
		var typeface = ResolveTestTypeface(control);
		var lineHeight = InvokeResolveLineHeight(control);
		return new Point(
			origin.X + control.LeftPadding + MeasureRenderedPrefixWidth(
				control,
				control.Document!.GetLineText(lineNumber),
				column,
				typeface),
			origin.Y + control.TopPadding + ((lineNumber - 1) * lineHeight) + (lineHeight / 2));
	}

    private static Dictionary<int, object> GetFormattedLineCacheEntries(
        VirtualizedPreviewTextControl control)
    {
        var field = typeof(VirtualizedPreviewTextControl).GetField(
            "_formattedLineCache",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            field!.GetValue(control));
        var entries = new Dictionary<int, object>(cache.Count);
        foreach (var key in cache.Keys)
        {
            var lineNumber = Assert.IsType<int>(key);
            entries.Add(lineNumber, cache[key]!);
        }

        return entries;
    }

    private static double InvokeResolveDistanceFromColumn(
        VirtualizedPreviewTextControl control,
        string lineText,
        int column,
        Typeface typeface)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "ResolveDistanceFromColumn",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return (double)method!.Invoke(control, [lineText, column, typeface])!;
    }

    private static object InvokeHitTestSelectionPosition(VirtualizedPreviewTextControl control, Point point)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "HitTestSelectionPosition",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        return method!.Invoke(control, [point])!;
    }

    private static void InvokeUpdateSelectionActivePosition(
        VirtualizedPreviewTextControl control,
        object selectionPosition)
    {
        var method = typeof(VirtualizedPreviewTextControl).GetMethod(
            "UpdateSelectionActivePosition",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(control, [selectionPosition]);
    }

    private static void SetSelectionAnchor(VirtualizedPreviewTextControl control, object selectionPosition)
    {
        var field = typeof(VirtualizedPreviewTextControl).GetField(
            "_selectionAnchor",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field!.SetValue(control, selectionPosition);
    }

    private static Typeface ResolveTestTypeface(VirtualizedPreviewTextControl control)
        => new(control.TextFontFamily ?? FontFamily.Default, FontStyle.Normal, FontWeight.Normal);

    private static double MeasureRenderedPrefixWidth(
        VirtualizedPreviewTextControl control,
        string lineText,
        int column,
        Typeface typeface)
    {
        var clampedColumn = Math.Clamp(column, 0, lineText.Length);
        var formattedText = new FormattedText(
            lineText[..clampedColumn],
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            control.TextFontSize,
            control.TextBrush ?? Brushes.White);

        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private sealed class SyntheticLargePreviewDocument(int lineCount) : IPreviewTextDocument
    {
        public int LineCount { get; } = lineCount;

        public int MaxLineLength => 4;

        public long CharacterCount => (long)LineCount * 5;

        public IReadOnlyList<PreviewDocumentSection> Sections => [];

        public string GetFullText() => "test";

        public string GetLineText(int lineNumber) => "test";

        public string GetLineRangeText(int firstLine, int lastLine) => "test";

        public void Dispose()
        {
        }
    }

}
