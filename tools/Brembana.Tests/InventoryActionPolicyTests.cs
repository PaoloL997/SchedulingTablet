using Brembana;
using Xunit;

namespace Brembana.Tests;

public class InventoryActionPolicyTests
{
    private const string UserPlant = "Valbrembo";

    [Fact]
    public void Create_without_match_opens_create_form()
    {
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Create, null, UserPlant);
        Assert.Equal(InventoryActionKind.CreateNew, decision.Kind);
        Assert.Null(decision.Match);
    }

    [Fact]
    public void Create_with_existing_item_does_not_move()
    {
        var match = Match("Schio");
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Create, match, UserPlant);
        Assert.Equal(InventoryActionKind.AlreadyPresent, decision.Kind);
        Assert.Same(match, decision.Match);
    }

    [Fact]
    public void Create_with_item_already_at_user_plant_is_found()
    {
        var match = Match(UserPlant);
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Create, match, UserPlant);
        Assert.Equal(InventoryActionKind.AlreadyPresent, decision.Kind);
    }

    [Fact]
    public void Move_without_match_is_not_found()
    {
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Move, null, UserPlant);
        Assert.Equal(InventoryActionKind.NotFound, decision.Kind);
    }

    [Fact]
    public void Move_same_plant_is_already_present()
    {
        var match = Match("valbrembo");
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Move, match, UserPlant);
        Assert.Equal(InventoryActionKind.AlreadyPresent, decision.Kind);
    }

    [Fact]
    public void Move_other_plant_requests_transfer()
    {
        var match = Match("Schio");
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Move, match, UserPlant);
        Assert.Equal(InventoryActionKind.Move, decision.Kind);
        Assert.Same(match, decision.Match);
    }

    [Fact]
    public void Maintenance_without_match_is_not_found()
    {
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, null, UserPlant);
        Assert.Equal(InventoryActionKind.NotFound, decision.Kind);
    }

    [Fact]
    public void Maintenance_rejects_unsupported_types()
    {
        var match = new InventoryMatch("Mandrino", "M-1", UserPlant, "15", "mandrino");
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, match, UserPlant);
        Assert.Equal(InventoryActionKind.UnsupportedType, decision.Kind);
    }

    [Fact]
    public void Maintenance_off_requests_enable()
    {
        var match = Match(UserPlant, inManutenzione: false);
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, match, null);
        Assert.Equal(InventoryActionKind.EnableMaintenance, decision.Kind);
    }

    [Fact]
    public void Maintenance_on_requests_disable()
    {
        var match = Match("Schio", inManutenzione: true);
        var decision = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, match, null);
        Assert.Equal(InventoryActionKind.DisableMaintenance, decision.Kind);
    }

    private static InventoryMatch Match(string plant, bool inManutenzione = false) =>
        new("Testa", "OV-T1", plant, "TP60", "testa", inManutenzione);
}
