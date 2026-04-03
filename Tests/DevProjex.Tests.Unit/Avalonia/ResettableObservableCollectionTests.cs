using System.Collections.Specialized;
using DevProjex.Avalonia.Collections;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class ResettableObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesSingleReset_AndReplacesContents()
    {
        var collection = new ResettableObservableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => events.Add(e);

        collection.ReplaceAll([4, 5]);

        Assert.Equal([4, 5], collection);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);
    }

    [Fact]
    public void ReplaceAll_WithEmptySequence_ClearsCollectionWithSingleReset()
    {
        var collection = new ResettableObservableCollection<string> { "a", "b" };
        var events = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => events.Add(e);

        collection.ReplaceAll(Array.Empty<string>());

        Assert.Empty(collection);
        Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, events[0].Action);
    }
}
