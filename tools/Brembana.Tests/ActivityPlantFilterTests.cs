using Brembana;
using Xunit;

namespace Brembana.Tests;

public class ActivityPlantFilterTests
{
    [Fact]
    public void Empty_user_plant_returns_no_activities()
    {
        var source = new[]
        {
            Activity("A1", "Valbrembo"),
            Activity("A2", "Schio"),
        };

        Assert.Empty(BridgeDataService.FilterOpenActivities(source, null));
        Assert.Empty(BridgeDataService.FilterOpenActivities(source, "  "));
    }

    [Fact]
    public void Keeps_only_open_activities_of_the_user_plant()
    {
        var source = new[]
        {
            Activity("Local", "Valbrembo"),
            Activity("Other", "Schio"),
            Activity("NoPlant", ""),
            Activity("Done", "Valbrembo", actualFinish: "2026-08-01"),
            Activity("DonePill", "Valbrembo", completedPill: true),
        };

        var result = BridgeDataService.FilterOpenActivities(source, "valbrembo");

        Assert.Single(result);
        Assert.Equal("Local", result[0].ActivityId);
    }

    [Fact]
    public void Plant_comparison_is_case_and_whitespace_insensitive()
    {
        Assert.True(BridgeDataService.PlantsEqual(" Valbrembo ", "valbrembo"));
        Assert.False(BridgeDataService.PlantsEqual("Valbrembo", "Schio"));
        Assert.False(BridgeDataService.PlantsEqual(null, "Valbrembo"));
    }

    [Fact]
    public void Outgoing_when_actual_start_is_missing()
    {
        Assert.True(BridgeDataService.IsOutgoingOperation(Activity("A", "Valbrembo")));
        Assert.False(BridgeDataService.IsOutgoingOperation(Activity("A", "Valbrembo", actualStart: "2026-08-01")));
    }

    [Fact]
    public void Session_requires_matching_user_plant()
    {
        Assert.False(InventoryActionPolicy.CanStartActivitySession("Valbrembo", null));
        Assert.False(InventoryActionPolicy.CanStartActivitySession("Schio", "Valbrembo"));
        Assert.True(InventoryActionPolicy.CanStartActivitySession(" Valbrembo ", "valbrembo"));
    }

    private static AttivitaDto Activity(
        string id,
        string plant,
        string? actualStart = null,
        string? actualFinish = null,
        bool completedPill = false)
    {
        return new AttivitaDto
        {
            ActivityId = id,
            Plant = plant,
            ActualStart = actualStart,
            ActualFinish = actualFinish,
            StatusPills = completedPill
                ? new List<StatusPillDto> { new() { Key = "completata", Label = "Completata" } }
                : null
        };
    }
}
