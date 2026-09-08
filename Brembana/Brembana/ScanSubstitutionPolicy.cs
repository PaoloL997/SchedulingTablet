namespace Brembana;

/// <summary>Cosa fare con un codice scansionato che non è tra gli accessori dell'attività.</summary>
public enum ScanSubstitutionKind
{
    /// <summary>Il codice non esiste in inventario: resta il vecchio "non trovato".</summary>
    NotInInventory,

    /// <summary>Testa o generatore: serve il pezzo esatto, niente sostituzione.</summary>
    ExactTypeRequired,

    /// <summary>Mandrino, tastatore o maschera: il server decide se è compatibile.</summary>
    Candidate
}

public sealed record ScanSubstitutionDecision(ScanSubstitutionKind Kind, InventoryMatch? Match);

/// <summary>
/// Regole locali della sostituzione via scansione. La compatibilità dimensionale e lo
/// stato del pezzo nelle date dell'attività li verifica il server
/// (<c>POST /api/bridge/attivita/&lt;pk&gt;/scan/</c>): qui si decide solo se ha senso
/// chiederglielo e come riportare la risposta sul tracker della sessione.
/// </summary>
public static class ScanSubstitutionPolicy
{
    public const string EsitoCorrispondente = "corrispondente";
    public const string EsitoSostituzione = "sostituzione";
    public const string RiassegnazioneScoperta = "scoperta";

    private static readonly HashSet<string> ExactTipi = new(StringComparer.OrdinalIgnoreCase)
    {
        "testa",
        "generatore"
    };

    private static readonly HashSet<string> AccessoryTipi = new(StringComparer.OrdinalIgnoreCase)
    {
        "mandrino",
        "tastatore",
        "maschera"
    };

    public static bool IsExactTipo(string? tipoKey) =>
        !string.IsNullOrWhiteSpace(tipoKey) && ExactTipi.Contains(tipoKey.Trim());

    public static bool IsAccessoryTipo(string? tipoKey) =>
        !string.IsNullOrWhiteSpace(tipoKey) && AccessoryTipi.Contains(tipoKey.Trim());

    public static ScanSubstitutionDecision Decide(InventoryMatch? match)
    {
        if (match == null)
            return new ScanSubstitutionDecision(ScanSubstitutionKind.NotInInventory, null);
        if (IsExactTipo(match.TipoKey))
            return new ScanSubstitutionDecision(ScanSubstitutionKind.ExactTypeRequired, match);
        if (IsAccessoryTipo(match.TipoKey))
            return new ScanSubstitutionDecision(ScanSubstitutionKind.Candidate, match);
        return new ScanSubstitutionDecision(ScanSubstitutionKind.NotInInventory, match);
    }

    /// <summary>Uid della categoria <paramref name="tipoKey"/> assegnati all'attività, in ordine di elenco.</summary>
    internal static IReadOnlyList<string> AssignedUidsOfTipo(JobOrderTracker tracker, string tipoKey) =>
        tracker.TrackedComponents
            .Where(c => string.Equals(c.TipoKey, tipoKey, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ResourceName)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

    /// <summary>
    /// Riporta sul tracker la sostituzione già applicata dal server: il componente previsto
    /// prende l'uid e la misura del pezzo scansionato e risulta selezionato per la
    /// registrazione; senza un previsto, il pezzo viene aggiunto.
    /// </summary>
    internal static TrackedComponent ApplySubstitution(
        JobOrderTracker tracker,
        string tipoKey,
        string? atteso,
        InventoryMatch scanned)
    {
        var target = string.IsNullOrWhiteSpace(atteso)
            ? null
            : tracker.TrackedComponents.FirstOrDefault(c =>
                string.Equals(c.TipoKey, tipoKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.ResourceName, atteso, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            target = new TrackedComponent
            {
                TipoKey = tipoKey,
                Categoria = scanned.Table,
                SpecLabel = SpecLabelFor(tipoKey),
                SpecUnit = "mm"
            };
            tracker.TrackedComponents.Add(target);
        }

        var previous = target.ResourceName;
        target.ResourceName = scanned.Uid;
        target.Modello = scanned.MeasureValue ?? "";
        target.SpecValue = scanned.MeasureValue ?? "";
        target.IsSelectedForRegister = true;

        foreach (var activity in tracker.MatchedActivities)
        {
            if (!string.IsNullOrWhiteSpace(previous))
                activity.AssignedResources.RemoveAll(r =>
                    string.Equals(r, previous, StringComparison.OrdinalIgnoreCase));
            if (!activity.AssignedResources.Contains(scanned.Uid, StringComparer.OrdinalIgnoreCase))
                activity.AssignedResources.Add(scanned.Uid);
        }

        return target;
    }

    /// <summary>
    /// Rimette il pezzo previsto al posto di quello scansionato. Serve quando l'operatore
    /// annulla una sostituzione che non è ancora stata scritta sul server.
    /// </summary>
    internal static void RevertSubstitution(
        JobOrderTracker tracker,
        TrackedComponent target,
        string previousUid,
        string previousModello,
        string previousSpecValue)
    {
        var scannedUid = target.ResourceName;

        if (string.IsNullOrWhiteSpace(previousUid))
        {
            // Il pezzo era stato aggiunto, non sostituito: sparisce.
            tracker.TrackedComponents.Remove(target);
        }
        else
        {
            target.ResourceName = previousUid;
            target.Modello = previousModello;
            target.SpecValue = previousSpecValue;
            target.IsSelectedForRegister = false;
        }

        foreach (var activity in tracker.MatchedActivities)
        {
            activity.AssignedResources.RemoveAll(r =>
                string.Equals(r, scannedUid, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(previousUid)
                && !activity.AssignedResources.Contains(previousUid, StringComparer.OrdinalIgnoreCase))
            {
                activity.AssignedResources.Add(previousUid);
            }
        }
    }

    /// <summary>Attività rimaste senza sostituto dopo la sottrazione del pezzo.</summary>
    public static IReadOnlyList<string> UncoveredActivities(AttivitaScanResult result) =>
        (result.Riassegnazioni ?? new List<ScanRiassegnazioneDto>())
            .Where(r => string.Equals(r.Esito, RiassegnazioneScoperta, StringComparison.OrdinalIgnoreCase))
            .Select(r => string.IsNullOrWhiteSpace(r.ActivityId) ? r.Label ?? "?" : r.ActivityId)
            .ToList();

    private static string SpecLabelFor(string tipoKey) =>
        string.Equals(tipoKey, "maschera", StringComparison.OrdinalIgnoreCase) ? "Passo" : "Diametro";
}
