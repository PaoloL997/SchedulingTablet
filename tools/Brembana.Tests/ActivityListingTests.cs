using Brembana;
using Xunit;

namespace Brembana.Tests;

public class ActivityListingTests
{
    [Fact]
    public void Label_drops_the_commessa()
    {
        var a = Activity("24017", "TD512-3", "Saldatura", "2026-10-01");

        Assert.Equal("TD512-3-Saldatura", ActivityListing.ActivityLabel(a));
    }

    [Fact]
    public void Commesse_are_sorted_by_ascending_number()
    {
        var source = new[]
        {
            Activity("24100", "D-1", "A", "2026-10-01"),
            Activity("9", "D-1", "A", "2026-10-01"),
            Activity("24017", "D-1", "A", "2026-10-01"),
            Activity("24017", "D-2", "A", "2026-10-02"),
        };

        var groups = ActivityListing.GroupByCommessa(source);

        Assert.Equal(new[] { "9", "24017", "24100" }, groups.Select(g => g.Commessa));
        Assert.Equal(2, groups[1].Activities.Count);
    }

    [Fact]
    public void Activities_are_sorted_by_ascending_planned_start_with_undated_last()
    {
        var source = new[]
        {
            Activity("1", "D-1", "Late", "2026-11-05"),
            Activity("1", "D-1", "NoDate", null),
            Activity("1", "D-1", "Early", "2026-09-30"),
        };

        var sorted = ActivityListing.SortActivities(source);

        Assert.Equal(new[] { "Early", "Late", "NoDate" }, sorted.Select(a => a.Activity));
    }

    private static AttivitaDto Activity(string commessa, string item, string activity, string? start) => new()
    {
        Commessa = commessa,
        Item = item,
        Activity = activity,
        ActivityId = $"{commessa}-{item}-{activity}",
        Start = start
    };
}
