using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// ObservableCollection with a single-notification bulk replace. A library
/// refresh used to raise one Remove/Insert/Move per card, and every event made
/// the virtualizing panel tear down its realized containers.
/// </summary>

/// <summary>
/// Bulk, change-aware observable collection.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Skips the reset when the ordered item identities are unchanged. Search
    /// filtering runs on every library view change, and the common case leaves
    /// the collection list untouched.
    /// </summary>
    public void ReplaceAllIfChanged(IReadOnlyList<T> items)
    {
        if (Count == items.Count)
        {
            var same = true;
            var comparer = EqualityComparer<T>.Default;
            for (var index = 0; index < items.Count; index++)
            {
                if (comparer.Equals(Items[index], items[index])) continue;
                same = false;
                break;
            }
            if (same) return;
        }
        ReplaceAll(items);
    }
}

/// <summary>
/// Presentation data for one local-library book. The domain model remains
/// untouched; this class only turns paths and enum values into UI labels and a
/// loadable Avalonia bitmap.
/// </summary>
