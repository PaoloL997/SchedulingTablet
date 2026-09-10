using Brembana;
using Newtonsoft.Json;
using Xunit;

namespace Brembana.Tests;

/// <summary>
/// La disponibilità la calcola il server, con la regola della web app e sulle attività
/// di tutti gli stabilimenti; il tablet la riporta sulla riga senza ricalcolarla.
/// </summary>
public class InventoryAvailabilityTests
{
    private const string BusyTooltip =
        "Occupata oggi da:\n• 25078-02-SNA (09/09/2026 – 21/09/2026)\nSi libera fra 12 giorni (22/09/2026).";

    private static InventarioDisponibilitaDto Busy() =>
        new() { Stato = "occupata", Libera = false, Tooltip = BusyTooltip };

    private static InventarioDisponibilitaDto Free() =>
        new() { Stato = "libera", Libera = true, Tooltip = "Libera oggi.\nNessun impegno pianificato." };

    [Fact]
    public void Row_follows_the_server_availability()
    {
        var inventario = new InventarioDto
        {
            Testa =
            {
                new InventarioTestaDto { Uid = "T-1", Disponibilita = Busy() },
                new InventarioTestaDto { Uid = "T-2", Disponibilita = Free() },
            },
        };

        var rows = InventoryListing.Project(inventario, "Testa");

        var busy = rows.Single(r => r.Uid == "T-1");
        Assert.False(busy.IsAvailableToday);
        Assert.Equal(BusyTooltip, busy.AvailabilityText);

        var free = rows.Single(r => r.Uid == "T-2");
        Assert.True(free.IsAvailableToday);
        Assert.StartsWith("Libera oggi.", free.AvailabilityText);
    }

    [Fact]
    public void Every_table_reads_its_availability()
    {
        var inventario = new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T-1", Disponibilita = Busy() } },
            Generatore = { new InventarioGeneratoreDto { Uid = "G-1", Disponibilita = Busy() } },
            Mandrino = { new InventarioMandrinoDto { Uid = "M-1", Disponibilita = Busy() } },
            Tastatore = { new InventarioTastatoreDto { Uid = "A-1", Disponibilita = Busy() } },
            Maschera = { new InventarioMascheraDto { Uid = "S-1", Disponibilita = Busy() } },
        };

        foreach (var tab in InventoryListing.Tabs)
        {
            var rows = InventoryListing.Project(inventario, tab.Table);
            Assert.Single(rows);
            Assert.False(rows[0].IsAvailableToday);
        }
    }

    [Fact]
    public void Missing_descriptor_or_tooltip_falls_back_to_a_plain_label()
    {
        var inventario = new InventarioDto
        {
            Testa =
            {
                // Server non ancora aggiornato: nessun dato di disponibilità.
                new InventarioTestaDto { Uid = "T-1" },
                // Descrittore senza tooltip: etichetta breve secondo lo stato.
                new InventarioTestaDto { Uid = "T-2", Disponibilita = new InventarioDisponibilitaDto { Libera = false } },
            },
        };

        var rows = InventoryListing.Project(inventario, "Testa");

        Assert.True(rows[0].IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowFree, rows[0].AvailabilityText);
        Assert.False(rows[1].IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowBusy, rows[1].AvailabilityText);
    }

    [Fact]
    public void Maintenance_does_not_change_the_availability_dot()
    {
        // Le due colonne restano indipendenti: la manutenzione ha già la sua.
        var inventario = new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T-1", InManutenzione = true, Disponibilita = Free() } },
        };

        var row = InventoryListing.Project(inventario, "Testa")[0];

        Assert.True(row.IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowInMaintenance, row.MaintenanceText);
    }

    [Fact]
    public void Availability_does_not_take_part_in_the_search()
    {
        var inventario = new InventarioDto
        {
            Generatore = { new InventarioGeneratoreDto { Uid = "G-1", Model = "TX-40", Disponibilita = Busy() } },
        };

        var rows = InventoryListing.Project(inventario, "Generatore");

        // Il pallino non ha testo: come la manutenzione, resta fuori dalla ricerca.
        Assert.Empty(InventoryListing.Filter(rows, "Occupata"));
        Assert.Empty(InventoryListing.Filter(rows, "25078"));
        Assert.Single(InventoryListing.Filter(rows, "TX-40"));
    }

    [Fact]
    public void Dataset_payload_carries_the_availability_descriptor()
    {
        // Forma di serialize_inventario lato server: il descrittore di resource_availability.
        const string json = """
        {
          "Testa": [{
            "uid": "H05", "model": "TP60", "plant": "Valbrembo", "notes": null, "in_manutenzione": false,
            "disponibilita": {
              "stato": "occupata", "libera": false, "etichetta": "Occupata",
              "occupata_fino": "2026-09-30", "libera_dal": "2026-10-01", "prossimo_impegno": null, "giorni": 21,
              "attivita": [{"attivita_id": 3921, "label": "25078-01-SND", "data_inizio": "2026-09-01", "data_fine": "2026-09-30"}],
              "tooltip": "Occupata oggi da:\n• 25078-01-SND (01/09/2026 – 30/09/2026)\nSi libera fra 21 giorni (01/10/2026)."
            }
          }],
          "Mandrino": [{
            "uid": "C200", "diameter": "12,00", "plant": "Ricengo", "notes": null,
            "disponibilita": {"stato": "libera", "libera": true, "tooltip": "Libera oggi.\nNessun impegno pianificato."}
          }],
          "Tastatore": [],
          "Maschera": [{
            "uid": "M161", "pitch": "1,5", "center_distance": null, "plant": "Valbrembo",
            "disponibilita": {"stato": "occupata", "libera": false, "tooltip": "Occupata oggi da: …"}
          }],
          "Generatore": []
        }
        """;

        var inventario = JsonConvert.DeserializeObject<InventarioDto>(json)!;

        var testa = InventoryListing.Project(inventario, "Testa")[0];
        Assert.False(testa.IsAvailableToday);
        Assert.StartsWith("Occupata oggi da:\n• 25078-01-SND", testa.AvailabilityText);

        Assert.True(InventoryListing.Project(inventario, "Mandrino")[0].IsAvailableToday);
        Assert.False(InventoryListing.Project(inventario, "Maschera")[0].IsAvailableToday);
    }
}
