using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DevProjex.Terminal.Tui;

internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
	private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
	private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
	private static readonly NotifyCollectionChangedEventArgs CollectionReset =
		new(NotifyCollectionChangedAction.Reset);

	public void Reset(IReadOnlyList<T> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		using (BlockReentrancy())
		{
			Items.Clear();
			for (var index = 0; index < items.Count; index++)
				Items.Add(items[index]);
		}

		OnPropertyChanged(CountChanged);
		OnPropertyChanged(IndexerChanged);
		OnCollectionChanged(CollectionReset);
	}
}
