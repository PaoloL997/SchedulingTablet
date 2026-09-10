namespace Brembana;

/// <summary>
/// Cosa offre la barra di selezione dell'inventario per la riga scelta.
/// Logica pura: le regole restano quelle di <see cref="InventoryActionPolicy"/>, qui si
/// traducono solo in stato ed etichette dei pulsanti.
/// </summary>
public sealed record InventorySelectionActions(
    bool CanMove,
    string? MoveHint,
    bool ShowMaintenance,
    bool MaintenanceEnables,
    string MaintenanceLabel)
{
    /// <summary>Null senza riga selezionata: la barra resta nascosta.</summary>
    public static InventorySelectionActions? For(InventoryListRow? row, string? userPlant)
    {
        if (row == null)
            return null;

        var match = row.ToMatch();

        bool canMove;
        string? moveHint;
        if (string.IsNullOrWhiteSpace(userPlant))
        {
            // PlantsEqual(null, null) è vero: senza questo controllo una riga senza
            // stabilimento risulterebbe "già qui" invece di "manca lo stabilimento".
            canMove = false;
            moveHint = UiStrings.InventoryMoveNeedsPlant;
        }
        else
        {
            // Sul tablet un pulsante disattivato non mostra tooltip: il motivo va scritto.
            var move = InventoryActionPolicy.Decide(InventoryActionMode.Move, match, userPlant);
            canMove = move.Kind == InventoryActionKind.Move;
            moveHint = canMove ? null : UiStrings.FormatInventoryAlreadyInPlant(userPlant.Trim());
        }

        var maintenance = InventoryActionPolicy.Decide(InventoryActionMode.Maintenance, match, userPlant);
        var enables = maintenance.Kind == InventoryActionKind.EnableMaintenance;

        return new InventorySelectionActions(
            canMove,
            moveHint,
            ShowMaintenance: enables || maintenance.Kind == InventoryActionKind.DisableMaintenance,
            MaintenanceEnables: enables,
            MaintenanceLabel: enables
                ? UiStrings.InventoryMaintenanceEnableAction
                : UiStrings.InventoryMaintenanceDisableAction);
    }
}
