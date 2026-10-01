using DevProjex.Application.Preview;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class VirtualizedPreviewTextControlSelectionStreamingTests
{
	[AvaloniaTheory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public async Task SelectionAndClipboard_PreserveUnicodeBoundariesAndLineEndings(int storage)
	{
		const string source = "😀alpha\r\n\r\nβeta 文書\r\ngamma\r\n";
		using var directory = new TemporaryDirectory();
		using var document = CreateDocument(directory, storage, source);
		var control = new VirtualizedPreviewTextControl { Document = document, Text = source };
		(PreviewSelectionRange Range, string Expected)[] cases =
		[
			(new(1, 1, 4, 3), "\uDE00alpha\n\nβeta 文書\ngam"),
			(new(4, 3, 1, 1), "\uDE00alpha\n\nβeta 文書\ngam"),
			(new(1, 0, 1, 2), "😀"),
			(new(1, 1, 1, 2), "\uDE00"),
			(new(2, 0, 3, 5), "\nβeta "),
			(new(3, 7, 4, 0), "\n"),
			(new(2, 0, 2, 99), string.Empty),
			(new(1, 100, 4, 100), "\n\nβeta 文書\ngamma"),
			(new(4, 0, 5, 0), "gamma\n"),
			(new(3, 2, 7, 3), "ta 文書\ngamma\n\n\n")
		];
		long admittedCharacters = 0;
		Exception? failure = null;
		control.CopyingToClipboard += (_, _) => admittedCharacters = control.PendingClipboardCharacterCount;
		control.ClipboardCopyFailed += (_, args) => failure = args.Exception;

		foreach (var (range, expected) in cases)
		{
			SelectRange(control, range);
			Assert.Equal(expected, control.GetSelectedText());
			string? clipboard = null;
			admittedCharacters = 0;
			await control.CopySelectionToClipboardUsingAsync(text =>
			{
				clipboard = text;
				return Task.CompletedTask;
			});

			var normalizedClipboard = expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
			var expectedClipboard = storage == 0 ? expected : normalizedClipboard;
			Assert.Null(failure);
			Assert.Equal(expectedClipboard, clipboard ?? string.Empty);
			Assert.Equal(Encoding.UTF8.GetBytes(expectedClipboard), Encoding.UTF8.GetBytes(clipboard ?? string.Empty));
			Assert.Equal(normalizedClipboard.Length, admittedCharacters);
			Assert.Equal(0, control.PendingClipboardCharacterCount);
		}
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CopySelection_ReadsRangesAndChecksAdmissionBeforeBuildingPayload(bool cancelCopy)
	{
		var source = string.Join("\r\n", Enumerable.Repeat("Привет 文書 😀", 2_050));
		using var document = new CountingPreviewDocument(new InMemoryPreviewTextDocument(source));
		var control = new VirtualizedPreviewTextControl { Document = document };
		control.SelectAll();
		document.ResetCounts();
		long admittedCharacters = 0;
		int readsAtAdmission = 0;
		string? clipboard = null;
		Exception? failure = null;
		control.ClipboardCopyFailed += (_, args) => failure = args.Exception;
		control.CopyingToClipboard += (_, args) =>
		{
			admittedCharacters = control.PendingClipboardCharacterCount;
			readsAtAdmission = document.RangeReads;
			args.Cancel = cancelCopy;
		};

		await control.CopySelectionToClipboardUsingAsync(text =>
		{
			clipboard = text;
			return Task.CompletedTask;
		});

		var expected = source.Replace("\r\n", Environment.NewLine, StringComparison.Ordinal);
		Assert.Null(failure);
		Assert.Equal(expected.Length, admittedCharacters);
		Assert.Equal(0, control.PendingClipboardCharacterCount);
		Assert.Equal(cancelCopy ? null : expected, clipboard);
		Assert.Equal(0, document.RandomReads);
		Assert.Equal(1, readsAtAdmission);
		Assert.Equal(cancelCopy ? 1 : 2, document.RangeReads);
	}

	private static IPreviewTextDocument? CreateDocument(TemporaryDirectory directory, int storage, string text)
	{
		if (storage == 0)
			return null;
		if (storage == 1)
			return new InMemoryPreviewTextDocument(text);

		var bytes = Encoding.UTF8.GetBytes(text);
		var path = directory.CreateBinaryFile("clipboard.preview.txt", bytes);
		var offsets = new List<long> { 0 };
		for (var index = 0; index < bytes.Length; index++)
		{
			if (bytes[index] == '\n')
				offsets.Add(index + 1);
		}
		return new FileBackedPreviewTextDocument(path, offsets.ToArray(), bytes.Length, 7, text.Length);
	}

	private static void SelectRange(VirtualizedPreviewTextControl control, PreviewSelectionRange range)
	{
		var positionType = typeof(VirtualizedPreviewTextControl).GetNestedType("SelectionPosition", BindingFlags.NonPublic);
		Assert.NotNull(positionType);
		var anchor = Activator.CreateInstance(positionType, range.StartLine, range.StartColumn);
		var active = Activator.CreateInstance(positionType, range.EndLine, range.EndColumn);
		var anchorField = typeof(VirtualizedPreviewTextControl).GetField("_selectionAnchor", BindingFlags.Instance | BindingFlags.NonPublic);
		var activeField = typeof(VirtualizedPreviewTextControl).GetField("_selectionActive", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(anchorField);
		Assert.NotNull(activeField);
		anchorField.SetValue(control, anchor);
		activeField.SetValue(control, active);
	}

	private sealed class CountingPreviewDocument(InMemoryPreviewTextDocument inner) : IPreviewTextDocument
	{
		public int RandomReads { get; private set; }
		public int RangeReads { get; private set; }
		public int LineCount => inner.LineCount;
		public int MaxLineLength => inner.MaxLineLength;
		public long CharacterCount => inner.CharacterCount;
		public IReadOnlyList<PreviewDocumentSection> Sections => inner.Sections;
		public string GetFullText() => inner.GetFullText();
		public string GetLineRangeText(int firstLine, int lastLine) => inner.GetLineRangeText(firstLine, lastLine);
		public string GetLineText(int lineNumber)
		{
			RandomReads++;
			return inner.GetLineText(lineNumber);
		}
		public void VisitLines(int firstLine, int lastLine, PreviewTextLineVisitor visitor, CancellationToken cancellationToken = default)
		{
			RangeReads++;
			inner.VisitLines(firstLine, lastLine, visitor, cancellationToken);
		}
		public void ResetCounts() => (RandomReads, RangeReads) = (0, 0);
		public void Dispose() => inner.Dispose();
	}
}
