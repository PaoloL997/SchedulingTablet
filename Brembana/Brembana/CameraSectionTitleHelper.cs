namespace Brembana;

internal static class CameraSectionTitleHelper
{
    public static string ForInventoryMode(InventoryActionMode mode) => mode switch
    {
        InventoryActionMode.Move => UiStrings.HomeMoveComponent,
        InventoryActionMode.Maintenance => UiStrings.HomeMaintenanceComponent,
        _ => UiStrings.HomeAddComponent
    };

    /// <summary>Fuori dall'inventario il titolo dice cosa si sta scansionando: uscita o rientro.</summary>
    public static string ForCameraSession(bool inventoryMode, InventoryActionMode mode, bool isOutgoing = true) =>
        inventoryMode
            ? ForInventoryMode(mode)
            : isOutgoing ? UiStrings.CameraScanTitleOutgoing : UiStrings.CameraScanTitleIncoming;
}
