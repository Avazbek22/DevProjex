namespace DevProjex.Avalonia.Collections;

/// <summary>
/// Replaces the collection contents with a single Reset notification.
/// This is intentionally used on large UI-bound option lists to avoid layout churn from
/// hundreds of per-item add/remove notifications during project load and settings refresh.
/// </summary>
internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public ResettableObservableCollection()
    {
    }

    public ResettableObservableCollection(int capacity)
    {
        EnsureCapacity(capacity);
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        RaiseReset();
    }

    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (Items is List<T> list)
            list.EnsureCapacity(capacity);
    }

    public void TrimExcess()
    {
        if (Items is List<T> list)
            list.TrimExcess();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
