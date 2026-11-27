namespace Libzora.Core;

/// <summary>An effective counted interval, never a wall-clock gap between saves.</summary>
public sealed record ReadingTimeSlice(DateTimeOffset Start, DateTimeOffset End, bool Listening = false)
{
    public IReadOnlyList<ReadingDashboardDay> SplitUtcDays()
    {
        var from = Start.UtcDateTime;
        var end = End.UtcDateTime;
        if (end <= from) return [];
        var total = (long)(end - from).TotalSeconds;
        if (total <= 0) return [];
        var result = new List<ReadingDashboardDay>();
        long assigned = 0;
        for (var cursor = from; cursor < end;)
        {
            var boundary = cursor.Date.AddDays(1);
            var next = boundary < end ? boundary : end;
            // Cumulative rounding conserves seconds even on a fractional midnight.
            var cumulative = next == end ? total : (long)(next - from).TotalSeconds;
            var seconds = cumulative - assigned;
            if (seconds > 0)
                result.Add(new(DateOnly.FromDateTime(cursor), seconds) { ListeningSeconds = Listening ? seconds : 0 });
            assigned = cumulative;
            cursor = next;
        }
        return result;
    }
}
