using Brembana;
using Xunit;

namespace Brembana.Tests;

public class InventoryListingTests
{
    private static InventarioDto SampleInventory() => new()
    {
        Testa =
        {
            new InventarioTestaDto { Uid = "T-1", Model = "TP60", Plant = "Schio", Notes = "nota testa", InManutenzione = true },
            new InventarioTestaDto { Uid = "T-2", Model = "TP80", Plant = "Ricengo" },
        },
        Generatore =
        {
            new InventarioGeneratoreDto { Uid = "G-1", Model = "TX-40", Plant = "Valbrembo", Notes = "nota gen" },
        },
        Mandrino =
        {
            new InventarioMandrinoDto { Uid = "M-1", Diameter = "15,35", Plant = "Valbrembo", Notes = "nota mandrino" },
        },
        Tastatore =
        {
            new InventarioTastatoreDto { Uid = "A-1", Diameter = "8,00", Plant = "Ricengo", Notes = "nota tastatore" },
        },
        Maschera =
        {
            new InventarioMascheraDto { Uid = "S-1", Pitch = "22,00", CenterDistance = "31,75", Plant = "Valbrembo" },
        },
    };

    [Fact]
    public void Project_maps_each_type_to_its_measure_and_extra_columns()
    {
        var inventario = SampleInventory();

        var testa = InventoryListing.Project(inventario, "Testa")[0];
        Assert.Equal("TP60", testa.MeasureValue);
        Assert.Equal("nota testa", testa.ExtraValue);
        Assert.True(testa.InManutenzione);

        var generatore = InventoryListing.Project(inventario, "Generatore")[0];
        Assert.Equal("TX-40", generatore.MeasureValue);
        Assert.Equal("nota gen", generatore.ExtraValue);
        Assert.False(generatore.InManutenzione);

        var mandrino = InventoryListing.Project(inventario, "Mandrino")[0];
        Assert.Equal("15,35", mandrino.MeasureValue);
        Assert.Equal("nota mandrino", mandrino.ExtraValue);
        Assert.False(mandrino.InManutenzione);

        var tastatore = InventoryListing.Project(inventario, "Tastatore")[0];
        Assert.Equal("8,00", tastatore.MeasureValue);
        Assert.Equal("nota tastatore", tastatore.ExtraValue);

        // Per la maschera la quarta colonna è l'interasse, non le note.
        var maschera = InventoryListing.Project(inventario, "Maschera")[0];
        Assert.Equal("22,00", maschera.MeasureValue);
        Assert.Equal("31,75", maschera.ExtraValue);
    }

    [Fact]
    public void Project_returns_only_the_requested_table()
    {
        var inventario = SampleInventory();
        var total = 0;

        foreach (var tab in InventoryListing.Tabs)
        {
            var rows = InventoryListing.Project(inventario, tab.Table);
            Assert.All(rows, r => Assert.Equal(tab.Table, r.Table));
            total += rows.Count;
        }

        Assert.Equal(6, total);
        Assert.Equal(2, InventoryListing.Project(inventario, "Testa").Count);
    }

    [Fact]
    public void Project_sorts_uids_in_natural_order()
    {
        var inventario = new InventarioDto
        {
            Generatore =
            {
                new InventarioGeneratoreDto { Uid = "C100" },
                new InventarioGeneratoreDto { Uid = "C10" },
                new InventarioGeneratoreDto { Uid = "C2" },
                new InventarioGeneratoreDto { Uid = "c9" },
                new InventarioGeneratoreDto { Uid = "C101" },
            },
        };

        var uids = InventoryListing.Project(inventario, "Generatore").Select(r => r.Uid).ToList();

        // Le cifre valgono come numeri: C2 prima di C10, non l'ordine testuale
        // C10 < C100 < C2 che darebbe il server.
        Assert.Equal(new[] { "C2", "c9", "C10", "C100", "C101" }, uids);
    }

    [Fact]
    public void Natural_comparer_handles_prefixes_padding_and_multiple_number_groups()
    {
        var c = NaturalUidComparer.Instance;

        Assert.True(c.Compare("C2", "C10") < 0);
        Assert.True(c.Compare("C10", "C2") > 0);
        Assert.Equal(0, c.Compare("WO400", "wo400"));

        // Gli zeri iniziali non cambiano il valore, ma danno un ordine stabile.
        Assert.True(c.Compare("C007", "C7") > 0);
        Assert.Equal(0, c.Compare("C7", "C7"));

        // Più gruppi numerici nello stesso codice.
        Assert.True(c.Compare("M1-2", "M1-10") < 0);
        Assert.True(c.Compare("M2-1", "M10-1") < 0);

        // Prefissi diversi: decide il testo.
        Assert.True(c.Compare("A100", "B1") < 0);

        // Un codice che è prefisso dell'altro viene prima.
        Assert.True(c.Compare("C1", "C1A") < 0);

        Assert.Equal(0, c.Compare(null, null));
        Assert.True(c.Compare(null, "C1") < 0);
    }

    [Fact]
    public void Project_substitutes_the_em_dash_for_missing_values()
    {
        var inventario = new InventarioDto
        {
            Generatore =
            {
                new InventarioGeneratoreDto { Uid = "", Model = null, Plant = "   ", Notes = "" },
            },
        };

        var row = InventoryListing.Project(inventario, "Generatore")[0];

        Assert.Equal("—", row.UidText);
        Assert.Equal("—", row.MeasureText);
        Assert.Equal("—", row.PlantText);
        Assert.Equal("—", row.ExtraText);

        // Il valore grezzo resta vuoto: il trattino è solo presentazione.
        Assert.Equal("", row.Uid);
    }

