using Brembana;
using Xunit;

namespace Brembana.Tests;

public class ActivityStartPlantMoveTests
{
    [Fact]
    public void Lists_only_components_registered_in_another_plant()
    {
        var service = ServiceWith(new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T1", Plant = "Schio" } },
            Mandrino =
            {
                new InventarioMandrinoDto { Uid = "M-HERE", Plant = " valbrembo " },
                new InventarioMandrinoDto { Uid = "M-NOPLANT", Plant = "" },
            },
            Maschera = { new InventarioMascheraDto { Uid = "MA1", Plant = "Ricengo" } },
        });

        var result = service.FindComponentsInOtherPlant(new[]
        {
            Comp("T1", "testa"),
            Comp("M-HERE", "mandrino"),
            Comp("M-NOPLANT", "mandrino"),
            Comp("MISSING", "tastatore"),
            Comp("MA1", "maschera"),
        }, "Valbrembo");

        Assert.Equal(new[] { "Testa:T1", "Maschera:MA1" }, result.Select(m => $"{m.Table}:{m.Uid}"));
        Assert.Equal("Schio", result[0].Plant);
    }

    [Fact]
    public void Uses_the_component_type_when_the_uid_repeats_across_tables()
    {
        var service = ServiceWith(new InventarioDto
        {
            Mandrino = { new InventarioMandrinoDto { Uid = "C10", Plant = "Valbrembo" } },
            Tastatore = { new InventarioTastatoreDto { Uid = "C10", Plant = "Schio" } },
        });

        Assert.Empty(service.FindComponentsInOtherPlant(new[] { Comp("C10", "mandrino") }, "Valbrembo"));

        var tastatore = Assert.Single(service.FindComponentsInOtherPlant(new[] { Comp("C10", "tastatore") }, "Valbrembo"));
        Assert.Equal("Tastatore", tastatore.Table);
    }

    [Fact]
    public void Nothing_to_move_without_a_tablet_plant()
    {
        var service = ServiceWith(new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T1", Plant = "Schio" } },
        });

        Assert.Empty(service.FindComponentsInOtherPlant(new[] { Comp("T1", "testa") }, null));
        Assert.Empty(service.FindComponentsInOtherPlant(new[] { Comp("T1", "testa") }, "  "));
    }

    [Fact]
    public void The_same_piece_is_listed_once()
    {
        var service = ServiceWith(new InventarioDto
        {
            Mandrino = { new InventarioMandrinoDto { Uid = "M1", Plant = "Schio" } },
        });

        var result = service.FindComponentsInOtherPlant(
            new[] { Comp("M1", "mandrino"), Comp(" m1 ", "mandrino") }, "Valbrembo");

        Assert.Single(result);
    }

    [Fact]
    public void Moved_message_names_every_piece_and_the_destination()
    {
        var moved = new[]
        {
            new InventoryMatch("Testa", "T1", "Schio", null, "testa"),
            new InventoryMatch("Maschera", "MA1", "Ricengo", null, "maschera"),
        };

        Assert.Equal(
            "Inventario aggiornato: Testa T1, Maschera MA1 ora in Valbrembo.",
            UiStrings.FormatMovedToPlant(moved, "Valbrembo"));
    }

    private static BridgeDataService ServiceWith(InventarioDto inventario)
    {
        var service = new BridgeDataService(new BridgeApiClient());
        service.LoadDatasetForTests(inventario: inventario);
        return service;
    }

    private static TrackedComponent Comp(string uid, string tipoKey) =>
        new() { ResourceName = uid, TipoKey = tipoKey };
}
