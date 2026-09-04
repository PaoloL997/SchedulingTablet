using Brembana;
using Xunit;

namespace Brembana.Tests;

public class CameraSectionTitleHelperTests
{
    [Fact]
    public void Move_uses_home_card_label()
    {
        Assert.Equal("Sposta componente", CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Move));
    }

    [Fact]
    public void Create_uses_home_card_label()
    {
        Assert.Equal("Aggiungi componente", CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Create));
    }

    [Fact]
    public void Maintenance_uses_home_card_label()
    {
        Assert.Equal("Manutenzione componente", CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Maintenance));
    }

    [Fact]
    public void Inventory_titles_match_home_strings()
    {
        Assert.Equal(UiStrings.HomeMoveComponent, CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Move));
        Assert.Equal(UiStrings.HomeAddComponent, CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Create));
        Assert.Equal(UiStrings.HomeMaintenanceComponent, CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Maintenance));
    }

    [Fact]
    public void Each_inventory_mode_has_a_distinct_title()
    {
        var titles = new[]
        {
            CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Create),
            CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Move),
            CameraSectionTitleHelper.ForInventoryMode(InventoryActionMode.Maintenance)
        };

        Assert.All(titles, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(titles.Length, titles.Distinct().Count());
    }

    [Fact]
    public void Camera_session_in_inventory_mode_uses_the_section_title()
    {
        Assert.Equal(
            UiStrings.HomeMaintenanceComponent,
            CameraSectionTitleHelper.ForCameraSession(inventoryMode: true, InventoryActionMode.Maintenance));
    }

    [Fact]
    public void Camera_session_outside_inventory_shows_accessory_scan_title()
    {
        Assert.Equal(
            UiStrings.CameraAccessoryScanTitle,
            CameraSectionTitleHelper.ForCameraSession(inventoryMode: false, InventoryActionMode.Move));
    }

    [Fact]
    public void No_title_mentions_the_vision_provider()
    {
        var titles = new[]
        {
            CameraSectionTitleHelper.ForCameraSession(true, InventoryActionMode.Create),
            CameraSectionTitleHelper.ForCameraSession(true, InventoryActionMode.Move),
            CameraSectionTitleHelper.ForCameraSession(true, InventoryActionMode.Maintenance),
            CameraSectionTitleHelper.ForCameraSession(false, InventoryActionMode.Create)
        };

        Assert.All(titles, t => Assert.DoesNotContain("Vision", t, StringComparison.OrdinalIgnoreCase));
    }
}
