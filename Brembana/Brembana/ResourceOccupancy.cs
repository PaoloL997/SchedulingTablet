using System.Globalization;

namespace Brembana;

/// <summary>
/// Le risorse impegnate da un'attività a una certa data: uscita già registrata e
/// rientro non ancora registrato. È l'unica fonte della colonna Disponibilità
/// dell'inventario.
/// Logica pura, senza dipendenze da Microsoft.UI.Xaml, come <see cref="InventoryListing"/>.
/// </summary>
public sealed class ResourceOccupancy
{
    /// <summary>Formati con cui il bridge scrive le date attività ("yyyy-MM-dd" è quello nostro).</summary>
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy" };

    /// <summary>Chiavi "tabella|uid" delle risorse di cui l'attività dichiara il tipo.</summary>
    private readonly HashSet<string> _byTable;

    /// <summary>Uid delle risorse elencate solo in <c>resources</c>, quindi senza tipo.</summary>
    private readonly HashSet<string> _byUid;

    private ResourceOccupancy(HashSet<string> byTable, HashSet<string> byUid)
    {
        _byTable = byTable;
        _byUid = byUid;
    }

    /// <summary>Nessuna risorsa impegnata: tutto libero. Usata quando il dataset non è disponibile.</summary>
    public static ResourceOccupancy Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Occupazione alla data odierna, quella che serve all'elenco inventario.</summary>
    public static ResourceOccupancy FromActivities(IEnumerable<AttivitaDto>? attivita) =>
        FromActivities(attivita, DateOnly.FromDateTime(DateTime.Today));

    /// <summary>
    /// Occupazione a una data qualsiasi. La data è esplicita per poterla fissare nei test,
    /// visto che "ad oggi" cambia significato a ogni esecuzione.
    /// </summary>
    public static ResourceOccupancy FromActivities(IEnumerable<AttivitaDto>? attivita, DateOnly today)
    {
        if (attivita == null)
            return Empty;

        var byTable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byUid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var activity in attivita)
        {
            if (activity == null || !IsInProgressOn(activity, today))
                continue;

            var typed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (activity.ResourcesByTipo != null)
            {
                foreach (var (tipo, uids) in activity.ResourcesByTipo)
                {
                    if (uids == null)
                        continue;

                    // Stesso vocabolario tipo/tabella già usato dallo scan inventario:
                    // nessuna mappa parallela da tenere allineata.
                    var table = BridgeDataService.MapComponentTypeToTable(tipo) ?? tipo?.Trim() ?? "";
                    foreach (var uid in uids)
                    {
                        if (string.IsNullOrWhiteSpace(uid))
                            continue;

                        typed.Add(uid.Trim());
                        byTable.Add(Key(table, uid));
                    }
                }
            }

            // L'elenco piatto non porta il tipo: vale come rete di sicurezza per le risorse
            // che resources_by_tipo non riporta, al prezzo di poter tingere di rosso lo
            // stesso uid duplicato su un altro tipo. Meglio segnalare in più che in meno.
            foreach (var uid in BridgeDataService.ParseResources(activity.Resources))
            {
                if (!typed.Contains(uid))
                    byUid.Add(uid);
            }
        }

        return new ResourceOccupancy(byTable, byUid);
    }

    public bool IsOccupied(string? table, string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return false;

        var code = uid.Trim();
        return _byUid.Contains(code) || _byTable.Contains(Key(table, code));
    }

    /// <summary>
    /// Attività che tiene fuori le sue risorse alla data indicata: uscita registrata
    /// (<c>actual_start</c> valorizzato e non nel futuro) e attività non ancora chiusa.
    /// </summary>
    private static bool IsInProgressOn(AttivitaDto attivita, DateOnly today)
    {
        if (BridgeDataService.IsCompleted(attivita))
            return false;

        // Senza uscita registrata la risorsa è ancora in magazzino: stessa lettura di
        // BridgeDataService.IsOutgoingOperation.
        if (string.IsNullOrWhiteSpace(attivita.ActualStart))
            return false;

        // Data non interpretabile: l'uscita risulta comunque registrata, quindi la
        // risorsa è fuori. Una data futura invece non impegna nulla oggi.
        return !TryParseDate(attivita.ActualStart, out var start) || start <= today;
    }

    private static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();

        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            date = DateOnly.FromDateTime(exact);
            return true;
        }

        // Timestamp ISO completi ("2026-09-09T07:30:00") e simili.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var loose))
        {
            date = DateOnly.FromDateTime(loose);
            return true;
        }

        return false;
    }

    private static string Key(string? table, string uid) => $"{table?.Trim()}|{uid.Trim()}";
}
