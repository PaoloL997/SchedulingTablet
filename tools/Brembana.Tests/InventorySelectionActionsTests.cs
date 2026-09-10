using Brembana;
using Xunit;

namespace Brembana.Tests;

public class InventorySelectionActionsTests
{
    private const string UserPlant = "Ricengo";

    /// <summary>Una riga vera, passata da Project come nell'hub.</summary>
    private static InventoryListRow Row(string table, string? plant = "Valbrembo", bool inManutenzione = false)
    {
        var inventario = table switch
        {
            "Testa" => new InventarioDto
            {
                Testa = { new InventarioTestaDto { Uid = "T-1", Plant = plant, InManutenzione = inManutenzione } }
            },
            "Generatore" => new InventarioDto
            {
                Generatore = { new InventarioGeneratoreDto { Uid = "G-1", Plant = plant, InManutenzione = inManutenzione } }
            },
            "Mandrino" => new InventarioDto { Mandrino = { new InventarioMandrinoDto { Uid = "M-1", Plant = plant } } },
            "Tastatore" => new InventarioDto { Tastatore = { new InventarioTastatoreDto { Uid = "A-1", Plant = plant } } },
            _ => new InventarioDto { Maschera = { new InventarioMascheraDto { Uid = "S-1", Plant = plant } } },
        };

        return InventoryListing.Project(inventario, table)[0];
    }

    [Fact]
    public void No_selection_hides_the_bar()
    {
        Assert.Null(InventorySelectionActions.For(null, UserPlant));
    }

    [Fact]
    public void Move_is_enabled_for_a_piece_in_another_plant()
    {
        var actions = InventorySelectionActions.For(Row("Mandrino", plant: "Valbrembo"), UserPlant)!;

        Assert.True(actions.CanMove);
        Assert.Null(actions.MoveHint);
    }

    [Fact]
    public void Move_is_disabled_for_a_piece_already_in_the_user_plant()
    {
        // Stesso confronto tollerante di PlantsEqual: maiuscole e spazi non contano.
        var actions = InventorySelectionActions.For(Row("Mandrino", plant: " ricengo "), UserPlant)!;

        Assert.False(actions.CanMove);
        Assert.Equal(UiStrings.FormatInventoryAlreadyInPlant(UserPlant), actions.MoveHint);
    }

    [Fact]
    public void Move_needs_a_plant_on_the_user_profile()
    {
        foreach (var userPlant in new[] { null, "", "   " })
        {
            var actions = InventorySelectionActions.For(Row("Mandrino"), userPlant)!;
            Assert.False(actions.CanMove);
            Assert.Equal(UiStrings.InventoryMoveNeedsPlant, actions.MoveHint);
        }

        // PlantsEqual(null, null) è vero: senza il controllo esplicito questa riga
        // risulterebbe "già nel tuo stabilimento".
        var noPlants = InventorySelectionActions.For(Row("Mandrino", plant: null), null)!;
        Assert.Equal(UiStrings.InventoryMoveNeedsPlant, noPlants.MoveHint);
    }

    [Fact]
    public void Maintenance_label_follows_the_current_state()
    {
        var off = InventorySelectionActions.For(Row("Generatore", inManutenzione: false), UserPlant)!;
        Assert.True(off.ShowMaintenance);
        Assert.True(off.MaintenanceEnables);
        Assert.Equal(UiStrings.InventoryMaintenanceEnableAction, off.MaintenanceLabel);

        var on = InventorySelectionActions.For(Row("Testa", inManutenzione: true), UserPlant)!;
        Assert.True(on.ShowMaintenance);
        Assert.False(on.MaintenanceEnables);
        Assert.Equal(UiStrings.InventoryMaintenanceDisableAction, on.MaintenanceLabel);
    }

    [Fact]
    public void Maintenance_is_hidden_where_it_does_not_apply()
    {
        foreach (var table in new[] { "Mandrino", "Tastatore", "Maschera" })
            Assert.False(InventorySelectionActions.For(Row(table), UserPlant)!.ShowMaintenance);
    }

    [Fact]
    public void Maintenance_does_not_need_a_plant_on_the_user_profile()
    {
        var actions = InventorySelectionActions.For(Row("Generatore"), null)!;

        Assert.True(actions.ShowMaintenance);
        Assert.False(actions.CanMove);
    }

    [Fact]
    public void Action_labels_say_what_happens()
    {
        Assert.Equal("Sposta di stabilimento", UiStrings.InventoryMoveAction);
        Assert.Equal("Manda in manutenzione", UiStrings.InventoryMaintenanceEnableAction);
        Assert.Equal("Rimuovi dalla manutenzione", UiStrings.InventoryMaintenanceDisableAction);
    }
}

public class InventoryHubStringsTests
{
    [Fact]
    public void Visible_count_reads_visible_of_total()
    {
        Assert.Equal("8 di 8 componenti", UiStrings.FormatInventoryVisibleCount(8, 8));
        Assert.Equal("0 di 3 componenti", UiStrings.FormatInventoryVisibleCount(0, 3));
        Assert.Equal("1 di 1 componente", UiStrings.FormatInventoryVisibleCount(1, 1));
    }

    [Fact]
    public void User_initial_is_the_first_letter_uppercased()
    {
        Assert.Equal("P", UiStrings.FormatUserInitial(" paolo"));
        Assert.Equal("", UiStrings.FormatUserInitial(null));
        Assert.Equal("", UiStrings.FormatUserInitial("   "));
    }

    [Fact]
    public void Only_the_inventory_page_hides_the_global_chrome()
    {
        Assert.False(PageChromePolicy.ShowsGlobalChrome(typeof(InventoryHubPage)));
        Assert.True(PageChromePolicy.ShowsGlobalChrome(typeof(HomePage)));
        Assert.True(PageChromePolicy.ShowsGlobalChrome(typeof(ActivitiesPage)));
        Assert.True(PageChromePolicy.ShowsGlobalChrome(null));
    }
}
