using DevProjex.Application.Preview;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class VirtualizedPreviewTextControlTests
{
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
}