    [Fact]
    public void Project_handles_an_empty_inventory()
    {
        var empty = new InventarioDto();

        foreach (var tab in InventoryListing.Tabs)
            Assert.Empty(InventoryListing.Project(empty, tab.Table));

        Assert.Empty(InventoryListing.Project(null, "Testa"));
    }

    [Fact]
    public void Filter_matches_uid_measure_plant_and_extra_case_insensitively()
    {
        var rows = InventoryListing.Project(SampleInventory(), "Mandrino");

        Assert.Single(InventoryListing.Filter(rows, "m-1"));
        Assert.Single(InventoryListing.Filter(rows, "15,35"));
        Assert.Single(InventoryListing.Filter(rows, "VALBREMBO"));
        Assert.Single(InventoryListing.Filter(rows, "nota mandrino"));

        Assert.Empty(InventoryListing.Filter(rows, "TP60"));

        Assert.Equal(rows.Count, InventoryListing.Filter(rows, "").Count);
        Assert.Equal(rows.Count, InventoryListing.Filter(rows, "   ").Count);
        Assert.Equal(rows.Count, InventoryListing.Filter(rows, null).Count);
    }

    [Fact]
    public void Filter_does_not_match_on_the_maintenance_label()
    {
        var rows = InventoryListing.Project(SampleInventory(), "Testa");
        Assert.Contains(rows, r => r.MaintenanceText == UiStrings.InventoryRowInMaintenance);

        // Parità con la web app, dove la cella manutenzione è un pulsante senza testo
        // e quindi non partecipa alla ricerca su textContent.
        Assert.Empty(InventoryListing.Filter(rows, "manutenzione"));
    }

    [Fact]
    public void Maintenance_text_is_populated_only_for_testa_and_generatore()
    {
        var inventario = new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T-1", InManutenzione = true } },
            Generatore = { new InventarioGeneratoreDto { Uid = "G-1", InManutenzione = false } },
            Mandrino = { new InventarioMandrinoDto { Uid = "M-1" } },
            Tastatore = { new InventarioTastatoreDto { Uid = "A-1" } },
            Maschera = { new InventarioMascheraDto { Uid = "S-1" } },
        };

        foreach (var tab in InventoryListing.Tabs)
        {
            foreach (var row in InventoryListing.Project(inventario, tab.Table))
            {
                var expected = InventoryActionPolicy.SupportsMaintenance(row.Table) && row.InManutenzione;
                Assert.Equal(expected, !string.IsNullOrEmpty(row.MaintenanceText));
            }
        }
    }

    [Fact]
    public void ColumnsFor_matches_the_web_headers_per_type()
    {
        foreach (var table in new[] { "Testa", "Generatore" })
        {
            var c = InventoryListing.ColumnsFor(table);
            Assert.Equal(UiStrings.InventoryColModel, c.Measure);
            Assert.Equal(UiStrings.InventoryColNotes, c.Extra);
            Assert.Equal(UiStrings.InventoryColMaintenance, c.Status);
        }

        foreach (var table in new[] { "Mandrino", "Tastatore" })
        {
            var c = InventoryListing.ColumnsFor(table);
            Assert.Equal(UiStrings.InventoryColDiameter, c.Measure);
            Assert.Equal(UiStrings.InventoryColNotes, c.Extra);
            Assert.Equal("", c.Status);
        }

        var maschera = InventoryListing.ColumnsFor("Maschera");
        Assert.Equal(UiStrings.InventoryColPitch, maschera.Measure);
        Assert.Equal(UiStrings.InventoryColCenterDistance, maschera.Extra);
        Assert.NotEqual(UiStrings.InventoryColNotes, maschera.Extra);
        Assert.Equal("", maschera.Status);

        Assert.All(
            InventoryListing.Tabs,
            t =>
            {
                var c = InventoryListing.ColumnsFor(t.Table);
                Assert.Equal(UiStrings.InventoryColUid, c.Uid);
                Assert.Equal(UiStrings.InventoryFieldPlant, c.Plant);

                // La disponibilità vale per ogni tipo, a differenza della manutenzione.
                Assert.Equal(UiStrings.InventoryColAvailability, c.Availability);
            });
    }

    [Fact]
    public void Tabs_cover_every_inventory_table_exactly_once_in_display_order()
    {
        var tables = InventoryListing.Tabs.Select(t => t.Table).ToList();

        Assert.Equal(5, tables.Count);
        Assert.Equal(tables.Count, tables.Distinct().Count());

        // Rete di sicurezza: un sesto tipo aggiunto al bridge senza tab fa fallire qui.
        Assert.Equal(
            BridgeDataService.InventoryTables.OrderBy(t => t).ToList(),
            tables.OrderBy(t => t).ToList());

        Assert.Equal(new[] { "Generatore", "Mandrino", "Maschera", "Tastatore", "Testa" }, tables);
        Assert.All(InventoryListing.Tabs, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
        Assert.Equal(UiStrings.InventoryTabGeneratori, InventoryListing.LabelFor("Generatore"));
    }
}
