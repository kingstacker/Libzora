using Libzora.Core;

namespace Libzora;

/// <summary>
/// Projects a shelf page into display items, replacing each stackable series
/// with one front card. The caller keeps the full page elsewhere for selection,
/// paging and batch actions.
/// </summary>
internal static class SeriesShelfProjection
{
    public static IReadOnlyList<T> Project<T>(
        IReadOnlyList<T> items,
        bool enabled,
        Func<T, BookSeriesCandidate> candidate,
        Func<T, string> id,
        Action<T, string?, string?, IReadOnlyList<T>?> assign,
        IDictionary<string, string> promotedFrontIds)
        where T : class
    {
        foreach (var item in items)
            assign(item, null, null, null);
        if (!enabled || items.Count < 2) return items;

        var stacks = BookSeriesGrouping.Stacks(items.Select(candidate));
        if (stacks.Count == 0) return items;

        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
            byId.TryAdd(id(item), item);

        var stackByMember = new Dictionary<string, BookSeriesStack>(StringComparer.Ordinal);
        foreach (var stack in stacks)
            foreach (var member in stack.Members)
                stackByMember[member.Id] = stack;

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var shelf = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (!stackByMember.TryGetValue(id(item), out var stack))
            {
                shelf.Add(item);
                continue;
            }
            // Keep the stack where its first volume sits in the current sort.
            if (!emitted.Add(stack.Key)) continue;

            var members = stack.Members
                .Select(member => byId[member.Id])
                .ToArray();
            var front = members[0];
            if (promotedFrontIds.TryGetValue(stack.Key, out var frontId)
                && byId.TryGetValue(frontId, out var promoted)
                && members.Contains(promoted))
                front = promoted;

            var ordered = new T[members.Length];
            ordered[0] = front;
            var position = 1;
            foreach (var member in members)
                if (!ReferenceEquals(member, front))
                    ordered[position++] = member;

            assign(front, stack.Key, stack.Title, ordered);
            shelf.Add(front);
        }
        return shelf;
    }
}
