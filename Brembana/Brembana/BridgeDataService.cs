namespace Brembana;

public sealed record InventoryMatch(
    string Table,
    string Uid,
    string? Plant,
    string? MeasureValue,
    string TipoKey,
    bool InManutenzione = false);

public sealed class InventoryCreateForm
{
    public string Table { get; set; } = "";
    public string Uid { get; set; } = "";
    public string? Plant { get; set; }
    public string? MeasureValue { get; set; }
    public string? CenterDistanceValue { get; set; }
}

public sealed class BridgeDataService
{
    private static readonly Dictionary<string, string> TipoLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["testa"] = "Testa",
        ["generatore"] = "Generatore",
        ["mandrino"] = "Mandrino",
        ["tastatore"] = "Tastatore",
        ["maschera"] = "Maschera",
    };

    private static readonly Dictionary<string, string> TipoToTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["testa"] = "Testa",
        ["generatore"] = "Generatore",
        ["mandrino"] = "Mandrino",
        ["tastatore"] = "Tastatore",
        ["maschera"] = "Maschera",
    };

    private static readonly Dictionary<string, string> TableToTipo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Testa"] = "testa",
        ["Generatore"] = "generatore",
        ["Mandrino"] = "mandrino",
        ["Tastatore"] = "tastatore",
        ["Maschera"] = "maschera",
    };

    public static IReadOnlyList<string> InventoryTables { get; } =
        new[] { "Testa", "Generatore", "Mandrino", "Tastatore", "Maschera" };

    private readonly BridgeApiClient _client;
    private List<AttivitaDto> _attivita = new();
    private InventarioDto _inventario = new();

    public BridgeDataService(BridgeApiClient client) => _client = client;

    public IReadOnlyList<AttivitaDto> Attivita => _attivita;

    /// <summary>Ultimo inventario sincronizzato, per la sola consultazione dall'hub.</summary>
    public InventarioDto Inventario => _inventario;

    public async Task SyncDatasetAsync(CancellationToken cancellationToken = default)
    {
        var dataset = await _client.GetDatasetAsync(cancellationToken);
        _attivita = dataset.Attivita ?? new List<AttivitaDto>();
        _inventario = dataset.Inventario ?? new InventarioDto();
    }

    public List<AttivitaDto> GetOpenActivities() =>
        FilterOpenActivities(_attivita, App.CurrentUserStabilimento);

    public static List<AttivitaDto> FilterOpenActivities(IEnumerable<AttivitaDto> source, string? userPlant)
    {
        if (string.IsNullOrWhiteSpace(userPlant))
            return new List<AttivitaDto>();

        var plant = userPlant.Trim();
        return source
            .Where(a => !IsCompleted(a) && PlantsEqual(a.Plant, plant))
            .ToList();
    }

    internal void LoadDatasetForTests(IEnumerable<AttivitaDto>? attivita = null, InventarioDto? inventario = null)
    {
        if (attivita != null)
            _attivita = attivita.ToList();
        if (inventario != null)
            _inventario = inventario;
    }

    public static bool IsOutgoingOperation(AttivitaDto attivita) =>
        string.IsNullOrWhiteSpace(attivita.ActualStart);

    public static int CountResources(AttivitaDto attivita) =>
        ParseResources(attivita.Resources).Count;

    public List<string> GetJobOrderLabels() =>
        GetOpenActivities().Select(FormatJobOrderLabel).ToList();

    public AttivitaDto? FindByActivityId(string activityId) =>
        _attivita.FirstOrDefault(a => a.ActivityId.Equals(activityId, StringComparison.OrdinalIgnoreCase));

    public static string? MapComponentTypeToTable(string? componentType)
    {
        if (string.IsNullOrWhiteSpace(componentType))
            return null;

        return TipoToTable.TryGetValue(componentType.Trim(), out var table) ? table : null;
    }

    public static string MapTableToTipoKey(string table) =>
        TableToTipo.TryGetValue(table, out var tipo) ? tipo : table.ToLowerInvariant();

    public static bool PlantsEqual(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    public List<InventoryMatch> FindInventoryMatches(string code, ComponentScanResult? scan = null)
    {
        var lookupCodes = BuildLookupCodes(code, scan);
        if (lookupCodes.Count == 0)
            return new List<InventoryMatch>();

        var matches = new List<InventoryMatch>();

        void AddUidMatches(
            IEnumerable<(string Table, string Uid, string? Plant, string? Measure, bool InManutenzione)> items,
            string tipoKey)
        {
            foreach (var item in items)
            {
                if (ContainsExactCode(lookupCodes, item.Uid))
                    matches.Add(new InventoryMatch(
                        item.Table, item.Uid, item.Plant, item.Measure, tipoKey, item.InManutenzione));
            }
        }

        var testa = _inventario.Testa.Select(x => ("Testa", x.Uid, x.Plant, x.Model, x.InManutenzione));
        var generatore = _inventario.Generatore.Select(x => ("Generatore", x.Uid, x.Plant, x.Model, x.InManutenzione));
        var mandrino = _inventario.Mandrino.Select(x => ("Mandrino", x.Uid, x.Plant, x.Diameter, false));
        var tastatore = _inventario.Tastatore.Select(x => ("Tastatore", x.Uid, x.Plant, x.Diameter, false));
        var maschera = _inventario.Maschera.Select(x => ("Maschera", x.Uid, x.Plant, x.Pitch, false));

        AddUidMatches(testa, "testa");
        AddUidMatches(generatore, "generatore");
        AddUidMatches(mandrino, "mandrino");
        AddUidMatches(tastatore, "tastatore");
        AddUidMatches(maschera, "maschera");

        return matches
            .GroupBy(m => $"{m.Table}|{m.Uid}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Il componente già in inventario con questo uid, se esiste.
    /// Cerca su tutte le tabelle: l'uid è chiave primaria <em>per tabella</em>, quindi
    /// lo stesso codice può essere duplicato tra tipi diversi senza che il server lo rifiuti.
    /// Con <paramref name="preferredTable"/> valorizzata si privilegia la corrispondenza
    /// nello stesso tipo, che è quella che l'operatore si aspetta di vedere citata.
    /// </summary>
    public InventoryMatch? FindExistingUid(string? uid, string? preferredTable = null)
    {
        if (string.IsNullOrWhiteSpace(uid))
            return null;

        var matches = FindInventoryMatches(uid.Trim());
        if (matches.Count == 0)
            return null;

        return matches.FirstOrDefault(m =>
                   string.Equals(m.Table, preferredTable, StringComparison.OrdinalIgnoreCase))
               ?? matches[0];
    }

    private static HashSet<string> BuildLookupCodes(string code, ComponentScanResult? scan)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(code))
            codes.Add(code.Trim());

        if (scan?.AllCodes != null)
        {
            foreach (var candidate in scan.AllCodes)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                    codes.Add(candidate.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(scan?.ComponentCode) &&
            !scan.ComponentCode.Equals("NO_COMPONENT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            codes.Add(scan.ComponentCode.Trim());
        }

        return codes;
    }

    private static bool ContainsExactCode(HashSet<string> lookupCodes, string inventoryCode)
    {
        if (lookupCodes.Count == 0 || string.IsNullOrWhiteSpace(inventoryCode))
            return false;
        return lookupCodes.Contains(inventoryCode.Trim());
    }

    public InventoryMatch? FindInInventory(string code, ComponentScanResult? scan = null)
    {
        var matches = FindInventoryMatches(code, scan);
        if (matches.Count == 0)
            return null;

        if (matches.Count == 1)
            return matches[0];

        var preferredTable = MapComponentTypeToTable(scan?.ComponentType);
        if (!string.IsNullOrWhiteSpace(preferredTable))
        {
            var typed = matches.FirstOrDefault(m => m.Table.Equals(preferredTable, StringComparison.OrdinalIgnoreCase));
            if (typed != null)
                return typed;
        }

        return matches[0];
    }

    public InventoryCreateForm BuildCreateForm(ComponentScanResult scan, string defaultPlant)
    {
        var code = scan.ComponentCode?.Trim() ?? "";
        return new InventoryCreateForm
        {
            Table = "",
            Uid = code,
            Plant = defaultPlant,
            MeasureValue = "",
            CenterDistanceValue = ""
        };
    }

    public IReadOnlyList<string> GetExistingModelOptions(string table)
    {
        return table switch
        {
            "Testa" => _inventario.Testa
                .Select(x => x.Model?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList(),
            "Generatore" => _inventario.Generatore
                .Select(x => x.Model?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList(),
            _ => Array.Empty<string>()
        };
    }

    public Dictionary<string, object?> BuildCreateRequestBody(InventoryCreateForm form)
    {
        var body = new Dictionary<string, object?>
        {
            ["uid"] = form.Uid.Trim(),
            ["plant"] = string.IsNullOrWhiteSpace(form.Plant) ? null : form.Plant.Trim()
        };

        var measure = string.IsNullOrWhiteSpace(form.MeasureValue) ? null : form.MeasureValue.Trim();
        switch (form.Table)
        {
            case "Mandrino":
            case "Tastatore":
                body["diameter"] = measure;
                break;
            case "Maschera":
                body["pitch"] = measure;
                body["center_distance"] = string.IsNullOrWhiteSpace(form.CenterDistanceValue) ? null : form.CenterDistanceValue.Trim();
                break;
            default:
                body["model"] = measure;
                break;
        }

        return body;
    }

    public async Task CreateInventoryResourceAsync(InventoryCreateForm form, CancellationToken cancellationToken = default)
    {
        var body = BuildCreateRequestBody(form);
        await _client.CreateInventoryResourceAsync(form.Table, body, cancellationToken);
        await RefreshAfterInventoryMutationAsync(cancellationToken);
    }

    public async Task MoveInventoryResourceAsync(InventoryMatch match, string destinationPlant, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["plant"] = destinationPlant.Trim() };
        await _client.PatchInventoryResourceAsync(match.Table, match.Uid, body, cancellationToken);
        await RefreshAfterInventoryMutationAsync(cancellationToken);
    }

    public async Task SetMaintenanceAsync(InventoryMatch match, bool inMaintenance, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["in_manutenzione"] = inMaintenance };
        await _client.PatchInventoryResourceAsync(match.Table, match.Uid, body, cancellationToken);
        await RefreshAfterInventoryMutationAsync(cancellationToken);
    }

    private async Task RefreshAfterInventoryMutationAsync(CancellationToken cancellationToken)
    {
        await _client.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        await SyncDatasetAsync(cancellationToken);
    }

    internal JobOrderTracker BuildTracker(AttivitaDto attivita)
    {
        var tracker = new JobOrderTracker { JobOrderName = attivita.ActivityId };
        var resources = ParseResources(attivita.Resources);
        var status = DeriveStatus(attivita);

        tracker.MatchedActivities.Add(new TrackedActivity
        {
            OrmId = attivita.OrmId,
            ActivityName = attivita.ActivityId,
            Responsabile = attivita.Responsible ?? "",
            Stabilimento = attivita.Plant ?? "",
            ProjectStatus = status,
            AssignedResources = resources,
            ActualStartSet = !string.IsNullOrWhiteSpace(attivita.ActualStart),
            ActualFinishSet = !string.IsNullOrWhiteSpace(attivita.ActualFinish),
        });

        var tipoByUid = BuildTipoLookup(attivita);
        foreach (var uid in resources)
        {
            tipoByUid.TryGetValue(uid, out var tipo);
            var spec = ResolveComponentSpec(uid, tipo);
            tracker.TrackedComponents.Add(new TrackedComponent
            {
                ResourceName = uid,
                Categoria = spec.Categoria,
                Modello = spec.SpecValue,
                TipoKey = spec.TipoKey,
                SpecLabel = spec.SpecLabel,
                SpecValue = spec.SpecValue,
                SpecUnit = spec.SpecUnit,
                SpecDetail = spec.SpecDetail,
            });
        }

        return tracker;
    }

    internal async Task SaveActivityDatesAsync(IEnumerable<TrackedActivity> activities, CancellationToken cancellationToken = default)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var activity in activities)
        {
            var update = new AttivitaUpdateRequest();
            if (activity.DateWasUpdated && activity.ActualStartSet)
                update.ActualStart = today;
            if (activity.DateWasUpdated && activity.ActualFinishSet &&
                activity.ProjectStatus.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                update.ActualFinish = today;

            if (update.ActualStart == null && update.ActualFinish == null)
                continue;

            await _client.UpdateAttivitaAsync(activity.OrmId, update, cancellationToken);
        }
    }

    public static string FormatJobOrderLabel(AttivitaDto attivita)
    {
        if (!string.IsNullOrWhiteSpace(attivita.Plant))
            return $"{attivita.ActivityId}  \u2014  {attivita.Plant}";
        return attivita.ActivityId;
    }

    public static string ExtractActivityIdFromLabel(string label)
    {
        if (label.Contains('\u2014'))
            return label.Split('\u2014')[0].Trim();
        return label.Trim();
    }

    /// <summary>
    /// Attività chiusa: rientro registrato oppure pill "completata" dal server.
    /// Pubblica perché la stessa regola decide sia le attività aperte sia le risorse
    /// ancora impegnate (<see cref="ResourceOccupancy"/>).
    /// </summary>
    public static bool IsCompleted(AttivitaDto attivita)
    {
        if (!string.IsNullOrWhiteSpace(attivita.ActualFinish))
            return true;
        return attivita.StatusPills?.Any(p => p.Key.Equals("completata", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string DeriveStatus(AttivitaDto attivita)
    {
        if (!string.IsNullOrWhiteSpace(attivita.ActualFinish)) return "Finished";
        if (!string.IsNullOrWhiteSpace(attivita.ActualStart)) return "Ongoing";
        return "Pending";
    }

    /// <summary>Le risorse dell'attività dall'elenco piatto separato da virgole o punti e virgola.</summary>
    public static List<string> ParseResources(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .Where(r => !string.IsNullOrEmpty(r))
            .ToList();
    }

    private static Dictionary<string, string> BuildTipoLookup(AttivitaDto attivita)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attivita.ResourcesByTipo == null) return lookup;
        foreach (var (tipo, ids) in attivita.ResourcesByTipo)
        {
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    lookup[id] = tipo;
            }
        }
        return lookup;
    }

    private ComponentSpec ResolveComponentSpec(string uid, string? tipo)
    {
        var tipoKey = tipo?.ToLowerInvariant() ?? "";
        var categoria = !string.IsNullOrWhiteSpace(tipo) && TipoLabels.TryGetValue(tipo, out var label)
            ? label
            : (tipo ?? "");

        switch (tipoKey)
        {
            case "testa":
            {
                var item = _inventario.Testa.FirstOrDefault(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
                var value = item?.Model ?? "";
                return new ComponentSpec(categoria, tipoKey, "Modello", value, "");
            }
            case "generatore":
            {
                var item = _inventario.Generatore.FirstOrDefault(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
                var value = item?.Model ?? "";
                return new ComponentSpec(categoria, tipoKey, "Modello", value, "");
            }
            case "mandrino":
            {
                var item = _inventario.Mandrino.FirstOrDefault(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
                var value = item?.Diameter ?? "";
                return new ComponentSpec(categoria, tipoKey, "Diametro", value, "mm");
            }
            case "tastatore":
            {
                var item = _inventario.Tastatore.FirstOrDefault(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
                var value = item?.Diameter ?? "";
                return new ComponentSpec(categoria, tipoKey, "Diametro", value, "mm");
            }
            case "maschera":
            {
                var item = _inventario.Maschera.FirstOrDefault(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase));
                var pitch = item?.Pitch ?? "";
                var interasse = item?.CenterDistance ?? "";
                string detail = "";
                if (!string.IsNullOrWhiteSpace(pitch) && !string.IsNullOrWhiteSpace(interasse))
                    detail = $"Interasse: {interasse} mm";
                else if (!string.IsNullOrWhiteSpace(interasse))
                    return new ComponentSpec(categoria, tipoKey, "Interasse", interasse, "mm");
                return new ComponentSpec(categoria, tipoKey, "Passo", pitch, "mm", detail);
            }
            default:
                return new ComponentSpec(categoria, tipoKey, "", "", "");
        }
    }

    private readonly record struct ComponentSpec(
        string Categoria,
        string TipoKey,
        string SpecLabel,
        string SpecValue,
        string SpecUnit,
        string SpecDetail = "");
}
