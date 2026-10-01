using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;

namespace TinyTracker.Presentation.Tests;

public class CollectionSyncTests
{
    private sealed record Item(string Name);

    private static readonly Item A = new("a"), B = new("b"), C = new("c"), D = new("d");

    [Fact]
    public void Target_EndsUpLikeWanted()
    {
        var target = new ObservableCollection<Item> { A, B, C };
        CollectionSync.Apply(target, [C, D, A]);
        Assert.Equal([C, D, A], target);
    }

    [Fact]
    public void KeptItems_AreMoved_NotReplaced()
    {
        var target = new ObservableCollection<Item> { A, B, C };
        var changes = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => changes.Add(e.Action);
        CollectionSync.Apply(target, [C, A, B]);
        Assert.Equal([NotifyCollectionChangedAction.Move], changes);
    }

    [Fact]
    public void EqualButDistinctItems_AreReplaced()
    {
        var copy = new Item("a");
        var target = new ObservableCollection<Item> { A };
        CollectionSync.Apply(target, [copy]);
        Assert.Same(copy, Assert.Single(target));
    }
}
