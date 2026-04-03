using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DevProjex.Avalonia.Collections;

/// <summary>
/// Replaces the collection contents with a single Reset notification.
/// This is intentionally used on large UI-bound option lists to avoid layout churn from
/// hundreds of per-item add/remove notifications during project load and settings refresh.
/// </summary>
internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
