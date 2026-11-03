using System.Globalization;

namespace Brembana;

/// <summary>
/// Una categoria della lista della spesa: etichetta, pezzi previsti (<see cref="Components"/>)
/// e pezzi compatibili scansionati al loro posto (<see cref="Substitutes"/>, in ordine di
/// scansione). Un sostituto occupa uno dei posti della categoria senza essere legato a un
/// previsto preciso: l'abbinamento si fa all'avvio.
/// </summary>
internal sealed record ComponentGroup(
    string TipoKey,
    string Label,
    IReadOnlyList<TrackedComponent> Components,
    IReadOnlyList<TrackedComponent> Substitutes)
{
    public ComponentGroup(string tipoKey, string label, IReadOnlyList<TrackedComponent> components)
        : this(tipoKey, label, components, Array.Empty<TrackedComponent>())
    {
    }
}

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
    internal static TrackedComponent? FindPlanned(JobOrderTracker tracker, string? code) =>
        FindPlanned(tracker, code, out _);

    /// <summary>
    /// Come <see cref="FindPlanned(JobOrderTracker, string?)"/>, ma una lettura parziale che
    /// sta dentro più pezzi (es. "C10" con C102 e C103) non sceglie a caso: restituisce
    /// <c>null</c> e mette in <paramref name="ambiguous"/> i pezzi possibili.
    /// </summary>
    internal static TrackedComponent? FindPlanned(
        JobOrderTracker tracker,
        string? code,
        out IReadOnlyList<TrackedComponent> ambiguous)
    {
        ambiguous = Array.Empty<TrackedComponent>();
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

        var partial = comps
            .Where(c => ContainsCode(c.ResourceName, normalized) || ContainsCode(c.Modello, normalized))
            .ToList();
        if (partial.Count == 1)
            return partial[0];
        if (partial.Count > 1)
            ambiguous = partial;
        return null;
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

    /// <summary>
    /// Componenti raggruppati per categoria nell'ordine di <see cref="CategoryOrder"/>;
    /// categorie ignote in coda. I previsti sono ordinati per codice, i sostituti restano
    /// nell'ordine di scansione.
    /// </summary>
    internal static IReadOnlyList<ComponentGroup> GroupByCategory(
        IEnumerable<TrackedComponent> comps,
        IEnumerable<TrackedComponent>? substitutes = null)
    {
        var planned = comps.ToList();
        var extra = substitutes?.ToList() ?? new List<TrackedComponent>();

        return planned.Concat(extra)
            .Select(c => KeyOf(c.TipoKey))
            .Distinct()
            .Select(key =>
            {
                var inGroup = planned.Where(c => KeyOf(c.TipoKey) == key).ToList();
                var subs = extra.Where(c => KeyOf(c.TipoKey) == key).ToList();
                var first = inGroup.FirstOrDefault() ?? subs[0];
                return new ComponentGroup(
                    key,
                    UiStrings.FormatComponentTypeName(first.Categoria, key),
                    inGroup.OrderBy(c => c.ResourceName, StringComparer.OrdinalIgnoreCase).ToList(),
                    subs);
            })
            .OrderBy(g => CategoryRank(g.TipoKey))
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string KeyOf(string? tipoKey) => (tipoKey ?? "").Trim().ToLowerInvariant();

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

    /// <summary>
    /// Avanzamento di una sola categoria, per il conteggio sulla pastiglia. I posti sono i
    /// pezzi previsti; ognuno si riempie con un previsto confermato o con un sostituto.
    /// Una categoria senza previsti (pezzi aggiunti all'attività) ha un posto per sostituto.
    /// </summary>
    internal static ChecklistProgress ProgressOf(ComponentGroup group)
    {
        var total = group.Components.Count > 0 ? group.Components.Count : group.Substitutes.Count;
        var filled = group.Components.Count(c => c.IsSelectedForRegister) + group.Substitutes.Count;
        return new ChecklistProgress(Math.Min(filled, total), total);
    }

    /// <summary>Avanzamento di tutta l'attività: la somma dei posti di ogni categoria.</summary>
    internal static ChecklistProgress Progress(IEnumerable<ComponentGroup> groups)
    {
        var confirmed = 0;
        var total = 0;
        foreach (var group in groups)
        {
            var progress = ProgressOf(group);
            confirmed += progress.Confirmed;
            total += progress.Total;
        }
        return new ChecklistProgress(confirmed, total);
    }

    /// <summary>
    /// Posti ancora liberi per un sostituto: previsti meno previsti confermati meno
    /// sostituti. Negativo quando un previsto confermato ha riempito un posto già preso.
    /// </summary>
    internal static int FreeSlots(ComponentGroup group) =>
        group.Components.Count
        - group.Components.Count(c => c.IsSelectedForRegister)
        - group.Substitutes.Count;

    /// <summary>Previsti della categoria non ancora confermati, in ordine di elenco.</summary>
    internal static IReadOnlyList<TrackedComponent> MissingPlanned(ComponentGroup group) =>
        group.Components.Where(c => !c.IsSelectedForRegister).ToList();

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
