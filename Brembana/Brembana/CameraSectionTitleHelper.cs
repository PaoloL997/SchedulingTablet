namespace Brembana;

internal static class CameraSectionTitleHelper
{
    public static string ForInventoryMode(InventoryActionMode mode) => mode switch
    {
        InventoryActionMode.Move => UiStrings.HomeMoveComponent,
        InventoryActionMode.Maintenance => UiStrings.HomeMaintenanceComponent,
        _ => UiStrings.HomeAddComponent
    };

    public static string ForCameraSession(bool inventoryMode, InventoryActionMode mode) =>
        inventoryMode ? ForInventoryMode(mode) : UiStrings.CameraAccessoryScanTitle;
}
