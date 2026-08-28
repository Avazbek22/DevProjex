using System.Collections.Specialized;

namespace DevProjex.Tests.Terminal;

public sealed class ResettableObservableCollectionTests
{
	[Fact]
	public void ResetPublishesOneChangeForAReplacedParameterList()
	{
		var collection = new ResettableObservableCollection<int>();
		var changes = new List<NotifyCollectionChangedEventArgs>();
		collection.CollectionChanged += (_, args) => changes.Add(args);

		collection.Reset(Enumerable.Range(0, 500).ToArray());

		var change = Assert.Single(changes);
		Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action);
		Assert.Equal(500, collection.Count);
	}
}
