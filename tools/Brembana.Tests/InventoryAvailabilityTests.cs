using Brembana;
using Xunit;

namespace Brembana.Tests;

public class InventoryAvailabilityTests
{
    private static readonly DateOnly Today = new(2026, 9, 9);

    [Fact]
    public void Started_and_unfinished_activity_occupies_its_resources()
    {
        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", tipo: "testa", typedUids: "T-1") },
            Today);

        Assert.True(occupancy.IsOccupied("Testa", "T-1"));
        Assert.False(occupancy.IsOccupied("Testa", "T-2"));
    }

    [Fact]
    public void Activity_without_actual_start_leaves_its_resources_free()
    {
        // Uscita non ancora registrata: il componente è ancora in magazzino.
        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", tipo: "testa", typedUids: "T-1") },
            Today);

        Assert.False(occupancy.IsOccupied("Testa", "T-1"));
    }

    [Fact]
    public void Closed_activity_frees_its_resources()
    {
        var finished = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", finish: "2026-09-05", tipo: "testa", typedUids: "T-1") },
            Today);
        Assert.False(finished.IsOccupied("Testa", "T-1"));

        // Stessa regola di BridgeDataService.FilterOpenActivities: vale anche la pill.
        var pill = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", completedPill: true, tipo: "testa", typedUids: "T-1") },
            Today);
        Assert.False(pill.IsOccupied("Testa", "T-1"));
    }

    [Fact]
    public void Start_date_in_the_future_does_not_occupy_today()
    {
        var activities = new[]
        {
            Activity("Futura", start: "2026-09-10", tipo: "mandrino", typedUids: "M-1"),
            Activity("Oggi", start: "2026-09-09", tipo: "mandrino", typedUids: "M-2"),
            Activity("Passata", start: "2026-01-02", tipo: "mandrino", typedUids: "M-3"),
        };

        var occupancy = ResourceOccupancy.FromActivities(activities, Today);

        Assert.False(occupancy.IsOccupied("Mandrino", "M-1"));
        Assert.True(occupancy.IsOccupied("Mandrino", "M-2"));
        Assert.True(occupancy.IsOccupied("Mandrino", "M-3"));
    }

    [Fact]
    public void Start_dates_are_read_in_every_format_the_bridge_uses()
    {
        var activities = new[]
        {
            Activity("Iso", start: "2026-09-01T07:30:00", tipo: "tastatore", typedUids: "A-1"),
            Activity("Italiana", start: "01/09/2026", tipo: "tastatore", typedUids: "A-2"),
            // Data illeggibile: l'uscita risulta comunque registrata.
            Activity("Illeggibile", start: "non una data", tipo: "tastatore", typedUids: "A-3"),
        };

        var occupancy = ResourceOccupancy.FromActivities(activities, Today);

        Assert.True(occupancy.IsOccupied("Tastatore", "A-1"));
        Assert.True(occupancy.IsOccupied("Tastatore", "A-2"));
        Assert.True(occupancy.IsOccupied("Tastatore", "A-3"));

        // "01/09/2026" è il 1° settembre, non il 9 gennaio: al 1° maggio l'uscita è
        // ancora nel futuro e il tastatore risulta libero.
        Assert.False(ResourceOccupancy
            .FromActivities(activities, new DateOnly(2026, 5, 1))
            .IsOccupied("Tastatore", "A-2"));
    }

    [Fact]
    public void Typed_resources_occupy_only_their_own_table()
    {
        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", resources: "C-1", tipo: "maschera", typedUids: "C-1") },
            Today);

        // Lo stesso uid può esistere su tabelle diverse: resources_by_tipo dice quale.
        Assert.True(occupancy.IsOccupied("Maschera", "C-1"));
        Assert.False(occupancy.IsOccupied("Generatore", "C-1"));
    }

    [Fact]
    public void Untyped_resources_occupy_the_uid_on_any_table()
    {
        // Attività senza resources_by_tipo: resta solo l'elenco piatto, che non porta il tipo.
        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", resources: "G-1; G-2 , ") },
            Today);

        Assert.True(occupancy.IsOccupied("Generatore", "G-1"));
        Assert.True(occupancy.IsOccupied("Testa", "G-2"));
        Assert.False(occupancy.IsOccupied("Generatore", "G-3"));
    }

    [Fact]
    public void Lookup_ignores_case_padding_and_empty_codes()
    {
        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", tipo: "generatore", typedUids: "G-1") },
            Today);

        Assert.True(occupancy.IsOccupied("generatore", " g-1 "));
        Assert.False(occupancy.IsOccupied("Generatore", ""));
        Assert.False(occupancy.IsOccupied("Generatore", null));
    }

    [Fact]
    public void Empty_occupancy_leaves_everything_free()
    {
        Assert.False(ResourceOccupancy.Empty.IsOccupied("Testa", "T-1"));
        Assert.False(ResourceOccupancy.FromActivities(null, Today).IsOccupied("Testa", "T-1"));
        Assert.False(ResourceOccupancy
            .FromActivities(Array.Empty<AttivitaDto>(), Today)
            .IsOccupied("Testa", "T-1"));
    }

    [Fact]
    public void Project_marks_the_occupied_rows_as_busy()
    {
        var inventario = new InventarioDto
        {
            Testa =
            {
                new InventarioTestaDto { Uid = "T-1" },
                new InventarioTestaDto { Uid = "T-2" },
            },
        };

        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", tipo: "testa", typedUids: "T-1") },
            Today);

        var rows = InventoryListing.Project(inventario, "Testa", occupancy);

        var busy = rows.Single(r => r.Uid == "T-1");
        Assert.False(busy.IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowBusy, busy.AvailabilityText);

        var free = rows.Single(r => r.Uid == "T-2");
        Assert.True(free.IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowFree, free.AvailabilityText);
    }

    [Fact]
    public void Project_without_occupancy_reports_every_row_as_free()
    {
        var inventario = new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T-1", InManutenzione = true } },
            Mandrino = { new InventarioMandrinoDto { Uid = "M-1" } },
        };

        foreach (var tab in InventoryListing.Tabs)
        {
            foreach (var row in InventoryListing.Project(inventario, tab.Table))
            {
                Assert.True(row.IsAvailableToday);
                Assert.Equal(UiStrings.InventoryRowFree, row.AvailabilityText);
            }
        }
    }

    [Fact]
    public void Maintenance_does_not_change_the_availability_dot()
    {
        // Le due colonne restano indipendenti: la manutenzione ha già la sua.
        var inventario = new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "T-1", InManutenzione = true } },
        };

        var row = InventoryListing.Project(inventario, "Testa", ResourceOccupancy.Empty)[0];

        Assert.True(row.IsAvailableToday);
        Assert.Equal(UiStrings.InventoryRowInMaintenance, row.MaintenanceText);
    }

    [Fact]
    public void Availability_does_not_take_part_in_the_search()
    {
        var inventario = new InventarioDto
        {
            Generatore = { new InventarioGeneratoreDto { Uid = "G-1", Model = "TX-40" } },
        };

        var occupancy = ResourceOccupancy.FromActivities(
            new[] { Activity("A1", start: "2026-09-01", tipo: "generatore", typedUids: "G-1") },
            Today);

        var rows = InventoryListing.Project(inventario, "Generatore", occupancy);
        Assert.Equal(UiStrings.InventoryRowBusy, rows[0].AvailabilityText);

        // Il pallino non ha testo: come la manutenzione, resta fuori dalla ricerca.
        Assert.Empty(InventoryListing.Filter(rows, UiStrings.InventoryRowBusy));
        Assert.Single(InventoryListing.Filter(rows, "TX-40"));
    }

    /// <summary>
    /// Un'attività del bridge. <paramref name="tipo"/>/<paramref name="typedUids"/> popolano
    /// <c>resources_by_tipo</c>, <paramref name="resources"/> l'elenco piatto senza tipo.
    /// </summary>
    private static AttivitaDto Activity(
        string id,
        string? start = null,
        string? finish = null,
        bool completedPill = false,
        string? resources = null,
        string? tipo = null,
        string? typedUids = null)
    {
        var activity = new AttivitaDto
        {
            ActivityId = id,
            ActualStart = start,
            ActualFinish = finish,
            Resources = resources,
            StatusPills = completedPill
                ? new List<StatusPillDto> { new() { Key = "completata", Label = "Completata" } }
                : null
        };

        if (!string.IsNullOrWhiteSpace(tipo))
        {
            activity.ResourcesByTipo = new Dictionary<string, List<string>>
            {
                [tipo] = (typedUids ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(u => u.Trim())
                    .ToList(),
            };
        }

        return activity;
    }
}
