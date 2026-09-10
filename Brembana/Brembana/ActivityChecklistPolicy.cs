using System.Globalization;

namespace Brembana;

/// <summary>Una categoria della lista della spesa: etichetta e pezzi previsti.</summary>
internal sealed record ComponentGroup(string TipoKey, string Label, IReadOnlyList<TrackedComponent> Components);

/// <summary>Quanti pezzi dell'attività sono stati confermati presenti.</summary>
public sealed record ChecklistProgress(int Confirmed, int Total)
{
    public bool IsComplete => Total > 0 && Confirmed == Total;
}

/// <summary>
/// Regole della checklist di presenza: un'attività si avvia solo quando tutti i suoi
/// componenti sono usciti e si chiude solo quando sono tutti rientrati. Qui non c'è UI:
/// la pagina accessori e l'overlay fotocamera chiamano queste funzioni.
/// </summary>
public static class ActivityChecklistPolicy
{
    private const int MinContainmentLength = 3;
    private const string NoComponentPlaceholder = "NO_COMPONENT_FOUND";

    internal static ChecklistProgress Progress(IEnumerable<TrackedComponent> comps)
    {
        var confirmed = 0;
        var total = 0;
        foreach (var comp in comps)
        {
            total++;
            if (comp.IsSelectedForRegister)
                confirmed++;
        }
        return new ChecklistProgress(confirmed, total);
    }

    internal static void ConfirmAll(IEnumerable<TrackedComponent> comps)
    {
        foreach (var comp in comps)
            comp.IsSelectedForRegister = true;
    }

    internal static void ClearAll(IEnumerable<TrackedComponent> comps)
    {
        foreach (var comp in comps)
            comp.IsSelectedForRegister = false;
    }

    /// <summary>Avvio: tutti i pezzi escono adesso.</summary>
    internal static void ApplyStart(IEnumerable<TrackedComponent> comps, DateTime now)
    {
        var list = RequireComplete(comps);
        foreach (var comp in list)
        {
            comp.IsOut = true;
            comp.OutgoingScanTime = now;
            comp.IncomingScanTime = null;
            comp.IsSelectedForRegister = false;
        }
    }

    /// <summary>Chiusura: tutti i pezzi rientrano adesso; l'orario di uscita resta.</summary>
    internal static void ApplyClose(IEnumerable<TrackedComponent> comps, DateTime now)
    {
        var list = RequireComplete(comps);
        foreach (var comp in list)
        {
            comp.IsOut = false;
            comp.IncomingScanTime = now;
            comp.OutgoingScanTime ??= now;
            comp.IsSelectedForRegister = false;
        }
    }

    private static List<TrackedComponent> RequireComplete(IEnumerable<TrackedComponent> comps)
    {
        var list = comps.ToList();
        if (!Progress(list).IsComplete)
            throw new InvalidOperationException("Checklist incompleta: confermare tutti i componenti prima di registrare.");
        return list;
    }

    /// <summary>
    /// Cerca il codice tra i pezzi previsti: prima uguaglianza esatta su uid o modello,
    /// poi contenimento (l'OCR spesso legge solo una parte), ma non sotto i 3 caratteri.
    /// </summary>
    internal static TrackedComponent? FindPlanned(JobOrderTracker tracker, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var normalized = code.Trim();
        var comps = tracker.TrackedComponents;

        var exact = comps.FirstOrDefault(c =>
            SameCode(c.ResourceName, normalized) || SameCode(c.Modello, normalized));
        if (exact != null)
            return exact;

        if (normalized.Length < MinContainmentLength)
            return null;

        return comps.FirstOrDefault(c =>
            ContainsCode(c.ResourceName, normalized) || ContainsCode(c.Modello, normalized));
    }

