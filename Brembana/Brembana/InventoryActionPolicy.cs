namespace Brembana;

public enum InventoryActionKind
{
    CreateNew,
    AlreadyPresent,
    Move,
    NotFound,
    EnableMaintenance,
    DisableMaintenance,
    UnsupportedType
}

public sealed record InventoryActionDecision(InventoryActionKind Kind, InventoryMatch? Match);

public static class InventoryActionPolicy
{
    public static bool SupportsMaintenance(string? table) =>
        table is "Testa" or "Generatore";

    public static InventoryActionDecision Decide(
        InventoryActionMode mode,
        InventoryMatch? match,
        string? userPlant)
    {
        if (mode == InventoryActionMode.Create)
        {
            return match == null
                ? new InventoryActionDecision(InventoryActionKind.CreateNew, null)
                : new InventoryActionDecision(InventoryActionKind.AlreadyPresent, match);
        }

        if (mode == InventoryActionMode.Maintenance)
        {
            if (match == null)
                return new InventoryActionDecision(InventoryActionKind.NotFound, null);
            if (!SupportsMaintenance(match.Table))
                return new InventoryActionDecision(InventoryActionKind.UnsupportedType, match);
            return match.InManutenzione
                ? new InventoryActionDecision(InventoryActionKind.DisableMaintenance, match)
                : new InventoryActionDecision(InventoryActionKind.EnableMaintenance, match);
        }

        if (match == null)
            return new InventoryActionDecision(InventoryActionKind.NotFound, null);

        if (BridgeDataService.PlantsEqual(match.Plant, userPlant))
            return new InventoryActionDecision(InventoryActionKind.AlreadyPresent, match);

        return new InventoryActionDecision(InventoryActionKind.Move, match);
    }

    public static bool CanStartActivitySession(string? activityPlant, string? userPlant)
    {
        if (string.IsNullOrWhiteSpace(userPlant))
            return false;
        return BridgeDataService.PlantsEqual(activityPlant, userPlant);
    }
}
