using System.Collections.ObjectModel;

namespace TinyTracker.Presentation;

public static class CollectionSync
{
    // Removes, moves and inserts items until target matches wanted, so lists animate instead of blinking.
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = IndexOf(target, wanted[i]);
            if (at < 0) target.Insert(i, wanted[i]);
            else if (at != i) target.Move(at, i);
        }
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item) where T : class
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item)) return i;
        return -1;
    }
}