    private static bool SameCode(string? candidate, string code) =>
        !string.IsNullOrWhiteSpace(candidate)
        && candidate.Trim().Equals(code, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCode(string? candidate, string code)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var left = candidate.Trim();
        if (left.Length < MinContainmentLength)
            return false;
        return left.Contains(code, StringComparison.OrdinalIgnoreCase)
            || code.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Codici da provare, in ordine: quello scelto, poi quelli letti dall'immagine.</summary>
    public static IReadOnlyList<string> CandidateCodes(string? code, ComponentScanResult? scan)
    {
        var codes = new List<string>();
        Add(codes, code);
        if (scan != null)
        {
            Add(codes, scan.ComponentCode);
            foreach (var candidate in scan.AllCodes ?? new List<string>())
                Add(codes, candidate);
        }
        return codes;
    }

    private static void Add(List<string> codes, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        var trimmed = candidate.Trim();
        if (trimmed.Equals(NoComponentPlaceholder, StringComparison.OrdinalIgnoreCase))
            return;
        if (!codes.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            codes.Add(trimmed);
    }

    /// <summary>Un pezzo compatibile può sostituire quello previsto solo in uscita: ciò che rientra è ciò che è uscito.</summary>
    public static bool AllowsSubstitution(bool isOutgoing) => isOutgoing;

    /// <summary>Ordine della lista della spesa: prima i pezzi a corrispondenza esatta, poi gli accessori.</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
        new[] { "testa", "generatore", "tastatore", "mandrino", "maschera" };

    /// <summary>Componenti raggruppati per categoria nell'ordine di <see cref="CategoryOrder"/>; categorie ignote in coda.</summary>
    internal static IReadOnlyList<ComponentGroup> GroupByCategory(IEnumerable<TrackedComponent> comps)
    {
        return comps
            .GroupBy(c => (c.TipoKey ?? "").Trim().ToLowerInvariant())
            .Select(g => new ComponentGroup(
                g.Key,
                UiStrings.FormatComponentTypeName(g.First().Categoria, g.Key),
                g.OrderBy(c => c.ResourceName, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => CategoryRank(g.TipoKey))
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CategoryRank(string tipoKey)
    {
        for (var i = 0; i < CategoryOrder.Count; i++)
        {
            if (string.Equals(CategoryOrder[i], tipoKey, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return CategoryOrder.Count;
    }

    /// <summary>
    /// Requisito di misura dell'attività per una categoria, pronto da mostrare:
    /// "Ø min 18,9 · puntuale 19,86 · max 21,1 mm" per mandrini e tastatori,
    /// "Passo 25,4 mm" per le maschere, "Modello TP60" per le teste. Vuoto quando la
    /// pianificazione non lo indica.
    /// </summary>
    public static string FormatRequirement(
        string? tipoKey,
        double? boreMin,
        double? boreMid,
        double? boreMax,
        double? tubePitch,
        string? modelloTesta)
    {
        switch ((tipoKey ?? "").Trim().ToLowerInvariant())
        {
            case "mandrino":
            case "tastatore":
                var parts = new List<string>();
                if (boreMin.HasValue) parts.Add($"min {Num(boreMin.Value)}");
                if (boreMid.HasValue) parts.Add($"puntuale {Num(boreMid.Value)}");
                if (boreMax.HasValue) parts.Add($"max {Num(boreMax.Value)}");
                return parts.Count == 0 ? "" : $"Ø {string.Join(" · ", parts)} mm";
            case "maschera":
                return tubePitch.HasValue ? $"Passo {Num(tubePitch.Value)} mm" : "";
            case "testa":
                return string.IsNullOrWhiteSpace(modelloTesta) ? "" : $"Modello {modelloTesta.Trim()}";
            default:
                return "";
        }
    }

    /// <summary>Avanzamento di una sola categoria, per il conteggio sulla pastiglia.</summary>
    internal static ChecklistProgress ProgressOf(ComponentGroup group) => Progress(group.Components);

    /// <summary>Primo pezzo della categoria ancora da confermare, <c>null</c> se sono tutti fatti.</summary>
    internal static TrackedComponent? NextToConfirm(ComponentGroup group) =>
        group.Components.FirstOrDefault(c => !c.IsSelectedForRegister);

    /// <summary>Posizione (da 1) del prossimo pezzo da confermare; 0 se la categoria è completa.</summary>
    internal static int IndexOfNext(ComponentGroup group)
    {
        for (var i = 0; i < group.Components.Count; i++)
        {
            if (!group.Components[i].IsSelectedForRegister)
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// Categoria su cui puntare l'occhio: si resta su quella corrente finché ha pezzi da
    /// confermare, altrimenti si passa alla prima incompleta in avanti, girando in tondo.
    /// Con tutto confermato resta la corrente (o la prima categoria).
    /// </summary>
    internal static string? NextIncompleteTipo(IReadOnlyList<ComponentGroup> groups, string? current)
    {
        if (groups.Count == 0)
            return null;

        var start = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (string.Equals(groups[i].TipoKey, current, StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }

        for (var step = 0; step < groups.Count; step++)
        {
            var group = groups[(start + step) % groups.Count];
            if (!ProgressOf(group).IsComplete)
                return group.TipoKey;
        }

        return string.IsNullOrWhiteSpace(current) ? groups[0].TipoKey : current;
    }

    private static string Num(double value) =>
        value.ToString("0.##", CultureInfo.GetCultureInfo("it-IT"));
}
